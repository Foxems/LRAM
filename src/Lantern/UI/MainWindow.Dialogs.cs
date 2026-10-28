using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Lantern.Core;

namespace Lantern.UI
{
    public partial class MainWindow
    {
        // toast
        readonly DispatcherTimer _toastTimer = new DispatcherTimer();

        void ShowToast(string text, bool error)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            ToastText.Text = StreamBlurNames ? MaskNames(text) : text;
            ToastIcon.Text = error ? "\uE7BA" : "\uE73E";
            ToastIcon.SetResourceReference(TextBlock.ForegroundProperty, error ? "BrushDanger" : "BrushSuccess");

            double bottom = (PageAccounts.Visibility == Visibility.Visible ? LaunchBar.ActualHeight : 0) + 16;
            Toast.Margin = new Thickness(90, 0, 14, bottom);

            Toast.Visibility = Visibility.Visible;
            Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
            ToastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

            _toastTimer.Stop();
            _toastTimer.Interval = TimeSpan.FromMilliseconds(Math.Min(9000, (error ? 5000 : 3200) + text.Length * 25));
            _toastTimer.Tick -= HideToast;
            _toastTimer.Tick += HideToast;
            _toastTimer.Start();
        }

        void HideToast(object sender, EventArgs e)
        {
            _toastTimer.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
            fade.Completed += (s, ev) => { if (Toast.Opacity == 0) Toast.Visibility = Visibility.Collapsed; };
            Toast.BeginAnimation(OpacityProperty, fade);
            ToastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, TimeSpan.FromMilliseconds(180)));
        }

        // dialog engine
        TaskCompletionSource<bool> _dlgTcs;
        Func<Task<bool>> _dlgOk;
        Action _dialogCancel;
        bool _dlgBusy;
        bool _dlgWired;

        void WireDialog()
        {
            if (_dlgWired) return;
            _dlgWired = true;

            DlgCancel.Click += (s, e) =>
            {
                if (_dlgSecondary != null) { _dlgSecondary(); return; }   // e.g. "Copy report"
                if (!_dlgBusy) CloseDialog(false);
            };
            DlgOk.Click += async (s, e) =>
            {
                if (_dlgBusy || _dlgOk == null) return;
                _dlgBusy = true;
                DlgOk.IsEnabled = false;
                DlgCancel.IsEnabled = false;
                bool ok;
                try { ok = await _dlgOk(); }
                catch (Exception ex) { Core.Logger.Write("Dialog: " + ex); ok = false; }
                _dlgBusy = false;
                DlgOk.IsEnabled = true;
                DlgCancel.IsEnabled = true;
                if (ok) CloseDialog(true);
            };
            TabSignIn.Checked += (s, e) => UpdateAddTab();
            TabCookie.Checked += (s, e) => UpdateAddTab();
            CookieBox.TextChanged += (s, e) => UpdateCookieCount();
            PasteClipboardButton.Click += (s, e) =>
            {
                var clip = ClipboardText();
                if (string.IsNullOrWhiteSpace(clip)) { CookieCount.Text = "The clipboard is empty."; return; }
                CookieBox.Text = (CookieBox.Text.Length > 0 && !CookieBox.Text.EndsWith("\n") ? CookieBox.Text + "\n" : CookieBox.Text) + clip.Trim();
                CookieBox.CaretIndex = CookieBox.Text.Length;
            };
        }

        void ResetDialog()
        {
            DlgBody.Visibility = Visibility.Collapsed;
            DlgInput.Visibility = Visibility.Collapsed;
            DlgAdd.Visibility = Visibility.Collapsed;
            DlgError.Visibility = Visibility.Collapsed;
            DlgReport.Visibility = Visibility.Collapsed;
            DlgCancel.Visibility = Visibility.Visible;
            DlgCancel.Content = "Cancel";
            DialogCard.MaxWidth = 500;
            _dlgSecondary = null;
            DlgOk.IsEnabled = true;
            DlgCancel.IsEnabled = true;
        }

        Task<bool> OpenDialog(string title, string okText, bool danger, Func<Task<bool>> onOk, UIElement focus)
        {
            WireDialog();
            if (_dlgTcs != null && !_dlgTcs.Task.IsCompleted) return Task.FromResult(false);

            _dlgTcs = new TaskCompletionSource<bool>();
            _dlgOk = onOk;
            _dialogCancel = () => { if (!_dlgBusy) CloseDialog(false); };
            DlgTitle.Text = title;
            DlgOk.Content = okText;
            DlgOk.Style = (Style)FindResource(danger ? "DangerButton" : "AccentButton");

            DialogLayer.Visibility = Visibility.Visible;
            DialogLayer.Opacity = 0;
            DialogLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
            Dispatcher.BeginInvoke(new Action(() => (focus ?? DlgOk).Focus()), DispatcherPriority.Input);
            return _dlgTcs.Task;
        }

        void CloseDialog(bool result)
        {
            DialogLayer.Visibility = Visibility.Collapsed;
            DialogLayer.BeginAnimation(OpacityProperty, null);
            CookieBox.Clear();
            _dlgOk = null;
            _dialogCancel = null;
            _dlgTcs?.TrySetResult(result);
        }

        public Task<bool> ConfirmAsync(string title, string body, string okText, bool danger)
        {
            ResetDialog();
            DlgBody.Text = body;
            DlgBody.Visibility = Visibility.Visible;
            return OpenDialog(title, okText, danger, () => Task.FromResult(true), DlgOk);
        }

        /// <summary>Returns the entered text, or null if cancelled.</summary>
        public async Task<string> PromptAsync(string title, string body, string initial, string okText, string placeholder = "")
        {
            ResetDialog();
            DlgBody.Text = body;
            DlgBody.Visibility = Visibility.Visible;
            DlgInput.Text = initial ?? "";
            Ui.SetPlaceholder(DlgInput, placeholder);
            DlgInput.Visibility = Visibility.Visible;
            bool ok = await OpenDialog(title, okText, false, () => Task.FromResult(true), DlgInput);
            if (!ok) return null;
            return DlgInput.Text;
        }

        // add accounts
        static string ClipboardText()
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
            catch { return ""; }
        }

        void UpdateCookieCount()
        {
            int n = InstanceManager.ExtractCookies(CookieBox.Text).Count;
            CookieCount.Text = string.IsNullOrWhiteSpace(CookieBox.Text) ? ""
                : n == 0 ? "No cookie found in that text yet."
                : n + (n == 1 ? " cookie ready to add" : " cookies ready to add");
            if (_dlgOk != null && !_dlgBusy && TabCookie.IsChecked == true)
                DlgOk.Content = n > 1 ? "Add " + n + " accounts" : "Add account";
        }

        void UpdateAddTab()
        {
            bool signIn = TabSignIn.IsChecked == true;
            SignInHelp.Visibility = signIn ? Visibility.Visible : Visibility.Collapsed;
            KeepAdding.Visibility = signIn ? Visibility.Visible : Visibility.Collapsed;
            CookiePanel.Visibility = signIn ? Visibility.Collapsed : Visibility.Visible;
            DlgError.Visibility = Visibility.Collapsed;
            if (_dlgOk != null && !_dlgBusy)
            {
                if (signIn) DlgOk.Content = "Open sign-in";
                else UpdateCookieCount();
            }
            if (!signIn) Dispatcher.BeginInvoke(new Action(() => CookieBox.Focus()), DispatcherPriority.Input);
        }

        /// <summary>Opens the add dialog. <paramref name="pasted"/> pre-fills the cookie box and switches to it.</summary>
        async Task AddAccountFlowAsync(bool pasteTab = false, string pasted = null)
        {
            ResetDialog();
            DlgAdd.Visibility = Visibility.Visible;
            DlgBody.Visibility = Visibility.Collapsed;

            // A cookie already on the clipboard is almost certainly what the user is about to paste.
            string note = null;
            if (string.IsNullOrEmpty(pasted))
            {
                var clip = ClipboardText();
                if (clip.Length < 200000 && InstanceManager.ExtractCookies(clip).Count > 0 && clip.Contains("_|WARNING"))
                {
                    pasted = clip;
                    note = "Found on your clipboard";
                }
            }
            if (!string.IsNullOrEmpty(pasted)) pasteTab = true;

            CookieBox.Text = pasted ?? "";
            TabCookie.IsChecked = pasteTab;
            TabSignIn.IsChecked = !pasteTab;

            Func<Task<bool>> onOk = async () =>
            {
                DlgError.Visibility = Visibility.Collapsed;
                if (TabSignIn.IsChecked == true) return await SignInLoopAsync();
                return await AddPastedAsync();
            };

            var t = OpenDialog("Add accounts", "Open sign-in", false, onOk, pasteTab ? (UIElement)CookieBox : TabSignIn);
            UpdateAddTab();
            if (note != null && pasteTab) CookieCount.Text = note + " · " + CookieCount.Text;
            await t;
        }

        async Task<bool> SignInLoopAsync()
        {
            int added = 0;
            do
            {
                var cookie = LoginWindow.Run(this);
                if (cookie == null) return added > 0;   // closed the sign-in window: done (or nothing to add)

                DlgOk.Content = "Checking…";
                var sum = await _mgr.AddManyAsync(new List<string> { cookie });
                UpdateAddTab();
                if (sum.Ok == 0)
                {
                    DlgError.Text = sum.Errors.FirstOrDefault() ?? "Couldn't add that account.";
                    DlgError.Visibility = Visibility.Visible;
                    return added > 0;
                }
                added++;
                ShowToast(sum.Updated > 0 ? sum.Last.Display + "'s session was refreshed." : "Added " + sum.Last.Display + ".", false);
            }
            while (KeepAdding.IsChecked == true);
            return true;
        }

        async Task<bool> AddPastedAsync()
        {
            var cookies = InstanceManager.ExtractCookies(CookieBox.Text);
            if (cookies.Count == 0)
            {
                DlgError.Text = "No cookie found. It starts with _|WARNING:-DO-NOT-SHARE-THIS…";
                DlgError.Visibility = Visibility.Visible;
                return false;
            }

            var sum = await _mgr.AddManyAsync(cookies, (n, total) => { if (total > 1) DlgOk.Content = "Adding " + n + " of " + total + "…"; else DlgOk.Content = "Checking…"; });
            UpdateAddTab();

            if (sum.Ok == 0)
            {
                DlgError.Text = string.Join("\n", sum.Errors.Take(4));
                DlgError.Visibility = Visibility.Visible;
                return false;
            }

            string msg = sum.Added > 0 ? "Added " + sum.Added + (sum.Added == 1 ? " account" : " accounts") : "";
            if (sum.Updated > 0) msg += (msg.Length > 0 ? " · " : "") + sum.Updated + " refreshed";
            if (sum.Errors.Count > 0) msg += " · " + sum.Errors.Count + " failed (" + sum.Errors[0] + ")";
            ShowToast(msg + ".", sum.Errors.Count > 0);
            ClearClipboardIfCookie();
            return true;
        }

        /// <summary>Re-sign-in for one account (expired session).</summary>
        async Task SignInFlowAsync()
        {
            var cookie = LoginWindow.Run(this);
            if (cookie == null) return;
            var sum = await _mgr.AddManyAsync(new List<string> { cookie });
            if (sum.Ok == 0) { ShowToast(sum.Errors.FirstOrDefault() ?? "Couldn't sign in.", true); return; }
            ShowToast(sum.Updated > 0 ? sum.Last.Display + " is signed in again." : "Added " + sum.Last.Display + ".", false);
        }

        /// <summary>Ctrl+V / drag-and-drop entry point: opens the dialog straight on the pasted cookies if there are any.</summary>
        async Task<bool> TryAddFromTextAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 2_000_000) return false;
            if (InstanceManager.ExtractCookies(text).Count == 0) return false;
            NavAccounts.IsChecked = true;
            await AddAccountFlowAsync(true, text);
            return true;
        }
    }
}

