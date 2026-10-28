using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lantern.Core;

namespace Lantern.UI
{
    /// <summary>
    /// The start-up window (see <see cref="GlassSplash"/> for the picture). It runs on a UI thread of its own, so it keeps moving
    /// while the main window is built and the accounts are checked on the main thread. It covers exactly the main window's frame,
    /// corners included, while that window loads cloaked underneath. When loading is done (<see cref="Finish"/>) the lantern lifts
    /// away, the main window is revealed in the same place and the picture fades out over it.
    /// </summary>
    public sealed class Splash
    {
        /// <summary>From "ready": the main window is revealed underneath, then the picture fades out over it.</summary>
        const double RevealAt = 0.3, FadeFrom = 0.35, FinaleSeconds = 1.15;

        readonly Dispatcher _owner;
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly SplashColors _colors;
        GlassSplash _art;
        Dispatcher _disp;
        Window _win;
        Grid _frame;
        EventHandler _rendering;
        volatile string _status = "Waking up";
        double _progress = -1;
        volatile bool _finishWanted, _failed;
        Action _done;
        double _finishAt = -1;
        bool _revealed, _closed;

        Splash(Dispatcher owner, SplashColors colors) { _owner = owner; _colors = colors; }

        /// <summary>
        /// Opens the window on its own thread over <paramref name="bounds"/> (the main window's planned place, in device-independent
        /// units). Returns once it is up, or has failed, in which case nothing waits on it.
        /// </summary>
        public static Splash Start(Rect bounds)
        {
            var s = new Splash(Dispatcher.CurrentDispatcher, SplashColors.Current());
            var ready = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                try { s.Run(bounds, ready); }
                catch (Exception ex) { Logger.Write("Splash: " + ex.Message); s.Fail(); ready.Set(); }
            }) { IsBackground = true, Name = "Lantern.Splash" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait(3000);
            return s;
        }

        /// <summary>What is being done right now, shown under the name ("Checking accounts 3/12").</summary>
        public void SetStatus(string text) => _status = text ?? "";

        /// <summary>How far loading is, 0..1 (negative: not known yet).</summary>
        public void SetProgress(double value) => Interlocked.Exchange(ref _progress, value);

        /// <summary>Lays the picture exactly over the main window's visible frame (screen pixels), once that window exists.</summary>
        public void Fit(int x, int y, int w, int h)
        {
            if (_failed || _disp == null || w <= 0 || h <= 0) return;
            _disp.BeginInvoke(new Action(() =>
            {
                if (_closed || _win == null) return;
                var hwnd = new WindowInteropHelper(_win).Handle;
                if (hwnd != IntPtr.Zero) Native.Place(hwnd, x, y, w, h);
            }));
        }

        /// <summary>
        /// Loading is done: play the lift-off (not before the minimum time), then call <paramref name="reveal"/> on the main
        /// thread, under the fading picture. Called straight away if the window couldn't be shown.
        /// </summary>
        public void Finish(Action reveal)
        {
            _done = reveal;
            _finishWanted = true;
            if (_failed) RunDone();
        }

        void Fail()
        {
            _failed = true;
            if (_finishWanted) RunDone();
        }

        void RunDone()
        {
            var d = Interlocked.Exchange(ref _done, null);
            if (d != null) _owner.BeginInvoke(d);
        }

        void Run(Rect bounds, ManualResetEventSlim ready)
        {
            _disp = Dispatcher.CurrentDispatcher;
            _disp.UnhandledException += (s, e) => { Logger.Write("Splash: " + e.Exception.Message); e.Handled = true; Close(); };

            _art = new GlassSplash(_colors);
            _frame = new Grid { ClipToBounds = true, Background = new SolidColorBrush(_colors.Back), Children = { _art.Root } };
            _win = new Window
            {
                Title = "Lantern", WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height, Content = _frame, UseLayoutRounding = true
            };
            try { _win.Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Lantern;component/Assets/lantern.ico")); } catch { }
            _win.SizeChanged += (s, e) => Layout();
            _win.DpiChanged += (s, e) => Layout();

            _rendering = (s, e) => Frame();
            CompositionTarget.Rendering += _rendering;
            _win.Show();
            Layout();
            Frame();
            ready.Set();
            Dispatcher.Run();
        }

        /// <summary>Sizes the stage to the window, with the corners rounded like a Windows 11 window's.</summary>
        void Layout()
        {
            if (_win.ActualWidth <= 0) return;
            _art.Resize(_win.ActualWidth, _win.ActualHeight);
            double r = Environment.OSVersion.Version.Build >= 22000 ? 8 : 0;
            _frame.Clip = new RectangleGeometry(new Rect(0, 0, _win.ActualWidth, _win.ActualHeight), r, r);
        }

        void Frame()
        {
            if (_closed || _art == null) return;
            double t = _clock.Elapsed.TotalSeconds;
            if (_finishWanted && _finishAt < 0 && t >= GlassSplash.MinSeconds) _finishAt = t;
            double fin = _finishAt < 0 ? -1 : t - _finishAt;

            _art.Frame(t, fin, fin >= 0 ? "Ready" : _status, fin >= 0 ? 1 : Interlocked.CompareExchange(ref _progress, 0, 0));

            if (fin >= RevealAt && !_revealed)
            {
                _revealed = true;
                _win.Topmost = true;   // stay above the main window while it is revealed underneath
                RunDone();
            }
            if (fin >= FadeFrom)
            {
                double k = Math.Min(1, (fin - FadeFrom) / (FinaleSeconds - FadeFrom));
                _win.Opacity = 1 - k * k * (3 - 2 * k);
            }
            if (fin >= FinaleSeconds) Close();
        }

        void Close()
        {
            if (_closed) return;
            _closed = true;
            if (_rendering != null) CompositionTarget.Rendering -= _rendering;
            try { _win?.Close(); } catch { }
            RunDone();
            _disp.BeginInvokeShutdown(DispatcherPriority.Background);
        }
    }
}
