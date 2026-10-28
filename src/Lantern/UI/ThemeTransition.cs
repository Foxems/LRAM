using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Lantern.Core;

namespace Lantern.UI
{
    /// <summary>Whether the app may animate: its own switch, and Windows' "animation effects" setting.</summary>
    public static class Motion
    {
        public static bool Enabled(SettingsData s) => (s == null || s.Animations) && SystemParameters.ClientAreaAnimation;

        public static readonly IEasingFunction Out = new CubicEase { EasingMode = EasingMode.EaseOut };
        public static readonly IEasingFunction InOut = new CubicEase { EasingMode = EasingMode.EaseInOut };
    }

    /// <summary>
    /// Changing the theme is a transition, not a cut: the old picture of the window is laid over the new look and fades away. The
    /// snapshot includes the backdrop colour the window had, so the title area doesn't flash.
    /// </summary>
    public sealed class ThemeTransition
    {
        readonly Image _layer;
        readonly FrameworkElement _root;
        EventHandler _tick;
        Stopwatch _clock;

        public ThemeTransition(FrameworkElement root, Image layer) { _root = root; _layer = layer; }

        public bool Running => _tick != null;

        /// <summary>Applies <paramref name="change"/> under a transition. Without animation (or before the window is up) it just applies it.</summary>
        public void Run(Action change, bool animate)
        {
            Cancel();
            if (!animate || !_root.IsLoaded || _root.ActualWidth < 40 || _root.ActualHeight < 40) { change(); return; }

            BitmapSource shot;
            try { shot = Snapshot(_root, Theme.WindowBase); }
            catch (Exception ex) { Logger.Write("Transition: " + ex.Message); change(); return; }

            change();
            _root.UpdateLayout();

            _layer.Width = _root.ActualWidth; _layer.Height = _root.ActualHeight;
            _layer.Opacity = 1;
            _layer.Visibility = Visibility.Visible;
            _clock = Stopwatch.StartNew();

            StartFade(shot);
        }

        /// <summary>Ends a running transition at once.</summary>
        public void Cancel() => Done();

        void Done()
        {
            if (_tick != null) { CompositionTarget.Rendering -= _tick; _tick = null; }
            _layer.Visibility = Visibility.Collapsed;
            _layer.Opacity = 1;
            _layer.Source = null;
        }

        // the clock
        // Progress is counted per frame, with any one frame worth at most MaxStepMs. The first frames after a change of theme are
        // the slow ones (the new look renders for the first time), and a wall clock would jump the effect forward over them; this
        // way a stall only pauses it. The very first frame never counts, so it starts from the full picture.
        const double MaxStepMs = 1000 / 50.0;

        void Drive(double durationMs, Action<double> frame)
        {
            double t = 0, last = -1;
            _tick = (s, e) =>
            {
                double now = _clock.Elapsed.TotalMilliseconds;
                if (last >= 0) t += Math.Min(MaxStepMs, now - last);
                last = now;
                double k = Math.Min(1, t / durationMs);
                frame(k);
                if (k >= 1) Done();
            };
            CompositionTarget.Rendering += _tick;
        }

        // cross-fade
        const double FadeMs = 340;

        void StartFade(BitmapSource shot)
        {
            RenderOptions.SetBitmapScalingMode(_layer, BitmapScalingMode.Linear);
            _layer.Source = shot;
            Drive(FadeMs, k => { double u = 1 - k; _layer.Opacity = u * u * u; });   // cubic ease-out
        }

        // snapshot
        /// <summary>The element as pixels (device resolution), over the colour the window's backdrop had.</summary>
        static BitmapSource Snapshot(FrameworkElement root, Color back)
        {
            var src = PresentationSource.FromVisual(root);
            var m = src?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            int w = Math.Max(1, (int)Math.Round(root.ActualWidth * m.M11)), h = Math.Max(1, (int)Math.Round(root.ActualHeight * m.M22));
            var rtb = new RenderTargetBitmap(w, h, 96 * m.M11, 96 * m.M22, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen())
                dc.DrawRectangle(new SolidColorBrush(back), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            rtb.Render(bg);
            rtb.Render(root);
            rtb.Freeze();
            return rtb;
        }
    }
}
