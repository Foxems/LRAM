using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Lantern.Core;

namespace Lantern.UI
{
    /// <summary>
    /// Pulse: is every window really inside a game, how often do launches work, how often do windows drop or crash (per
    /// window-hour), what did Lantern have to fix, and do the memory cap or the number of windows cost stability. Everything comes
    /// from Roblox's own client logs, Lantern's records and one batched presence request while the page is open; the Roblox
    /// client itself is never touched. The board can be copied as a picture, and a plain-JSON report is kept next to it.
    /// The page is the farm's one place for numbers: Overview (this), Uptime (MainWindow.Stats) and Board (MainWindow.Reactor),
    /// under one range.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>Which of the Pulse page's views shows: overview | uptime | board.</summary>
        string _pulseView = "overview";
        DispatcherTimer _pulseTimer, _pulseReportTimer;
        StackPanel _pulseBoard;
        int _pulseTick;
        bool _pulseNarrow;

        void WirePulse()
        {
            _pulseView = _s.PulseView == "uptime" || _s.PulseView == "board" ? _s.PulseView : "overview";
            if (_pulseView != "uptime" && _s.PulseRange == "all") _s.PulseRange = "30d";   // "All time" is offered by Uptime only
            var r = _s.PulseRange;
            PulseR1.IsChecked = r == "1h";
            PulseR7.IsChecked = r == "7d";
            PulseR30.IsChecked = r == "30d";
            PulseRAll.IsChecked = r == "all";
            PulseR24.IsChecked = r != "1h" && r != "7d" && r != "30d" && r != "all";
            PulseR1.Checked += (s, e) => SetPulseRange("1h");
            PulseR24.Checked += (s, e) => SetPulseRange("24h");
            PulseR7.Checked += (s, e) => SetPulseRange("7d");
            PulseR30.Checked += (s, e) => SetPulseRange("30d");
            PulseRAll.Checked += (s, e) => SetPulseRange("all");

            (_pulseView == "uptime" ? PulseViewUptime : _pulseView == "board" ? PulseViewBoard : PulseViewOverview).IsChecked = true;
            PulseViewOverview.Checked += (s, e) => SetPulseView("overview");
            PulseViewUptime.Checked += (s, e) => SetPulseView("uptime");
            PulseViewBoard.Checked += (s, e) => SetPulseView("board");

            PulseCopyButton.Click += (s, e) => CopyPulseImage();
            PulseSaveButton.Click += (s, e) => SavePulseImage();
            PulseFolderButton.Click += (s, e) => OpenPulseFolder();

            PulseReportSwitch.IsChecked = _s.PulseReportFile;
            PulseReportSwitch.Checked += (s, e) => { _s.PulseReportFile = true; _mgr.SaveSettings(); WritePulseReport(); UpdatePulseReportCaption(); };
            PulseReportSwitch.Unchecked += (s, e) => { _s.PulseReportFile = false; _mgr.SaveSettings(); UpdatePulseReportCaption(); };
            UpdatePulseReportCaption();

            // while the page is open: redraw every 5 s, ask Roblox's presence service once a minute
            _pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _pulseTimer.Tick += async (s, e) =>
            {
                if (PagePulse.Visibility != Visibility.Visible) { _pulseTimer.Stop(); return; }
                if (++_pulseTick % 12 == 0) await _mgr.RefreshPresenceAsync();
                if (_pulseView == "overview") RefreshPulse();
            };
            NavPulse.Unchecked += (s, e) => _pulseTimer.Stop();
            ContentPane.SizeChanged += (s, e) =>
            {
                if (PagePulse.Visibility == Visibility.Visible && IsStatsNarrow() != _pulseNarrow) RefreshPulse();
            };

            // the report file is kept up to date whether or not the page is open
            _pulseReportTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            _pulseReportTimer.Tick += (s, e) => WritePulseReport();
            _pulseReportTimer.Start();
        }

        async void OnPulseShown()
        {
            SetPulseView(_pulseView);
            _pulseTick = 0;
            _pulseTimer.Start();
            await _mgr.RefreshPresenceAsync();
            if (PagePulse.Visibility == Visibility.Visible) RefreshPulse();
        }

        const string OverviewCaption = "Is every window really inside a game, how often do they drop, and do your memory settings cost stability? Read from Roblox's own logs and Lantern's records only.";
        const string UptimeCaption = "How long each account has really been online, and how its windows ended.";

        void SetPulseView(string view)
        {
            _pulseView = view;
            if (_s.PulseView != view) { _s.PulseView = view; _mgr.SaveSettings(); }
            bool overview = view == "overview", uptime = view == "uptime", board = view == "board";
            PulseOverviewPane.Visibility = overview ? Visibility.Visible : Visibility.Collapsed;
            StatsBody.Visibility = uptime ? Visibility.Visible : Visibility.Collapsed;
            StatsLivePane.Visibility = board ? Visibility.Visible : Visibility.Collapsed;
            PulseRangeRow.Visibility = board ? Visibility.Collapsed : Visibility.Visible;
            PulseCopyButton.Visibility = PulseSaveButton.Visibility = overview ? Visibility.Visible : Visibility.Collapsed;
            StatsExportButton.Visibility = StatsClearButton.Visibility = uptime ? Visibility.Visible : Visibility.Collapsed;
            PulseRAll.Visibility = uptime ? Visibility.Visible : Visibility.Collapsed;
            if (!uptime && _s.PulseRange == "all") PulseR30.IsChecked = true;   // (redraws through SetPulseRange)

            if (!board) { _reactorTimer?.Stop(); StopPulse(); }
            if (overview) { PulseCaption.Text = OverviewCaption; RefreshPulse(); }
            else if (uptime) RefreshStats();
            else { PulseCaption.Text = BoardCaption; RefreshReactor(); _reactorTimer.Start(); }
        }

