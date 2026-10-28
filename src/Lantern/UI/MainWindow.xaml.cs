using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Lantern.Core;

namespace Lantern.UI
{
    public partial class MainWindow : Window
    {
        readonly SettingsData _s;
        readonly InstanceManager _mgr;
        readonly GameLibrary _games = new GameLibrary();
        readonly ICollectionView _view;
        readonly DispatcherTimer _targetDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        ParsedTarget _parsed = new ParsedTarget { IsEmpty = true };
        bool _ready;
        bool _forceClose;

        public MainWindow(SettingsData settings)
        {
            InitializeComponent();
            _s = settings;
            RestorePlacement();

            _mgr = new InstanceManager(_s);
            _mgr.Message += (text, error) => ShowToast(text, error);
            _mgr.BatchChanged += UpdateLaunchButton;

            _view = CollectionViewSource.GetDefaultView(_mgr.Accounts);
            _view.Filter = o =>
            {
                var a = (AccountItem)o;
                if (_groupFilter != null && !(_groupFilter.Length == 0 ? !a.HasGroup : string.Equals(a.Group, _groupFilter, StringComparison.OrdinalIgnoreCase))) return false;
                var q = SearchBox.Text.Trim();
                if (q.Length == 0) return true;
                return a.Display.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                    || a.Data.Username.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
            };
            AccountsList.ItemsSource = _view;
            _mgr.Accounts.CollectionChanged += OnAccountsChanged;

            LibraryList.ItemsSource = _games.Games;
            GamesList.ItemsSource = _games.Games;
            _games.Games.CollectionChanged += (s, e) => UpdateGamesEmpty();

            WireShell();
            WireSettings();
            WireFeatures();
            WireReorder();
            WireStats();
            WirePulse();
            WireLive();

            // Keep the footprint small: when Lantern is in the background, return idle memory to Windows.
            var trim = new DispatcherTimer { Interval = TimeSpan.FromSeconds(40) };
            trim.Tick += (s, e) => { if (!IsActive || WindowState == WindowState.Minimized) Native.TrimOwnWorkingSet(); };
            trim.Start();
            Deactivated += (s, e) => Dispatcher.BeginInvoke(new Action(Native.TrimOwnWorkingSet), DispatcherPriority.ApplicationIdle);

            SourceInitialized += OnSourceInitialized;
            Loaded += OnLoaded;
            Closing += OnClosing;
            SizeChanged += (s, e) => ApplyResponsiveLayout();
            Theme.Changed += () => { if (IsLoaded || PresentationSource.FromVisual(this) != null) ApplyChrome(); };
        }

        IntPtr Handle => new WindowInteropHelper(this).Handle;

        // window chrome
        void OnSourceInitialized(object sender, EventArgs e) => ApplyChrome();

        /// <summary>
        /// Mica behind a transparent window on Windows 11 22H2 and later, a plain surface colour on anything older.
        /// </summary>
        void ApplyChrome()
        {
            var h = Handle;
            if (h == IntPtr.Zero) return;
            var src = HwndSource.FromHwnd(h);
            bool mica = Environment.OSVersion.Version.Build >= 22621;
            if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = mica ? Colors.Transparent : Theme.WindowBase;
            if (mica) Background = Brushes.Transparent;
            else SetResourceReference(BackgroundProperty, "BrushSurface");
            Native.ApplyBackdrop(h, Theme.IsDark, mica);
        }

        const double MinW = 520, MinH = 460;

        void RestorePlacement()
        {
            var (bounds, saved) = PlannedBounds(_s);
            Width = bounds.Width;
            Height = bounds.Height;
            if (saved)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = bounds.Left;
                Top = bounds.Top;
            }
        }

