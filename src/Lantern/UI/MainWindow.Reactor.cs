using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Lantern.Core;

namespace Lantern.UI
{
    /// <summary>
    /// Pulse → Board: every account is one square (the way a contribution grid is), filled with the system accent colour while its window
    /// runs and stronger the busier it is, above gauges for what all the windows cost together. It is made of the same cards as the
    /// rest of the app, so it follows light/dark and the accent colour; it shows no names (they are in the tooltips) and can be copied
    /// or saved as a picture.
    /// </summary>
    public partial class MainWindow
    {
        DispatcherTimer _reactorTimer;
        Border _reactorBoard;
        UniformGrid _rxCells;
        TextBlock _rxCount, _rxOf, _rxStatus, _rxStamp, _rxUptime, _rxEmpty;
        Dot _rxDot;
        Border _rxChip;
        Gauge _gCpu, _gMem, _gGpu;
        readonly List<RxCell> _rxList = new List<RxCell>();
        readonly List<RxCell> _rxLegend = new List<RxCell>();

        sealed class RxCell { public AccountItem Acc; public Border Box; public string Kind = ""; public int Level; }
        sealed class Gauge { public TextBlock Value, Note; public Border Fill; public ColumnDefinition Used, Free; }

        /// <summary>The accent colour at a strength from "quiet" (0) to "busy" (3), as a brush that is rebuilt when the theme changes.</summary>
        static Brush AccentLevel(int level)
        {
            Color c = Application.Current.Resources["ColorAccent"] is Color acc ? acc : Colors.DodgerBlue;
            var b = new SolidColorBrush(c) { Opacity = 0.34 + 0.22 * Math.Max(0, Math.Min(3, level)) };
            b.Freeze();
            return b;
        }

        void WireReactor()
        {
            _reactorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _reactorTimer.Tick += (s, e) => { if (_pulseView == "board") RefreshReactor(); };
            NavPulse.Unchecked += (s, e) => { _reactorTimer.Stop(); StopPulse(); };

            BuildReactorPane();

            // the shades are made from the accent colour, so they are painted again when the theme or accent changes
            Theme.Changed += () =>
            {
                foreach (var c in _rxList.Concat(_rxLegend)) Paint(c);
                if (_reactorBoard != null && _pulseView == "board") RefreshReactor();
            };
        }

        const string BoardCaption = "Every square is one account. A filled square is a running window, and a stronger shade means a busier one; hover a square to see whose it is. Copy it as a picture to share.";

