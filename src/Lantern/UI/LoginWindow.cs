using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Lantern.Core;

namespace Lantern.UI
{
    /// <summary>
    /// A throw-away, private browser window for signing in. It runs in WebView2's InPrivate mode on its own temporary profile
    /// (cleared and deleted on close), can only visit roblox.com, and only the session cookie leaves it.
    /// </summary>
    public sealed class LoginWindow : Window
    {
        readonly WebView2 _web = new WebView2();
        readonly TextBlock _status = new TextBlock();
        readonly DispatcherTimer _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        const string ProfilePrefix = "Lantern-signin-";
        string _profile;
        bool _polling;

        public string Cookie { get; private set; }

        public static string Run(Window owner)
        {
            var w = new LoginWindow { Owner = owner };
            w.ShowDialog();
            return w.Cookie;
        }

        LoginWindow()
        {
            Title = "Sign in to Roblox — Lantern";
            Width = 500; Height = 760; MinWidth = 420; MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            SetResourceReference(BackgroundProperty, "BrushSurface");
            SetResourceReference(ForegroundProperty, "BrushText");
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("FontText");

            _status.HorizontalAlignment = HorizontalAlignment.Center;
            _status.VerticalAlignment = VerticalAlignment.Center;
            _status.TextWrapping = TextWrapping.Wrap;
            _status.TextAlignment = TextAlignment.Center;
            _status.Margin = new Thickness(32);
            _status.Text = "Opening a private sign-in window…";
            _status.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary");

            var grid = new Grid();
            grid.Children.Add(_status);
            _web.Visibility = Visibility.Hidden;
            grid.Children.Add(_web);
            Content = grid;

            SourceInitialized += (s, e) => Native.SetDarkTitleBar(new WindowInteropHelper(this).Handle, Theme.IsDark);
            Loaded += async (s, e) => await StartAsync();
            Closed += (s, e) => Cleanup();
            _poll.Tick += async (s, e) => await CheckCookieAsync();
        }

        async Task StartAsync()
        {
            try
            {
                _profile = Path.Combine(Path.GetTempPath(), ProfilePrefix + Guid.NewGuid().ToString("N"));
                var env = await CoreWebView2Environment.CreateAsync(null, _profile);
                var options = env.CreateCoreWebView2ControllerOptions();
                options.IsInPrivateModeEnabled = true;
                await _web.EnsureCoreWebView2Async(env, options);

                var core = _web.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Settings.IsPasswordAutosaveEnabled = false;
                core.Settings.IsGeneralAutofillEnabled = false;

                core.NavigationStarting += (s, e) =>
                {
                    if (!IsAllowed(e.Uri)) e.Cancel = true;
                };
                core.NewWindowRequested += (s, e) =>
                {
                    e.Handled = true;
                    if (IsAllowed(e.Uri)) core.Navigate(e.Uri);
                };
                core.NavigationCompleted += (s, e) =>
                {
                    _status.Visibility = Visibility.Collapsed;
                    _web.Visibility = Visibility.Visible;
                };

                core.Navigate("https://www.roblox.com/login");
                _poll.Start();
            }
            catch (Exception ex)
            {
                Logger.Write("LoginWindow: " + ex.Message);
                _status.Text = "The sign-in window couldn't start (Microsoft Edge WebView2 is required).\n\nUse “Paste cookie” instead, or install the WebView2 runtime from Microsoft.";
            }
        }

        static bool IsAllowed(string uri)
        {
            try
            {
                var u = new Uri(uri);
                if (u.Scheme == "about" || u.Scheme == "data") return true;
                return RobloxApi.IsRobloxHost(u);
            }
            catch { return false; }
        }

        async Task CheckCookieAsync()
        {
            if (_polling || _web.CoreWebView2 == null) return;
            _polling = true;
            try
            {
                var cookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync("https://www.roblox.com");
                foreach (var c in cookies)
                {
                    if (c.Name == ".ROBLOSECURITY" && !string.IsNullOrEmpty(c.Value) && c.Value.Length > 100)
                    {
                        Cookie = c.Value;
                        _poll.Stop();
                        Close();
                        return;
                    }
                }
            }
            catch (Exception ex) { Logger.Write("Cookie poll: " + ex.Message); }
            finally { _polling = false; }
        }

        void Cleanup()
        {
            _poll.Stop();
            var profile = _profile;
            try { _web.CoreWebView2?.CookieManager.DeleteAllCookies(); } catch { }
            try { _web.Dispose(); } catch { }
            if (string.IsNullOrEmpty(profile)) return;
            DeleteProfile(profile);
        }

        /// <summary>The browser process holds files for a moment after dispose, so deleting is retried quietly.</summary>
        static void DeleteProfile(string profile)
        {
            Task.Run(async () =>
            {
                for (int i = 0; i < 8; i++)
                {
                    try { if (Directory.Exists(profile)) Directory.Delete(profile, true); return; }
                    catch { await Task.Delay(700); }
                }
            });
        }

        /// <summary>
        /// Sign-in profiles left behind by a crash or a killed process. Only one Lantern runs at a time, so at start-up none of
        /// them can be in use.
        /// </summary>
        public static void RemoveStaleProfiles()
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), ProfilePrefix + "*"))
                    DeleteProfile(dir);
            }
            catch (Exception ex) { Logger.Write("Stale sign-in profiles: " + ex.Message); }
        }
    }
}