        /// <summary>
        /// Where the window will open: its saved place if that is still on a screen, otherwise centred on the main screen. The
        /// start-up picture opens there too, before the window itself exists.
        /// </summary>
        internal static (Rect bounds, bool saved) PlannedBounds(SettingsData s)
        {
            double w = Math.Max(MinW, s.WindowWidth), h = Math.Max(MinH, s.WindowHeight);
            if (!double.IsNaN(s.WindowLeft) && !double.IsNaN(s.WindowTop))
            {
                bool visible = s.WindowLeft < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80
                            && s.WindowTop < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80
                            && s.WindowLeft + w > SystemParameters.VirtualScreenLeft + 80
                            && s.WindowTop >= SystemParameters.VirtualScreenTop - 10;
                if (visible) return (new Rect(s.WindowLeft, s.WindowTop, w, h), true);
            }
            var area = SystemParameters.WorkArea;
            return (new Rect(area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 2, w, h), false);
        }

        void ApplyResponsiveLayout()
        {
            double w = ContentPane.ActualWidth;
            if (w <= 0) return;
            LayoutState.Current.Update(w);
            double g = w < 700 ? 16 : 28;

            HeaderGrid.Margin = new Thickness(g, 24, g, 16);
            LaunchBar.Padding = new Thickness(g, 14, g, 14);
            PageGames.Children[0].SetValue(MarginProperty, new Thickness(g, 24, g, 16));
            PageGames.Children[1].SetValue(MarginProperty, new Thickness(g, 0, g, 16));
            PageProfiles.Children[0].SetValue(MarginProperty, new Thickness(g, 24, g, 16));
            PageProfiles.Children[1].SetValue(MarginProperty, new Thickness(g, 0, g, 16));
            GroupBar.Margin = new Thickness(g, 0, g, 6);
            AdviceBar.Margin = new Thickness(g, 2, g, 10);
            ApplySettingsLayout(w, g);
            ApplyErrorsLayout(w, g);
            ApplyCreditsLayout(w, g);
            PulseRoot.Margin = new Thickness(g, 24, g, 40);
            LiveHeader.Margin = new Thickness(g, 24, w < 760 ? g : 20, 8);
            LiveArea.Margin = new Thickness(g, 0, w < 760 ? g : 20, 20);

            // Header: controls drop under the title when the page is narrow.
            bool stackHeader = w < 720;
            Grid.SetRow(HeaderControls, stackHeader ? 1 : 0);
            Grid.SetColumn(HeaderControls, stackHeader ? 0 : 1);
            Grid.SetColumnSpan(HeaderControls, stackHeader ? 2 : 1);
            HeaderControls.Margin = new Thickness(0, stackHeader ? 14 : 0, 0, 0);
            double fixedWidth = 32 + 2 + 32 + 12 + (AddButton.ActualWidth > 0 ? AddButton.ActualWidth : 140) + 8;
            SearchHost.Width = stackHeader ? Math.Max(120, Math.Min(320, w - 2 * g - fixedWidth)) : 220;

            // Launch bar: job-id box wraps below the place box on narrow pages.
            bool stackLaunch = w < 740;
            Grid.SetRow(JobBox, stackLaunch ? 1 : 0);
            Grid.SetColumn(JobBox, stackLaunch ? 0 : 2);
            Grid.SetColumnSpan(JobBox, stackLaunch ? 3 : 1);
            JobBox.Width = stackLaunch ? double.NaN : 200;
            JobBox.Margin = stackLaunch ? new Thickness(0, 8, 0, 0) : new Thickness(8, 0, 0, 0);
        }

        // startup / shutdown
        async void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplyResponsiveLayout();
            if (!App.Demo) { _games.Load(); _profiles.Load(); }
            UpdateGamesEmpty();

            TargetBox.Text = _s.LastTarget ?? "";
            JobBox.Text = _s.LastJobId ?? "";
            BackupBox.Text = _s.LastBackup ?? "";
            ApplyTarget();
            _ready = true;
            if (App.Demo) LoadDemoData();

            if (App.Demo)
            {
                UpdateAccountsUi();
                RevealFromSplash();
                return;
            }

            if (_s.MultiInstance)
            {
                if (!MultiInstance.Enable(out var err)) ShowToast(err, true);
                UpdateMultiStatus();
            }