        // the board
        void BuildReactorPane()
        {
            // card 1: how many run, the squares, the key
            var inner = new StackPanel();

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var countRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
            _rxCount = T("0", "BrushAccentText", 40, true); _rxCount.SetResourceReference(TextBlock.FontFamilyProperty, "FontDisplay");
            _rxOf = T(" / 0", "BrushTextTertiary", 20); _rxOf.Margin = new Thickness(0, 0, 10, 4); _rxOf.VerticalAlignment = VerticalAlignment.Bottom;
            var cap = T("running", "BrushTextSecondary", 13); cap.VerticalAlignment = VerticalAlignment.Bottom; cap.Margin = new Thickness(0, 0, 0, 8);
            countRow.Children.Add(_rxCount); countRow.Children.Add(_rxOf); countRow.Children.Add(cap);
            head.Children.Add(countRow);

            _rxDot = new Dot { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            _rxDot.SetResourceReference(Border.BackgroundProperty, "BrushTextTertiary");
            _rxStatus = T("No accounts", "BrushText", 12.5);
            var chipRow = new StackPanel { Orientation = Orientation.Horizontal };
            chipRow.Children.Add(_rxDot); chipRow.Children.Add(_rxStatus);
            _rxChip = new Box("RadiusMd") { Padding = new Thickness(12, 5, 14, 5), BorderThickness = new Thickness(1), Child = chipRow, VerticalAlignment = VerticalAlignment.Top };
            _rxChip.SetResourceReference(Border.BackgroundProperty, "BrushControl");
            _rxChip.SetResourceReference(Border.BorderBrushProperty, "BrushControlStroke");
            Grid.SetColumn(_rxChip, 1);
            head.Children.Add(_rxChip);
            inner.Children.Add(head);

            _rxCells = new UniformGrid { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-2.5, 16, 0, 0) };
            inner.Children.Add(_rxCells);
            _rxEmpty = T("Add accounts and they will show up here.", "BrushTextSecondary", 13); _rxEmpty.Margin = new Thickness(0, 18, 0, 6); _rxEmpty.Visibility = Visibility.Collapsed;
            inner.Children.Add(_rxEmpty);

            var legend = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
            void Key(string kind, int level, string text)
            {
                var sw = new Box("RadiusXs") { Width = 10, Height = 10, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                var cell = new RxCell { Box = sw, Kind = kind, Level = level };
                _rxLegend.Add(cell); Paint(cell);
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
                row.Children.Add(sw); row.Children.Add(T(text, "BrushTextSecondary", 11.5));
                legend.Children.Add(row);
            }
            Key("on", 0, "quiet"); Key("on", 3, "busy"); Key("starting", 0, "starting"); Key("attention", 0, "needs you"); Key("off", 0, "off");
            inner.Children.Add(legend);

            // card row 2: what all the windows cost together
            var gauges = new UniformGrid { Columns = 3, Margin = new Thickness(0, 10, -10, 0) };
            _gCpu = MakeGauge("CPU", gauges);
            _gMem = MakeGauge("Memory", gauges);
            _gGpu = MakeGauge("Graphics", gauges);

            // footer
            var foot = new Grid { Margin = new Thickness(2, 6, 2, 0) };
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _rxStamp = T("", "BrushTextTertiary", 11.5);
            _rxUptime = T("", "BrushTextTertiary", 11.5);
            Grid.SetColumn(_rxUptime, 1);
            foot.Children.Add(_rxStamp); foot.Children.Add(_rxUptime);

            var root = new StackPanel();
            root.Children.Add(CardBox(inner, new Thickness(0)));
            root.Children.Add(gauges);
            root.Children.Add(foot);

            _reactorBoard = new Border { MinWidth = 640, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left, Child = root };
            StatsLivePane.Children.Add(_reactorBoard);

            var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
            var copy = new Button { Content = "Copy as image", Margin = new Thickness(0, 0, 8, 6) };
            copy.Click += (s, e) => CopyReactorImage();
            var save = new Button { Content = "Save image…", Margin = new Thickness(0, 0, 8, 6) };
            save.Click += (s, e) => SaveReactorImage();
            buttons.Children.Add(copy); buttons.Children.Add(save);
            StatsLivePane.Children.Add(buttons);
            var note = T("The picture shows no account names. Hovering a square shows whose it is, which a screenshot doesn't capture.", "BrushTextTertiary", 12);
            note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 2, 0, 0);
            StatsLivePane.Children.Add(note);
        }

        Gauge MakeGauge(string label, UniformGrid host)
        {
            var g = new Gauge();
            var sp = new StackPanel();
            sp.Children.Add(T(label, "BrushTextSecondary", 12));
            g.Value = T("0%", "BrushText", 24, true); g.Value.SetResourceReference(TextBlock.FontFamilyProperty, "FontDisplay"); g.Value.Margin = new Thickness(0, 4, 0, 10);
            sp.Children.Add(g.Value);

            // a slim track with the used part filled in, like a Windows progress bar
            var track = new Grid();
            track.SetResourceReference(HeightProperty, "MeterSlim");
            g.Used = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
            g.Free = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
            track.ColumnDefinitions.Add(g.Used); track.ColumnDefinitions.Add(g.Free);
            var back = new Box("RadiusXs");
            back.SetResourceReference(Border.BackgroundProperty, "BrushStroke");
            Grid.SetColumnSpan(back, 2);
            track.Children.Add(back);
            g.Fill = new Box("RadiusXs");
            g.Fill.SetResourceReference(Border.BackgroundProperty, "BrushAccent");
            track.Children.Add(g.Fill);
            sp.Children.Add(track);

            g.Note = T("", "BrushTextTertiary", 11.5); g.Note.Margin = new Thickness(0, 8, 0, 0); g.Note.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(g.Note);
            host.Children.Add(CardBox(sp, new Thickness(0, 0, 10, 0), 14));
            return g;
        }

