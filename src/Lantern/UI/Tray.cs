using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Lantern.Core;

namespace Lantern.UI
{
    /// <summary>One line of the tray quick-menu.</summary>
    public sealed class TrayEntry
    {
        public string Text = "";
        public string Glyph = "";
        public Action Click;
        public bool Header, Separator, Danger;
        public bool Enabled = true;

        public static TrayEntry Sep() => new TrayEntry { Separator = true };
        public static TrayEntry Head(string text) => new TrayEntry { Header = true, Text = text };
    }

    /// <summary>
    /// The quick menu that opens on right-click of the tray icon. A real WPF popup (not the old WinForms
    /// context menu) so it matches the rest of the app in light and dark.
    /// </summary>
    public sealed class TrayMenuWindow : Window
    {
        readonly DispatcherTimer _guard = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        public IReadOnlyList<Button> Buttons => _buttons;
        readonly List<Button> _buttons = new List<Button>();

        public TrayMenuWindow(IList<TrayEntry> entries)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            ShowActivated = true;
            FontFamily = (FontFamily)Application.Current.FindResource("FontText");
            SetResourceReference(FontSizeProperty, "BaseFontSize");
            SetResourceReference(ForegroundProperty, "BrushText");
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

            var stack = new StackPanel { MinWidth = 260 };
            foreach (var e in entries) stack.Children.Add(Build(e));

            var card = new Box("RadiusLg")
            {
                Margin = new Thickness(12),
                Padding = new Thickness(4),
                BorderThickness = new Thickness(1),
                Child = stack
            };
            card.SetResourceReference(Border.EffectProperty, "EffectPopup");
            card.SetResourceReference(Border.BackgroundProperty, "BrushSurface");
            card.SetResourceReference(Border.BorderBrushProperty, "BrushSurfaceStroke");
            Content = card;

            Deactivated += (s, e) => CloseSafe();
            KeyDown += (s, e) => { if (e.Key == Key.Escape) CloseSafe(); };
        }

        void CloseSafe() { try { if (IsVisible) Close(); } catch { } }

        UIElement Build(TrayEntry e)
        {
            if (e.Separator)
            {
                var r = new System.Windows.Shapes.Rectangle { Height = 1, Margin = new Thickness(8, 4, 8, 4) };
                r.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "BrushStroke");
                return r;
            }
            if (e.Header)
            {
                var h = new TextBlock { Text = e.Text, FontSize = 12, Margin = new Thickness(10, 8, 10, 4) };
                h.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextTertiary");
                return h;
            }

            var icon = new TextBlock { Text = e.Glyph, Width = 24, TextAlignment = TextAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(FrameworkElement.StyleProperty, "Icon");
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            icon.IconSize(14);
            var label = new TextBlock { Text = e.Text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 280 };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(icon);
            row.Children.Add(label);

            var b = new Button
            {
                Content = row,
                Tag = e,
                Height = 34,
                Margin = new Thickness(0, 1, 0, 1),
                Padding = new Thickness(10, 0, 10, 0),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                IsEnabled = e.Enabled
            };
            b.SetResourceReference(Button.StyleProperty, "SubtleButton");
            if (e.Danger) { icon.SetResourceReference(TextBlock.ForegroundProperty, "BrushDanger"); label.SetResourceReference(TextBlock.ForegroundProperty, "BrushDanger"); }
            b.Click += (s, ev) =>
            {
                CloseSafe();
                if (e.Click != null) Dispatcher.BeginInvoke(e.Click, DispatcherPriority.Background);
            };
            _buttons.Add(b);
            return b;
        }

        /// <summary>Opens the menu next to the mouse (where the tray icon was clicked), kept inside the screen.</summary>
        public void ShowAtCursor()
        {
            var cursor = System.Windows.Forms.Cursor.Position;
            var area = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
            double scale = 1.0;
            try { var src = PresentationSource.FromVisual(Application.Current.MainWindow); if (src?.CompositionTarget != null) scale = src.CompositionTarget.TransformToDevice.M11; } catch { }

            Left = -24000; Top = -24000;
            Show();
            UpdateLayout();
            double w = ActualWidth, h = ActualHeight;
            double ax = area.Left / scale, ay = area.Top / scale, aw = area.Width / scale, ah = area.Height / scale;
            double x = cursor.X / scale - w + 24, y = cursor.Y / scale - h + 12;
            Left = Math.Max(ax, Math.Min(x, ax + aw - w));
            Top = Math.Max(ay, Math.Min(y, ay + ah - h));
            Activate();
            Native.Foreground(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        }
    }

    /// <summary>The notification-area icon. Only created when the user switches the tray on, so it costs nothing otherwise.</summary>
    public sealed class TrayManager : IDisposable
    {
        readonly MainWindow _w;
        readonly System.Windows.Forms.NotifyIcon _icon;
        TrayMenuWindow _menu;

        public TrayManager(MainWindow w)
        {
            _w = w;
            System.Drawing.Icon ico;
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Lantern;component/Assets/lantern.ico"));
            using (res.Stream) ico = new System.Drawing.Icon(res.Stream, System.Windows.Forms.SystemInformation.SmallIconSize);

            _icon = new System.Windows.Forms.NotifyIcon { Icon = ico, Text = "Lantern", Visible = true };
            _icon.MouseUp += (s, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left) _w.ToggleFromTray();
                else if (e.Button == System.Windows.Forms.MouseButtons.Right) ShowMenu();
            };
        }

        public void SetTip(string text)
        {
            if (string.IsNullOrEmpty(text)) text = "Lantern";
            _icon.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        void ShowMenu()
        {
            try { _menu?.Close(); } catch { }
            _menu = new TrayMenuWindow(_w.BuildTrayEntries());
            _menu.ShowAtCursor();
        }

        public void Dispose()
        {
            try { _menu?.Close(); } catch { }
            _icon.Visible = false;
            _icon.Dispose();
        }
    }
}
