using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Lantern.Core;
using Microsoft.Win32;

namespace Lantern.UI
{
    /// <summary>
    /// Colours, corner radii, fonts and shadows, published as application resources. Controls only refer to these tokens through
    /// DynamicResource, so a change of theme is applied live. The look follows Windows 11: Segoe UI, rounded corners and the
    /// system accent colour, in System, Light or Dark.
    /// </summary>
    public static class Theme
    {
        public const string FollowSystem = "System", Light = "Light", Dark = "Dark";

        public static bool IsDark { get; private set; } = true;
        public static event Action Changed;

        /// <summary>The colour behind everything in the main window (what Mica stands in for); transitions are drawn over it.</summary>
        public static Color WindowBase { get; private set; } = Color.FromRgb(0x20, 0x20, 0x20);

        static string _preference = FollowSystem;
        static bool _hooked;
        static ResourceDictionary _tokens;

        public static void Init(SettingsData settings) => Init(settings.Theme);

        public static void Init(string preference)
        {
            if (!_hooked)
            {
                _hooked = true;
                SystemEvents.UserPreferenceChanged += (s, e) =>
                {
                    if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color || e.Category == UserPreferenceCategory.VisualStyle)
                        Application.Current?.Dispatcher.BeginInvoke(new Action(() => Apply(_preference)));
                };
            }
            Apply(preference);
        }

        public static string Normalize(string preference) =>
            preference == Light ? Light : preference == Dark ? Dark : FollowSystem;

        public static void Apply(string preference)
        {
            _preference = Normalize(preference);

            // All tokens go into a fresh dictionary that replaces the old one in a single step. Setting ~150 keys on the
            // application's resources one by one re-resolves every DynamicResource per key and freezes the window.
            var r = new ResourceDictionary();
            ApplyColours(r, _preference);
            ApplyTokens(r);
            var merged = Application.Current.Resources.MergedDictionaries;
            int at = _tokens == null ? -1 : merged.IndexOf(_tokens);
            if (at >= 0) merged[at] = r; else merged.Add(r);
            _tokens = r;
            Changed?.Invoke();
        }

        static void Set(ResourceDictionary r, string key, Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            r[key] = b;
            r[key.Replace("Brush", "Color")] = c;
        }

        static Color A(byte a, byte rr, byte g, byte b) => Color.FromArgb(a, rr, g, b);

