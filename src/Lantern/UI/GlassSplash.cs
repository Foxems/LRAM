using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Lantern.UI
{
    /// <summary>The colours the start screens need, taken from the live theme (so the splash thread never touches the app's resources).</summary>
    public struct SplashColors
    {
        public bool Dark;
        public Color Back, Text, TextSecondary, TextTertiary, Accent;

        public static SplashColors Current()
        {
            var r = System.Windows.Application.Current.Resources;
            Color C(string key, Color fallback) => r[key] is Color c ? c : fallback;
            bool dark = Theme.IsDark;
            var back = Theme.WindowBase;
            var text = dark ? Colors.White : Color.FromRgb(0x1A, 0x1A, 0x1A);
            return new SplashColors
            {
                Dark = dark, Back = back, Text = text,
                TextSecondary = Theme.Mix(text, back, 0.30), TextTertiary = Theme.Mix(text, back, 0.50),
                Accent = C("ColorAccent", Color.FromRgb(0x00, 0x78, 0xD4)),
            };
        }
    }

    /// <summary>
    /// The start screen: a paper lantern lights up on the theme's own ground, a warm glow breathes around it, sparks rise off the
    /// flame, the name and a thin accent progress bar settle in underneath, and when loading is done the lantern lifts away.
    /// Everything is vector and eased, drawn at the display's full frame rate.
    /// </summary>
    public sealed class GlassSplash
    {
        public const double MinSeconds = 3.0;

        readonly SplashColors _c;
        readonly Canvas _stage = new Canvas { ClipToBounds = true, SnapsToDevicePixels = false };
        public UIElement Root => _stage;

        double _w = 1, _h = 1;
        Rectangle _ground, _grain;
        Canvas _lantern; TranslateTransform _lanternPos = new TranslateTransform(); ScaleTransform _lanternScale = new ScaleTransform(1, 1);
        Ellipse _halo, _core;
        Path _flame;
        DropShadowEffect _bodyGlowFx;
        readonly RotateTransform _sway = new RotateTransform();
        readonly ScaleTransform _flameScale = new ScaleTransform(1, 1);
        readonly List<Ellipse> _embers = new List<Ellipse>();
        StackPanel _text; TextBlock _title, _subtitle, _status;
        TranslateTransform _titleShift = new TranslateTransform(), _subShift = new TranslateTransform(), _statusShift = new TranslateTransform();
        Grid _bar; Border _fill, _track;
        double _shown;   // the bar as drawn: it glides towards the real figure

        public GlassSplash(SplashColors colors)
        {
            _c = colors;
            Build();
        }

        static SolidColorBrush Brush(Color c, double alpha = 1)
        {
            var b = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * Math.Max(0, Math.Min(1, alpha))), c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        static double Clamp(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
        static double Smooth(double a, double b, double v) { double k = Clamp((v - a) / (b - a)); return k * k * (3 - 2 * k); }
        static double OutCubic(double k) { k = Clamp(k); return 1 - Math.Pow(1 - k, 3); }
        static double Lerp(double a, double b, double k) => a + (b - a) * k;
        static double Hash(int n) { double s = Math.Sin(n * 12.9898 + 78.233) * 43758.5453; return s - Math.Floor(s); }

        static readonly Color Warm = Color.FromRgb(0xFF, 0x9E, 0x48), WarmDeep = Color.FromRgb(0xFF, 0x7A, 0x2E), Cream = Color.FromRgb(0xFF, 0xF1, 0xB8);

        void Build()
        {
            var back = _c.Back;
            bool dark = _c.Dark;

            // ground: the theme's own colour with a faint warm light behind where the lantern will be
            _ground = new Rectangle();
            _stage.Children.Add(_ground);

            // a soft warm light behind the lantern
            _halo = new Ellipse { Width = 300, Height = 300, IsHitTestVisible = false };
            _halo.Fill = new RadialGradientBrush(new GradientStopCollection
            {
                new GradientStop(Color.FromArgb((byte)(dark ? 70 : 46), Warm.R, Warm.G, Warm.B), 0),
                new GradientStop(Color.FromArgb((byte)(dark ? 22 : 16), WarmDeep.R, WarmDeep.G, WarmDeep.B), 0.5),
                new GradientStop(Color.FromArgb(0, WarmDeep.R, WarmDeep.G, WarmDeep.B), 1),
            });
            _stage.Children.Add(_halo);

            BuildLantern();
            // words and the progress bar
            _title = new TextBlock
            {
                Text = "Lantern", FontSize = 34, FontWeight = FontWeights.SemiBold, Foreground = Brush(_c.Text), HorizontalAlignment = HorizontalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), RenderTransform = _titleShift, Opacity = 0
            };
            _subtitle = new TextBlock
            {
                Text = "Account manager", FontSize = 14.5, Foreground = Brush(_c.TextSecondary), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0),
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), RenderTransform = _subShift, Opacity = 0
            };
            _status = new TextBlock
            {
                Text = "", FontSize = 12.5, Foreground = Brush(_c.TextTertiary), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 26, 0, 0),
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), RenderTransform = _statusShift, Opacity = 0
            };
            _track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Brush(_c.Text, dark ? 0.12 : 0.10) };
            _fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            _fill.Background = new LinearGradientBrush(new GradientStopCollection { new GradientStop(_c.Accent, 0), new GradientStop(_c.Accent, 1) }, 0);
            _fill.Effect = new DropShadowEffect { Color = _c.Accent, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.6 };
            _bar = new Grid { Width = 232, Height = 4, Margin = new Thickness(0, 10, 0, 0), Opacity = 0, ClipToBounds = false };
            _bar.Children.Add(_track); _bar.Children.Add(_fill);

            _text = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
            _text.Children.Add(_title); _text.Children.Add(_subtitle); _text.Children.Add(_status); _text.Children.Add(_bar);
            _stage.Children.Add(_text);

            // a whisper of noise over everything: 8-bit gradients band into visible rings on a dark ground, this dithers them away
            _grain = new Rectangle { IsHitTestVisible = false, Fill = GrainBrush(dark) };
            _stage.Children.Add(_grain);
        }

        static Brush GrainBrush(bool dark)
        {
            const int n = 96;
            var px = new int[n * n];
            var rnd = new Random(7);
            for (int i = 0; i < px.Length; i++)
            {
                int a = rnd.Next(0, 7);                         // 0..6 of 255
                px[i] = rnd.Next(2) == 0 ? (a << 24) | 0xFFFFFF : (a << 24);   // white or black speck, premultiplied below
            }
            var wb = new System.Windows.Media.Imaging.WriteableBitmap(n, n, 96, 96, PixelFormats.Pbgra32, null);
            for (int i = 0; i < px.Length; i++)
            {
                int a = (int)((uint)px[i] >> 24), v = (px[i] & 0xFFFFFF) == 0 ? 0 : a;     // premultiplied: white = (a,a,a,a), black = (0,0,0,a)
                px[i] = (a << 24) | (v << 16) | (v << 8) | v;
            }
            wb.WritePixels(new Int32Rect(0, 0, n, n), px, n * 4, 0);
            wb.Freeze();
            var b = new ImageBrush(wb) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, n, n), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
            b.Freeze();
            return b;
        }

        const int EmberCount = 9;

        static LinearGradientBrush Vertical(Color top, Color bottom)
        {
            var b = new LinearGradientBrush(top, bottom, 90);
            b.Freeze();
            return b;
        }

        /// <summary>
        /// A paper lantern drawn in vectors around its own centre (0, 0): a hook, two lacquered caps, a glowing body with soft ribs, a
        /// flame and a tassel. The body swings a little from its hook, and sparks rise off it.
        /// </summary>
        void BuildLantern()
        {
            _lantern = new Canvas { Width = 0, Height = 0, RenderTransformOrigin = new Point(0, 0), IsHitTestVisible = false };
            var tg = new TransformGroup();
            tg.Children.Add(_lanternScale); tg.Children.Add(_lanternPos);
            _lantern.RenderTransform = tg;

            var rim = Color.FromRgb(0x5A, 0x24, 0x10);
            var art = new Canvas { Width = 0, Height = 0 };
            _sway.CenterX = 0; _sway.CenterY = -66;
            art.RenderTransform = _sway;

            void Place(UIElement e, double x, double y) { Canvas.SetLeft(e, x); Canvas.SetTop(e, y); art.Children.Add(e); }

            // hook
            Place(new Ellipse { Width = 12, Height = 14, Stroke = Brush(rim), StrokeThickness = 2.5 }, -6, -70);

            // body: lit from inside, so the middle is pale and the edge deep amber
            var bodyFill = new RadialGradientBrush(new GradientStopCollection
            {
                new GradientStop(Color.FromRgb(0xFF, 0xF1, 0xC4), 0),
                new GradientStop(Color.FromRgb(0xFF, 0xC4, 0x6E), 0.38),
                new GradientStop(Color.FromRgb(0xF0, 0x8A, 0x3C), 0.74),
                new GradientStop(Color.FromRgb(0xC2, 0x55, 0x1E), 1),
            }) { Center = new Point(0.5, 0.58), GradientOrigin = new Point(0.5, 0.62), RadiusX = 0.62, RadiusY = 0.66 };
            _bodyGlowFx = new DropShadowEffect { Color = Warm, BlurRadius = 48, ShadowDepth = 0, Opacity = 0 };
            var body = new Ellipse { Width = 78, Height = 92, Fill = bodyFill, Stroke = Brush(Color.FromRgb(0x8A, 0x3C, 0x18), 0.55), StrokeThickness = 1.4, Effect = _bodyGlowFx };
            Place(body, -39, -46);

            // ribs: the same ellipse squeezed, which reads as the paper curving away
            var ribColor = Color.FromRgb(0x9A, 0x42, 0x18);
            foreach (var w in new[] { 54.0, 26.0 })
                Place(new Ellipse { Width = w, Height = 92, Stroke = Brush(ribColor, 0.34), StrokeThickness = 1.2 }, -w / 2, -46);
            Place(new Line { X1 = 0, Y1 = -45, X2 = 0, Y2 = 45, Stroke = Brush(ribColor, 0.34), StrokeThickness = 1.2 }, 0, 0);

            // the flame and its glow, in the middle of the body
            var core = new Ellipse
            {
                Width = 40, Height = 52, IsHitTestVisible = false, Effect = new BlurEffect { Radius = 9 },
                Fill = new RadialGradientBrush(new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(255, 0xFF, 0xFA, 0xE2), 0),
                    new GradientStop(Color.FromArgb(150, 0xFF, 0xE0, 0x8A), 0.5),
                    new GradientStop(Color.FromArgb(0, 0xFF, 0xD0, 0x70), 1),
                })
            };
            _core = core;
            Place(core, -20, -26);
            _flame = new Path
            {
                Data = Geometry.Parse("M 0,-14 C 5,-6 6,0 0,8 C -6,0 -5,-6 0,-14 Z"),
                Fill = Brush(Color.FromRgb(0xFF, 0xFD, 0xF0)), RenderTransformOrigin = new Point(0.5, 1), RenderTransform = _flameScale
            };
            Place(_flame, 0, 4);

            // a soft highlight on the paper
            var gloss = new Ellipse
            {
                Width = 11, Height = 40, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(14),
                Fill = new LinearGradientBrush(Color.FromArgb(120, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90)
            };
            Place(gloss, -29, -34);

            // caps
            var cap = Vertical(Color.FromRgb(0x7A, 0x33, 0x18), Color.FromRgb(0x4A, 0x1C, 0x0E));
            Place(new Border { Width = 40, Height = 9, CornerRadius = new CornerRadius(4, 4, 2, 2), Background = cap }, -20, -54);
            Place(new Border { Width = 32, Height = 9, CornerRadius = new CornerRadius(2, 2, 4, 4), Background = cap }, -16, 45);

            // tassel
            var thread = Brush(Color.FromRgb(0xC9, 0x8A, 0x4A));
            Place(new Line { X1 = 0, Y1 = 54, X2 = 0, Y2 = 68, Stroke = thread, StrokeThickness = 2 }, 0, 0);
            Place(new Ellipse { Width = 8, Height = 10, Fill = thread }, -4, 64);
            foreach (var dx in new[] { -5.0, 0.0, 5.0 })
                Place(new Line { X1 = 0, Y1 = 72, X2 = dx, Y2 = 90, Stroke = thread, StrokeThickness = 1.6 }, 0, 0);

            _lantern.Children.Add(art);

            // sparks rise off the lantern (they don't swing with it)
            for (int i = 0; i < EmberCount; i++)
            {
                var e = new Ellipse { Width = 3, Height = 3, Fill = Brush(Cream), Opacity = 0, IsHitTestVisible = false };
                _embers.Add(e);
                _lantern.Children.Add(e);
            }
            _stage.Children.Add(_lantern);
        }

        /// <summary>The size of the stage changed (in device-independent pixels).</summary>
        public void Resize(double w, double h)
        {
            _w = Math.Max(1, w); _h = Math.Max(1, h);
            _stage.Width = _w; _stage.Height = _h;
            _ground.Width = _w; _ground.Height = _h;
            _grain.Width = _w; _grain.Height = _h;
            var warmTint = Theme.Mix(_c.Back, Warm, _c.Dark ? 0.05 : 0.06);
            _ground.Fill = new RadialGradientBrush(new GradientStopCollection
            {
                new GradientStop(warmTint, 0), new GradientStop(Theme.Mix(_c.Back, warmTint, 0.45), 0.45), new GradientStop(_c.Back, 1),
            }) { Center = new Point(0.5, 0.40), GradientOrigin = new Point(0.5, 0.40), RadiusX = 0.78, RadiusY = 0.95 };
            _text.Width = _w;
            Canvas.SetLeft(_text, 0);
        }

        double Scale => Math.Max(0.9, Math.Min(1.5, _h / 640.0 * 1.15));

        public void Frame(double t, double fin, string status, double progress)
        {
            double s = Scale;
            double fadeOut = fin < 0 ? 1 : 1 - Smooth(0.0, 0.5, fin);
            double textFade = fin < 0 ? 1 : 1 - Smooth(0.0, 0.35, fin);
            double cx = _w / 2;

            // the lantern: eases up out of the ground like a Windows 11 splash icon, then rests with a very slight float
            double appear = OutCubic(t / 0.7);
            double settle = OutCubic((t - 0.1) / 1.1);
            double flicker = 0.94 + 0.06 * Math.Sin(t * 14) * Math.Sin(t * 5.3 + 1);
            double yHome = _h * 0.36, yStart = _h * 0.40;
            double bob = Math.Sin(t * 1.4) * 3.0 * settle;
            double y = Lerp(yStart, yHome, settle) + bob;
            double lift = fin < 0 ? 0 : Math.Pow(Clamp(fin / 0.9), 2.0) * _h * 0.8 + fin * 26;
            y -= lift;
            _lanternScale.ScaleX = _lanternScale.ScaleY = s * 1.3 * Lerp(0.92, 1.0, appear);
            _lanternPos.X = cx; _lanternPos.Y = Math.Round(y);
            _lantern.Opacity = appear * (fin < 0 ? 1 : 1 - Smooth(0.15, 0.8, fin));

            // the glow breathes with the flame
            double lit = Smooth(0.2, 1.0, t), halo = lit * flicker * fadeOut;
            _halo.Opacity = halo * (0.9 + 0.1 * Math.Sin(t * 1.7));
            _halo.Width = _halo.Height = 300 * s * (0.97 + 0.03 * Math.Sin(t * 1.7));
            Canvas.SetLeft(_halo, cx - _halo.Width / 2); Canvas.SetTop(_halo, y + 6 * s - _halo.Height / 2);
            // the paper swings gently from its hook; the flame wavers
            _sway.Angle = Math.Sin(t * 1.1) * 1.7 * settle;
            double wave = Math.Sin(t * 9.0) * Math.Sin(t * 3.7 + 0.6);
            _flameScale.ScaleY = 1 + 0.10 * wave; _flameScale.ScaleX = 1 - 0.05 * wave;
            double breathe = Smooth(0.2, 1.0, t) * fadeOut;
            _core.Opacity = breathe * (0.82 + 0.18 * flicker);
            _bodyGlowFx.Opacity = breathe * flicker * (_c.Dark ? 0.80 : 0.50);
            for (int i = 0; i < _embers.Count; i++)
            {
                double phase = (t * (0.16 + 0.05 * Hash(i)) + Hash(i + 40)) % 1.0;
                double size = 2.2 + 2.4 * Hash(i + 7);
                var e = _embers[i];
                e.Width = e.Height = size;
                Canvas.SetLeft(e, (Hash(i + 20) - 0.5) * 46 + Math.Sin(t * 1.3 + i * 1.7) * 7 * phase - size / 2);
                Canvas.SetTop(e, -52 - phase * 120 - size / 2);
                e.Opacity = Math.Sin(Math.PI * phase) * 0.85 * breathe * (_c.Dark ? 1 : 0.7);
            }
            // the words: each settles in a moment after the one before
            double tTitle = Smooth(0.45, 1.15, t), tSub = Smooth(0.65, 1.35, t), tStat = Smooth(0.85, 1.55, t);
            double top = _h * 0.60;
            Canvas.SetTop(_text, top);
            _title.Opacity = tTitle * textFade; _titleShift.Y = (1 - OutCubic(tTitle)) * 12 - (fin < 0 ? 0 : fin * 14);
            _subtitle.Opacity = tSub * textFade; _subShift.Y = (1 - OutCubic(tSub)) * 10 - (fin < 0 ? 0 : fin * 10);
            _status.Opacity = tStat * textFade; _statusShift.Y = (1 - OutCubic(tStat)) * 8;
            string text = fin >= 0 ? "Ready" : (status ?? "");
            if (_status.Text != text) _status.Text = text;
            _bar.Opacity = tStat * textFade;
            _title.FontSize = 34 * Math.Max(0.9, Math.Min(1.2, s));

            // the bar glides towards the real figure; with none, a light travels along it
            double barW = _bar.Width;
            _bar.ClipToBounds = progress < 0 && fin < 0;
            if (progress >= 0 || fin >= 0)
            {
                double target = fin >= 0 ? 1 : Clamp(progress);
                _shown += (target - _shown) * 0.12;
                if (Math.Abs(target - _shown) < 0.002) _shown = target;
                _fill.Width = Math.Max(4, barW * _shown); _fill.Margin = new Thickness(0);
            }
            else
            {
                double seg = barW * 0.32, pos = ((t * 0.55) % 1.5) - 0.32;   // slides in from the left and out through the right end
                _fill.Width = seg; _fill.Margin = new Thickness(barW * pos, 0, 0, 0);
            }
        }
    }
}