        // live values
        static string KindOf(AccountItem a)
        {
            switch (a.State)
            {
                case AccountState.Running: return "on";
                case AccountState.Queued:
                case AccountState.Launching:
                case AccountState.Rejoining:
                case AccountState.Closing:
                case AccountState.Checking: return "starting";
                case AccountState.Error:
                case AccountState.Expired: return "attention";
                default: return "off";
            }
        }

        /// <summary>0..3: how busy a window is. Thresholds are the share of the whole PC's CPU one window uses.</summary>
        static int LevelOf(AccountItem a) => !a.HasUsage ? 1 : a.Cpu < 0.5 ? 0 : a.Cpu < 1.0 ? 1 : a.Cpu < 2.0 ? 2 : 3;

        static readonly double[] CpuScales = { 5, 10, 25, 50, 100 };
        static double ScaleFor(double v) { foreach (var s in CpuScales) if (v * 1.25 <= s) return s; return 100; }

        void RefreshReactor()
        {
            if (_reactorBoard == null) return;
            var accounts = _mgr.Accounts.ToList();
            if (accounts.Count != _rxList.Count || !accounts.SequenceEqual(_rxList.Select(c => c.Acc))) RebuildCells(accounts);

            int running = 0, starting = 0, attention = 0;
            double cpu = 0, gpu = 0; bool anyGpu = false; long bytes = 0;
            TimeSpan longest = TimeSpan.Zero;
            var now = DateTime.UtcNow;

            for (int i = 0; i < _rxList.Count; i++)
            {
                var c = _rxList[i];
                var a = c.Acc;
                string kind = KindOf(a);
                int level = kind == "on" ? LevelOf(a) : 0;

                if (kind != c.Kind || level != c.Level)
                {
                    bool kindChanged = kind != c.Kind;
                    c.Kind = kind; c.Level = level;
                    Paint(c);
                    if (kindChanged) Pulse(c);
                }
                c.Box.ToolTip = a.Display + "\n" + a.StatusText + (kind == "on" && a.HasUsage ? "\n" + a.UsageText : "");

                if (kind == "on")
                {
                    running++;
                    if (a.HasUsage) { cpu += a.Cpu; bytes += a.WorkingSet; if (a.Gpu.HasValue) { gpu += a.Gpu.Value; anyGpu = true; } }
                    if (a.StartedAt.HasValue && now - a.StartedAt.Value > longest) longest = now - a.StartedAt.Value;
                }
                else if (kind == "starting") starting++;
                else if (kind == "attention") attention++;
            }

            int total = _rxList.Count;
            _rxEmpty.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
            _rxCells.Visibility = total == 0 ? Visibility.Collapsed : Visibility.Visible;
            _rxCount.Text = running.ToString(CultureInfo.CurrentCulture);
            _rxCount.SetResourceReference(TextBlock.ForegroundProperty, running > 0 ? "BrushAccentText" : "BrushTextTertiary");
            _rxOf.Text = " / " + total.ToString(CultureInfo.CurrentCulture);

            string status, dot;
            if (total == 0) { status = "No accounts"; dot = "BrushTextTertiary"; }
            else if (running == total) { status = "All running"; dot = "BrushAccent"; }
            else if (attention > 0) { status = attention == 1 ? "1 needs you" : attention + " need you"; dot = running > 0 ? "BrushWarning" : "BrushDangerFill"; }
            else if (running > 0) { status = "Running"; dot = "BrushAccent"; }
            else if (starting > 0) { status = "Starting up"; dot = "BrushWarning"; }
            else { status = "Idle"; dot = "BrushTextTertiary"; }
            _rxStatus.Text = status; _rxDot.SetResourceReference(Border.BackgroundProperty, dot);

            // gauges: CPU and GPU zoom to a readable scale (and say so); memory is always against the installed RAM.
            // The bar's colour comes from the real level, not from how far along the zoomed scale it is: warning and red mean the
            // PC is genuinely loaded, so a quiet 7% never shows a warning colour.
            double cpuTop = ScaleFor(cpu);
            SetGauge(_gCpu, Fmt.Percent(cpu), cpu / cpuTop, "of the whole PC  ·  scale 0–" + cpuTop.ToString("0", CultureInfo.CurrentCulture) + "%", ZoneOf(cpu / 100));

            Native.Memory(out var totalRam, out _);
            double memFrac = totalRam > 0 ? (double)bytes / totalRam : 0;
            SetGauge(_gMem, Fmt.Memory(bytes), memFrac, totalRam > 0 ? "of " + Fmt.Memory(totalRam) + " installed  ·  " + Math.Round(memFrac * 100).ToString("0", CultureInfo.CurrentCulture) + "%" : "", ZoneOf(memFrac));

            if (anyGpu) { double g = Math.Min(100, gpu), gTop = ScaleFor(g); SetGauge(_gGpu, Fmt.Percent(g), g / gTop, "3D engine  ·  scale 0–" + gTop.ToString("0", CultureInfo.CurrentCulture) + "%", ZoneOf(g / 100)); }
            else SetGauge(_gGpu, running > 0 ? "—" : "0%", 0, running > 0 ? "Windows has no GPU counters" : "3D engine", "BrushAccent");

            _rxStamp.Text = "Lantern  ·  " + DateTime.Now.ToString("d MMM yyyy  ·  HH:mm", CultureInfo.CurrentCulture);
            _rxUptime.Text = running > 0 ? "longest run " + Fmt.Duration(longest) : "";
        }

