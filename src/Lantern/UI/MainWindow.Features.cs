using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Lantern.Core;

namespace Lantern.UI
{
    /// <summary>Groups, profiles, player lookup, health/security reports, low-resource settings, tray.</summary>
    public partial class MainWindow
    {
        readonly ProfileLibrary _profiles = new ProfileLibrary();
        string _groupFilter;                 // null = everyone, "" = ungrouped, otherwise a group name
        string _chipSignature = "";
        TrayManager _tray;

        /// <summary>Where "copy" actions put text. A field so tests don't touch the real clipboard.</summary>
        internal Action<string> ClipboardSet = text => Clipboard.SetText(text);

        // wiring
        void WireFeatures()
        {
            ProfilesList.ItemsSource = _profiles.Profiles;
            _profiles.Profiles.CollectionChanged += (s, e) => { UpdateProfilesEmpty(); RefreshProfiles(); };
            NavProfiles.Checked += (s, e) => { ShowPage(PageProfiles); RefreshProfiles(); };
            ProfilesList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnProfileClick));
            SaveProfileButton.Click += async (s, e) => await SaveProfileFlowAsync();
            SaveCurrentProfileButton.Click += async (s, e) => await SaveProfileFlowAsync();
            FindPlayerButton.Click += async (s, e) => await FindPlayerFlowAsync();
            NewGroupButton.Click += async (s, e) => await NewGroupFromTickedAsync();
            AccountsList.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnStatusClick));
            UpdateProfilesEmpty();

            // low-resource mode
            var mode = string.IsNullOrEmpty(_s.PerfMode) ? "Off" : _s.PerfMode;
            PerfOff.IsChecked = mode == "Off";
            PerfBalanced.IsChecked = mode == "Balanced";
            PerfMinimal.IsChecked = mode == "Minimal";
            MinimizeSwitch.IsChecked = _s.MinimizeAfterLoad;
            PerfOff.Checked += (s, e) => SetPerf("Off");
            PerfBalanced.Checked += (s, e) => SetPerf("Balanced");
            PerfMinimal.Checked += (s, e) => SetPerf("Minimal");
            MinimizeSwitch.Checked += (s, e) => { _s.MinimizeAfterLoad = true; _mgr.SaveSettings(); };
            MinimizeSwitch.Unchecked += (s, e) => { _s.MinimizeAfterLoad = false; _mgr.SaveSettings(); };
            PerfRestoreButton.Click += async (s, e) => await RestorePerfAsync();
            NavSettings.Checked += (s, e) => UpdatePerfUi();
            var perfTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            perfTimer.Tick += (s, e) => { if (PageSettings.Visibility == Visibility.Visible) UpdatePerfUi(); };
            perfTimer.Start();
            UpdatePerfUi();

            // tray + privacy
            TraySwitch.IsChecked = _s.TrayIcon;
            CloseToTraySwitch.IsChecked = _s.CloseToTray;
            CloseToTraySwitch.IsEnabled = _s.TrayIcon;
            ClearClipSwitch.IsChecked = _s.ClearClipboard;
            TraySwitch.Checked += (s, e) => { _s.TrayIcon = true; ApplyTray(); _mgr.SaveSettings(); };
            TraySwitch.Unchecked += (s, e) => { _s.TrayIcon = false; ApplyTray(); _mgr.SaveSettings(); };
            CloseToTraySwitch.Checked += (s, e) => { _s.CloseToTray = true; _mgr.SaveSettings(); };
            CloseToTraySwitch.Unchecked += (s, e) => { _s.CloseToTray = false; _mgr.SaveSettings(); };
            ClearClipSwitch.Checked += (s, e) => { _s.ClearClipboard = true; _mgr.SaveSettings(); };
            ClearClipSwitch.Unchecked += (s, e) => { _s.ClearClipboard = false; _mgr.SaveSettings(); };
            HealthButton.Click += async (s, e) => await RunHealthAsync();
            SecurityButton.Click += async (s, e) => await RunSecurityAsync();

            // live usage + suggestion
            _mgr.UsageChanged += UpdateUsageUi;
            AdviceClose.Click += (s, e) => { if (_mgr.Advice != null) _dismissedAdvice = _mgr.Advice.Kind; UpdateUsageUi(); };
            AdviceApply.Click += (s, e) => ApplyAdvice();
            UsageApply.Click += (s, e) => ApplyAdvice();
            WireOptimizer();
            UpdateUsageUi();
        }

        // live usage + suggestion
        string _dismissedAdvice = "";

        void UpdateUsageUi()
        {
            var adv = _mgr.Advice;
            if (adv == null || adv.Kind != _dismissedAdvice) _dismissedAdvice = "";   // a dismissal only covers that one suggestion

            // the bar only interrupts for real problems; the "you have headroom" tip lives in Settings
            bool show = adv != null && adv.Level == Level.Warn && adv.Kind != _dismissedAdvice;
            AdviceBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (adv != null)
            {
                AdviceTitle.Text = adv.Title;
                AdviceDetail.Text = adv.Detail;
                AdviceApply.Content = adv.Mode != null ? "Switch to " + adv.Mode : "";
                AdviceApply.Visibility = adv.Mode != null ? Visibility.Visible : Visibility.Collapsed;
            }

            Rich.Set(UsageNow, _mgr.UsageSummary.Length > 0 ? "Right now, all windows together: " + Rich.Values(_mgr.UsageSummary) : "No windows are running, so there is nothing to measure yet.");
            UsageAdvice.Text = adv == null
                ? "A suggestion appears after about 20 seconds of running windows."
                : adv.Title + ". " + adv.Detail;
            UsageApply.Content = adv?.Mode != null ? "Switch to " + adv.Mode : "";
            UsageApply.Visibility = adv?.Mode != null ? Visibility.Visible : Visibility.Collapsed;

            UpdateAccountsUi();
        }

        void ApplyAdvice()
        {
            var mode = _mgr.Advice?.Mode;
            if (mode == null) return;
            (mode == "Minimal" ? PerfMinimal : mode == "Balanced" ? PerfBalanced : PerfOff).IsChecked = true;   // fires the normal setting handler
            ShowToast("Switched to " + mode + " mode. It applies to windows you launch next; open ones keep how they started.", false);
        }

        // report dialog
        Action _dlgSecondary;

        static readonly Dictionary<Level, (string glyph, string brush)> LevelStyle = new Dictionary<Level, (string, string)>
        {
            { Level.Ok,   ("", "BrushSuccess") },
            { Level.Info, ("", "BrushTextSecondary") },
            { Level.Warn, ("", "BrushWarning") },
            { Level.Bad,  ("", "BrushDanger") },
        };

        /// <summary>A scrollable list of findings in the dialog. <paramref name="copyText"/> adds a "Copy report" button.</summary>
        public Task<bool> ReportAsync(string title, string intro, IEnumerable<Finding> rows, string copyText = null)
        {
            ResetDialog();
            DialogCard.MaxWidth = 600;
            DlgBody.Text = intro ?? "";
            DlgBody.Visibility = string.IsNullOrWhiteSpace(intro) ? Visibility.Collapsed : Visibility.Visible;

            ReportList.Children.Clear();
            foreach (var f in rows)
            {
                var (glyph, brush) = LevelStyle[f.Level];
                var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var icon = new TextBlock { Text = glyph, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0), FontSize = 15 };
                icon.SetResourceReference(FrameworkElement.StyleProperty, "Icon");
                icon.HorizontalAlignment = HorizontalAlignment.Left;
                icon.SetResourceReference(TextBlock.ForegroundProperty, brush);
                row.Children.Add(icon);

                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = f.Title, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold });
                if (!string.IsNullOrWhiteSpace(f.Detail))
                {
                    var d = new TextBlock { Text = f.Detail, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 2, 0, 0) };
                    d.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary");
                    text.Children.Add(d);
                }
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                ReportList.Children.Add(row);
            }
            DlgReport.ScrollToTop();
            DlgReport.Visibility = Visibility.Visible;

            if (copyText != null)
            {
                DlgCancel.Content = "Copy report";
                _dlgSecondary = () =>
                {
                    try { ClipboardSet(copyText); ShowToast("Report copied. It contains no cookies.", false); }
                    catch { ShowToast("Couldn't reach the clipboard.", true); }
                };
            }
            else DlgCancel.Visibility = Visibility.Collapsed;

            return OpenDialog(title, "Close", false, () => Task.FromResult(true), DlgOk);
        }

        async Task ShowDetailsAsync(AccountItem a)
        {
            var rows = new List<Finding>();
            string title;
            if (a.Error != null)
            {
                title = "Why " + a.Display + " couldn't launch";
                rows.Add(new Finding(Level.Bad, a.Error.Title, a.Error.Http > 0 ? "Roblox answered with HTTP " + a.Error.Http + " at " + a.Error.Time.ToString("HH:mm:ss") + "." : "At " + a.Error.Time.ToString("HH:mm:ss") + "."));
                if (a.Error.Why.Length > 0) rows.Add(new Finding(Level.Info, "Why this usually happens", a.Error.Why));
                if (a.Error.Fix.Length > 0) rows.Add(new Finding(Level.Ok, "What to try", a.Error.Fix));
            }
            else title = a.Display + " — details";

            if (a.Exit != null)
                rows.Add(new Finding(a.Exit.Unexpected ? Level.Warn : Level.Info, "Last window: " + a.Exit.Title, a.Exit.Detail));
            if (a.History.Count > 0)
                rows.Add(new Finding(Level.Info, "Recent events", string.Join("\n", a.History.Skip(Math.Max(0, a.History.Count - 14)))));
            if (rows.Count == 0) rows.Add(new Finding(Level.Ok, "Nothing has gone wrong with this account so far."));

            await ReportAsync(title, "", rows, Health.ToText(title, rows));
        }

        // health + security
        async Task RunHealthAsync()
        {
            HealthButton.IsEnabled = false;
            ShowToast("Checking this PC…", false);
            try
            {
                var rows = await Health.SystemAsync(_s, _mgr.Accounts.ToList(), _mgr.MeasuredBytesPerWindow);
                int bad = rows.Count(r => r.Level == Level.Bad), warn = rows.Count(r => r.Level == Level.Warn);
                string summary = bad + warn == 0 ? "Everything looks good."
                    : (bad > 0 ? bad + (bad == 1 ? " problem" : " problems") : "") + (bad > 0 && warn > 0 ? " and " : "") + (warn > 0 ? warn + (warn == 1 ? " thing" : " things") + " worth a look" : "") + ".";
                await ReportAsync("Health check", summary, rows, Health.ToText("health check", rows));
            }
            catch (Exception ex) { Logger.Write("Health: " + ex); ShowToast("The health check failed: " + ex.Message, true); }
            finally { HealthButton.IsEnabled = true; }
        }

        async Task RunSecurityAsync()
        {
            if (_mgr.Accounts.Count == 0) { ShowToast("Add an account first.", true); return; }
            SecurityButton.IsEnabled = false;
            ShowToast("Checking your accounts…", false);
            try
            {
                var rows = await Health.SecurityAsync(_mgr.Accounts.ToList(), ClipboardText, () => Clipboard.Clear());
                int bad = rows.Count(r => r.Level == Level.Bad), warn = rows.Count(r => r.Level == Level.Warn);
                string summary = bad + warn == 0 ? "No problems found. Nothing in this report is secret: it never contains cookies."
                    : (bad + warn) + (bad + warn == 1 ? " thing needs" : " things need") + " attention. Nothing in this report is secret.";
                await ReportAsync("Account security check", summary, rows, Health.ToText("account security check", rows));
            }
            catch (Exception ex) { Logger.Write("Security: " + ex); ShowToast("The security check failed: " + ex.Message, true); }
            finally { SecurityButton.IsEnabled = true; }
        }

        /// <summary>After cookies were added from the clipboard: don't leave them there for other programs to read.</summary>
        void ClearClipboardIfCookie()
        {
            if (!_s.ClearClipboard) return;
            try { if (Clipboard.ContainsText() && Clipboard.GetText().Contains("_|WARNING:-DO-NOT-SHARE")) Clipboard.Clear(); }
            catch { /* the clipboard can be locked by another program; not worth failing over */ }
        }

        // groups
        void UpdateGroupChips()
        {
            var names = _mgr.GroupNames();
            int total = _mgr.Accounts.Count;
            GroupBar.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (!string.IsNullOrEmpty(_groupFilter) && !names.Contains(_groupFilter, StringComparer.OrdinalIgnoreCase)) { _groupFilter = null; _view.Refresh(); }
            if (_groupFilter == "" && names.Count == 0) { _groupFilter = null; _view.Refresh(); }

            int ungrouped = _mgr.Accounts.Count(a => !a.HasGroup);
            var sig = total + "|" + ungrouped + "|" + string.Join(",", names.Select(n => n + ":" + _mgr.InGroup(n).Count()));
            if (sig == _chipSignature) return;
            _chipSignature = sig;

            GroupChips.Children.Clear();
            if (names.Count == 0) return;                 // no groups yet: just the "Group" button
            AddChip("All", null, total);
            foreach (var n in names) AddChip(n, n, _mgr.InGroup(n).Count());
            if (ungrouped > 0) AddChip("Ungrouped", "", ungrouped);
        }

        void AddChip(string text, string filter, int count)
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Chip"),
                GroupName = "groupchips",
                Content = text + "  ·  " + count,
                Tag = filter,
                IsChecked = _groupFilter == filter
            };
            rb.Checked += (s, e) => { _groupFilter = (string)((RadioButton)s).Tag; _view.Refresh(); Dispatcher.BeginInvoke(new Action(UpdateAccountsUi)); };
            if (!string.IsNullOrEmpty(filter))
            {
                rb.ContextMenu = GroupMenu(filter);
                rb.ToolTip = "Right-click for group actions";
            }
            GroupChips.Children.Add(rb);
        }

        ContextMenu GroupMenu(string name)
        {
            var m = new ContextMenu();
            m.Items.Add(MenuEntry("", "Tick everyone in " + name, (s, e) => { foreach (var a in _mgr.InGroup(name)) a.IsSelected = true; }));
            m.Items.Add(MenuEntry("", "Launch " + name, async (s, e) => await LaunchGroupAsync(name)));
            m.Items.Add(MenuEntry("", "Close " + name, (s, e) =>
            {
                var active = _mgr.InGroup(name).Where(a => a.IsActive && a.State != AccountState.Closing).ToList();
                if (active.Count == 0) { ShowToast("Nothing in " + name + " is running.", false); return; }
                foreach (var a in active) _mgr.Close(a);
            }));
            m.Items.Add(new Separator());
            m.Items.Add(MenuEntry("", "Rename group…", async (s, e) =>
            {
                var to = await PromptAsync("Rename group", "Every account in “" + name + "” moves to the new name.", name, "Rename", "Group name");
                if (string.IsNullOrWhiteSpace(to)) return;
                _mgr.RenameGroup(name, to);
                if (string.Equals(_groupFilter, name, StringComparison.OrdinalIgnoreCase)) _groupFilter = to.Trim();
                _chipSignature = ""; UpdateGroupChips();
            }));
            m.Items.Add(MenuEntry("", "Ungroup everyone", async (s, e) =>
            {
                int n = _mgr.InGroup(name).Count();
                if (await ConfirmAsync("Remove the group “" + name + "”?", n + (n == 1 ? " account stays" : " accounts stay") + " in Lantern; they just aren't grouped anymore.", "Ungroup", true))
                    _mgr.DeleteGroup(name);
            }, true));
            return m;
        }

        async Task LaunchGroupAsync(string name)
        {
            var members = _mgr.InGroup(name).Where(a => !a.IsActive && a.State != AccountState.Expired).ToList();
            if (members.Count == 0) { ShowToast("Nothing in " + name + " is free to launch.", true); return; }
            if (!TryGetTarget(out var target)) { NavAccounts.IsChecked = true; return; }
            await EnsureNameAsync(target);
            await _mgr.LaunchAsync(members, target);
        }

        async Task NewGroupFromTickedAsync()
        {
            var ticked = _mgr.Accounts.Where(a => a.IsSelected).ToList();
            if (ticked.Count == 0) { ShowToast("Tick the accounts you want in the group first.", true); return; }
            var name = await PromptAsync("New group", "A name for the " + ticked.Count + (ticked.Count == 1 ? " ticked account" : " ticked accounts") + ", like “Farm” or “Alts”.", "", "Create", "Group name");
            if (string.IsNullOrWhiteSpace(name)) return;
            _mgr.SetGroup(ticked, name);
            ShowToast("Grouped " + ticked.Count + (ticked.Count == 1 ? " account" : " accounts") + " in “" + name.Trim() + "”.", false);
        }

        // profiles
        void UpdateProfilesEmpty()
        {
            ProfilesEmpty.Visibility = _profiles.Profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void RefreshProfiles()
        {
            foreach (var p in _profiles.Profiles) p.Refresh(_mgr.Accounts);
        }

        async void OnProfileClick(object sender, RoutedEventArgs e)
        {
            if (!(e.OriginalSource is Button b) || !(b.DataContext is ProfileItem p)) return;
            switch (b.Tag as string)
            {
                case "run": await RunProfileAsync(p, true); break;
                case "more": ShowProfileMenu(b, p); break;
            }
        }

        async Task RunProfileAsync(ProfileItem p, bool showAccounts)
        {
            if (showAccounts) NavAccounts.IsChecked = true;
            var problem = await _mgr.LaunchProfileAsync(p.Data);
            if (!string.IsNullOrEmpty(problem)) ShowToast(problem, true);
        }

        void ShowProfileMenu(Button anchor, ProfileItem p)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
            menu.Items.Add(MenuEntry("", "Rename…", async (s, e) =>
            {
                var n = await PromptAsync("Rename profile", "", p.Data.Name, "Save", "Profile name");
                if (string.IsNullOrWhiteSpace(n)) return;
                p.Data.Name = n.Trim(); p.Refresh(_mgr.Accounts); _profiles.Save();
            }));
            menu.Items.Add(MenuEntry("", "Update from the current selection", async (s, e) =>
            {
                var chosen = _mgr.Accounts.Where(a => a.IsSelected).ToList();
                if (chosen.Count == 0) { ShowToast("Tick the accounts first (Accounts page), and put the game in the launch bar.", true); return; }
                if (!TryGetTarget(out var target)) return;
                await EnsureNameAsync(target);
                p.Data.AccountIds = chosen.Select(a => a.Data.Id).ToList();
                p.Data.TargetRaw = TargetBox.Text.Trim(); p.Data.JobId = JobBox.Text.Trim(); p.Data.BackupRaw = BackupBox.Text.Trim(); p.Data.GameName = target.GameName;
                p.Refresh(_mgr.Accounts); _profiles.Save();
                ShowToast("Updated “" + p.Name + "”.", false);
            }));

            var perf = MenuEntry("", "Low-resource mode", null);
            foreach (var (label, value) in new[] { ("Follow the global setting", ""), ("Off for this profile", "Off"), ("Balanced", "Balanced"), ("Minimal", "Minimal"), ("Custom", "Custom") })
            {
                var v = value;
                perf.Items.Add(MenuEntry(p.Data.PerfMode == v ? "" : "", label, (s, e) => { p.Data.PerfMode = v; p.Refresh(_mgr.Accounts); _profiles.Save(); }));
            }
            menu.Items.Add(perf);
            menu.Items.Add(MenuEntry("", "Create desktop shortcut", (s, e) => CreateProfileShortcut(p)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("", "Delete", async (s, e) =>
            {
                if (await ConfirmAsync("Delete “" + p.Name + "”?", "Only the profile goes; your accounts stay.", "Delete", true)) _profiles.Remove(p);
            }, true));
            menu.IsOpen = true;
        }

        // launching a profile from outside (shortcut / Task Scheduler / a second Lantern started with --launch)
        bool _startupDone;
        string _pendingLaunch;

        /// <summary>Called when another Lantern was started with <c>--launch</c>: it left the profile name in a small file.</summary>
        internal void ConsumeLaunchRequest()
        {
            try
            {
                var file = System.IO.Path.Combine(Paths.Root, "launch-request.txt");
                if (!System.IO.File.Exists(file)) return;
                var name = System.IO.File.ReadAllText(file).Trim();
                System.IO.File.Delete(file);
                if (name.Length == 0) return;
                if (!_startupDone) { _pendingLaunch = name; return; }       // still loading accounts: OnLoaded picks it up
                _ = LaunchProfileByNameAsync(name);
            }
            catch (Exception ex) { Logger.Write("LaunchRequest: " + ex.Message); }
        }

        internal async Task LaunchProfileByNameAsync(string name)
        {
            var p = _profiles.Profiles.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (p == null) { ShowToast("There is no profile called “" + name + "”.", true); return; }
            await RunProfileAsync(p, true);
        }

        void CreateProfileShortcut(ProfileItem p)
        {
            try
            {
                string safe = new string(p.Name.Where(c => Array.IndexOf(System.IO.Path.GetInvalidFileNameChars(), c) < 0).ToArray()).Trim();
                if (safe.Length == 0) safe = "profile";
                string file = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Lantern - " + safe + ".lnk");
                string exe = System.Reflection.Assembly.GetEntryAssembly().Location;

                var shell = Type.GetTypeFromProgID("WScript.Shell");
                object sh = Activator.CreateInstance(shell);
                object lnk = shell.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, sh, new object[] { file });
                var lt = lnk.GetType();
                void Set(string prop, string value) => lt.InvokeMember(prop, System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { value });
                Set("TargetPath", exe);
                Set("Arguments", "\"--launch=" + p.Name.Replace("\"", "") + "\"");
                Set("WorkingDirectory", System.IO.Path.GetDirectoryName(exe));
                Set("IconLocation", exe + ",0");
                Set("Description", "Start the profile “" + p.Name + "” with Lantern");
                lt.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, lnk, null);
                ShowToast("Added “Lantern - " + safe + "” to your desktop. Opening it starts this profile.", false);
            }
            catch (Exception ex) { ShowToast("Couldn't create the shortcut: " + ex.Message, true); }
        }

        async Task SaveProfileFlowAsync()
        {
            var chosen = _mgr.Accounts.Where(a => a.IsSelected).ToList();
            if (chosen.Count == 0) { NavAccounts.IsChecked = true; ShowToast("Tick the accounts that belong in the profile first.", true); return; }
            if (!TryGetTarget(out var target)) { NavAccounts.IsChecked = true; return; }
            await EnsureNameAsync(target);

            string suggestion = target.GameName ?? "";
            var groups = chosen.Select(a => a.Group).Distinct().ToList();
            if (groups.Count == 1 && groups[0].Length > 0) suggestion = groups[0] + (suggestion.Length > 0 ? " · " + suggestion : "");

            var name = await PromptAsync("Save profile",
                chosen.Count + (chosen.Count == 1 ? " account" : " accounts") + " will launch into " + (string.IsNullOrEmpty(target.GameName) ? "this game" : "“" + target.GameName + "”") + ". Give the profile a name.",
                suggestion, "Save", "Profile name");
            if (string.IsNullOrWhiteSpace(name)) return;
            _profiles.Add(name, chosen, TargetBox.Text, JobBox.Text, target.GameName, backupRaw: BackupBox.Text);
            ShowToast("Saved profile “" + name.Trim() + "”.", false);
        }

        // join a player
        // "Join a player" fills the launch bar with the place and server id; the follow-this-user request they came from is kept
        // alongside, valid only while the bar still says what the lookup put there (a private server can't be joined by id alone).
        long _followUserId;
        string _followBar = "";

        string BarKey() => TargetBox.Text.Trim() + "|" + JobBox.Text.Trim();

        async Task FindPlayerFlowAsync()
        {
            var name = await PromptAsync("Join a player",
                "Type a Roblox username. One of your accounts has to be allowed to see where they are (usually: friends, with joins turned on).",
                "", "Find", "Username");
            if (string.IsNullOrWhiteSpace(name)) return;

            FindPlayerButton.IsEnabled = false;
            ShowToast("Looking for " + name.Trim() + "…", false);
            PlayerFind r;
            try { r = await _mgr.FindPlayerAsync(name); }
            finally { FindPlayerButton.IsEnabled = true; }

            if (r.Target == null) { ShowToast(r.Problem, true); return; }

            string who = r.User.DisplayName.Length > 0 ? r.User.DisplayName : r.User.Name;
            await EnsureNameAsync(r.Target);
            string game = string.IsNullOrEmpty(r.Target.GameName) ? "their game" : "“" + r.Target.GameName + "”";

            TargetBox.Text = r.Target.PlaceId.ToString();
            JobBox.Text = r.Target.JobId;
            BackupBox.Clear();
            ApplyTarget();
            _followUserId = r.Target.FollowUserId;
            _followBar = BarKey();
            NavAccounts.IsChecked = true;

            var chosen = _mgr.Accounts.Where(a => a.IsSelected && !a.IsActive && a.State != AccountState.Expired && a.Data.UserId != r.User.Id).ToList();
            if (chosen.Count == 0)
            {
                ShowToast(who + " is in " + game + ". The launch bar is ready — tick the accounts that should join and press Launch.", false);
                return;
            }
            if (!await ConfirmAsync("Join " + who + "?", chosen.Count + (chosen.Count == 1 ? " account" : " accounts") + " will join " + who + "'s server in " + game + ".", "Join", false)) return;
            foreach (var a in chosen) { a.IsSelected = false; a.Log("Joining " + who + "'s server"); }
            await _mgr.LaunchAsync(chosen, r.Target);
        }

        // links
        /// <summary>A pasted Roblox game / server link goes straight into the launch bar.</summary>
        void LoadLink(string clip)
        {
            TargetBox.Text = clip.Trim();
            JobBox.Clear(); BackupBox.Clear();
            ApplyTarget();
            NavAccounts.IsChecked = true;
            ShowToast(_parsed.Ok ? "Link loaded — tick the accounts that should join and press Launch." : _parsed.Summary, !_parsed.Ok);
        }

        // status click
        void OnStatusClick(object sender, MouseButtonEventArgs e)
        {
            for (var d = e.OriginalSource as DependencyObject; d != null && d != AccountsList; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            {
                if (d is FrameworkElement fe && (fe.Tag as string) == "status" && fe.DataContext is AccountItem a)
                {
                    if (a.State == AccountState.Error || (a.Exit != null && a.Exit.Unexpected)) { e.Handled = true; _ = ShowDetailsAsync(a); }
                    return;
                }
            }
        }

        // low-resource mode
        void SetPerf(string mode)
        {
            _s.PerfMode = mode;
            _mgr.SaveSettings();
            UpdatePerfUi();
        }

        void UpdatePerfUi()
        {
            var mode = string.IsNullOrEmpty(_s.PerfMode) ? "Off" : _s.PerfMode;
            if (_customPanel != null) _customPanel.Visibility = mode == "Custom" ? Visibility.Visible : Visibility.Collapsed;
            if (_flagRows != null) { _flagRows.IsEnabled = _s.UseEngineFlags; _flagRows.Opacity = _s.UseEngineFlags ? 1 : 0.5; }
            Rich.Set(PerfDescription, Perf.Lead(mode) + (mode != "Off" && !Perf.SettingsFileExists ? " (Roblox hasn't created its settings file yet — **start it once**.)" : ""));
            ShowPerfFacts(mode);
            Rich.Set(PerfStatus, Perf.Pending
                ? "Roblox's graphics settings are **lowered right now**. They go back to your originals when Lantern's windows close."
                : "Roblox's own settings are **untouched**.");
            PerfRestoreButton.IsEnabled = Perf.Pending;
        }

        string _perfFactsMode;

        /// <summary>What the chosen mode changes, one small chip per setting. They fade in one after the other when the mode changes.</summary>
        void ShowPerfFacts(string mode)
        {
            var facts = Perf.Facts(mode, _s);
            PerfFacts.Children.Clear();
            PerfFacts.Visibility = facts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            bool animate = _perfFactsMode != null && _perfFactsMode != mode && Motion.Enabled(_s);
            _perfFactsMode = mode;
            int i = 0;
            foreach (var f in facts)
            {
                var chip = new Box("RadiusSm") { BorderThickness = new Thickness(1), Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(0, 0, 6, 6), RenderTransform = new TranslateTransform() };
                chip.SetResourceReference(Border.BackgroundProperty, "BrushAccentSubtle");
                chip.SetResourceReference(Border.BorderBrushProperty, "BrushControlStroke");
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                var label = new TextBlock { Text = f.Key, FontSize = 12, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
                label.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary");
                var value = new TextBlock { Text = f.Value, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
                value.SetResourceReference(TextBlock.ForegroundProperty, "BrushAccentText");
                value.SetResourceReference(TextBlock.FontWeightProperty, "HiWeight");
                row.Children.Add(label); row.Children.Add(value);
                chip.Child = row;
                PerfFacts.Children.Add(chip);
                if (animate)
                {
                    var delay = TimeSpan.FromMilliseconds(35 * i);
                    chip.Opacity = 0;
                    chip.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { BeginTime = delay, EasingFunction = Motion.Out });
                    ((TranslateTransform)chip.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(220)) { BeginTime = delay, EasingFunction = Motion.Out });
                }
                i++;
            }
        }

        async Task RestorePerfAsync()
        {
            if (_mgr.Accounts.Any(a => a.IsActive) &&
                !await ConfirmAsync("Restore now?", "Lantern's Roblox windows are still open. They keep the lean settings they started with, and Roblox may write its own values again when they close. Restore anyway?", "Restore", false))
                return;
            Perf.Restore(out var note);
            ShowToast(note, !Perf.Pending ? false : true);
            UpdatePerfUi();
        }

        // tray
        void ApplyTray()
        {
            if (_s.TrayIcon && _tray == null)
            {
                try { _tray = new TrayManager(this); UpdateTray(); }
                catch (Exception ex)
                {
                    Logger.Write("Tray: " + ex.Message);
                    _s.TrayIcon = false;
                    TraySwitch.IsChecked = false;
                    ShowToast("Couldn't create the tray icon: " + ex.Message, true);
                }
            }
            else if (!_s.TrayIcon && _tray != null) { _tray.Dispose(); _tray = null; }

            CloseToTraySwitch.IsEnabled = _s.TrayIcon;
            if (!_s.TrayIcon && _s.CloseToTray) { _s.CloseToTray = false; CloseToTraySwitch.IsChecked = false; }
            if (!IsVisible && _tray == null) Show();     // never leave a hidden window with no icon to bring it back
        }

        void UpdateTray()
        {
            if (_tray == null) return;
            int n = _mgr.Accounts.Count(a => a.IsActive);
            _tray.SetTip(n > 0 ? "Lantern · " + n + (n == 1 ? " account running" : " accounts running") : "Lantern");
        }

        internal void ShowFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Native.Foreground(Handle);
        }

        internal void ToggleFromTray()
        {
            if (IsVisible && WindowState != WindowState.Minimized && IsActive) { Hide(); Native.TrimOwnWorkingSet(); }
            else ShowFromTray();
        }

        internal List<TrayEntry> BuildTrayEntries()
        {
            var l = new List<TrayEntry> { new TrayEntry { Text = "Open Lantern", Glyph = "", Click = ShowFromTray } };

            var profiles = _profiles.Profiles.Take(8).ToList();
            if (profiles.Count > 0)
            {
                l.Add(TrayEntry.Sep());
                l.Add(TrayEntry.Head("Launch a profile"));
                foreach (var p in profiles)
                {
                    var item = p;
                    l.Add(new TrayEntry { Text = item.Name, Glyph = "", Click = () => _ = RunProfileAsync(item, false) });
                }
            }

            l.Add(TrayEntry.Sep());
            int active = _mgr.Accounts.Count(a => a.IsActive);
            l.Add(new TrayEntry
            {
                Text = active > 0 ? "Close all windows (" + active + ")" : "No windows running",
                Glyph = "", Enabled = active > 0, Click = () => _mgr.CloseAll()
            });
            l.Add(new TrayEntry { Text = "Tile windows", Glyph = "", Enabled = _mgr.RunningCount > 0, Click = () => _mgr.TileWindows(Handle) });
            l.Add(TrayEntry.Sep());
            l.Add(new TrayEntry { Text = "Quit Lantern", Glyph = "", Danger = true, Click = () => _ = QuitFromTrayAsync() });
            return l;
        }

        async Task QuitFromTrayAsync()
        {
            int running = _mgr.Accounts.Count(a => a.IsActive);
            if (running > 0)
            {
                ShowFromTray();
                if (!await ConfirmAsync("Quit Lantern and its Roblox windows?", ClosingWarning(running), "Quit everything", true)) return;
            }
            _forceClose = true;
            SavePlacement();
            _mgr.Shutdown();
            _tray?.Dispose();
            Close();
        }

        // row menu
        MenuItem MenuEntry(string glyph, string header, RoutedEventHandler click, bool danger = false)
        {
            var mi = new MenuItem
            {
                Header = header,
                Icon = new TextBlock { Text = glyph, Style = (Style)FindResource("Icon"), HorizontalAlignment = HorizontalAlignment.Left }.IconSize(14)
            };
            if (danger) mi.Style = (Style)FindResource("DangerMenuItem");
            if (click != null) mi.Click += click;
            return mi;
        }

        void ShowRowMenu(Button anchor, AccountItem item)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };

            // Join an alt's server: from the running account ("bring the ticked accounts here"),
            // or from a free account ("join the server of …").
            if (item.State == AccountState.Running)
            {
                menu.Items.Add(MenuEntry("", "Bring selected accounts here", async (s, e) =>
                {
                    var alts = _mgr.Accounts.Where(a => a.IsSelected && a != item && !a.IsActive && a.State != AccountState.Expired).ToList();
                    if (alts.Count == 0) { ShowToast("Tick the accounts you want to bring along first.", true); return; }
                    foreach (var a in alts) a.IsSelected = false;
                    await _mgr.JoinServerOfAsync(item, alts);
                }));
                menu.Items.Add(MenuEntry("", "Use this server in the launch bar", async (s, e) => await UseServerOfAsync(item)));
                menu.Items.Add(MenuEntry("", "Copy server link", async (s, e) => await CopyServerLinkAsync(item)));
                menu.Items.Add(new Separator());
            }
            else if (!item.IsActive && item.State != AccountState.Expired)
            {
                var hosts = _mgr.Accounts.Where(a => a != item && a.State == AccountState.Running).ToList();
                if (hosts.Count > 0)
                {
                    var join = MenuEntry("", "Join the server of", null);
                    foreach (var host in hosts)
                    {
                        var h = host;
                        join.Items.Add(MenuEntry("", h.Display, async (s, e) => await _mgr.JoinServerOfAsync(h, new[] { item })));
                    }
                    menu.Items.Add(join);
                    menu.Items.Add(new Separator());
                }
            }

            // group
            var grp = MenuEntry("", "Group", null);
            var names = _mgr.GroupNames();
            foreach (var g in names)
            {
                var name = g;
                grp.Items.Add(MenuEntry(string.Equals(item.Group, name, StringComparison.OrdinalIgnoreCase) ? "" : "", name, (s, e) => _mgr.SetGroup(new[] { item }, name)));
            }
            if (names.Count > 0) grp.Items.Add(new Separator());
            grp.Items.Add(MenuEntry("", "New group…", async (s, e) =>
            {
                var n = await PromptAsync("New group", "Put " + item.Display + " in a new group.", "", "Create", "Group name");
                if (!string.IsNullOrWhiteSpace(n)) _mgr.SetGroup(new[] { item }, n);
            }));
            if (item.HasGroup) grp.Items.Add(MenuEntry("", "Remove from group", (s, e) => _mgr.SetGroup(new[] { item }, "")));
            menu.Items.Add(grp);

            menu.Items.Add(MoveMenu(item));
            if (item.State == AccountState.Running)
                menu.Items.Add(MenuEntry("", "Trim memory now", async (s, e) => await TrimNowAsync(item)));

            if (item.HasDetails)
                menu.Items.Add(MenuEntry("", item.State == AccountState.Error ? "Why did it fail?" : "Details and history…", async (s, e) => await ShowDetailsAsync(item)));
            menu.Items.Add(new Separator());

            menu.Items.Add(MenuEntry("", "Rename…", async (s, e) => await RenameAsync(item)));
            menu.Items.Add(MenuEntry("", "Check session", async (s, e) =>
            {
                await _mgr.RefreshAsync(item);
                ShowToast(item.State == AccountState.Expired ? item.Display + "'s sign-in has expired." : item.Display + " is signed in.", item.State == AccountState.Expired);
            }));
            menu.Items.Add(MenuEntry("", "Sign in again…", async (s, e) => await SignInFlowAsync()));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("", "Copy user ID", (s, e) =>
            {
                try { ClipboardSet(item.Data.UserId.ToString()); ShowToast("Copied " + item.Data.UserId + ".", false); } catch { ShowToast("Couldn't reach the clipboard.", true); }
            }));
            menu.Items.Add(MenuEntry("", "Open profile", (s, e) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.roblox.com/users/" + item.Data.UserId + "/profile") { UseShellExecute = true }); } catch { }
            }));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("", "Remove…", async (s, e) => await RemoveAsync(item), true));
            menu.IsOpen = true;
        }

        async Task TrimNowAsync(AccountItem item)
        {
            var (ok, before, after) = await _mgr.TrimAsync(item);
            if (!ok) { ShowToast("Windows wouldn't let Lantern trim " + item.Display + "'s window.", true); return; }
            ShowToast(item.Display + ": " + Fmt.Memory(before) + " → " + Fmt.Memory(after) + ". The game takes back what it needs, so it creeps up again.", false);
        }

        async Task CopyServerLinkAsync(AccountItem src)
        {
            var t = await _mgr.ResolveServerAsync(src);
            var link = ServerLink.Build(t);
            if (string.IsNullOrEmpty(link)) { ShowToast("Couldn't tell which server " + src.Display + " is in yet.", true); return; }
            try { ClipboardSet(link); ShowToast("Server link copied. Paste it in Lantern's launch bar (Ctrl+V) to join.", false); }
            catch { ShowToast("Couldn't reach the clipboard.", true); }
        }
    }
}
