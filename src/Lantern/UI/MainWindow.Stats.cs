using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Lantern.Core;

namespace Lantern.UI
{
    /// <summary>Pulse → Uptime: online time per account, per game and per day, and the latest sessions; plus the opt-in memory trim switch.</summary>
    public partial class MainWindow
    {
        DispatcherTimer _statsTimer;
        bool _statsNarrow;

        void WireStats()
        {
            StatsExportButton.Click += async (s, e) => await ExportStatsAsync();
            StatsClearButton.Click += async (s, e) => await ClearStatsAsync();

            // live: a running window's time keeps growing while the page is open
            _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _statsTimer.Tick += (s, e) => { if (UptimeShowing) RefreshStats(); };
            _statsTimer.Start();
            ContentPane.SizeChanged += (s, e) =>
            {
                if (UptimeShowing && IsStatsNarrow() != _statsNarrow) RefreshStats();
            };

            // memory trim (opt-in)
            TrimSwitch.IsChecked = _s.TrimMinimized;
            TrimSwitch.Checked += (s, e) => _mgr.EnableTrim(true);
            TrimSwitch.Unchecked += (s, e) => _mgr.EnableTrim(false);
            _mgr.TrimUnavailableChanged += () => TrimSwitch.IsChecked = false;

            WireReactor();
        }

        bool UptimeShowing => PagePulse.Visibility == Visibility.Visible && _pulseView == "uptime";

        bool IsStatsNarrow() => ContentPane.ActualWidth > 0 && ContentPane.ActualWidth < 760;

        // build the page
        internal void RefreshStats()
        {
            if (StatsBody == null || _pulseView != "uptime") return;   // the other views own the page's caption while they show
            var now = DateTime.UtcNow;
            var all = _mgr.Sessions.Snapshot();

            DateTime from;
            if (_s.PulseRange == "all")
            {
                from = all.Count > 0 ? all.Min(x => x.Start) : now.AddDays(-7);
                if (now - from < TimeSpan.FromHours(24)) from = now.AddHours(-24);
            }
            else from = PulseFrom(_s.PulseRange, now);
            var rep = StatsReport.Build(all, from, now, now);
            _statsNarrow = IsStatsNarrow();

            StatsBody.Children.Clear();
            if (all.Count == 0)
            {
                PulseCaption.Text = UptimeCaption;
                StatsBody.Children.Add(EmptyNote("Nothing recorded yet",
                    "From now on, every window Lantern launches is timed here: when it started, how long it ran, why it ended and how much memory it used. Windows that are already open when Lantern starts are counted from that moment."));
                return;
            }
            PulseCaption.Text = "Showing " + PulseRangeName(_s.PulseRange) + (rep.Sessions > 0 ? " · " + rep.Sessions + (rep.Sessions == 1 ? " session" : " sessions") + " across " + rep.Accounts.Count + (rep.Accounts.Count == 1 ? " account" : " accounts") : "");
            if (rep.Sessions == 0)
            {
                StatsBody.Children.Add(EmptyNote("No windows ran in " + PulseRangeName(_s.PulseRange), "Try a longer range, or launch an account."));
                return;
            }

            StatsBody.Children.Add(Tiles(rep));
            StatsBody.Children.Add(Section("Online time", UnitTitle(rep) , ChartCard(rep)));
            StatsBody.Children.Add(Section("Accounts", "Who was online the most.", AccountsTable(rep)));
            StatsBody.Children.Add(Section("Games", "Where the time went, and what a window costs in memory there.", GamesTable(rep)));
            StatsBody.Children.Add(Section("Recent sessions", "The latest windows in this range.", RecentTable(rep)));

            var note = T("Time counts from the moment Lantern saw a window start. A window that was already open when Lantern started counts from then, and its time stops if Lantern is closed. Memory is the average of what the window used while it was in view; trimmed windows are left out.",
                         "BrushTextTertiary", 12);
            note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 22, 0, 0);
            StatsBody.Children.Add(note);
        }