        /// <summary>Accent while the PC has room, warning when it is getting full, red when it is nearly out (share of the whole thing, 0..1).</summary>
        static string ZoneOf(double share) => share >= 0.85 ? "BrushDangerFill" : share >= 0.6 ? "BrushWarning" : "BrushAccent";

        static void SetGauge(Gauge g, string value, double fraction, string note, string zone)
        {
            g.Value.Text = value;
            g.Note.Text = note;
            double f = Math.Max(0, Math.Min(1, fraction));
            if (fraction > 0 && f < 0.01) f = 0.01;
            g.Used.Width = new GridLength(f, GridUnitType.Star);
            g.Free.Width = new GridLength(1 - f, GridUnitType.Star);
            g.Fill.Visibility = f > 0 ? Visibility.Visible : Visibility.Collapsed;
            g.Fill.SetResourceReference(Border.BackgroundProperty, zone);
        }

        // the squares
        static int ColumnsFor(int n)
        {
            if (n <= 8) return Math.Max(1, n);
            int best = 6; double bestScore = double.MaxValue;
            for (int cols = 6; cols <= 20; cols++)
            {
                int rows = (int)Math.Ceiling(n / (double)cols);
                double score = (rows * cols - n) * 3 + Math.Abs(cols / (double)rows - 2.6);
                if (score < bestScore) { bestScore = score; best = cols; }
            }
            return best;
        }

        void RebuildCells(List<AccountItem> accounts)
        {
            StopPulse();
            _rxCells.Children.Clear();
            _rxList.Clear();
            int n = accounts.Count;
            _rxEmpty.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
            _rxCells.Visibility = n == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (n == 0) return;

            // squares as big as suits the lantern, but never wider than the card's content (about 600 px)
            double cap = n <= 16 ? 56 : n <= 40 ? 52 : n <= 90 ? 34 : 20;
            int cols = n <= 40 ? ColumnsFor(n) : Math.Min(n, (int)(600 / (cap + 5)));
            _rxCells.Columns = cols;
            double size = Math.Max(14, Math.Min(cap, Math.Floor(600.0 / cols) - 5));
            foreach (var a in accounts)
            {
                var box = new Box(size > 20 ? "RadiusMd" : "RadiusSm") { Width = size, Height = size, Margin = new Thickness(2.5), BorderThickness = new Thickness(1) };
                var cell = new RxCell { Acc = a, Box = box };
                Paint(cell);
                _rxCells.Children.Add(box);
                _rxList.Add(cell);
            }
        }

