using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lantern.UI
{
    /// <summary>Settings → Appearance: the theme picker and the fade that plays when the theme changes.</summary>
    public partial class MainWindow
    {
        ThemeTransition _transition;
        readonly Dictionary<string, RadioButton> _themeTiles = new Dictionary<string, RadioButton>();
        readonly Dictionary<string, Border> _themeMocks = new Dictionary<string, Border>();
        bool _pickersReady;

        internal static readonly (string Value, string Name, string Caption)[] ThemeChoices =
        {
            (Theme.FollowSystem, "System", "Follows Windows, and switches when Windows does."),
            (Theme.Light, "Light", "Bright surfaces, dark text."),
            (Theme.Dark, "Dark", "Dark surfaces, easy on the eyes at night."),
        };

        void WireAppearance()
        {
            _transition = new ThemeTransition(RootGrid, TransitionLayer);

            MotionSwitch.IsChecked = _s.Animations;
            MotionSwitch.Checked += (s, e) => { _s.Animations = true; _mgr.SaveSettings(); };
            MotionSwitch.Unchecked += (s, e) => { _s.Animations = false; _transition.Cancel(); _mgr.SaveSettings(); };
            if (!SystemParameters.ClientAreaAnimation)
            {
                MotionSwitch.IsEnabled = false;
                MotionCaption.Text += " Windows has animations turned off right now.";
            }

            foreach (var choice in ThemeChoices)
            {
                var mock = new Border();
                var tile = ThemeTile(choice.Name, choice.Caption, mock);
                var value = choice.Value;
                tile.Checked += (s, e) => SetTheme(value);
                _themeTiles[value] = tile;
                _themeMocks[value] = mock;
                ThemeTiles.Children.Add(tile);
            }

            RefreshAppearancePickers();
        }

        void SetTheme(string value)
        {
            if (!_pickersReady || _s.Theme == value) return;
            _transition.Run(() =>
            {
                _s.Theme = value;
                Theme.Apply(_s.Theme);
                RefreshAppearancePickers();
                RefreshAfterLookChange();
            }, Motion.Enabled(_s));
            _mgr.SaveSettings();
        }

        /// <summary>Pages that are built in code redraw now, so nothing keeps the old look until its next refresh.</summary>
        void RefreshAfterLookChange()
        {
            ApplyResponsiveLayout();
            if (PagePulse.Visibility == Visibility.Visible)
            {
                if (_pulseView == "overview") RefreshPulse(); else if (_pulseView == "uptime") RefreshStats(); else RefreshReactor();
            }
            if (PageErrors.Visibility == Visibility.Visible) RenderErrors();
            if (PageLive.Visibility == Visibility.Visible) { RefreshLive(); RefreshLiveSide(); }
            RefreshSettingsView(false);
        }

        /// <summary>The tiles show the current choice, and the miniatures follow the system theme and accent colour.</summary>
        void RefreshAppearancePickers()
        {
            _pickersReady = false;
            var current = Theme.Normalize(_s.Theme);
            foreach (var kv in _themeTiles) kv.Value.IsChecked = kv.Key == current;
            foreach (var kv in _themeMocks) kv.Value.Child = Mock(Theme.Preview(kv.Key), 176, 104);
            _pickersReady = true;
        }

        internal static RadioButton ThemeTile(string name, string caption, Border mockHost)
        {
            var title = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) };
            var text = new TextBlock { Text = caption, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), MaxWidth = 176 };
            text.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary");
            var sp = new StackPanel();
            sp.Children.Add(mockHost); sp.Children.Add(title); sp.Children.Add(text);
            var tile = new RadioButton { GroupName = "themepick", Content = sp, Width = 200, ToolTip = caption };
            tile.SetResourceReference(StyleProperty, "Tile");
            return tile;
        }

        /// <summary>A little drawing of the window in a theme's colours: rail, header, rows, a button.</summary>
        internal static UIElement Mock(Theme.Swatch sw, double w, double h)
        {
            Brush B(Color c, byte a = 255) { var b = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)); b.Freeze(); return b; }
            var cv = new Canvas { Width = w, Height = h, ClipToBounds = true };
            void Rect(double x, double y, double cw, double ch, Brush fill, double radius, Brush stroke = null)
            {
                var b = new Border { Width = cw, Height = ch, Background = fill, CornerRadius = new CornerRadius(radius) };
                if (stroke != null) { b.BorderBrush = stroke; b.BorderThickness = new Thickness(1); }
                Canvas.SetLeft(b, x); Canvas.SetTop(b, y); cv.Children.Add(b);
            }
            cv.Children.Add(new Border { Width = w, Height = h, Background = B(sw.Bg), BorderBrush = B(sw.Stroke), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) });
            const double railW = 22;
            for (int i = 0; i < 4; i++) Rect(6, 10 + i * 17, 10, 10, i == 0 ? B(sw.Accent) : B(sw.Text, 70), 2);
            var pane = new Border { Width = w - railW - 1, Height = h - 8, Background = B(sw.Layer), BorderBrush = B(sw.Stroke), BorderThickness = new Thickness(1, 1, 0, 0), CornerRadius = new CornerRadius(6, 0, 0, 0) };
            Canvas.SetLeft(pane, railW); Canvas.SetTop(pane, 8); cv.Children.Add(pane);
            double x0 = railW + 10;
            Rect(x0, 16, 44, 7, B(sw.Text, 235), 2);
            Rect(x0, 27, 70, 4, B(sw.Text, 110), 2);
            Rect(w - 52, 15, 40, 12, B(sw.Accent), 2);
            for (int i = 0; i < 3; i++)
            {
                double y = 40 + i * 17;
                Rect(x0, y, w - x0 - 12, 14, B(sw.Card), 3, B(sw.Stroke));
                Rect(x0 + 6, y + 4, 8, 6, i == 0 ? B(sw.Accent) : B(sw.Text, 90), 3);
                Rect(x0 + 20, y + 5, 36, 4, B(sw.Text, 200), 2);
                Rect(w - 52, y + 4, 28, 6, B(sw.Text, 70), 2);
            }
            return new Border { Child = cv, CornerRadius = new CornerRadius(6), ClipToBounds = true };
        }
    }
}
