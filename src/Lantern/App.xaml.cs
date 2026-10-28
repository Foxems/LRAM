using System;
using System.Linq;
using System.Threading;
using System.Windows;
using Lantern.Core;
using Lantern.UI;

namespace Lantern
{
    public partial class App : Application
    {
        Mutex _single;
        EventWaitHandle _showSignal;

        public static SettingsData Settings { get; private set; }

        /// <summary>
        /// <c>--demo</c>: sample accounts, groups and profiles in a throw-away data folder. Nothing real is read, saved or sent,
        /// which makes it safe for screenshots and for trying the interface out.
        /// </summary>
        public static bool Demo { get; private set; }

        /// <summary>Set by <c>--launch=Name</c> (or <c>--launch Name</c>): start that profile once Lantern is up. Handy for shortcuts and Task Scheduler.</summary>
        public static string LaunchProfile { get; private set; }

        static string ParseLaunch(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--launch=", StringComparison.OrdinalIgnoreCase)) return args[i].Substring(9).Trim().Trim('"');
                if (string.Equals(args[i], "--launch", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) return args[i + 1].Trim().Trim('"');
            }
            return null;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Demo = Array.IndexOf(e.Args, "--demo") >= 0;
            LaunchProfile = ParseLaunch(e.Args);
            if (string.IsNullOrEmpty(LaunchProfile)) LaunchProfile = null;
            if (Demo) Environment.SetEnvironmentVariable("LANTERN_HOME", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Lantern-demo"));

            // One Lantern at a time; a second launch just brings the first to the front. A demo has names of its own, so it can run
            // beside a real Lantern without answering that one's "show yourself".
            string suffix = Demo ? ".Demo" : "";
            _single = new Mutex(true, @"Local\Lantern" + suffix + ".Single", out bool first);
            if (!first && !Demo)
            {
                // Lantern is already open: hand it the profile to start (a small file, then the same "show yourself" signal).
                if (LaunchProfile != null)
                {
                    try { Paths.Ensure(); System.IO.File.WriteAllText(System.IO.Path.Combine(Paths.Root, "launch-request.txt"), LaunchProfile); } catch { }
                }
                try { using (var show = EventWaitHandle.OpenExisting(@"Local\Lantern.Show")) show.Set(); } catch { }
                Shutdown();
                return;
            }

            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Lantern" + suffix + ".Show");
            var waiter = new Thread(() =>
            {
                while (_showSignal.WaitOne())
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (MainWindow == null) return;
                        MainWindow.Show();
                        Native.Foreground(new System.Windows.Interop.WindowInteropHelper(MainWindow).Handle);
                        (MainWindow as MainWindow)?.ConsumeLaunchRequest();
                    }));
            }) { IsBackground = true, Name = "Lantern.ShowSignal" };
            waiter.Start();

            DispatcherUnhandledException += (s, a) =>
            {
                Logger.Write("Unhandled: " + a.Exception);
                MessageBox.Show("Something went wrong:\n\n" + a.Exception.Message + "\n\nDetails were saved to:\n" + Paths.Log,
                    "Lantern", MessageBoxButton.OK, MessageBoxImage.Error);
                a.Handled = true;
            };

            Paths.Ensure();
            LoginWindow.RemoveStaleProfiles();
            Settings = Store.LoadPlain<SettingsData>(Paths.Settings);
            var themeArg = e.Args.FirstOrDefault(a => a.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase));
            if (Demo && themeArg != null) Settings.Theme = Theme.Normalize(char.ToUpper(themeArg[8]) + themeArg.Substring(9).ToLowerInvariant());
            Theme.Init(Settings);

            // First run: the disclaimer, the theme and how cookies are kept safe, before anything else happens.
            // Closing the window without accepting quits.
            bool forceSetup = Demo && e.Args.Contains("--first-run");
            if (forceSetup || (!Demo && !Settings.SetupDone))
            {
                // The setup is the first window, which WPF would take for the main one and quit when it closes.
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var setup = new SetupWindow(Settings);
                setup.ShowDialog();
                if (!setup.Completed) { Shutdown(); return; }
                Store.SavePlain(Paths.Settings, Settings);
            }

            // The start-up picture runs on its own thread, before the main window is even built.
            bool wantSplash = Demo ? e.Args.Contains("--splash") : Settings.ShowSplash && !e.Args.Contains("--no-splash");
            Splash splash = null;
            if (wantSplash)
            {
                // Where the main window will be, without its invisible resize borders (the picture is laid exactly over it once it exists).
                var b = Lantern.UI.MainWindow.PlannedBounds(Settings).bounds;
                splash = Splash.Start(new Rect(b.Left + 7, b.Top, Math.Max(100, b.Width - 14), Math.Max(100, b.Height - 7)));
            }

            var window = new MainWindow(Settings);
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            if (splash != null) window.StartBehind(splash);
            window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            MultiInstance.Disable();
            base.OnExit(e);
        }
    }
}
