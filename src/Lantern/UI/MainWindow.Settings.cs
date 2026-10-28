using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Lantern.Core;

namespace Lantern.UI
{
    public partial class MainWindow
    {
        bool _wiped;

        void WireSettings()
        {
            // Initial values first, handlers after - so setting them doesn't fire the handlers.
            MultiSwitch.IsChecked = _s.MultiInstance;
            RejoinSwitch.IsChecked = _s.AutoRejoin;
            IdleRejoinSwitch.IsChecked = _s.RejoinAfterIdleKick;
            SplashSwitch.IsChecked = _s.ShowSplash;
            SplashSwitch.Checked += (s, e) => { _s.ShowSplash = true; _mgr.SaveSettings(); };
            SplashSwitch.Unchecked += (s, e) => { _s.ShowSplash = false; _mgr.SaveSettings(); };
            PrioNormal.IsChecked = _s.Priority == "Normal";
            PrioBelow.IsChecked = _s.Priority == "BelowNormal";
            PrioIdle.IsChecked = _s.Priority == "Idle";
            DataPath.Text = Paths.Root;
            RefreshStepperLabels();
            UpdateRejoinRows();

            MultiSwitch.Checked += (s, e) => SetMulti(true);
            MultiSwitch.Unchecked += (s, e) => SetMulti(false);

            RejoinSwitch.Checked += (s, e) => { _s.AutoRejoin = true; UpdateRejoinRows(); _mgr.SaveSettings(); };
            RejoinSwitch.Unchecked += (s, e) => { _s.AutoRejoin = false; UpdateRejoinRows(); _mgr.SaveSettings(); };

            IdleRejoinSwitch.Checked += (s, e) => { _s.RejoinAfterIdleKick = true; UpdateRejoinRows(); _mgr.SaveSettings(); };
            IdleRejoinSwitch.Unchecked += (s, e) => { _s.RejoinAfterIdleKick = false; UpdateRejoinRows(); _mgr.SaveSettings(); };

            Step(StaggerMinus, -1, () => _s.StaggerSeconds, v => _s.StaggerSeconds = v, 1, 60);
            Step(StaggerPlus, +1, () => _s.StaggerSeconds, v => _s.StaggerSeconds = v, 1, 60);
            Step(RejoinMinus, -5, () => _s.RejoinDelaySeconds, v => _s.RejoinDelaySeconds = v, 5, 300);
            Step(RejoinPlus, +5, () => _s.RejoinDelaySeconds, v => _s.RejoinDelaySeconds = v, 5, 300);
            Step(TriesMinus, -1, () => _s.MaxRejoins, v => _s.MaxRejoins = v, 1, 20);
            Step(TriesPlus, +1, () => _s.MaxRejoins, v => _s.MaxRejoins = v, 1, 20);

            PrioNormal.Checked += (s, e) => SetPriority("Normal");
            PrioBelow.Checked += (s, e) => SetPriority("BelowNormal");
            PrioIdle.Checked += (s, e) => SetPriority("Idle");

            OpenDataButton.Click += (s, e) =>
            {
                try { Paths.Ensure(); Process.Start(new ProcessStartInfo(Paths.Root) { UseShellExecute = true }); } catch { }
            };
            WipeButton.Click += async (s, e) =>
            {
                if (Store.IsProtectedFolder(Paths.Root))
                {
                    ShowToast("Lantern's data folder is " + Paths.Root + ", which also holds other things, so Lantern won't erase it. Delete Lantern's files from it yourself.", true);
                    return;
                }
                if (!await ConfirmAsync("Erase everything?",
                    "This deletes every saved account, game and setting from this PC and closes Lantern. Roblox then closes the windows that are open (they only stay open while Lantern holds its multi-window lock). Your Roblox accounts themselves aren't touched.",
                    "Erase and quit", true)) return;
                var left = Store.WipeEverything();
                if (left.Count > 0)
                {
                    // Lantern stays open: quitting now would leave the user thinking the accounts are gone
                    ShowToast("Couldn't erase " + left.Count + (left.Count == 1 ? " file" : " files") + " (" + System.IO.Path.GetFileName(left[0]) +
                              (left.Count > 1 ? " and others" : "") + "). Something else is using " + (left.Count == 1 ? "it" : "them") +
                              ": close backup, sync or antivirus tools and try again, or delete " + Paths.Root + " yourself.", true);
                    return;
                }
                _wiped = true;
                MultiInstance.Disable();
                Application.Current.Shutdown();
            };

            WireSettingsNav();
            WireAppearance();
            WireAbout();

            GameAddButton.Click += async (s, e) => await AddGameAsync();
            GameAddBox.KeyDown += async (s, e) => { if (e.Key == Key.Enter) await AddGameAsync(); };
            GamesList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                if (!(e.OriginalSource is Button b) || !(b.DataContext is GameItem g)) return;
                if ((b.Tag as string) == "use") UseGame(g);
                else if ((b.Tag as string) == "remove") _games.Remove(g);
            }));
        }

        // multi-instance
        void SetMulti(bool on)
        {
            if (on)
            {
                if (!MultiInstance.Enable(out var err))
                {
                    // Keep the preference but be honest that it isn't active yet.
                    ShowToast(err, true);
                }
                _s.MultiInstance = true;
            }
            else
            {
                MultiInstance.Disable();
                _s.MultiInstance = false;
            }
            UpdateMultiStatus();
            _mgr.SaveSettings();
        }

        void UpdateMultiStatus()
        {
            MultiStatusRow.Visibility = _s.MultiInstance ? Visibility.Visible : Visibility.Collapsed;
            if (MultiInstance.Held)
            {
                MultiDot.SetResourceReference(Border.BackgroundProperty, "BrushSuccess");
                MultiStatus.Text = "Active — Lantern is holding Roblox's single-instance lock.";
            }
            else
            {
                MultiDot.SetResourceReference(Border.BackgroundProperty, "BrushWarning");
                MultiStatus.Text = "Not active yet. Close every Roblox window, then switch this off and on again.";
            }
        }

        // steppers / simple options
        void Step(Button b, int delta, Func<int> get, Action<int> set, int min, int max)
        {
            b.Click += (s, e) =>
            {
                set(Math.Max(min, Math.Min(max, get() + delta)));
                RefreshStepperLabels();
                _mgr.SaveSettings();
            };
        }

        void RefreshStepperLabels()
        {
            StaggerValue.Text = _s.StaggerSeconds + " s";
            RejoinValue.Text = _s.RejoinDelaySeconds + " s";
            TriesValue.Text = _s.MaxRejoins + "×";
        }

        void UpdateRejoinRows()
        {
            var v = _s.AutoRejoin || _s.RejoinAfterIdleKick ? Visibility.Visible : Visibility.Collapsed;
            RejoinDelayRow.Visibility = v;
            RejoinTriesRow.Visibility = v;
        }

        void SetPriority(string value)
        {
            _s.Priority = value;
            foreach (var a in _mgr.Accounts.Where(a => a.State == AccountState.Running && a.Pid > 0))
                RobloxProcesses.SetPriority(a.Pid, value);
            _mgr.SaveSettings();
        }

        // games page
        void UpdateGamesEmpty() =>
            GamesEmpty.Visibility = _games.Games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        async Task AddGameAsync()
        {
            var raw = GameAddBox.Text;
            GameAddButton.IsEnabled = false;
            var (item, error) = await _games.AddAsync(raw);
            GameAddButton.IsEnabled = true;
            if (item == null) { ShowToast(error, true); return; }
            GameAddBox.Clear();
            ShowToast("Saved " + item.Name + ".", false);
        }
    }
}