        static void Paint(RxCell c)
        {
            switch (c.Kind)
            {
                case "on":
                    c.Box.Background = AccentLevel(c.Level);
                    c.Box.BorderBrush = Brushes.Transparent;
                    break;
                case "starting":
                    c.Box.SetResourceReference(Border.BackgroundProperty, "BrushWarning");
                    c.Box.BorderBrush = Brushes.Transparent;
                    break;
                case "attention":
                    c.Box.SetResourceReference(Border.BackgroundProperty, "BrushDangerFill");
                    c.Box.BorderBrush = Brushes.Transparent;
                    break;
                default:
                    c.Box.SetResourceReference(Border.BackgroundProperty, "BrushControl");
                    c.Box.SetResourceReference(Border.BorderBrushProperty, "BrushControlStroke");
                    break;
            }
        }

        /// <summary>A gentle blink on starting squares. Only while this view is on screen.</summary>
        static void Pulse(RxCell c)
        {
            c.Box.BeginAnimation(UIElement.OpacityProperty, null);
            c.Box.Opacity = 1;
            if (c.Kind != "starting") return;
            c.Box.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.45, 1.0, TimeSpan.FromSeconds(0.7))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase()
            });
        }

        void StopPulse()
        {
            foreach (var c in _rxList)
            {
                c.Box.BeginAnimation(UIElement.OpacityProperty, null);
                c.Box.Opacity = 1;
                c.Kind = "";   // so the next refresh paints and animates it again
            }
        }

        // share it
        /// <summary>The board as a picture, twice the screen size so it stays sharp when posted, on the window's own surface colour. Null when it isn't on screen.</summary>
        internal BitmapSource RenderReactorImage()
        {
            if (_reactorBoard == null || _reactorBoard.ActualWidth < 1 || _reactorBoard.ActualHeight < 1) return null;
            const double scale = 2, pad = 16;
            var size = new Size(_reactorBoard.ActualWidth + 2 * pad, _reactorBoard.ActualHeight + 2 * pad);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle((Brush)FindResource("BrushSurface"), null, new Rect(size));
                dc.DrawRectangle(new VisualBrush(_reactorBoard), null, new Rect(pad, pad, _reactorBoard.ActualWidth, _reactorBoard.ActualHeight));
            }
            var bmp = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }

        void CopyReactorImage()
        {
            var img = RenderReactorImage();
            if (img == null) { ShowToast("Open the Live view first.", true); return; }
            try
            {
                Clipboard.SetDataObject(new DataObject(DataFormats.Bitmap, img), true);
                ShowToast("Copied. Paste it anywhere.", false);
            }
            catch (Exception ex) { ShowToast("Couldn't copy the picture: " + ex.Message, true); }
        }

        void SaveReactorImage()
        {
            var img = RenderReactorImage();
            if (img == null) { ShowToast("Open the Live view first.", true); return; }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save the picture", Filter = "PNG image (*.png)|*.png", DefaultExt = ".png",
                FileName = "lantern-live-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".png"
            };
            if (dlg.ShowDialog(this) != true) return;
            SaveReactorImageTo(dlg.FileName, img);
        }

        internal bool SaveReactorImageTo(string path, BitmapSource img = null)
        {
            img = img ?? RenderReactorImage();
            if (img == null) return false;
            try
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(img));
                using (var f = File.Create(path)) enc.Save(f);
                ShowToast("Saved the picture.", false);
                return true;
            }
            catch (Exception ex) { ShowToast("Couldn't save the picture: " + ex.Message, true); return false; }
        }
    }
}