        void SetPulseRange(string r)
        {
            _s.PulseRange = r;
            _mgr.SaveSettings();
            if (_pulseView == "uptime") RefreshStats(); else RefreshPulse();
        }

        static string PulseRangeName(string r) => r == "1h" ? "the last hour" : r == "7d" ? "the last 7 days" : r == "30d" ? "the last 30 days" : r == "all" ? "all time" : "the last 24 hours";

        static DateTime PulseFrom(string r, DateTime now) =>
            r == "1h" ? now.AddHours(-1) : r == "7d" ? now.AddDays(-7) : r == "30d" || r == "all" ? now.AddDays(-30) : now.AddHours(-24);

        PulseReport BuildPulse(string range, DateTime now) =>
            PulseReport.Build(_mgr.Sessions.Snapshot(), _mgr.Sessions.EventsSnapshot(), PulseFrom(range, now), now, now);

        // the page
        internal void RefreshPulse()
        {
            if (PulseBody == null || _pulseView != "overview") return;
            var now = DateTime.UtcNow;
            var rep = BuildPulse(_s.PulseRange, now);
            var live = PulseNow.From(_mgr.Accounts, now);
            _pulseNarrow = IsStatsNarrow();

            PulseBody.Children.Clear();
            _pulseBoard = new StackPanel { Background = Brushes.Transparent };
            PulseBody.Children.Add(_pulseBoard);

            _pulseBoard.Children.Add(NowCard(live));

            if (rep.Empty)
            {
                _pulseBoard.Children.Add(EmptyNote("Nothing recorded in " + PulseRangeName(_s.PulseRange) + " yet",
                    "From now on Lantern notes when each window gets into a server, every launch and rejoin, why each window ended and the memory cap it ran under. The rates fill in as the farm runs; a few hours make them meaningful."));
                return;
            }

            _pulseBoard.Children.Add(PulseTiles(rep));
            _pulseBoard.Children.Add(Section("Over time", OverTimeCaption(rep), IncidentChart(rep)));
            _pulseBoard.Children.Add(Section("Why windows ended", "Every end that you or Lantern didn't ask for. Crashes and drops count as problems; idle kicks and server restarts are the game's doing.", ReasonsCard(rep)));
            _pulseBoard.Children.Add(Section("Does the setup hurt?", "The same rates for each memory cap and for how many windows ran at once. Rows with under 2 window-hours are greyed: too little to judge.", SetupCard(rep)));
            _pulseBoard.Children.Add(Section("Accounts to look at", "The accounts with the most trouble in this range, worst first.", AttentionTable(rep)));
            _pulseBoard.Children.Add(Section("Recent incidents", "Unplanned ends, failed launches and give-ups, newest first.", IncidentsTable(rep)));

            var stamp = T("Lantern Pulse  ·  " + PulseRangeName(_s.PulseRange) + "  ·  " + DateTime.Now.ToString("d MMM yyyy  HH:mm", CultureInfo.CurrentCulture), "BrushTextTertiary", 11.5);
            stamp.Margin = new Thickness(2, 18, 0, 0);
            _pulseBoard.Children.Add(stamp);

            var note = T("\"In a game\" counts from the moment Roblox's own log shows the account joining a server. Problems are counted per 10 window-hours, so 30 windows aren't judged against 15. Sources: Roblox's client logs (read-only), Lantern's own records, Windows' process list, and once a minute while this page is open one presence request through one of your accounts, the same one the website's friends list makes. Nothing inside Roblox is read or changed.",
                         "BrushTextTertiary", 12);
            note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 10, 0, 0);
            PulseBody.Children.Add(note);
        }

        // right now
        Border StatusChip(string text, string dotBrush)
        {
            var dot = new Dot { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(Border.BackgroundProperty, dotBrush);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(dot); row.Children.Add(T(text, "BrushText", 12.5));
            var chip = new Box("RadiusMd") { Padding = new Thickness(12, 5, 14, 5), BorderThickness = new Thickness(1), Child = row, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Right };
            chip.SetResourceReference(Border.BackgroundProperty, "BrushControl");
            chip.SetResourceReference(Border.BorderBrushProperty, "BrushControlStroke");
            return chip;
        }

        UIElement Pill(string brush, string label, int n, string tip)
        {
            var dot = new Dot { Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(Border.BackgroundProperty, brush);
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 6), ToolTip = tip, Background = Brushes.Transparent };
            sp.Children.Add(dot);
            sp.Children.Add(T(label + " ", "BrushTextSecondary", 12.5));
            sp.Children.Add(T(n.ToString(CultureInfo.CurrentCulture), n > 0 ? "BrushText" : "BrushTextTertiary", 12.5, true));
            return sp;
        }

        UIElement NowCard(PulseNow n)
        {
            var inner = new StackPanel();
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var count = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
            var big = T(n.InGame.ToString(CultureInfo.CurrentCulture), n.InGame > 0 ? "BrushAccentText" : "BrushTextTertiary", 40, true);
            big.SetResourceReference(TextBlock.FontFamilyProperty, "FontDisplay");
            var of = T(" / " + n.Accounts.ToString(CultureInfo.CurrentCulture), "BrushTextTertiary", 20); of.VerticalAlignment = VerticalAlignment.Bottom; of.Margin = new Thickness(0, 0, 10, 4);
            var cap = T("in a game right now", "BrushTextSecondary", 13); cap.VerticalAlignment = VerticalAlignment.Bottom; cap.Margin = new Thickness(0, 0, 0, 8);
            count.Children.Add(big); count.Children.Add(of); count.Children.Add(cap);
            head.Children.Add(count);

            int trouble = n.Attention + n.Still + n.Mismatch + n.NotJoined;
            string status, dot;
            if (n.Accounts == 0) { status = "No accounts"; dot = "BrushTextTertiary"; }
            else if (trouble > 0) { status = trouble == 1 ? "1 needs a look" : trouble + " need a look"; dot = n.Attention > 0 || n.Still > 0 ? "BrushDangerFill" : "BrushWarning"; }
            else if (n.Running > 0 && n.InGame == n.Running && n.Starting == 0) { status = n.Running == n.Accounts ? "All in a game" : "Every running window is in a game"; dot = "BrushSuccess"; }
            else if (n.Running > 0 || n.Starting > 0) { status = "Getting there"; dot = "BrushWarning"; }
            else { status = "Nothing running"; dot = "BrushTextTertiary"; }
            var chip = StatusChip(status, dot);
            Grid.SetColumn(chip, 1);
            head.Children.Add(chip);
            inner.Children.Add(head);

            // one bar, one segment per state, in the order a window moves through them
            var bar = new Grid { Height = 8 };
            void Seg(int k, string brush, string tip)
            {
                if (k <= 0) return;
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(k, GridUnitType.Star) });
                var b = new Box("RadiusXs") { Margin = new Thickness(0, 0, 2, 0), ToolTip = tip };
                b.SetResourceReference(Border.BackgroundProperty, brush);
                Grid.SetColumn(b, bar.ColumnDefinitions.Count - 1);
                bar.Children.Add(b);
            }
            int off = Math.Max(0, n.Accounts - n.Running - n.Starting - n.Attention);
            Seg(n.InGame, "BrushAccent", n.InGame + " in a game");
            Seg(n.Unconfirmed, "BrushTextTertiary", n.Unconfirmed + " found already open, not confirmed yet");
            Seg(n.Loading + n.NotJoined, "BrushWarning", (n.Loading + n.NotJoined) + " up, not in a server yet");
            Seg(n.Starting, "BrushTextTertiary", n.Starting + " starting or rejoining");
            Seg(n.Attention, "BrushDangerFill", n.Attention + " need you");
            Seg(off, "BrushStroke", off + " off");
            if (bar.ColumnDefinitions.Count == 0) Seg(1, "BrushStroke", "nothing to show");
            var clip = new Box("RadiusSm") { Child = bar, ClipToBounds = true, Margin = new Thickness(0, 14, 0, 12) };
            clip.SetResourceReference(Border.BackgroundProperty, "BrushStroke");
            inner.Children.Add(clip);

            var pills = new WrapPanel();
            pills.Children.Add(Pill("BrushAccent", "In a game", n.InGame, "Roblox's log shows these accounts inside a server."));
            pills.Children.Add(Pill("BrushWarning", "Loading", n.Loading, "The window is up but hasn't joined a server yet."));
            pills.Children.Add(Pill("BrushTextTertiary", "Starting / rejoining", n.Starting, "Queued, launching, or waiting to rejoin."));
            pills.Children.Add(Pill("BrushDangerFill", "Needs you", n.Attention, "Failed launches, expired sign-ins, or auto-rejoin gave up."));
            pills.Children.Add(Pill("BrushDangerFill", "Frozen", n.Still, "Running but using no CPU at all for 90 s, which a live client never does."));
            if (n.Unconfirmed > 0) pills.Children.Add(Pill("BrushTextTertiary", "Not confirmed yet", n.Unconfirmed, "Already open when Lantern started. Its log or Roblox's presence check will confirm the server shortly."));
            pills.Children.Add(Pill("BrushWarning", "No join seen", n.NotJoined, "Up for " + (int)InstanceManager.JoinWatchFor.TotalMinutes + "+ minutes without its log showing a server."));
            inner.Children.Add(pills);

            // what Roblox itself says
            string presence;
            if (_mgr.PresenceCheckedAt == null || n.PresenceKnown == 0)
                presence = n.Running > 0 ? "Asking Roblox which accounts it sees in a game…" : "Roblox's presence check runs once a minute while this page is open.";
            else
            {
                int ago = (int)(DateTime.UtcNow - _mgr.PresenceCheckedAt.Value).TotalSeconds;
                presence = "Roblox confirms " + n.Confirmed + " of " + n.PresenceKnown + " in a game" + (n.Mismatch > 0 ? "  ·  " + n.Mismatch + " it doesn't see in one" : "") +
                           "  ·  checked " + (ago < 60 ? ago + " s" : ago / 60 + " min") + " ago";
            }
            var pres = T(presence, n.Mismatch > 0 ? "BrushWarning" : "BrushTextSecondary", 12.5); pres.TextWrapping = TextWrapping.Wrap; pres.Margin = new Thickness(0, 4, 0, 0);
            inner.Children.Add(pres);

            if (n.Flags.Count > 0)
            {
                var flags = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
                foreach (var (name, what) in n.Flags.Take(6))
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                    var warn = T("", "BrushWarning", 12); warn.SetResourceReference(TextBlock.FontFamilyProperty, "FontIcons"); warn.IconSize(12); warn.Margin = new Thickness(0, 0, 8, 0);
                    row.Children.Add(warn);
                    row.Children.Add(T(name, "BrushText", 13, true));
                    row.Children.Add(T("   " + what, "BrushTextSecondary", 13));
                    flags.Children.Add(row);
                }
                if (n.Flags.Count > 6) flags.Children.Add(T("and " + (n.Flags.Count - 6) + " more", "BrushTextTertiary", 12));
                inner.Children.Add(flags);
            }

            // what the windows cost and how they are set up
            Native.Memory(out var total, out var avail);
            var setup = new List<string>();
            if (n.Running > 0) setup.Add("Roblox: CPU " + Fmt.Percent(n.Cpu) + "  ·  RAM " + Fmt.Memory(n.WorkingSet) + (n.Private > 0 ? "  ·  reserved " + Fmt.Memory(n.Private) : ""));
            if (total > 0) setup.Add("free RAM " + Fmt.Memory((long)avail) + " of " + Fmt.Memory((long)total));
            setup.Add((_s.CapMinimized ? PulseReport.CapLabel(_mgr.CapMbNow).ToLowerInvariant() : "no cap") + (_s.TrimMinimized ? " + trim" : "") + "  ·  " + (_s.PerfMode ?? "Off") + " mode");
            var foot = T(string.Join("     ", setup), "BrushTextTertiary", 12); foot.TextWrapping = TextWrapping.Wrap; foot.Margin = new Thickness(0, 12, 0, 0);
            inner.Children.Add(foot);

            return CardBox(inner, new Thickness(0, 4, 0, 0));
        }

        // the rates
        /// <summary>good | warn | bad, for a value where higher is better (shares 0..1).</summary>
        static string ZoneHigh(double? v, double good, double ok) => !v.HasValue ? "none" : v.Value >= good ? "good" : v.Value >= ok ? "warn" : "bad";
        static string ZoneLow(double? v, double good, double ok) => !v.HasValue ? "none" : v.Value <= good ? "good" : v.Value <= ok ? "warn" : "bad";
        /// <summary>"97.4%", rounded down so 99.96% never reads as a perfect 100%; a tiny non-zero share reads "&lt;0.1%".</summary>
        static string Pct(double? share)
        {
            if (!share.HasValue) return "—";
            double v = share.Value;
            if (v > 0 && v < 0.001) return "<" + 0.1.ToString("0.0", CultureInfo.CurrentCulture) + "%";
            double shown = v >= 1 ? 100 : Math.Floor(v * 1000) / 10;
            return shown.ToString("0.#", CultureInfo.CurrentCulture) + "%";
        }

        UIElement PulseTiles(PulseReport rep)
        {
            var wrap = new UniformGrid { Columns = _pulseNarrow ? 2 : 4, Margin = new Thickness(0, 10, -10, 0) };
            void Tile(string label, string value, string sub, string zone, string tip)
            {
                var sp = new StackPanel();
                var top = new StackPanel { Orientation = Orientation.Horizontal };
                if (zone != "none")
                {
                    var dot = new Dot { Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
                    dot.SetResourceReference(Border.BackgroundProperty, zone == "good" ? "BrushSuccess" : zone == "warn" ? "BrushWarning" : "BrushDangerFill");
                    top.Children.Add(dot);
                }
                top.Children.Add(T(label, "BrushTextSecondary", 12));
                sp.Children.Add(top);
                var v = T(value, zone == "bad" ? "BrushDanger" : "BrushText", 24, true); v.SetResourceReference(TextBlock.FontFamilyProperty, "FontDisplay"); v.Margin = new Thickness(0, 4, 0, 0);
                sp.Children.Add(v);
                var s = T(sub, "BrushTextTertiary", 12); s.TextWrapping = TextWrapping.Wrap; s.TextTrimming = TextTrimming.None;
                sp.Children.Add(s);
                var card = CardBox(sp, new Thickness(0, 0, 10, 10), 14);
                card.ToolTip = tip;
                wrap.Children.Add(card);
            }

            double hours = rep.WindowHours;
            string per(int k) => hours >= 0.5 ? (k / hours).ToString("0.0", CultureInfo.CurrentCulture) + " per window-hour" : k == 1 ? "1 time" : k + " times";

            Tile("In a game", Pct(rep.InGameShare),
                 rep.InGameShare.HasValue ? Fmt.Duration(rep.InGame) + " of " + Fmt.Duration(rep.Tracked) + " window time" : "recorded from now on",
                 ZoneHigh(rep.InGameShare, 0.97, 0.9), "How much of the time the windows were up they were really inside a server, as Roblox's log shows. Loading screens, error screens and the time before a join count against it.");
            Tile("Launches that worked", Pct(rep.LaunchSuccess),
                 rep.Launches > 0 ? rep.LaunchesOk + " of " + rep.Launches + (rep.MedianJoinSeconds.HasValue ? "  ·  joins in " + Math.Round(rep.MedianJoinSeconds.Value) + " s" : "") : "no launches in this range",
                 ZoneHigh(rep.LaunchSuccess, 0.98, 0.9), "Launches and rejoins that ended with a Roblox window, and the typical time from pressing Launch to being inside a server.");
            Tile("Problems per 10 h", rep.ProblemsPer10h.HasValue ? rep.ProblemsPer10h.Value.ToString("0.0", CultureInfo.CurrentCulture) : "—",
                 rep.Problems == 0 ? (hours >= 0.25 ? "none in " + Fmt.Duration(rep.WindowTime) : "too little time yet") : "one every " + Fmt.Duration(rep.BetweenProblems.Value) + " of window time",
                 ZoneLow(rep.ProblemsPer10h, 0.5, 2), "Crashes and drops (lost connection, kicked, couldn't connect, signed in elsewhere) per 10 hours of window time, all windows added up. Idle kicks and server restarts aren't counted here.");
            Tile("Lost to rejoins", Fmt.Duration(rep.LostToRejoins),
                 rep.LostShare.HasValue ? Pct(rep.LostShare) + " of farm time" : "",
                 ZoneLow(rep.LostShare, 0.01, 0.04), "From the moment a window ended unexpectedly until the account was back inside a server, all accounts added up.");
            Tile("Auto rejoins", rep.AutoRejoins.ToString(CultureInfo.CurrentCulture), rep.AutoRejoins > 0 ? per(rep.AutoRejoins) : "Lantern didn't have to step in", "none",
                 "Windows Lantern reopened after a crash, kick or lost connection.");
            Tile("Idle-kick rejoins", rep.IdleRejoins.ToString(CultureInfo.CurrentCulture), rep.IdleKicks + " idle kick" + (rep.IdleKicks == 1 ? "" : "s") + " in this range", "none",
                 "Roblox removes an account after 20 minutes without input (code 278). With “Rejoin after an idle kick” on, Lantern reopens it.");
            Tile("Crashes", rep.Crashes.ToString(CultureInfo.CurrentCulture),
                 rep.Crashes == 0 ? "no crash or out-of-memory" : string.Join(", ", rep.Reasons.Where(x => x.Class == EndClass.Crash).Select(x => x.Count + " " + x.Text.ToLowerInvariant())),
                 rep.Crashes == 0 ? "good" : hours > 0 && rep.Crashes / hours * 10 <= 0.5 ? "warn" : "bad",
                 "Windows that died: crashed or force-closed, out of memory (code 286), or removed for unexpected client behaviour (code 268). If these rise after lowering the memory cap, the cap is too tight.");
            int needed = rep.GaveUp + rep.NeverJoined;
            Tile("Needed you", needed.ToString(CultureInfo.CurrentCulture),
                 "gave up " + rep.GaveUp + "  ·  never joined " + rep.NeverJoined + "  ·  failed " + (rep.Launches - rep.LaunchesOk),
                 needed == 0 ? "good" : "warn",
                 "Accounts Lantern stopped rejoining after repeated quick failures, and windows that ran 5+ minutes without getting into a server.");
            return wrap;
        }

        // over time
        static string OverTimeCaption(PulseReport rep) =>
            rep.BucketUnit == "5min" ? "Problems, idle kicks and failed launches per 5 minutes." :
            rep.BucketUnit == "hour" ? "Problems, idle kicks and failed launches per hour." : "Problems, idle kicks and failed launches per day.";

        UIElement IncidentChart(PulseReport rep)
        {
            const double plotHeight = 120;
            var buckets = rep.Buckets;
            int peak = buckets.Count > 0 ? buckets.Max(b => b.Problems + b.IdleKicks + b.Failed) : 0;
            var root = new StackPanel();

            var legend = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            void Key(string brush, string text)
            {
                var sw = new Box("RadiusXs") { Width = 10, Height = 10, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                sw.SetResourceReference(Border.BackgroundProperty, brush);
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
                row.Children.Add(sw); row.Children.Add(T(text, "BrushTextSecondary", 11.5));
                legend.Children.Add(row);
            }
            Key("BrushDangerFill", "problems"); Key("BrushWarning", "idle kicks"); Key("BrushTextTertiary", "failed launches");
            var head = new Grid();
            head.Children.Add(legend);
            var scale = T(peak > 0 ? "0 – " + NiceMax(peak).ToString("0", CultureInfo.CurrentCulture) : "", "BrushTextTertiary", 11.5);
            scale.HorizontalAlignment = HorizontalAlignment.Right; scale.VerticalAlignment = VerticalAlignment.Top;
            head.Children.Add(scale);
            root.Children.Add(head);

            if (peak == 0)
            {
                var ok = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
                var tick = T("", "BrushSuccess", 14); tick.SetResourceReference(TextBlock.FontFamilyProperty, "FontIcons"); tick.IconSize(14); tick.Margin = new Thickness(0, 0, 10, 0);
                ok.Children.Add(tick);
                ok.Children.Add(T("Nothing went wrong in " + PulseRangeName(_s.PulseRange) + ".", "BrushText", 14));
                root.Children.Add(ok);
                return CardBox(root, new Thickness(0), 16);
            }

            double top = NiceMax(peak);
            var plot = new Grid { Height = plotHeight };
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
                var cell = new Grid { Background = Brushes.Transparent };
                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(gap, 0, gap, 0), MaxWidth = 40 };
                void Part(int k, string brush, bool topmost)
                {
                    if (k <= 0) return;
                    var p = new Box(topmost ? "RadiusTopXs" : "RadiusNone") { Height = Math.Max(2, k / top * (plotHeight - 1)), Margin = new Thickness(0, topmost ? 0 : 1, 0, 0) };
                    p.SetResourceReference(Border.BackgroundProperty, brush);
                    stack.Children.Add(p);
                }
                // drawn top to bottom: failed launches, idle kicks, problems at the base
                bool f = b.Failed > 0, i = b.IdleKicks > 0;
                Part(b.Failed, "BrushTextTertiary", true);
                Part(b.IdleKicks, "BrushWarning", !f);
                Part(b.Problems, "BrushDangerFill", !f && !i);
                cell.Children.Add(stack);
                string when = rep.BucketUnit == "day" ? b.StartLocal.ToString("dddd d MMM", CultureInfo.CurrentCulture) : b.StartLocal.ToString("d MMM HH:mm", CultureInfo.CurrentCulture) + "–" + b.EndLocal.ToString("HH:mm", CultureInfo.CurrentCulture);
                cell.ToolTip = when + "\n" + b.Problems + " problems · " + b.IdleKicks + " idle kicks · " + b.Failed + " failed launches\n" + Fmt.Duration(b.WindowTime) + " window time";
                cell.MouseEnter += (s, e) => cell.Background = (Brush)FindResource("BrushRowHover");
                cell.MouseLeave += (s, e) => cell.Background = Brushes.Transparent;
                cols.Children.Add(cell);
            }
            plot.Children.Add(cols);
            root.Children.Add(plot);

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

        // why windows ended
        static string ClassBrush(string cls) =>
            cls == EndClass.Crash || cls == "GaveUp" ? "BrushDangerFill" : cls == EndClass.Drop || cls == "Failed" ? "BrushWarning" : cls == EndClass.Idle ? "BrushAccent" : "BrushTextTertiary";

        UIElement ReasonsCard(PulseReport rep)
        {
            var sp = new StackPanel();
            if (rep.Reasons.Count == 0)
            {
                sp.Children.Add(T("No window ended unexpectedly.", "BrushText", 14));
            }
            else
            {
                foreach (var r in rep.Reasons)
                {
                    var g = new Grid { Height = 34 };
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_pulseNarrow ? 150 : 210) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
                    var name = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                    name.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    name.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var dot = new Dot { Width = 8, Height = 8, Margin = new Thickness(0, 0, 10, 0) };
                    dot.SetResourceReference(Border.BackgroundProperty, ClassBrush(r.Class));
                    name.Children.Add(dot);
                    var label = T(r.Text, "BrushText", 13.5); label.ToolTip = r.Text;
                    Grid.SetColumn(label, 1); name.Children.Add(label);
                    g.Children.Add(name);
                    var track = new Grid { Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center };
                    track.SetResourceReference(HeightProperty, "MeterHeight");
                    double share = Math.Max(0.01, Math.Min(1, r.Share));
                    track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(share, GridUnitType.Star) });
                    track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - share + 0.0001, GridUnitType.Star) });
                    var back = new Box("RadiusXs"); back.SetResourceReference(Border.BackgroundProperty, "BrushStroke"); Grid.SetColumnSpan(back, 2);
                    var fill = new Box("RadiusXs"); fill.SetResourceReference(Border.BackgroundProperty, ClassBrush(r.Class));
                    track.Children.Add(back); track.Children.Add(fill);
                    Grid.SetColumn(track, 1); g.Children.Add(track);
                    var c = T(r.Count.ToString(CultureInfo.CurrentCulture), "BrushText", 13.5, true, TextAlignment.Right); c.HorizontalAlignment = HorizontalAlignment.Right;
                    Grid.SetColumn(c, 2); g.Children.Add(c);
                    var p = T(Pct(r.Share), "BrushTextSecondary", 13, false, TextAlignment.Right); p.HorizontalAlignment = HorizontalAlignment.Right;
                    Grid.SetColumn(p, 3); g.Children.Add(p);
                    sp.Children.Add(g);
                }
            }
            if (rep.Planned > 0)
            {
                var foot = T("Not counted: " + rep.Planned + " closed by you or Lantern.", "BrushTextTertiary", 12);
                foot.Margin = new Thickness(0, 8, 0, 0);
                sp.Children.Add(foot);
            }
            return CardBox(sp, new Thickness(0), 16);
        }

        // does the setup hurt?
        UIElement VerdictLine(PulseVerdict v)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = T(v.Level == "ok" ? "" : v.Level == "warn" ? "" : "", v.Level == "ok" ? "BrushSuccess" : v.Level == "warn" ? "BrushWarning" : "BrushTextSecondary", 13);
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "FontIcons"); icon.IconSize(13); icon.Margin = new Thickness(0, 2, 10, 0); icon.VerticalAlignment = VerticalAlignment.Top;
            row.Children.Add(icon);
            var t = T(v.Text, "BrushText", 13.5); t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None;
            Grid.SetColumn(t, 1); row.Children.Add(t);
            return row;
        }

        UIElement GroupTable(string first, List<PulseGroup> groups, bool withInGame)
        {
            var cols = _pulseNarrow
                ? new List<Col> { Col.Star(first, 1, 100), Col.Px("Time", 76), Col.Px("Problems", 84), Col.Px("Crashes", 76) }
                : withInGame
                    ? new List<Col> { Col.Star(first, 1, 130), Col.Px("Window time", 104), Col.Px("In a game", 92), Col.Px("Problems / 10 h", 124), Col.Px("Crashes / 10 h", 116), Col.Px("Join", 70) }
                    : new List<Col> { Col.Star(first, 1, 130), Col.Px("Window time", 104), Col.Px("Problems / 10 h", 124), Col.Px("Crashes / 10 h", 116), Col.Px("Idle kicks", 92) };
            string Rate(double? v) => v.HasValue ? v.Value.ToString("0.0", CultureInfo.CurrentCulture) : "—";
            var rows = new List<UIElement[]>();
            foreach (var g in groups)
            {
                string tone = g.LittleData ? "BrushTextTertiary" : "BrushText";
                string tone2 = g.LittleData ? "BrushTextTertiary" : "BrushTextSecondary";
                var label = T(g.Label + (g.LittleData ? "  (little data)" : ""), tone, 14, !g.LittleData);
                var time = T(Fmt.Duration(g.WindowTime), tone2, 14, false, TextAlignment.Right);
                var probs = T(Rate(g.ProblemsPer10h), g.LittleData ? tone2 : ZoneLow(g.ProblemsPer10h, 0.5, 2) == "bad" ? "BrushDanger" : "BrushText", 14, false, TextAlignment.Right);
                var crashes = T(Rate(g.CrashesPer10h), tone2, 14, false, TextAlignment.Right);
                if (_pulseNarrow) rows.Add(new UIElement[] { label, time, probs, crashes });
                else if (withInGame)
                    rows.Add(new UIElement[] { label, time, T(Pct(g.InGameShare), tone2, 14, false, TextAlignment.Right), probs, crashes,
                                               T(g.MedianJoinSeconds.HasValue ? Math.Round(g.MedianJoinSeconds.Value) + " s" : "—", tone2, 14, false, TextAlignment.Right) });
                else rows.Add(new UIElement[] { label, time, probs, crashes, T(g.IdleKicks.ToString(CultureInfo.CurrentCulture), tone2, 14, false, TextAlignment.Right) });
            }
            return Table(cols, rows);
        }

        UIElement SetupCard(PulseReport rep)
        {
            var sp = new StackPanel();
            foreach (var v in new[] { PulseVerdict.Cap(rep), PulseVerdict.Windows(rep) })
                if (v != null) sp.Children.Add(VerdictLine(v));
            var capTitle = T("By memory cap", "BrushTextSecondary", 12, true); capTitle.Margin = new Thickness(0, sp.Children.Count > 0 ? 8 : 0, 0, 2);
            sp.Children.Add(capTitle);
            sp.Children.Add(GroupTable("Setting", rep.ByCap, true));
            var winTitle = T("By windows running at once", "BrushTextSecondary", 12, true); winTitle.Margin = new Thickness(0, 18, 0, 2);
            sp.Children.Add(winTitle);
            sp.Children.Add(GroupTable("Windows", rep.ByWindows, false));
            return CardBox(sp, new Thickness(0), 16);
        }

        // accounts + incidents
        UIElement AttentionTable(PulseReport rep)
        {
            if (rep.Attention.Count == 0)
            {
                var ok = new StackPanel { Orientation = Orientation.Horizontal };
                var tick = T("", "BrushSuccess", 14); tick.SetResourceReference(TextBlock.FontFamilyProperty, "FontIcons"); tick.IconSize(14); tick.Margin = new Thickness(0, 0, 10, 0);
                ok.Children.Add(tick); ok.Children.Add(T("Every account ran cleanly.", "BrushText", 14));
                return CardBox(ok, new Thickness(0), 16);
            }
            var cols = _pulseNarrow
                ? new List<Col> { Col.Star("Account", 1, 110), Col.Px("Problems", 80), Col.Px("Rejoins", 70), Col.Px("In a game", 84) }
                : new List<Col> { Col.Star("Account", 1, 130), Col.Px("Problems", 84), Col.Px("Rejoins", 74), Col.Px("Failed", 66), Col.Px("In a game", 88), Col.Px("Memory growth", 120), new Col { Header = "Last problem", Width = new GridLength(170), PadLeft = 18 } };
            var rows = new List<UIElement[]>();
            foreach (var a in rep.Attention)
            {
                var name = T(a.Name.Length > 0 ? a.Name : "Account " + a.UserId, "BrushText", 14, true); name.Margin = new Thickness(0, 0, 8, 0);
                var probs = T(a.Problems + (a.Crashes > 0 ? " (" + a.Crashes + " crash" + (a.Crashes == 1 ? "" : "es") + ")" : ""), a.Problems > 0 ? "BrushText" : "BrushTextTertiary", 14, false, TextAlignment.Right);
                var rej = T(a.Rejoins.ToString(CultureInfo.CurrentCulture), "BrushTextSecondary", 14, false, TextAlignment.Right);
                var ing = T(Pct(a.InGameShare), ZoneHigh(a.InGameShare, 0.97, 0.9) == "bad" ? "BrushDanger" : "BrushTextSecondary", 14, false, TextAlignment.Right);
                if (_pulseNarrow) { rows.Add(new UIElement[] { name, probs, rej, ing }); continue; }
                bool leak = a.GrowthMbPerHour >= PulseReport.LeakMbPerHour;
                var growth = T(a.GrowthMbPerHour.HasValue ? (a.GrowthMbPerHour.Value >= 0 ? "+" : "") + Math.Round(a.GrowthMbPerHour.Value) + " MB/h" : "—", leak ? "BrushWarning" : "BrushTextSecondary", 14, false, TextAlignment.Right);
                if (leak) growth.ToolTip = "Its reserved memory keeps growing after the game settled. Restarting that window resets it.";
                var last = T(a.LastProblemUtc.HasValue ? WhenText(a.LastProblemUtc.Value) + " · " + a.LastProblem : a.GaveUp > 0 ? "Lantern gave up" : a.FailedLaunches > 0 ? "Launch failed" : "—", "BrushTextSecondary", 13);
                rows.Add(new UIElement[] { name, probs, rej, T(a.FailedLaunches.ToString(CultureInfo.CurrentCulture), "BrushTextSecondary", 14, false, TextAlignment.Right), ing, growth, last });
            }
            return Table(cols, rows);
        }

        UIElement IncidentsTable(PulseReport rep)
        {
            if (rep.Incidents.Count == 0) return CardBox(T("No incidents.", "BrushText", 14), new Thickness(0), 16);
            var cols = _pulseNarrow
                ? new List<Col> { Col.Px("When", 110, false), Col.Star("Account", 1, 90), new Col { Header = "What happened", Width = new GridLength(1.4, GridUnitType.Star), PadLeft = 12 } }
                : new List<Col> { Col.Px("When", 130, false), Col.Star("Account", 1, 120), new Col { Header = "What happened", Width = new GridLength(2, GridUnitType.Star), PadLeft = 12 } };
            var rows = new List<UIElement[]>();
            foreach (var i in rep.Incidents.Take(12))
            {
                var what = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var dot = new Dot { Width = 7, Height = 7, Margin = new Thickness(0, 0, 8, 0) };
                dot.SetResourceReference(Border.BackgroundProperty, ClassBrush(i.Class));
                what.Children.Add(dot);
                what.Children.Add(T(i.Text, i.Class == EndClass.Crash || i.Class == "GaveUp" ? "BrushDanger" : "BrushTextSecondary", 13));
                rows.Add(new UIElement[] { T(WhenText(i.Utc), "BrushTextSecondary", 13), T(i.Name, "BrushText", 14, true), what });
            }
            return Table(cols, rows);
        }

        // picture + report file
        /// <summary>The board as a picture at twice the screen size, on the window's surface colour. Null when it isn't on screen.</summary>
        internal BitmapSource RenderPulseImage()
        {
            if (_pulseBoard == null || _pulseBoard.ActualWidth < 1 || _pulseBoard.ActualHeight < 1) return null;
            const double scale = 2, pad = 16;
            var size = new Size(_pulseBoard.ActualWidth + 2 * pad, _pulseBoard.ActualHeight + 2 * pad);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle((Brush)FindResource("BrushSurface"), null, new Rect(size));
                dc.DrawRectangle(new VisualBrush(_pulseBoard), null, new Rect(pad, pad, _pulseBoard.ActualWidth, _pulseBoard.ActualHeight));
            }
            var bmp = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }

        void CopyPulseImage()
        {
            var img = RenderPulseImage();
            if (img == null) { ShowToast("Open the Pulse page first.", true); return; }
            try
            {
                Clipboard.SetDataObject(new DataObject(DataFormats.Bitmap, img), true);
                ShowToast("Copied. Paste it anywhere.", false);
            }
            catch (Exception ex) { ShowToast("Couldn't copy the picture: " + ex.Message, true); }
        }

        void SavePulseImage()
        {
            var img = RenderPulseImage();
            if (img == null) { ShowToast("Open the Pulse page first.", true); return; }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save the picture", Filter = "PNG image (*.png)|*.png", DefaultExt = ".png",
                FileName = "lantern-pulse-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".png"
            };
            if (dlg.ShowDialog(this) != true) return;
            SavePulseImageTo(dlg.FileName, img);
        }

        internal bool SavePulseImageTo(string path, BitmapSource img = null)
        {
            img = img ?? RenderPulseImage();
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

        static string PulseReportPath => System.IO.Path.Combine(Paths.PulseDir, "pulse.json");

        void UpdatePulseReportCaption()
        {
            PulseReportCaption.Text = "Every 5 minutes Lantern writes these numbers for the last hour, 24 hours and 7 days to " + PulseReportPath +
                                      " — account names and counts only, never cookies — so a spreadsheet, a script or an assistant can read them." +
                                      (_s.PulseReportFile ? "" : " Off: nothing is written.");
            PulseFolderButton.Visibility = _s.PulseReportFile ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Writes pulse.json (atomically) if the switch is on. Names and numbers only.</summary>
        internal bool WritePulseReport()
        {
            if (!_s.PulseReportFile || App.Demo) return false;
            try
            {
                var now = DateTime.UtcNow;
                var snapS = _mgr.Sessions.Snapshot(); var snapE = _mgr.Sessions.EventsSnapshot();
                var ranges = new[] { ("last_hour", now.AddHours(-1)), ("last_24h", now.AddHours(-24)), ("last_7d", now.AddDays(-7)) }
                    .Select(x => new KeyValuePair<string, PulseReport>(x.Item1, PulseReport.Build(snapS, snapE, x.Item2, now, now)));
                Native.Memory(out var total, out var avail);
                var data = PulseExport.Build(PulseNow.From(_mgr.Accounts, now), _s, (long)total, (long)avail, ranges);
                var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(data);
                Directory.CreateDirectory(Paths.PulseDir);
                var tmp = PulseReportPath + ".tmp";
                File.WriteAllText(tmp, PrettyJson(json), new UTF8Encoding(false));
                if (File.Exists(PulseReportPath)) File.Replace(tmp, PulseReportPath, null); else File.Move(tmp, PulseReportPath);
                return true;
            }
            catch (Exception ex) { Logger.Write("Pulse report: " + ex.Message); return false; }
        }

        /// <summary>Indents compact JSON so the file reads well by eye (strings are copied through untouched).</summary>
        static string PrettyJson(string json)
        {
            var sb = new StringBuilder(json.Length * 2);
            int depth = 0; bool inStr = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inStr)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < json.Length) sb.Append(json[++i]);
                    else if (c == '"') inStr = false;
                    continue;
                }
                switch (c)
                {
                    case '"': inStr = true; sb.Append(c); break;
                    case '{': case '[':
                        if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']')) { sb.Append(c).Append(json[++i]); break; }
                        sb.Append(c).Append('\n').Append(' ', ++depth * 2); break;
                    case '}': case ']': sb.Append('\n').Append(' ', --depth * 2).Append(c); break;
                    case ',': sb.Append(c).Append('\n').Append(' ', depth * 2); break;
                    case ':': sb.Append(": "); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        void OpenPulseFolder()
        {
            try
            {
                if (!WritePulseReport() && !File.Exists(PulseReportPath)) { ShowToast("The report hasn't been written yet.", true); return; }
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + PulseReportPath + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { ShowToast("Couldn't open the folder: " + ex.Message, true); }
        }
    }
}