        static string UnitTitle(StatsReport rep) =>
            rep.BucketUnit == "hour" ? "Minutes online per hour, all accounts added up." :
            rep.BucketUnit == "week" ? "Hours online per week, all accounts added up." : "Hours online per day, all accounts added up.";

        // small builders
        TextBlock T(string text, string brush = "BrushText", double size = 14, bool bold = false, TextAlignment align = TextAlignment.Left)
        {
            var t = new TextBlock { Text = text, FontSize = size, TextAlignment = align, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Left };
            if (bold) t.FontWeight = FontWeights.SemiBold;
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return t;
        }

        Border CardBox(UIElement child, Thickness margin, double pad = 16)
        {
            var b = new Box("RadiusLg") { BorderThickness = new Thickness(1), Padding = new Thickness(pad), Margin = margin, Child = child };
            b.SetResourceReference(Border.BackgroundProperty, "BrushCardGlass");
            b.SetResourceReference(Border.BorderBrushProperty, "BrushControlStroke");
            return b;
        }

        UIElement EmptyNote(string title, string body)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 40, 0, 0), MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Left };
            var h = T(title, "BrushText", 16, true);
            h.SetResourceReference(TextBlock.FontFamilyProperty, "FontDisplay");
            sp.Children.Add(h);
            var b = T(body, "BrushTextSecondary", 13); b.TextWrapping = TextWrapping.Wrap; b.Margin = new Thickness(0, 8, 0, 0);
            sp.Children.Add(b);
            return sp;
        }

        UIElement Section(string title, string caption, UIElement body)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 26, 0, 0) };
            var h = T(title, "BrushText", 16, true);
            h.SetResourceReference(TextBlock.FontFamilyProperty, "FontDisplay");
            sp.Children.Add(h);
            var c = T(caption, "BrushTextSecondary", 12); c.Margin = new Thickness(0, 2, 0, 10); c.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(c);
            sp.Children.Add(body);
            return sp;
        }

        UIElement Tiles(StatsReport rep)
        {
            var wrap = new UniformGrid { Columns = _statsNarrow ? 2 : 3, Margin = new Thickness(0, 4, -10, 0) };
            void Tile(string label, string value, string sub, string tip = null)
            {
                var sp = new StackPanel();
                sp.Children.Add(T(label, "BrushTextSecondary", 12));
                var v = T(value, "BrushText", 24, true); v.SetResourceReference(TextBlock.FontFamilyProperty, "FontDisplay"); v.Margin = new Thickness(0, 4, 0, 0);
                sp.Children.Add(v);
                var subText = T(sub, "BrushTextTertiary", 12); subText.TextWrapping = TextWrapping.Wrap; subText.TextTrimming = TextTrimming.None; sp.Children.Add(subText);
                var card = CardBox(sp, new Thickness(0, 0, 10, 10), 14);
                if (tip != null) card.ToolTip = tip;
                wrap.Children.Add(card);
            }
            int pct = rep.Sessions > 0 ? (int)Math.Round(100.0 * rep.Unplanned / rep.Sessions) : 0;
            Tile("Online time", Fmt.Duration(rep.Total), "all windows added up", "Every window's running time summed, so two windows for an hour count as two hours.");
            Tile("Sessions", rep.Sessions.ToString(CultureInfo.CurrentCulture), "average " + Fmt.Duration(rep.Average));
            Tile("Longest run", Fmt.Duration(rep.Longest), "one window, no break");
            Tile("Most at once", rep.PeakAtOnce.ToString(CultureInfo.CurrentCulture), rep.PeakAtOnce == 1 ? "window" : "windows together");
            Tile("Ended unexpectedly", rep.Unplanned.ToString(CultureInfo.CurrentCulture), rep.Sessions > 0 ? pct + "% of sessions" : "", "Kicked, disconnected, crashed or signed in elsewhere, as read from Roblox's log.");
            Tile("Memory per window", rep.AvgMb > 0 ? Fmt.Memory(rep.AvgMb * 1024 * 1024) : "—", "average while in view");
            return wrap;
        }

        // chart
        static readonly double[] NiceSteps = { 1, 2, 3, 4, 5, 6, 8, 10, 12, 15, 20, 24, 30, 40, 48, 50, 60, 80, 100, 120, 150, 200, 250, 300, 400, 500, 600, 800, 1000, 1500, 2000, 3000, 5000, 10000 };

        static double NiceMax(double v)
        {
            foreach (var s in NiceSteps) if (v <= s) return s;
            return Math.Ceiling(v / 1000) * 1000;
        }

        UIElement ChartCard(StatsReport rep)
        {
            const double plotHeight = 150;
            var buckets = rep.Buckets;
            bool minutes = rep.BucketUnit == "hour";
            Func<Bucket, double> val = b => minutes ? b.Time.TotalMinutes : b.Time.TotalHours;
            double top = NiceMax(buckets.Count > 0 ? buckets.Max(val) : 0);
            string unit = minutes ? "min" : "h";

            var root = new StackPanel();
            var scale = T("0 – " + top.ToString("0", CultureInfo.CurrentCulture) + " " + unit, "BrushTextTertiary", 11.5);
            scale.HorizontalAlignment = HorizontalAlignment.Right; scale.Margin = new Thickness(0, 0, 0, 6);
            root.Children.Add(scale);

            var plot = new Grid { Height = plotHeight };
            // recessive gridlines at the top, the middle and the baseline
            foreach (var (va, op) in new[] { (VerticalAlignment.Top, 0.6), (VerticalAlignment.Center, 0.6), (VerticalAlignment.Bottom, 1.0) })
            {
                var line = new Rectangle { Height = 1, VerticalAlignment = va, Opacity = op, IsHitTestVisible = false };
                line.SetResourceReference(Shape.FillProperty, "BrushStroke");
                plot.Children.Add(line);
            }

            var cols = new UniformGrid { Rows = 1, Columns = Math.Max(1, buckets.Count) };
            double gap = buckets.Count > 40 ? 1 : 3;
            foreach (var b in buckets)
            {
                double v = val(b);
                var cell = new Grid { Background = Brushes.Transparent };
                var bar = new Box("RadiusTopXs")
                {
                    VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Stretch, MaxWidth = 40,
                    Margin = new Thickness(gap, 0, gap, 0),
                    Height = v <= 0 ? 0 : Math.Max(2, v / top * (plotHeight - 1)), Opacity = 0.85
                };
                bar.SetResourceReference(Border.BackgroundProperty, "BrushAccent");
                cell.Children.Add(bar);
                string when = rep.BucketUnit == "hour" ? b.StartLocal.ToString("d MMM HH:mm", CultureInfo.CurrentCulture)
                            : rep.BucketUnit == "week" ? "Week of " + b.StartLocal.ToString("d MMM", CultureInfo.CurrentCulture)
                            : b.StartLocal.ToString("dddd d MMM", CultureInfo.CurrentCulture);
                cell.ToolTip = when + "\n" + (b.Time.TotalSeconds < 1 ? "nobody online" : Fmt.Duration(b.Time) + " online");
                cell.MouseEnter += (s, e) => { bar.Opacity = 1; cell.Background = (Brush)FindResource("BrushRowHover"); };
                cell.MouseLeave += (s, e) => { bar.Opacity = 0.85; cell.Background = Brushes.Transparent; };
                cols.Children.Add(cell);
            }
            plot.Children.Add(cols);
            root.Children.Add(plot);

            // x labels: about eight of them, each starting under its bar
            int n = buckets.Count;
            if (n > 0)
            {
                int step = (int)Math.Ceiling(n / 8.0);
                var axis = new Grid { Margin = new Thickness(0, 6, 0, 0) };
                int groups = (int)Math.Ceiling(n / (double)step);
                for (int g = 0; g < groups; g++)
                {
                    int count = Math.Min(step, n - g * step);
                    axis.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(count, GridUnitType.Star) });
                    var label = T(buckets[g * step].Label, "BrushTextTertiary", 11.5);
                    label.TextTrimming = TextTrimming.None;
                    Grid.SetColumn(label, g);
                    axis.Children.Add(label);
                }
                root.Children.Add(axis);
            }
            return CardBox(root, new Thickness(0), 16);
        }

        // tables
        sealed class Col
        {
            public string Header = "";
            public GridLength Width;
            public double Min;
            public bool Right;
            public double PadLeft;
            public static Col Star(string h, double w, double min = 0, bool right = false) => new Col { Header = h, Width = new GridLength(w, GridUnitType.Star), Min = min, Right = right };
            public static Col Px(string h, double w, bool right = true) => new Col { Header = h, Width = new GridLength(w), Right = right };
        }

        Grid RowGrid(List<Col> cols)
        {
            var g = new Grid();
            foreach (var c in cols) g.ColumnDefinitions.Add(new ColumnDefinition { Width = c.Width, MinWidth = c.Min });
            return g;
        }

        UIElement Table(List<Col> cols, List<UIElement[]> rows)
        {
            var sp = new StackPanel();
            var head = RowGrid(cols); head.Height = 30;
            for (int i = 0; i < cols.Count; i++)
            {
                var h = T(cols[i].Header, "BrushTextTertiary", 12, false, cols[i].Right ? TextAlignment.Right : TextAlignment.Left);
                if (cols[i].Right) h.HorizontalAlignment = HorizontalAlignment.Right;
                h.Margin = new Thickness(cols[i].PadLeft, 0, 0, 0);
                Grid.SetColumn(h, i); head.Children.Add(h);
            }
            sp.Children.Add(head);
            sp.Children.Add(Rule());

            foreach (var cells in rows)
            {
                var g = RowGrid(cols); g.Height = 40;
                for (int i = 0; i < cells.Length; i++)
                {
                    if (cells[i] == null) continue;
                    if (cells[i] is FrameworkElement fe)
                    {
                        if (cols[i].Right) fe.HorizontalAlignment = HorizontalAlignment.Right;
                        if (cols[i].PadLeft > 0) fe.Margin = new Thickness(fe.Margin.Left + cols[i].PadLeft, fe.Margin.Top, fe.Margin.Right, fe.Margin.Bottom);
                    }
                    Grid.SetColumn(cells[i], i); g.Children.Add(cells[i]);
                }
                var host = new Border { Child = g, Background = Brushes.Transparent };
                host.MouseEnter += (s, e) => host.Background = (Brush)FindResource("BrushRowHover");
                host.MouseLeave += (s, e) => host.Background = Brushes.Transparent;
                sp.Children.Add(host);
                sp.Children.Add(Rule());
            }
            return sp;
        }

        UIElement Rule()
        {
            var r = new Rectangle { Height = 1 };
            r.SetResourceReference(Shape.FillProperty, "BrushStroke");
            return r;
        }

        /// <summary>A thin bar showing <paramref name="share"/> (0..1) of the track; one mark, one colour, rounded at the data end.</summary>
        UIElement ShareBar(double share, string tip)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = tip, Background = Brushes.Transparent };
            g.SetResourceReference(HeightProperty, "MeterHeight");
            share = Math.Max(0.001, Math.Min(1, share));
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(share, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - share + 0.0001, GridUnitType.Star) });
            var track = new Box("RadiusXs");
            track.SetResourceReference(Border.BackgroundProperty, "BrushStroke");
            Grid.SetColumnSpan(track, 2);
            g.Children.Add(track);
            var fill = new Box("RadiusXs");
            fill.SetResourceReference(Border.BackgroundProperty, "BrushAccent");
            g.Children.Add(fill);
            return g;
        }

        static string MemText(long mb) => mb > 0 ? Fmt.Memory(mb * 1024 * 1024) : "—";

        UIElement AccountsTable(StatsReport rep)
        {
            var cols = _statsNarrow
                ? new List<Col> { Col.Star("Account", 1, 120), Col.Px("Online", 84), Col.Px("Sessions", 70), Col.Px("Unexpected", 90) }
                : new List<Col> { Col.Star("Account", 1.2, 150), Col.Star("Share of the busiest", 1.3, 90), Col.Px("Online", 84), Col.Px("Sessions", 74), Col.Px("Longest", 84), Col.Px("Average", 84), Col.Px("Unexpected", 92), Col.Px("Memory", 84) };
            var rows = new List<UIElement[]>();
            foreach (var a in rep.Accounts)
            {
                var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                if (a.Online)
                {
                    var dot = new Dot { Width = 7, Height = 7, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Running now" };
                    dot.SetResourceReference(Border.BackgroundProperty, "BrushSuccess");
                    name.Children.Add(dot);
                }
                var nt = T(a.Name.Length > 0 ? a.Name : "Account " + a.UserId, "BrushText", 14, true); nt.MaxWidth = 220;
                name.Children.Add(nt);
                if (a.Online) { var on = T("  online", "BrushTextSecondary", 12); name.Children.Add(on); }

                var t = T(Fmt.Duration(a.Time), "BrushText", 14, false, TextAlignment.Right);
                var s = T(a.Sessions.ToString(CultureInfo.CurrentCulture), "BrushTextSecondary", 14, false, TextAlignment.Right);
                var un = T(a.Unplanned.ToString(CultureInfo.CurrentCulture), "BrushTextSecondary", 14, false, TextAlignment.Right);
                if (_statsNarrow) rows.Add(new UIElement[] { name, t, s, un });
                else rows.Add(new UIElement[]
                {
                    name, ShareBar(a.Share, Fmt.Duration(a.Time) + " online"), t, s,
                    T(Fmt.Duration(a.Longest), "BrushTextSecondary", 14, false, TextAlignment.Right),
                    T(Fmt.Duration(a.Average), "BrushTextSecondary", 14, false, TextAlignment.Right), un,
                    T(MemText(a.AvgMb), "BrushTextSecondary", 14, false, TextAlignment.Right)
                });
            }
            return Table(cols, rows);
        }

        UIElement GamesTable(StatsReport rep)
        {
            var cols = _statsNarrow
                ? new List<Col> { Col.Star("Game", 1, 120), Col.Px("Online", 84), Col.Px("Memory", 84) }
                : new List<Col> { Col.Star("Game", 1.2, 150), Col.Star("Share of the busiest", 1.3, 90), Col.Px("Online", 84), Col.Px("Sessions", 74), Col.Px("Memory", 84), Col.Px("Peak", 84) };
            var rows = new List<UIElement[]>();
            foreach (var g in rep.Games)
            {
                var name = T(g.Name, "BrushText", 14, true); name.MaxWidth = 260; name.Margin = new Thickness(0, 0, 8, 0);
                var t = T(Fmt.Duration(g.Time), "BrushText", 14, false, TextAlignment.Right);
                var mem = T(MemText(g.AvgMb), "BrushTextSecondary", 14, false, TextAlignment.Right);
                if (_statsNarrow) rows.Add(new UIElement[] { name, t, mem });
                else rows.Add(new UIElement[]
                {
                    name, ShareBar(g.Share, Fmt.Duration(g.Time) + " online"), t,
                    T(g.Sessions.ToString(CultureInfo.CurrentCulture), "BrushTextSecondary", 14, false, TextAlignment.Right), mem,
                    T(MemText(g.PeakMb), "BrushTextSecondary", 14, false, TextAlignment.Right)
                });
            }
            return Table(cols, rows);
        }

        static string WhenText(DateTime utc)
        {
            var l = utc.ToLocalTime();
            var today = DateTime.Now.Date;
            string day = l.Date == today ? "Today" : l.Date == today.AddDays(-1) ? "Yesterday" : l.ToString("d MMM", CultureInfo.CurrentCulture);
            return day + " " + l.ToString("HH:mm", CultureInfo.CurrentCulture);
        }

        UIElement RecentTable(StatsReport rep)
        {
            var now = DateTime.UtcNow;
            var cols = _statsNarrow
                ? new List<Col> { Col.Px("Started", 110, false), Col.Star("Account", 1, 90), Col.Px("Ran", 74), new Col { Header = "Ended", Width = new GridLength(150), PadLeft = 18 } }
                : new List<Col> { Col.Px("Started", 120, false), Col.Star("Account", 1, 110), Col.Star("Game", 1.2, 120), Col.Px("Ran", 80), new Col { Header = "Ended", Width = new GridLength(188), PadLeft = 18 } };
            var rows = new List<UIElement[]>();
            foreach (var r in rep.Recent)
            {
                var when = T(WhenText(r.Start) + (r.PickedUp ? "*" : ""), "BrushTextSecondary", 13);
                if (r.PickedUp) when.ToolTip = "Already open when Lantern started, so counted from then.";
                var who = T(r.Name, "BrushText", 14, true); who.Margin = new Thickness(0, 0, 8, 0);
                var ran = T(Fmt.Duration(r.End(now) - r.Start), "BrushText", 14, false, TextAlignment.Right);

                var end = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                if (r.Open)
                {
                    var dot = new Dot { Width = 7, Height = 7, Margin = new Thickness(0, 0, 8, 0) };
                    dot.SetResourceReference(Border.BackgroundProperty, "BrushSuccess");
                    end.Children.Add(dot);
                    end.Children.Add(T("Running", "BrushTextSecondary", 13));
                }
                else if (r.Unexpected)
                {
                    var warn = T("", "BrushDanger", 12); warn.SetResourceReference(TextBlock.FontFamilyProperty, "FontIcons"); warn.IconSize(12); warn.Margin = new Thickness(0, 0, 7, 0);
                    end.Children.Add(warn);
                    end.Children.Add(T(StatsReport.EndText(r.EndKind), "BrushDanger", 13));
                }
                else end.Children.Add(T(StatsReport.EndText(r.EndKind), "BrushTextSecondary", 13));

                if (_statsNarrow) rows.Add(new UIElement[] { when, who, ran, end });
                else
                {
                    var game = T(r.Game.Length > 0 ? r.Game : (r.PlaceId > 0 ? "Place " + r.PlaceId : "—"), "BrushTextSecondary", 14); game.Margin = new Thickness(0, 0, 8, 0);
                    rows.Add(new UIElement[] { when, who, game, ran, end });
                }
            }
            return Table(cols, rows);
        }

        // export / clear
        async Task ExportStatsAsync()
        {
            if (_mgr.Sessions.Count == 0) { ShowToast("There is nothing to export yet.", true); return; }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export session history", Filter = "CSV file (*.csv)|*.csv", DefaultExt = ".csv",
                FileName = "lantern-sessions-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".csv"
            };
            if (dlg.ShowDialog(this) != true) return;
            ExportStatsTo(dlg.FileName);
            await Task.CompletedTask;
        }

        internal bool ExportStatsTo(string path)
        {
            try
            {
                var all = _mgr.Sessions.Snapshot();
                File.WriteAllText(path, SessionLog.ToCsv(all, DateTime.UtcNow), new UTF8Encoding(true));   // BOM: Excel reads the names right
                ShowToast("Exported " + all.Count + (all.Count == 1 ? " session." : " sessions.") + " The file has names and times only, no cookies.", false);
                return true;
            }
            catch (Exception ex) { ShowToast("Couldn't write the file: " + ex.Message, true); return false; }
        }

        async Task ClearStatsAsync()
        {
            if (_mgr.Sessions.Count == 0) { ShowToast("There is no history to clear.", true); return; }
            if (!await ConfirmAsync("Clear the history?", "This forgets every recorded session. Windows that are open right now keep counting from their start.", "Clear", true)) return;
            _mgr.Sessions.Clear();
            RefreshStats();
            ShowToast("History cleared.", false);
        }
    }
}