            UpdateAccountsUi();
            ApplyTray();
            _splash?.SetStatus(_mgr.Accounts.Count == 0 ? "Opening your accounts" : "Checking accounts 0/" + _mgr.Accounts.Count);
            _splash?.SetProgress(_mgr.Accounts.Count == 0 ? -1 : 0);
            await _mgr.LoadAsync((done, total) => { _splash?.SetStatus("Checking accounts " + done + "/" + total); _splash?.SetProgress(done / (double)Math.Max(1, total)); });
            UpdateAccountsUi();
            RevealFromSplash();
            RefreshProfiles();
            Native.TrimOwnWorkingSet();
            if (Store.LoadWarning != null) ShowToast(Store.LoadWarning, true);
            if (RobloxProcesses.FindLauncher() == null)
                ShowToast("Roblox isn't installed yet. Install it from roblox.com — Lantern launches the official client.", true);

            _startupDone = true;
            var pending = _pendingLaunch ?? App.LaunchProfile;
            _pendingLaunch = null;
            if (!string.IsNullOrEmpty(pending)) await LaunchProfileByNameAsync(pending);
        }

        // start-up picture
        Splash _splash;
        bool _cloaked;

        /// <summary>
        /// Called before Show: the window starts cloaked (open, laid out and loading, but not on screen) behind the start-up picture,
        /// and is revealed when loading is done. If Windows won't cloak it, the picture simply finishes straight away.
        /// </summary>
        internal void StartBehind(Splash splash)
        {
            _splash = splash;
            SourceInitialized += (s, e) =>
            {
                _cloaked = Native.Cloak(Handle, true);
                if (!_cloaked) RevealFromSplash();
                else FitSplash();
            };
            Loaded += (s, e) => FitSplash();   // (in case the final placement only settled now)
            // never stay hidden: whatever happens to loading, the window comes out after a while
            var safety = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
            safety.Tick += (s, e) => { safety.Stop(); RevealFromSplash(); };
            safety.Start();
            var last = new DispatcherTimer { Interval = TimeSpan.FromSeconds(16) };
            last.Tick += (s, e) => { last.Stop(); Uncloak(); };
            last.Start();
        }

        /// <summary>Lays the start-up picture exactly over this window's visible frame.</summary>
        void FitSplash()
        {
            if (_splash == null || !Native.FrameBounds(Handle, out var r)) return;
            _splash.Fit(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }

        void RevealFromSplash()
        {
            var s = _splash;
            if (s == null) return;
            _splash = null;
            s.Finish(Uncloak);
        }

        void Uncloak()
        {
            if (!_cloaked) return;
            _cloaked = false;
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.Cloak(hwnd, false);
            Activate();
            Native.Foreground(hwnd);
        }

        void LoadDemoData()
        {
            _mgr.LoadDemo();
            UpdateUsageUi();
            foreach (var (name, place, priv) in new[] { ("Grow a Garden", 126884695634066L, false), ("Blox Fruits", 2753915549L, false), ("Brookhaven RP", 4924922222L, true), ("Adopt Me!", 920587237L, false) })
                _games.Games.Add(new GameItem(new GameData { Name = name, PlaceId = place, Private = priv }));
            _profiles.Demo = true;
            var farm = _mgr.InGroup("Farm").ToList(); var alts = _mgr.InGroup("Alts").ToList();
            _profiles.Add("Farm crew", farm, "https://www.roblox.com/games/126884695634066/Grow-a-Garden", "", "Grow a Garden");
            _profiles.Add("Alts \u00b7 Brookhaven", alts, "4924922222", "", "Brookhaven RP", "Minimal");
            RefreshProfiles();
            TargetBox.Text = "https://www.roblox.com/games/126884695634066/Grow-a-Garden";
            ApplyTarget();
            TargetInfo.Text = "Grow a Garden · Place 126884695634066 · public server";
        }

        async void OnClosing(object sender, CancelEventArgs e)
        {
            if (_wiped) return;
            if (_forceClose) { SavePlacement(); _tray?.Dispose(); return; }
            if (_s.CloseToTray && _tray != null) { e.Cancel = true; Hide(); Native.TrimOwnWorkingSet(); return; }   // keeps running in the tray

            int running = _mgr.Accounts.Count(a => a.IsActive);
            if (running == 0) { SavePlacement(); _mgr.Shutdown(); _tray?.Dispose(); return; }

            e.Cancel = true;
            var close = await ConfirmAsync("Close Lantern and its Roblox windows?", ClosingWarning(running), "Close everything", true);
            if (close) { _forceClose = true; SavePlacement(); _mgr.Shutdown(); Close(); }
        }

        /// <summary>
        /// What closing Lantern does to the windows. Every Roblox window keeps checking Roblox's multi-window lock; the moment Lantern
        /// lets go of it (closing, or a crash, since Windows releases it either way) the windows take it in turn and shut themselves down.
        /// </summary>
        internal static string ClosingWarning(int running) =>
            running + (running == 1 ? " account is" : " accounts are") + " running. Roblox closes all of its windows by itself once Lantern closes: " +
            "they only stay open while Lantern holds Roblox's multi-window lock.\n\nTo keep farming, minimize Lantern instead, or turn on Settings → Tray so the close button keeps it running in the background.";

        void SavePlacement()
        {
            if (WindowState == WindowState.Normal)
            {
                _s.WindowWidth = Width; _s.WindowHeight = Height; _s.WindowLeft = Left; _s.WindowTop = Top;
            }
            _mgr.SaveSettings();
        }

        // navigation + wiring
        void WireShell()
        {
            NavAccounts.Checked += (s, e) => ShowPage(PageAccounts);
            NavGames.Checked += (s, e) => ShowPage(PageGames);
            NavSettings.Checked += (s, e) => ShowPage(PageSettings);
            NavPulse.Checked += (s, e) => { ShowPage(PagePulse); OnPulseShown(); };
            NavLive.Checked += (s, e) => { ShowPage(PageLive); OnLiveShown(); };
            NavErrors.Checked += (s, e) => { ShowPage(PageErrors); RenderErrors(); };
            NavCredits.Checked += (s, e) => { ShowPage(PageCredits); CreditsScroll.ScrollToTop(); };

            SearchBox.TextChanged += (s, e) => { _view.Refresh(); UpdateAccountsUi(); };
            SearchBox.KeyDown += (s, e) => { if (e.Key == Key.Escape) { SearchBox.Clear(); Keyboard.ClearFocus(); } };
            PreviewKeyDown += (s, e) =>
            {
                if (DialogLayer.Visibility == Visibility.Visible)
                {
                    if (e.Key == Key.Escape) { _dialogCancel?.Invoke(); e.Handled = true; }
                    else if (e.Key == Key.Enter && !(Keyboard.FocusedElement is Button)) { DlgOk.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); e.Handled = true; }
                    return;
                }
                if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && PageAccounts.Visibility == Visibility.Visible
                    && !(Keyboard.FocusedElement is TextBox) && !(Keyboard.FocusedElement is PasswordBox))
                {
                    var clip = ClipboardText();
                    if (clip.Contains("_|WARNING")) { e.Handled = true; _ = TryAddFromTextAsync(clip); }
                    else if (ServerLink.LooksLikeLink(clip)) { e.Handled = true; LoadLink(clip); }
                    return;
                }
                if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    NavAccounts.IsChecked = true; SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true;
                }
            };

            AddButton.Click += async (s, e) => await AddAccountFlowAsync();
            EmptyAddButton.Click += async (s, e) => await AddAccountFlowAsync();
            EmptyPasteButton.Click += async (s, e) => await AddAccountFlowAsync(true);

            // Paste cookies anywhere: Ctrl+V on the Accounts page, or drop text / a .txt file on the window.
            DragOver += (s, e) =>
            {
                bool ok = DialogLayer.Visibility != Visibility.Visible && (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText));
                e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            Drop += async (s, e) =>
            {
                string text = null;
                try
                {
                    if (e.Data.GetDataPresent(DataFormats.FileDrop))
                    {
                        var sb = new System.Text.StringBuilder();
                        foreach (var f in (string[])e.Data.GetData(DataFormats.FileDrop))
                        {
                            var info = new System.IO.FileInfo(f);
                            if (info.Exists && info.Length < 2_000_000 && info.Extension.ToLowerInvariant() != ".exe") sb.AppendLine(System.IO.File.ReadAllText(f));
                        }
                        text = sb.ToString();
                    }
                    else if (e.Data.GetDataPresent(DataFormats.UnicodeText)) text = (string)e.Data.GetData(DataFormats.UnicodeText);
                }
                catch { }
                if (!await TryAddFromTextAsync(text)) ShowToast("No Roblox cookies found in what you dropped.", true);
            };
            TileButton.Click += (s, e) =>
            {
                int n = _mgr.TileWindows(Handle);
                ShowToast(n > 0 ? "Arranged " + n + (n == 1 ? " window." : " windows.") : "No running Roblox windows to arrange.", n == 0);
            };
            CloseAllButton.Click += async (s, e) =>
            {
                int n = _mgr.Accounts.Count(a => a.IsActive);
                if (n == 0) { ShowToast("Nothing is running.", false); return; }
                if (await ConfirmAsync("Close everything?", "This closes " + n + (n == 1 ? " Roblox window." : " Roblox windows."), "Close all", true))
                    _mgr.CloseAll();
            };

            SelectAll.Click += (s, e) =>
            {
                var items = _view.Cast<AccountItem>().ToList();
                bool target = !(items.Count > 0 && items.All(a => a.IsSelected));
                foreach (var a in items) a.IsSelected = target;
                UpdateSelectionUi();
            };

            AccountsList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnRowClick));

            TargetBox.TextChanged += (s, e) => { _targetDebounce.Stop(); _targetDebounce.Start(); };
            JobBox.TextChanged += (s, e) => { _targetDebounce.Stop(); _targetDebounce.Start(); };
            BackupBox.TextChanged += (s, e) => { _targetDebounce.Stop(); _targetDebounce.Start(); };
            _targetDebounce.Tick += (s, e) => { _targetDebounce.Stop(); ApplyTarget(); };
            TargetBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) LaunchButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); };
            LaunchButton.Click += async (s, e) => await OnLaunchClicked();

            LibraryButton.Click += (s, e) => { LibraryEmpty.Visibility = _games.Games.Count == 0 ? Visibility.Visible : Visibility.Collapsed; LibraryPopup.IsOpen = true; };
            LibraryList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                if (e.OriginalSource is Button b && b.DataContext is GameItem g) { UseGame(g); LibraryPopup.IsOpen = false; }
            }));
        }

        void ShowPage(UIElement page)
        {
            PageAccounts.Visibility = page == PageAccounts ? Visibility.Visible : Visibility.Collapsed;
            PageGames.Visibility = page == PageGames ? Visibility.Visible : Visibility.Collapsed;
            PageProfiles.Visibility = page == PageProfiles ? Visibility.Visible : Visibility.Collapsed;
            PagePulse.Visibility = page == PagePulse ? Visibility.Visible : Visibility.Collapsed;
            PageLive.Visibility = page == PageLive ? Visibility.Visible : Visibility.Collapsed;
            PageErrors.Visibility = page == PageErrors ? Visibility.Visible : Visibility.Collapsed;
            PageCredits.Visibility = page == PageCredits ? Visibility.Visible : Visibility.Collapsed;
            PageSettings.Visibility = page == PageSettings ? Visibility.Visible : Visibility.Collapsed;
            ApplyStreamMode();
        }

        // accounts list state
        void OnAccountsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            // (a drag-and-drop reorder is a Move: the same items again, already wired)
            if (e.NewItems != null && (e.Action == NotifyCollectionChangedAction.Add || e.Action == NotifyCollectionChangedAction.Replace))
                foreach (AccountItem a in e.NewItems)
                    a.PropertyChanged += (s, ev) =>
                    {
                        if (ev.PropertyName == nameof(AccountItem.IsSelected)) UpdateSelectionUi();
                        else if (ev.PropertyName == nameof(AccountItem.State)) UpdateAccountsUi();
                        else if (ev.PropertyName == nameof(AccountItem.Group)) { _view.Refresh(); UpdateGroupChips(); }
                    };
            UpdateAccountsUi();
            RefreshProfiles();
        }

        void UpdateAccountsUi()
        {
            int total = _mgr.Accounts.Count;
            int running = _mgr.Accounts.Count(a => a.State == AccountState.Running);
            AccountsSummary.Text = total == 0 ? "Nothing added yet"
                : total + (total == 1 ? " account" : " accounts") + (running > 0 ? " · " + running + " running" : "") + (running > 0 && _mgr.UsageSummary.Length > 0 ? "  ·  " + _mgr.UsageSummary : "");

            bool filtered = SearchBox.Text.Trim().Length > 0;
            int shown = _view.Cast<object>().Count();
            AccountsEmpty.Visibility = total == 0 && _ready ? Visibility.Visible : Visibility.Collapsed;
            NoMatches.Visibility = filtered && total > 0 && shown == 0 ? Visibility.Visible : Visibility.Collapsed;

            bool any = total > 0;
            ListHeader.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            TileButton.IsEnabled = running > 0;
            CloseAllButton.IsEnabled = _mgr.Accounts.Any(a => a.IsActive);
            SelectAll.IsEnabled = any;
            UpdateSelectionUi();
            UpdateGroupChips();
            UpdateTray();
        }

        void UpdateSelectionUi()
        {
            var items = _view.Cast<AccountItem>().ToList();
            int selected = items.Count(a => a.IsSelected);
            SelectAll.IsChecked = selected == 0 ? false : (selected == items.Count ? true : (bool?)null);
            UpdateLaunchButton();
        }

        void UpdateLaunchButton()
        {
            if (_mgr.BatchRunning)
            {
                LaunchGlyph.Text = "\uE711";
                LaunchLabel.Text = "Cancel";
                LaunchButton.IsEnabled = true;
                return;
            }
            int n = _mgr.Accounts.Count(a => a.IsSelected && !a.IsActive && a.State != AccountState.Expired);
            LaunchGlyph.Text = "\uE768";
            LaunchLabel.Text = n > 1 ? "Launch " + n : "Launch";
            LaunchButton.IsEnabled = n > 0;
            LaunchButton.ToolTip = n == 0 ? "Tick the accounts you want to launch" : null;
        }

        // launch target
        void ApplyTarget()
        {
            // The backup box only appears for a private server link (or while it still holds something).
            var main = TargetParser.Parse(TargetBox.Text, JobBox.Text);
            BackupBox.Visibility = main.Ok && main.Target.IsPrivate || BackupBox.Text.Trim().Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _parsed = TargetParser.Parse(TargetBox.Text, JobBox.Text, BackupBox.Text);
            TargetInfo.SetResourceReference(TextBlock.ForegroundProperty,
                string.IsNullOrEmpty(_parsed.Error) ? "BrushTextSecondary" : "BrushDanger");
            TargetInfo.Text = _parsed.Summary;

            if (_parsed.Ok && _parsed.Target.PlaceId > 0)
            {
                var snapshot = _parsed;
                _ = ResolveNameAsync(snapshot);
            }
        }

        async Task ResolveNameAsync(ParsedTarget snapshot)
        {
            var info = await _games.InfoAsync(snapshot.Target.PlaceId);
            if (info == null || !ReferenceEquals(snapshot, _parsed)) return;
            snapshot.Target.GameName = info.Name;
            TargetInfo.Text = info.Name + " · " + snapshot.Summary;
        }

        void UseGame(GameItem g)
        {
            TargetBox.Text = string.IsNullOrWhiteSpace(g.Data.Raw) ? g.Data.PlaceId.ToString() : g.Data.Raw;
            JobBox.Clear(); BackupBox.Clear();
            ApplyTarget();
            NavAccounts.IsChecked = true;
            ShowToast("Ready to join " + g.Name + ".", false);
        }

        bool TryGetTarget(out LaunchTarget target)
        {
            target = null;
            ApplyTarget();
            if (!_parsed.Ok)
            {
                ShowToast(_parsed.IsEmpty ? "Enter a place ID or paste a game link first." : _parsed.Summary, true);
                TargetBox.Focus();
                return false;
            }
            target = _parsed.Target.Clone();
            if (_followUserId > 0 && BarKey() == _followBar) target.FollowUserId = _followUserId;
            _s.LastTarget = TargetBox.Text.Trim();
            _s.LastJobId = JobBox.Text.Trim();
            _s.LastBackup = BackupBox.Text.Trim();
            _mgr.SaveSettings();
            return true;
        }

        async Task EnsureNameAsync(LaunchTarget t)
        {
            if (!string.IsNullOrEmpty(t.GameName) || t.PlaceId <= 0) return;
            var lookup = _games.InfoAsync(t.PlaceId);
            if (await Task.WhenAny(lookup, Task.Delay(2500)) == lookup && lookup.Result != null) t.GameName = lookup.Result.Name;
        }

        async Task OnLaunchClicked()
        {
            if (_mgr.BatchRunning) { _mgr.CancelBatch(); return; }
            var chosen = _mgr.Accounts.Where(a => a.IsSelected && !a.IsActive && a.State != AccountState.Expired).ToList();
            if (chosen.Count == 0) return;
            if (!TryGetTarget(out var target)) return;
            await EnsureNameAsync(target);
            foreach (var a in chosen) a.IsSelected = false;
            await _mgr.LaunchAsync(chosen, target);
        }

        // row actions
        async void OnRowClick(object sender, RoutedEventArgs e)
        {
            if (!(e.OriginalSource is Button b) || !(b.DataContext is AccountItem item)) return;
            switch (b.Tag as string)
            {
                case "action": await OnRowAction(item); break;
                case "more": ShowRowMenu(b, item); break;
            }
        }

        async Task OnRowAction(AccountItem item)
        {
            switch (item.State)
            {
                case AccountState.Running:
                case AccountState.Rejoining:
                case AccountState.Queued:
                case AccountState.Launching:
                    _mgr.Close(item);
                    break;
                case AccountState.Expired:
                    await SignInFlowAsync();
                    break;
                default:
                    if (!TryGetTarget(out var target)) return;
                    await EnsureNameAsync(target);
                    await _mgr.LaunchAsync(new[] { item }, target);
                    break;
            }
        }

        /// <summary>Copies a running account's server into the launch bar so any accounts can be ticked and launched into it.</summary>
        async Task UseServerOfAsync(AccountItem src)
        {
            var t = await _mgr.ResolveServerAsync(src);
            if (t == null) { ShowToast("Couldn't tell which server " + src.Display + " is in yet.", true); return; }

            BackupBox.Clear();
            if (!string.IsNullOrEmpty(t.LinkCode)) { TargetBox.Text = "https://www.roblox.com/games/" + t.PlaceId + "?privateServerLinkCode=" + t.LinkCode; JobBox.Clear(); }
            else if (!string.IsNullOrEmpty(t.ShareCode)) { TargetBox.Text = "https://www.roblox.com/share?code=" + t.ShareCode + "&type=Server"; JobBox.Clear(); }
            else { TargetBox.Text = t.PlaceId.ToString(); JobBox.Text = t.JobId; }
            ApplyTarget();
            ShowToast("Ready — tick the accounts that should join " + src.Display + " and press Launch.", false);
        }

        async Task RenameAsync(AccountItem item)
        {
            var name = await PromptAsync("Rename account", "A nickname only you see — handy for telling alts apart. Leave empty to use the Roblox name.",
                item.Data.Alias, "Save", "Nickname");
            if (name == null) return;
            item.Data.Alias = name.Trim();
            item.RefreshIdentity();
            _mgr.SaveAccounts();
        }

        async Task RemoveAsync(AccountItem item)
        {
            if (!await ConfirmAsync("Remove " + item.Display + "?",
                "Its saved session is deleted from this PC. You can add the account again later by signing in.", "Remove", true)) return;
            _mgr.Remove(item);
        }
    }
}