        static void ApplyColours(ResourceDictionary r, string preference)
        {
            bool dark = ResolveDark(preference);
            IsDark = dark;
            var accent = SystemAccent();
            void S(string key, Color c) => Set(r, key, c);

            // Lighter accent on dark surfaces, deeper on light ones, the same way WinUI does it.
            var acc = dark ? Mix(accent, Colors.White, 0.42) : Mix(accent, Colors.Black, 0.18);
            var accHover = dark ? Mix(acc, Colors.Black, 0.08) : Mix(acc, Colors.Black, 0.10);
            var accPress = dark ? Mix(acc, Colors.Black, 0.18) : Mix(acc, Colors.Black, 0.22);
            var onAcc = Luminance(acc) > 0.45 ? Color.FromArgb(0xE6, 0, 0, 0) : Colors.White;

            S("BrushAccent", acc);
            S("BrushAccentHover", accHover);
            S("BrushAccentPressed", accPress);
            S("BrushOnAccent", onAcc);
            S("BrushAccentSubtle", Color.FromArgb(dark ? (byte)0x26 : (byte)0x1C, acc.R, acc.G, acc.B));
            S("BrushAccentText", dark ? Mix(accent, Colors.White, 0.55) : Mix(accent, Colors.Black, 0.25));
            S("BrushHiBg", Colors.Transparent);

            if (dark)
            {
                S("BrushText", A(0xFF, 0xFF, 0xFF, 0xFF));
                S("BrushTextSecondary", A(0xC8, 0xFF, 0xFF, 0xFF));
                S("BrushTextTertiary", A(0x8B, 0xFF, 0xFF, 0xFF));
                S("BrushTextDisabled", A(0x5D, 0xFF, 0xFF, 0xFF));

                S("BrushLayer", A(0x4D, 0x3A, 0x3A, 0x3A));
                S("BrushLayerAlt", A(0x33, 0x00, 0x00, 0x00));
                S("BrushSurface", A(0xFF, 0x2C, 0x2C, 0x2C));
                S("BrushSurfaceStroke", A(0xFF, 0x42, 0x42, 0x42));
                S("BrushScrim", A(0x99, 0x00, 0x00, 0x00));

                S("BrushControl", A(0x12, 0xFF, 0xFF, 0xFF));
                S("BrushCardGlass", A(0x12, 0xFF, 0xFF, 0xFF));
                S("BrushControlHover", A(0x1A, 0xFF, 0xFF, 0xFF));
                S("BrushControlPressed", A(0x0B, 0xFF, 0xFF, 0xFF));
                S("BrushControlStroke", A(0x1C, 0xFF, 0xFF, 0xFF));
                S("BrushSubtleHover", A(0x12, 0xFF, 0xFF, 0xFF));
                S("BrushSubtlePressed", A(0x0A, 0xFF, 0xFF, 0xFF));
                S("BrushRowHover", A(0x0E, 0xFF, 0xFF, 0xFF));
                S("BrushStroke", A(0x16, 0xFF, 0xFF, 0xFF));
                S("BrushInput", A(0x4D, 0x00, 0x00, 0x00));
                S("BrushInputHover", A(0x66, 0x00, 0x00, 0x00));
                S("BrushInputFocus", A(0xCC, 0x1C, 0x1C, 0x1C));
                S("BrushCheckStroke", A(0x9C, 0xFF, 0xFF, 0xFF));
                S("BrushScrollThumb", A(0x80, 0xFF, 0xFF, 0xFF));
                S("BrushDisabledFill", A(0x28, 0xFF, 0xFF, 0xFF));

                S("BrushSuccess", A(0xFF, 0x6C, 0xCB, 0x5F));
                S("BrushDanger", A(0xFF, 0xFF, 0x99, 0xA4));
                S("BrushDangerFill", A(0xFF, 0xC4, 0x2B, 0x1C));
                S("BrushWarning", A(0xFF, 0xFC, 0xE1, 0x00));
            }
            else
            {
                S("BrushText", A(0xE4, 0x00, 0x00, 0x00));
                S("BrushTextSecondary", A(0x9E, 0x00, 0x00, 0x00));
                S("BrushTextTertiary", A(0x72, 0x00, 0x00, 0x00));
                S("BrushTextDisabled", A(0x5C, 0x00, 0x00, 0x00));

                S("BrushLayer", A(0x80, 0xFF, 0xFF, 0xFF));
                S("BrushLayerAlt", A(0x0A, 0x00, 0x00, 0x00));
                S("BrushSurface", A(0xFF, 0xFB, 0xFB, 0xFB));
                S("BrushSurfaceStroke", A(0xFF, 0xDD, 0xDD, 0xDD));
                S("BrushScrim", A(0x66, 0x00, 0x00, 0x00));

                S("BrushControl", A(0xB3, 0xFF, 0xFF, 0xFF));
                S("BrushCardGlass", A(0xB3, 0xFF, 0xFF, 0xFF));
                S("BrushControlHover", A(0x80, 0xF9, 0xF9, 0xF9));
                S("BrushControlPressed", A(0x4D, 0xF9, 0xF9, 0xF9));
                S("BrushControlStroke", A(0x1F, 0x00, 0x00, 0x00));
                S("BrushSubtleHover", A(0x0A, 0x00, 0x00, 0x00));
                S("BrushSubtlePressed", A(0x06, 0x00, 0x00, 0x00));
                S("BrushRowHover", A(0x08, 0x00, 0x00, 0x00));
                S("BrushStroke", A(0x12, 0x00, 0x00, 0x00));
                S("BrushInput", A(0xB3, 0xFF, 0xFF, 0xFF));
                S("BrushInputHover", A(0x80, 0xF9, 0xF9, 0xF9));
                S("BrushInputFocus", A(0xFF, 0xFF, 0xFF, 0xFF));
                S("BrushCheckStroke", A(0x9C, 0x00, 0x00, 0x00));
                S("BrushScrollThumb", A(0x70, 0x00, 0x00, 0x00));
                S("BrushDisabledFill", A(0x37, 0x00, 0x00, 0x00));

                S("BrushSuccess", A(0xFF, 0x0F, 0x7B, 0x0F));
                S("BrushDanger", A(0xFF, 0xC4, 0x2B, 0x1C));
                S("BrushDangerFill", A(0xFF, 0xC4, 0x2B, 0x1C));
                S("BrushWarning", A(0xFF, 0x9D, 0x5D, 0x00));
            }
            S("BrushBackdropVeil", Colors.Transparent);

            WindowBase = dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3);
            S("BrushBase", WindowBase);
        }

        /// <summary>Whether a System / Light / Dark preference comes out dark right now.</summary>
        public static bool ResolveDark(string preference) =>
            preference == Dark || (preference != Light && SystemUsesDarkTheme());

        /// <summary>The handful of colours a theme draws a miniature of itself with (the theme pickers).</summary>
        public sealed class Swatch
        {
            public Color Bg, Layer, Card, Text, Accent, Stroke;
        }

        public static Swatch Preview(string preference)
        {
            bool dark = ResolveDark(preference);
            var accent = SystemAccent();
            return dark
                ? new Swatch { Bg = Color.FromRgb(0x20, 0x20, 0x20), Layer = Color.FromRgb(0x2B, 0x2B, 0x2B), Card = Color.FromRgb(0x36, 0x36, 0x36), Text = Colors.White,
                               Accent = Mix(accent, Colors.White, 0.42), Stroke = Color.FromRgb(0x44, 0x44, 0x44) }
                : new Swatch { Bg = Color.FromRgb(0xEE, 0xEE, 0xEE), Layer = Color.FromRgb(0xF9, 0xF9, 0xF9), Card = Colors.White, Text = Color.FromRgb(0x1A, 0x1A, 0x1A),
                               Accent = Mix(accent, Colors.Black, 0.18), Stroke = Color.FromRgb(0xD8, 0xD8, 0xD8) };
        }

        static readonly FontFamily SegoeText = new FontFamily("Segoe UI Variable Text, Segoe UI");
        static readonly FontFamily SegoeDisplay = new FontFamily("Segoe UI Variable Display, Segoe UI");
        static readonly FontFamily SegoeIcons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        /// <summary>Icon sizes the XAML and the code ask for, each published as an <c>IconSize*</c> token.</summary>
        public static readonly double[] IconSizes = { 9, 10, 11, 12, 12.5, 13, 14, 15, 16, 18, 20, 24 };

        public static string IconSizeKey(double size) => "IconSize" + size.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture).Replace('.', '_');

        static void ApplyTokens(ResourceDictionary r)
        {
            r["FontText"] = SegoeText;
            r["FontDisplay"] = SegoeDisplay;
            r["FontIcons"] = SegoeIcons;
            r["FontRailIcons"] = SegoeIcons;
            r["RailIconSize"] = 18.0;
            r["MeterHeight"] = 6.0;
            r["MeterSlim"] = 4.0;
            r["BaseFontSize"] = 14.0;
            r["HiWeight"] = FontWeights.SemiBold;
            r["TextMode"] = TextFormattingMode.Ideal;
            r["IconRender"] = TextRenderingMode.Auto;
            foreach (var s in IconSizes) r[IconSizeKey(s)] = s;

            r["RadiusNone"] = new CornerRadius(0);
            r["RadiusXs"] = new CornerRadius(3);
            r["RadiusSm"] = new CornerRadius(4);
            r["RadiusMd"] = new CornerRadius(6);
            r["RadiusLg"] = new CornerRadius(8);
            r["RadiusXl"] = new CornerRadius(10);
            r["RadiusPill"] = new CornerRadius(999);
            r["RadiusPaneTL"] = new CornerRadius(8, 0, 0, 0);
            r["RadiusBottomSm"] = new CornerRadius(0, 0, 4, 4);
            r["RadiusBottomLg"] = new CornerRadius(0, 0, 8, 8);
            r["RadiusBottomMd"] = new CornerRadius(0, 0, 7, 7);
            r["RadiusTopXs"] = new CornerRadius(3, 3, 0, 0);
            r["FocusRadius"] = 5.0;
            // A switch is the one shape that really is a pill: its radius has to be half its height or WPF draws an ellipse.
            r["RadiusSwitch"] = new CornerRadius(10);
            r["RadiusSwitchThumb"] = new CornerRadius(7);
            r["SwitchThumb"] = 12.0;
            r["SwitchThumbHot"] = 14.0;

            r["EffectPopup"] = Shadow(14, 3, 0.28);
            r["EffectTip"] = Shadow(8, 2, 0.22);
            r["EffectDialog"] = Shadow(30, 8, 0.35);
            r["EffectToast"] = Shadow(16, 3, 0.30);
        }

        static DropShadowEffect Shadow(double blur, double depth, double opacity)
        {
            var e = new DropShadowEffect { BlurRadius = blur, ShadowDepth = depth, Direction = 270, Opacity = opacity, Color = Colors.Black };
            e.Freeze();
            return e;
        }

        public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));

        static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

        static bool SystemUsesDarkTheme()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return !(k?.GetValue("AppsUseLightTheme") is int v && v == 1);
            }
            catch { return true; }
        }

        static Color SystemAccent()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    if (k?.GetValue("AccentColor") is int abgr)
                        return Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
                }
            }
            catch { }
            return Color.FromRgb(0x00, 0x78, 0xD4);
        }
    }
}
