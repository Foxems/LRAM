using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Lantern.Core;

namespace Lantern.UI
{
    /// <summary>
    /// Live: every running window as a tile in one grid, with the Pulse numbers that matter beside it. The pictures are Windows'
    /// own live thumbnails (what the taskbar previews use), composed by the graphics card into Lantern's window; the Roblox windows
    /// stay ordinary top-level windows tucked behind Lantern. Nothing is read from or injected into Roblox. Pause minimises a
    /// window (it stops drawing, and the opt-in trim and cap then apply); Play lays the real window over the grid and focuses it,
    /// and clicking back into Lantern tucks it behind again.
    /// </summary>
    public partial class MainWindow
    {
        sealed class LiveTile
        {
            public AccountItem Item;
            public int Pid;
            public IntPtr Hwnd, Thumb;
            public double Aspect = 16 / 9.0;
            public bool Paused;
            public Border Card, Screen;
            public TextBlock Name, Info, Note, PauseGlyph;
            public Button PauseButton;
            /// <summary>Maximise and More: dropped on very small tiles so the name keeps some room.</summary>
            public Button[] Extra;
            /// <summary>Back and "Open Roblox window": only shown while this tile is zoomed in.</summary>
            public Button[] FocusOnly = new Button[0];
            /// <summary>A sleeping account: no window, a tile that starts it when clicked.</summary>
            public bool Ghost, Waking;
            public TextBlock GhostIcon, GhostAction;
            public Border GhostPill;
            public Native.RECT Placed;
            public bool PlacedVisible;
            public byte PlacedOpacity;
            // stream mode's own copy of the picture (names blurred)
            public Image Frame;
            public WriteableBitmap Bitmap;
            /// <summary>Copies, reads and holds back this window's picture; replaced (so it starts blurred again) when the window changes.</summary>
            public StreamCopier Copier;
            /// <summary>Pictures shown in stream mode since the last refresh, and the rate they made (shown in the header).</summary>
            public int Shown, Fps;
            public DateTime ShownSince = DateTime.UtcNow;
            /// <summary>A window that didn't hand over its picture in time is left alone for a while.</summary>
            public DateTime SkipUntil;
        }

        const double LiveHeaderHeight = 36, LiveGap = 10;
        /// <summary>A paused tile shows the window's last picture, dimmed.</summary>
        const byte LivePausedOpacity = 110;

        readonly List<LiveTile> _live = new List<LiveTile>();
        DispatcherTimer _liveTimer;
        /// <summary>The window Play put over the grid, and where it was before; when Lantern is activated again it goes back there, behind Lantern.</summary>
        IntPtr _livePlaying;
        Native.RECT? _livePlayedFrom;
        /// <summary>The account whose window is zoomed in inside Lantern (the "small fullscreen"); null = the grid.</summary>
        AccountItem _liveFocusItem;
        LiveTile FocusedTile => _liveFocusItem == null ? null : _live.FirstOrDefault(t => !t.Ghost && t.Item == _liveFocusItem);
        int _liveTick;
        PulseReport _liveRep;
        DateTime _liveRepAt;
        /// <summary>Roblox windows whose taskbar button Lantern took away (given back when the switch is turned off).</summary>
        readonly HashSet<IntPtr> _offTaskbar = new HashSet<IntPtr>();

        void WireLive()
        {
            LiveStream.Click += (s, e) => ShowStreamMenu();
            LiveBrandTitle.Text = About.Name + "  ·  free " + About.Tagline + "  ·  by " + About.Author;
            LiveBrandUrl.Text = "Download: " + About.RepositoryUrl.Replace("https://", "");
            LiveBrand.ToolTip = "Open the downloads page: " + About.ReleasesUrl;
            LiveBrand.MouseLeftButtonUp += (s, e) => OpenLink(About.ReleasesUrl);
            LiveHideTaskbar.IsChecked = _s.HideFromTaskbar;
            LiveHideTaskbar.Checked += (s, e) => { _s.HideFromTaskbar = true; _mgr.SaveSettings(); SyncTaskbar(); };
            LiveHideTaskbar.Unchecked += (s, e) => { _s.HideFromTaskbar = false; _mgr.SaveSettings(); SyncTaskbar(); };
            // Explorer adds a button back whenever a window is shown again (restore, Play), so this is checked every few seconds,
            // whichever page is open.
            var taskbarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            taskbarTimer.Tick += (s, e) => SyncTaskbar();
            if (!App.Demo) taskbarTimer.Start();

            LiveTrim.IsChecked = _s.TrimVisible;
            LiveTrim.Checked += (s, e) => { _mgr.EnableTrimVisible(true); RefreshLiveSide(); };
            LiveTrim.Unchecked += (s, e) => { _mgr.EnableTrimVisible(false); RefreshLiveSide(); };
            _mgr.TrimUnavailableChanged += () => LiveTrim.IsChecked = _s.TrimVisible;

            LiveShowOffline.IsChecked = _s.LiveShowOffline;
            LiveShowOffline.Checked += (s, e) => { _s.LiveShowOffline = true; _mgr.SaveSettings(); RefreshLive(); };
            LiveShowOffline.Unchecked += (s, e) => { _s.LiveShowOffline = false; _mgr.SaveSettings(); RefreshLive(); };
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && _liveFocusItem != null && PageLive.Visibility == Visibility.Visible && DialogLayer.Visibility != Visibility.Visible)
                { LeaveLiveFocus(); e.Handled = true; }
            };

            LiveLaunch.Click += (s, e) => ShowLiveLaunchMenu();
            LiveEmptyLaunch.Click += (s, e) => ShowLiveLaunchMenu(LiveEmptyLaunch);
            LiveEmptyAccounts.Click += (s, e) => NavAccounts.IsChecked = true;
            LivePauseAll.Click += (s, e) => { foreach (var t in _live.Where(x => x.Hwnd != IntPtr.Zero && !x.Paused)) PauseLive(t); RefreshLive(); };
            LiveResumeAll.Click += (s, e) => { foreach (var t in _live.Where(x => x.Hwnd != IntPtr.Zero && x.Paused)) ResumeLive(t); RefreshLive(); };

            _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _liveTimer.Tick += (s, e) =>
            {
                if (PageLive.Visibility != Visibility.Visible) { _liveTimer.Stop(); _liveFocusItem = null; ClearLive(); return; }
                RefreshLive();
                if (++_liveTick % 3 == 0) RefreshLiveSide();
            };
            NavLive.Unchecked += (s, e) => { _liveTimer.Stop(); _liveFocusItem = null; ClearLive(); };

            LiveArea.SizeChanged += (s, e) => LayoutLive();
            // Thumbnails are drawn by Windows on top of Lantern's content, so they follow the layout by hand (only changes reach Windows).
            LayoutUpdated += (s, e) => { if (_live.Count > 0) PlaceLiveThumbs(); };
            DialogLayer.IsVisibleChanged += (s, e) => PlaceLiveThumbs();
            Activated += (s, e) =>
            {
                if (_livePlaying == IntPtr.Zero) return;
                if (Native.WindowExists(_livePlaying))
                {
                    // back to its own size: a window left the size of the grid costs the game and stream mode several times more
                    if (_livePlayedFrom.HasValue) Native.PutBackBehind(_livePlaying, _livePlayedFrom.Value);
                    else Native.SendToBack(_livePlaying);
                }
                _livePlaying = IntPtr.Zero;
                _livePlayedFrom = null;
            };
        }

        void OnLiveShown()
        {
            _liveTick = 0;
            RefreshLive();
            // Windows that sit on top of Lantern would hide the grid: tuck the live ones behind (no focus change, no move).
            if (!App.Demo) foreach (var t in _live.Where(x => x.Hwnd != IntPtr.Zero && !x.Paused)) Native.SendToBack(t.Hwnd);
            RefreshLiveSide();
            _liveTimer.Start();
        }

        void ClearLive()
        {
            foreach (var t in _live) { Native.UnregisterThumbnail(t.Thumb); if (t.Copier != null) ForgetCopy(t); }
            _live.Clear();
            LiveTiles.Children.Clear();
        }

        // tiles
        int _lastWindows = -1;

        void RefreshLive()
        {
            if (PageLive.Visibility != Visibility.Visible) return;
            // sleeping accounts keep their place in the list, so one that starts wakes up where it sat
            var want = _mgr.Accounts.Where(a => a.IsActive || _s.LiveShowOffline).ToList();
            if (!want.Select(a => (a, a.IsActive)).SequenceEqual(_live.Select(t => (t.Item, !t.Ghost))))
            {
                ClearLive();
                foreach (var a in want) { var t = a.IsActive ? BuildLiveTile(a) : BuildGhostTile(a); _live.Add(t); LiveTiles.Children.Add(t.Card); }
            }
            if (_liveFocusItem != null && FocusedTile == null) _liveFocusItem = null;   // the zoomed window closed
            var focused = FocusedTile;

            var demoWindows = App.Demo ? DemoLiveWindows() : null;
            int paused = 0, live = 0;
            for (int i = 0; i < _live.Count; i++)
            {
                var t = _live[i];
                var a = t.Item;
                if (t.Ghost) { UpdateGhost(t); continue; }

                // the window behind this account: found again when the process changes or the window goes away
                if (t.Pid != a.Pid || (t.Hwnd != IntPtr.Zero && !Native.WindowExists(t.Hwnd)))
                {
                    Native.UnregisterThumbnail(t.Thumb);
                    t.Thumb = t.Hwnd = IntPtr.Zero;
                    t.Pid = a.Pid;
                    t.Placed = default; t.PlacedVisible = false;
                    if (t.Bitmap != null || t.Copier != null) ForgetCopy(t);   // a rejoined window starts blurred, never with the old one's picture
                }
                if (t.Hwnd == IntPtr.Zero)
                {
                    t.Hwnd = demoWindows != null ? (a.State == AccountState.Running && demoWindows.Count > 0 ? demoWindows[i % demoWindows.Count] : IntPtr.Zero)
                           : a.Pid > 0 ? Native.TopWindowsOf(new HashSet<int> { a.Pid }).FirstOrDefault() : IntPtr.Zero;
                    if (t.Hwnd != IntPtr.Zero)
                    {
                        t.Thumb = Native.RegisterThumbnail(Handle, t.Hwnd);
                        if (!App.Demo && IsActive && !Native.IsMinimized(t.Hwnd)) Native.SendToBack(t.Hwnd);   // a new window opened on top of the grid
                    }
                }

                t.Paused = t.Hwnd != IntPtr.Zero && Native.IsMinimized(t.Hwnd);
                if (t.Hwnd != IntPtr.Zero && !t.Paused)
                {
                    var (w, h) = Native.ClientSize(t.Hwnd);
                    if (w > 0 && h > 0) t.Aspect = (double)w / h;
                }
                if (t.Paused) paused++; else if (t.Thumb != IntPtr.Zero) live++;

                t.Name.Text = a.Display;
                t.Name.Effect = StreamBlurNames ? _nameBlur : null;
                var alert = LiveAlert(a, DateTime.UtcNow);
                t.Card.SetResourceReference(Border.BorderBrushProperty, alert == null ? "BrushControlStroke" : alert.Value.bad ? "BrushDangerFill" : "BrushWarning");
                t.Card.BorderThickness = new Thickness(alert == null ? 1 : 2);
                t.Card.Margin = new Thickness(LiveGap / 2 - (alert == null ? 0 : 1));   // the thicker outline doesn't shift the grid
                t.Info.SetResourceReference(TextBlock.ForegroundProperty, alert == null ? "BrushTextTertiary" : alert.Value.bad ? "BrushDanger" : "BrushWarning");
                t.Card.ToolTip = alert?.text;
                t.Info.Text = (alert != null ? alert.Value.text : a.State == AccountState.Running
                    ? (t.Paused ? "Paused" : !a.JoinedAt.HasValue && !a.PickedUp ? "Loading" : "") + (a.HasUsage ? (t.Paused ? "  ·  " : "") + Fmt.Memory(a.WorkingSet) + "  ·  CPU " + Fmt.Percent(a.Cpu) : "")
                    : a.StatusText)
                    + (StreamCapture && t.Copier != null && !t.Paused ? "  ·  " + t.Fps + " fps" : "");
                bool copy = StreamCapture;
                var since = DateTime.UtcNow - t.ShownSince;
                if (since.TotalSeconds >= 1) { t.Fps = (int)Math.Round(t.Shown / since.TotalSeconds); t.Shown = 0; t.ShownSince = DateTime.UtcNow; }
                if (!copy && (t.Bitmap != null || t.Copier != null)) ForgetCopy(t);
                t.Frame.Visibility = copy ? Visibility.Visible : Visibility.Collapsed;
                t.Note.Text = copy ? (t.Hwnd == IntPtr.Zero ? a.StatusText : t.Paused && t.Bitmap == null ? "Paused" : "Reading the window…")
                            : t.Thumb != IntPtr.Zero ? "" : a.State == AccountState.Running ? "Looking for the window…" : a.StatusText;
                t.PauseGlyph.Text = t.Paused ? "" : "";
                t.PauseButton.ToolTip = t.Paused ? "Resume: the window draws again" : "Pause: minimise the window so it stops drawing";
                t.PauseButton.IsEnabled = t.Hwnd != IntPtr.Zero;
                t.Screen.Cursor = focused == t ? Cursors.Arrow : Cursors.Hand;
                t.Screen.ToolTip = focused == t ? null : "Click to zoom in";
            }

            LiveEmpty.Visibility = _live.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            int asleep = _live.Count(t => t.Ghost);
            int n = _live.Count - asleep;
            _lastWindows = n;
            LivePageCaption.Text = focused != null ? "Zoomed in on one window  ·  Esc or Back returns to the grid"
                : n == 0 && asleep > 0 ? "Nothing running  ·  click a sleeping account to wake it up"
                : n == 0 ? "Running windows show up here as live tiles."
                : n + (n == 1 ? " window" : " windows") + (live > 0 ? "  ·  " + live + " live" : "") + (paused > 0 ? "  ·  " + paused + " paused" : "")
                  + (asleep > 0 ? "  ·  " + asleep + " asleep" : "") + "  ·  click a tile to zoom in";
            // only what can be done right now is offered, so the toolbar stays on one line
            LivePauseAll.Visibility = _live.Any(t => t.Hwnd != IntPtr.Zero && !t.Paused) ? Visibility.Visible : Visibility.Collapsed;
            LiveResumeAll.Visibility = _live.Any(t => t.Paused) ? Visibility.Visible : Visibility.Collapsed;
            LayoutLive();
            PlaceLiveThumbs();
            if (StreamCapture && !_copyRunning) _ = CopyLoopAsync();
        }

        /// <summary>Dev only: with LANTERN_DEMO_WINDOWS=&lt;process name&gt;, the demo's running tiles show that program's windows, to check the placement.</summary>
        static List<IntPtr> DemoLiveWindows()
        {
            var name = Environment.GetEnvironmentVariable("LANTERN_DEMO_WINDOWS");
            if (string.IsNullOrEmpty(name)) return new List<IntPtr>();
            var pids = new HashSet<int>();
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(name)) { pids.Add(p.Id); p.Dispose(); }
            return Native.TopWindowsOf(pids);
        }

        LiveTile BuildLiveTile(AccountItem a)
        {
            var t = new LiveTile { Item = a };

            var dot = new Dot { Style = (Style)FindResource("StatusDot"), DataContext = a, Margin = new Thickness(0, 0, 8, 0) };
            t.Name = T("", "BrushText", 13, true);
            t.Info = T("", "BrushTextTertiary", 11.5); t.Info.Margin = new Thickness(8, 1, 0, 0);
            var names = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(t.Name, Dock.Left);
            names.Children.Add(t.Name); names.Children.Add(t.Info);

            Button Icon(string glyph, string tip, Action click)
            {
                var b = new Button { Style = (Style)FindResource("IconButton"), Width = 28, Height = 28, Content = glyph, ToolTip = tip }.IconSize(12.5);
                b.Click += (s, e) => click();
                return b;
            }
            t.PauseGlyph = new TextBlock { Style = (Style)FindResource("Icon") }.IconSize(12.5);
            t.PauseButton = new Button { Style = (Style)FindResource("IconButton"), Width = 28, Height = 28, Content = t.PauseGlyph };
            t.PauseButton.Click += (s, e) => { if (t.Paused) ResumeLive(t); else PauseLive(t); RefreshLive(); };
            Button more = null;
            more = Icon("", "More", () => ShowRowMenu(more, a));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
            t.Extra = new[] { Icon("", "Play maximised", () => PlayLive(t, true)), more };
            // zoomed in (the "small fullscreen"): a way back, and the way to the real window
            var back = new Button { Style = (Style)FindResource("IconButton"), Width = 28, Height = 28, Content = "", ToolTip = "Back to all windows (Esc)", Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };
            back.IconSize(12.5);
            back.Click += (s, e) => LeaveLiveFocus();
            var openLabel = new StackPanel { Orientation = Orientation.Horizontal };
            openLabel.Children.Add(new TextBlock { Style = (Style)FindResource("Icon"), Text = "", Margin = new Thickness(0, 0, 8, 0) }.IconSize(12.5));
            openLabel.Children.Add(new TextBlock { Text = "Open Roblox window" });
            var open = new Button { Style = (Style)FindResource("AccentButton"), Height = 28, Content = openLabel, Margin = new Thickness(0, 0, 6, 0), Visibility = Visibility.Collapsed,
                                    ToolTip = "Lay the real Roblox window over this area and play in it. Click back into Lantern to put it away again." };
            open.Click += (s, e) => PlayLive(t, false);
            t.FocusOnly = new[] { back, open };
            buttons.Children.Add(open);
            buttons.Children.Add(t.PauseButton);
            foreach (var b in t.Extra) buttons.Children.Add(b);
            buttons.Children.Add(Icon("", "Close this window", () => _mgr.Close(a)));

            var head = new Grid { Height = LiveHeaderHeight, Margin = new Thickness(10, 0, 4, 0) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(dot, 1); Grid.SetColumn(names, 2); Grid.SetColumn(buttons, 3);
            head.Children.Add(back); head.Children.Add(dot); head.Children.Add(names); head.Children.Add(buttons);

            // the window's picture is drawn by Windows over this area; the note shows while there is no window yet
            t.Note = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12) };
            t.Frame = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(1), Visibility = Visibility.Collapsed };
            RenderOptions.SetBitmapScalingMode(t.Frame, BitmapScalingMode.Linear);
            var screenContent = new Grid();
            screenContent.Children.Add(t.Note); screenContent.Children.Add(t.Frame);
            t.Screen = new Box("RadiusBottomMd") { Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x0C, 0x0C)), Child = screenContent, Cursor = Cursors.Hand, ToolTip = "Click to zoom in" };
            t.Screen.MouseLeftButtonUp += (s, e) => { if (FocusedTile != t) FocusLive(t); };
            DockPanel.SetDock(head, Dock.Top);
            var body = new DockPanel();
            body.Children.Add(head); body.Children.Add(t.Screen);

            t.Card = new Box("RadiusLg") { BorderThickness = new Thickness(1), Margin = new Thickness(LiveGap / 2), Child = body };
            t.Card.SetResourceReference(Border.BackgroundProperty, "BrushControl");
            t.Card.SetResourceReference(Border.BorderBrushProperty, "BrushControlStroke");
            return t;
        }

        // sleeping accounts
        /// <summary>The tile of an account that isn't online: an empty slot with one round button that starts it.</summary>
        LiveTile BuildGhostTile(AccountItem a)
        {
            var t = new LiveTile { Item = a, Ghost = true, Extra = new Button[0] };

            var dot = new Dot { Style = (Style)FindResource("StatusDot"), DataContext = a, Margin = new Thickness(0, 0, 8, 0) };
            t.Name = T("", "BrushText", 13, true);
            t.Info = T("", "BrushTextTertiary", 11.5); t.Info.Margin = new Thickness(8, 1, 0, 0);
            var names = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(t.Name, Dock.Left);
            names.Children.Add(t.Name); names.Children.Add(t.Info);
            var head = new Grid { Height = LiveHeaderHeight, Margin = new Thickness(10, 0, 10, 0) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(names, 1);
            head.Children.Add(dot); head.Children.Add(names);

            // a dashed empty slot, with a round button and one word under it
            t.GhostIcon = new TextBlock { Style = (Style)FindResource("Icon"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }.IconSize(20);
            t.GhostPill = new Box("RadiusPill") { Width = 56, Height = 56, BorderThickness = new Thickness(1.5), HorizontalAlignment = HorizontalAlignment.Center, Child = t.GhostIcon };
            t.GhostAction = new TextBlock { FontSize = 12, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
            t.GhostAction.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextTertiary");
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(t.GhostPill); content.Children.Add(t.GhostAction);
            // (scaled down with a small tile, never up)
            var fit = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(20), Child = content };
            var slot = new System.Windows.Shapes.Rectangle { RadiusX = 6, RadiusY = 6, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 4 }, Margin = new Thickness(10), IsHitTestVisible = false };
            slot.SetResourceReference(Shape.StrokeProperty, "BrushStroke");
            var inside = new Grid();
            inside.Children.Add(slot); inside.Children.Add(fit);
            t.Screen = new Box("RadiusBottomMd") { Background = Brushes.Transparent, Child = inside };

            DockPanel.SetDock(head, Dock.Top);
            var body = new DockPanel();
            body.Children.Add(head); body.Children.Add(t.Screen);
            t.Card = new Box("RadiusLg") { BorderThickness = new Thickness(1), Margin = new Thickness(LiveGap / 2), Child = body };
            t.Card.SetResourceReference(Border.BackgroundProperty, "BrushControl");
            t.Card.MouseEnter += (s, e) => UpdateGhost(t);
            t.Card.MouseLeave += (s, e) => UpdateGhost(t);
            t.Card.MouseLeftButtonUp += async (s, e) => await WakeGhostAsync(t);
            UpdateGhost(t);
            return t;
        }

        void UpdateGhost(LiveTile t)
        {
            var a = t.Item;
            bool expired = a.State == AccountState.Expired, failed = a.State == AccountState.Error, checking = a.State == AccountState.Checking;
            bool busy = t.Waking || checking;
            bool hot = t.Card.IsMouseOver && !busy;
            t.Name.Text = a.Display;
            t.Name.Effect = StreamBlurNames ? _nameBlur : null;
            t.Info.Text = a.StatusText;
            t.GhostIcon.Text = expired ? "\uE72E" : failed ? "\uE7BA" : "\uE768";
            t.GhostIcon.SetResourceReference(TextBlock.ForegroundProperty, failed ? "BrushDanger" : hot ? "BrushAccentText" : "BrushTextSecondary");
            t.GhostAction.Text = t.Waking ? "Starting…" : checking ? "Checking…" : expired ? "Sign in" : failed ? "Try again" : "Start";
            t.GhostPill.SetResourceReference(Border.BorderBrushProperty, hot ? "BrushAccent" : failed ? "BrushDangerFill" : "BrushStroke");
            if (hot) t.GhostPill.SetResourceReference(Border.BackgroundProperty, "BrushControlHover"); else t.GhostPill.Background = Brushes.Transparent;
            t.GhostPill.Opacity = busy ? 0.4 : 1;
            t.Card.Opacity = hot || failed ? 1 : 0.75;
            t.Card.SetResourceReference(Border.BorderBrushProperty, failed ? "BrushDangerFill" : "BrushControlStroke");
            t.Card.Cursor = busy ? Cursors.Arrow : Cursors.Hand;
        }

        async Task WakeGhostAsync(LiveTile t)
        {
            var a = t.Item;
            if (t.Waking || a.IsActive || a.State == AccountState.Checking) return;
            try
            {
                if (a.State == AccountState.Expired) { await SignInFlowAsync(); return; }
                if (!TryGetTarget(out var target)) { NavAccounts.IsChecked = true; return; }   // the launch bar is on the Accounts page
                t.Waking = true; UpdateGhost(t);
                await LaunchLiveAccountAsync(a, target);
            }
            catch (Exception ex) { Logger.Write("Live wake: " + ex.Message); }
            finally { t.Waking = false; }
        }

        /// <summary>Starts one account to the launch bar's game.</summary>
        async Task LaunchLiveAccountAsync(AccountItem item, LaunchTarget target)
        {
            await EnsureNameAsync(target);
            item.IsSelected = false;
            await _mgr.LaunchAsync(new[] { item }, target);
        }

        // zoom ("small fullscreen")
        /// <summary>Shows one window big inside Lantern, so it can be shared as part of Lantern's own window; "Open Roblox window" is the real one.</summary>
        void FocusLive(LiveTile t)
        {
            if (t.Hwnd == IntPtr.Zero) { ShowToast(t.Item.Display + " has no window yet.", true); return; }
            _liveFocusItem = t.Item;
            RefreshLive();
        }

        void LeaveLiveFocus()
        {
            _liveFocusItem = null;
            RefreshLive();
        }

        /// <summary>Sizes the tiles so all of them fit the area at the windows' shape, as large as possible. Zoomed in, the one tile is all there is.</summary>
        void LayoutLive()
        {
            LiveSide.Visibility = (ContentPane.ActualWidth > 0 && ContentPane.ActualWidth < 760) ? Visibility.Collapsed : Visibility.Visible;
            var focused = FocusedTile;
            foreach (var t in _live)
            {
                t.Card.Visibility = focused == null || t == focused ? Visibility.Visible : Visibility.Collapsed;
                foreach (var b in t.FocusOnly) b.Visibility = t == focused ? Visibility.Visible : Visibility.Collapsed;
            }
            var shown = focused != null ? new List<LiveTile> { focused } : _live;
            int n = shown.Count;
            double W = LiveArea.ActualWidth, H = LiveArea.ActualHeight;
            if (n == 0 || W <= 0 || H <= 0) return;

            double aspect = shown.Where(t => t.Hwnd != IntPtr.Zero).Select(t => t.Aspect).DefaultIfEmpty(16 / 9.0).Average();
            aspect = Math.Max(0.5, Math.Min(3, aspect));
            double best = 0;
            for (int cols = 1; cols <= n; cols++)
            {
                int rows = (int)Math.Ceiling(n / (double)cols);
                double tw = W / cols - LiveGap - 1;
                double th = H / rows - LiveGap - 1;
                double screenH = Math.Min((tw - 2) / aspect, th - LiveHeaderHeight - 2);
                if (screenH <= 0) continue;
                best = Math.Max(best, screenH * aspect + 2);
            }
            double width = Math.Floor(Math.Max(best, 120));
            double height = Math.Floor((width - 2) / aspect);
            foreach (var t in shown)
            {
                if (!(Math.Abs(t.Card.Width - width) < 0.5)) t.Card.Width = width;      // (an unset size is NaN)
                if (!(Math.Abs(t.Screen.Height - height) < 0.5)) t.Screen.Height = height;
                t.Info.Visibility = width < 280 ? Visibility.Collapsed : Visibility.Visible;
                foreach (var b in t.Extra) b.Visibility = width < 210 ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        /// <summary>Tells Windows where each picture goes (in pixels of Lantern's client area), letterboxed to the window's shape.</summary>
        void PlaceLiveThumbs()
        {
            if (_live.Count == 0) return;
            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget == null) return;
            var m = src.CompositionTarget.TransformToDevice;
            // in stream mode with name blur the tiles show Lantern's own (blurred) copy instead, so Windows' live picture is switched off
            bool show = PageLive.Visibility == Visibility.Visible && DialogLayer.Visibility != Visibility.Visible && !StreamCapture;
            Rect area;
            try { area = LiveArea.TransformToAncestor(this).TransformBounds(new Rect(0, 0, LiveArea.ActualWidth, LiveArea.ActualHeight)); }
            catch { return; }

            foreach (var t in _live)
            {
                if (t.Thumb == IntPtr.Zero) continue;
                Rect r;
                try { r = t.Screen.TransformToAncestor(this).TransformBounds(new Rect(0, 0, t.Screen.ActualWidth, t.Screen.ActualHeight)); }
                catch { continue; }
                r.Inflate(-1, -1);
                if (r.Width > 0 && r.Height > 0)
                {
                    double w = Math.Min(r.Width, r.Height * t.Aspect), h = w / t.Aspect;
                    r = new Rect(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
                }
                var px = new Native.RECT
                {
                    Left = (int)Math.Round(r.Left * m.M11), Top = (int)Math.Round(r.Top * m.M22),
                    Right = (int)Math.Round(r.Right * m.M11), Bottom = (int)Math.Round(r.Bottom * m.M22)
                };
                // a tile that doesn't fit the area (very many windows on a small Lantern) is not drawn over the rest of the page
                bool visible = show && t.Card.Visibility == Visibility.Visible && r.Width > 8 && r.Height > 8 && area.Contains(r);
                byte opacity = t.Paused ? LivePausedOpacity : (byte)255;
                if (visible == t.PlacedVisible && opacity == t.PlacedOpacity && (!visible || px.Equals(t.Placed))) continue;
                if (Native.UpdateThumbnail(t.Thumb, px, visible, opacity)) { t.Placed = px; t.PlacedVisible = visible; t.PlacedOpacity = opacity; }
            }
        }

        /// <summary>Takes the running windows' buttons off the taskbar (or gives them all back when the switch is off).</summary>
        void SyncTaskbar()
        {
            if (App.Demo) return;
            if (!_s.HideFromTaskbar)
            {
                foreach (var h in _offTaskbar) if (Native.WindowExists(h)) Native.SetTaskbarButton(h, true);
                _offTaskbar.Clear();
                return;
            }
            var pids = new HashSet<int>(_mgr.Accounts.Where(a => a.State == AccountState.Running && a.Pid > 0).Select(a => a.Pid));
            var now = pids.Count > 0 ? Native.TopWindowsOf(pids) : new List<IntPtr>();
            foreach (var h in now) if (Native.SetTaskbarButton(h, false)) _offTaskbar.Add(h);
            _offTaskbar.RemoveWhere(h => !Native.WindowExists(h));
        }

        void ShowLiveLaunchMenu(Button anchor = null)
        {
            var menu = new ContextMenu { PlacementTarget = anchor ?? LiveLaunch, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            // (opened after the menu has closed, or the menu's closing takes the list's focus and shuts it again)
            menu.Items.Add(MenuEntry("", "Launch from list…", (s, e) => Dispatcher.BeginInvoke(new Action(() => ShowLiveAccountPicker(anchor ?? LiveLaunch)), DispatcherPriority.Input)));
            menu.Items.Add(new Separator());
            foreach (var p in _profiles.Profiles)
            {
                var item = p;
                menu.Items.Add(MenuEntry("", item.Name, async (s, e) => await RunProfileAsync(item, true)));
            }
            if (_profiles.Profiles.Count > 0) menu.Items.Add(new Separator());
            int ticked = _mgr.Accounts.Count(a => a.IsSelected && !a.IsActive && a.State != AccountState.Expired);
            if (ticked > 0)
                menu.Items.Add(MenuEntry("", "Launch the " + ticked + " ticked " + (ticked == 1 ? "account" : "accounts") + " to the launch bar's game", async (s, e) => await OnLaunchClicked()));
            menu.Items.Add(MenuEntry("", "Pick accounts on the Accounts page", (s, e) => NavAccounts.IsChecked = true));
            menu.IsOpen = true;
        }

        /// <summary>
        /// A list of the accounts that aren't online: click one and it starts, to the game in the launch bar, without leaving Live.
        /// The list stays open so several can be started one after the other; a started account drops out of it.
        /// </summary>
        void ShowLiveAccountPicker(FrameworkElement anchor)
        {
            var free = _mgr.Accounts.Where(a => !a.IsActive && a.CanAct && a.State != AccountState.Expired).ToList();
            int expired = _mgr.Accounts.Count(a => a.State == AccountState.Expired);
            if (free.Count == 0)
            {
                ShowToast(_mgr.Accounts.Count == 0 ? "No accounts yet." : expired > 0 && expired == _mgr.Accounts.Count ? "Every account needs signing in again." : "Every account is already online.", _mgr.Accounts.Count == 0);
                return;
            }

            ApplyTarget();
            string game = _parsed.Ok ? TargetInfo.Text : null;

            var popup = new System.Windows.Controls.Primitives.Popup
            {
                PlacementTarget = anchor,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true,
                PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade
            };

            var list = new StackPanel();
            var rows = new List<(Border row, AccountItem item)>();
            var empty = T("No account matches.", "BrushTextTertiary", 12.5); empty.Margin = new Thickness(10, 8, 10, 8); empty.Visibility = Visibility.Collapsed;
            var filter = new TextBox { Margin = new Thickness(0, 12, 0, 0), Visibility = free.Count > 6 ? Visibility.Visible : Visibility.Collapsed };
            Ui.SetPlaceholder(filter, "Search accounts");

            void Sync()
            {
                var q = filter.Text.Trim();
                foreach (var (row, item) in rows)
                    row.Visibility = row.Tag == null && (q.Length == 0 || new[] { item.Display, item.Data.Username, item.Group }.Any(x => (x ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0))
                        ? Visibility.Visible : Visibility.Collapsed;
                empty.Visibility = rows.Any(r => r.row.Visibility == Visibility.Visible) ? Visibility.Collapsed : Visibility.Visible;
            }

            async Task StartAsync(Border row, AccountItem item)
            {
                try
                {
                    if (!TryGetTarget(out var target)) { popup.IsOpen = false; NavAccounts.IsChecked = true; return; }   // the launch bar is on the Accounts page
                    row.Tag = "started"; Sync();
                    if (!rows.Any(r => r.row.Tag == null)) popup.IsOpen = false;
                    await LaunchLiveAccountAsync(item, target);
                }
                catch (Exception ex) { Logger.Write("Live launch: " + ex.Message); }
            }

            foreach (var a in free)
            {
                var item = a;
                // (the Accounts page's avatar, a little smaller so more accounts fit)
                var avatar = new Viewbox { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center,
                                           Child = new ContentPresenter { ContentTemplate = (DataTemplate)FindResource("AvatarTemplate"), Content = item } };
                var name = T(item.Display, "BrushText", 13.5, true);
                bool failed = item.State == AccountState.Error;
                string sub = item.State == AccountState.Idle && !(item.Exit?.Unexpected ?? false) && item.Tagline.Length > 0 ? item.Tagline : item.StatusText;
                var info = T(sub, failed ? "BrushDanger" : "BrushTextTertiary", 11.5); info.TextTrimming = TextTrimming.CharacterEllipsis;
                var text = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(name); text.Children.Add(info);
                var play = new TextBlock { Style = (Style)FindResource("Icon"), Text = "", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.55 }.IconSize(13);
                play.SetResourceReference(TextBlock.ForegroundProperty, "BrushAccentText");

                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(text, 1); Grid.SetColumn(play, 2);
                g.Children.Add(avatar); g.Children.Add(text); g.Children.Add(play);
                if (StreamBlurNames) { avatar.Effect = _nameBlur; name.Effect = _nameBlur; info.Effect = _nameBlur; }

                var row = new Box("RadiusMd") { Padding = new Thickness(8, 5, 10, 5), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = g, ToolTip = "Launch " + (StreamBlurNames ? "this account" : item.Display) };
                row.MouseEnter += (s, e) => { row.SetResourceReference(Border.BackgroundProperty, "BrushControlHover"); play.Opacity = 1; };
                row.MouseLeave += (s, e) => { row.Background = Brushes.Transparent; play.Opacity = 0.55; };
                row.MouseLeftButtonUp += async (s, e) => await StartAsync(row, item);
                rows.Add((row, item));
                list.Children.Add(row);
            }
            filter.TextChanged += (s, e) => Sync();
            filter.KeyDown += async (s, e) =>
            {
                if (e.Key == Key.Escape) popup.IsOpen = false;
                else if (e.Key == Key.Enter) { var first = rows.FirstOrDefault(r => r.row.Visibility == Visibility.Visible); if (first.row != null) await StartAsync(first.row, first.item); }
            };

            var title = T("Launch from list", "BrushText", 14, true);
            var sub2 = T(game ?? "No game set yet: pick an account to open the launch bar", game != null ? "BrushTextSecondary" : "BrushWarning", 11.5);
            sub2.TextTrimming = TextTrimming.CharacterEllipsis; sub2.Margin = new Thickness(0, 4, 0, 0);
            if (StreamBlurNames) sub2.Text = MaskNames(sub2.Text);

            // (the rows stop short of the right edge so the scrollbar has its own strip and doesn't sit on the play icons)
            list.Margin = new Thickness(0, 0, 14, 0);
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 340, Margin = new Thickness(-6, 12, -6, 0), Content = list };
            var body = new StackPanel { Width = 340 };
            body.Children.Add(title); body.Children.Add(sub2); body.Children.Add(filter); body.Children.Add(scroll); body.Children.Add(empty);
            if (expired > 0)
            {
                var note = T(expired + (expired == 1 ? " account needs" : " accounts need") + " signing in again (Accounts page).", "BrushTextTertiary", 11.5);
                note.Margin = new Thickness(0, 10, 0, 0); note.TextWrapping = TextWrapping.Wrap;
                body.Children.Add(note);
            }

            var card = new Box("RadiusLg") { BorderThickness = new Thickness(1), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(12), Child = body,
                                    Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.35, Color = Colors.Black } };
            card.SetResourceReference(Border.BackgroundProperty, "BrushSurface");           // (opaque, like the app's menus)
            card.SetResourceReference(Border.BorderBrushProperty, "BrushSurfaceStroke");
            popup.Child = card;
            popup.IsOpen = true;
            if (filter.Visibility == Visibility.Visible) Dispatcher.BeginInvoke(new Action(() => filter.Focus()), DispatcherPriority.Input);
        }

        /// <summary>What is wrong with a running window, if anything: (text, serious). The same checks as Pulse's "needs a look".</summary>
        static (string text, bool bad)? LiveAlert(AccountItem a, DateTime now)
        {
            if (a.State != AccountState.Running) return null;
            if (a.StillSince.HasValue && now - a.StillSince.Value >= PulseNow.StillFor) return ("Frozen: uses no CPU", true);
            if (a.JoinedAt.HasValue && a.PresenceAt.HasValue && now - a.PresenceAt.Value <= PulseNow.PresenceFresh && a.PresenceType != 2)
                return ("Roblox says it isn't in a game", false);
            if (!a.JoinedAt.HasValue && !a.PickedUp && a.StartedAt.HasValue && now - a.StartedAt.Value > InstanceManager.JoinWatchFor)
                return ("No server join in " + (int)InstanceManager.JoinWatchFor.TotalMinutes + " min", false);
            return null;
        }

        // stream mode
        /// <summary>On for this session only (see SettingsData.StreamHideRail).</summary>
        bool _streamOn;
        readonly System.Windows.Media.Effects.BlurEffect _nameBlur = new System.Windows.Media.Effects.BlurEffect { Radius = 9, KernelType = System.Windows.Media.Effects.KernelType.Gaussian };
        ImageSource _ownIcon;
        static readonly ImageSource BlankIcon = BitmapBlank();

        bool StreamBlurNames => _streamOn && _s.StreamBlurNames;
        /// <summary>Tiles show Lantern's own copy of each picture (names blurred) instead of Windows' live thumbnail.</summary>
        bool StreamCapture => StreamBlurNames && PageLive.Visibility == Visibility.Visible;

        static ImageSource BitmapBlank()
        {
            var b = System.Windows.Media.Imaging.BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, new byte[16 * 16 * 4], 16 * 4);
            b.Freeze();
            return b;
        }

        /// <summary>Lantern's own messages while stream mode blurs names: every account name (any of its forms) becomes dots.</summary>
        internal string MaskNames(string text)
        {
            var forms = _mgr.Accounts.SelectMany(a => new[] { a.Display, a.Data.Username, a.Data.DisplayName, a.Data.Alias })
                                     .Where(n => !string.IsNullOrWhiteSpace(n) && n.Trim().Length >= 2).Select(n => n.Trim())
                                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Length);
            foreach (var n in forms)
                text = System.Text.RegularExpressions.Regex.Replace(text, System.Text.RegularExpressions.Regex.Escape(n), "•••••", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return text;
        }

        internal void SetStreamMode(bool on)
        {
            _streamOn = on;
            ApplyStreamMode();
            RefreshLive();
            RefreshLiveSide();
        }

        /// <summary>Stream mode hides the left rail on the Live page, so no button of Lantern's own is in the audience's picture.</summary>
        void ApplyStreamRail()
        {
            bool hide = _streamOn && _s.StreamHideRail && PageLive.Visibility == Visibility.Visible;
            NavRail.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
            Shell.ColumnDefinitions[0].Width = new GridLength(hide ? 0 : 76);
            if (hide) ContentPane.CornerRadius = new CornerRadius(0); else ContentPane.SetResourceReference(Border.CornerRadiusProperty, "RadiusPaneTL");
            ContentPane.BorderThickness = new Thickness(hide ? 0 : 1, 1, 0, 0);
            if (PageLive.Visibility == Visibility.Visible) LayoutLive();
        }

        /// <summary>
        /// Applies what stream mode hides. The left panel only goes while the Live page is showing (it is the way to every other
        /// page); the title becomes a neutral one, which is also what Discord's window picker lists.
        /// </summary>
        void ApplyStreamMode()
        {
            if (_s == null || NavRail == null) return;
            ApplyStreamRail();

            bool title = _streamOn && _s.StreamHideTitle;
            if (_ownIcon == null) _ownIcon = Icon;
            Title = title ? "Live view" : "Lantern";
            Icon = title ? BlankIcon : _ownIcon;

            LiveStreamLabel.Text = _streamOn ? "Stream mode: on" : "Stream mode";
            // (clearing, not null: a null Style would drop the app's own button look for Windows' classic one)
            if (_streamOn) LiveStream.Style = (Style)FindResource("AccentButton"); else LiveStream.ClearValue(StyleProperty);
        }

        void ShowStreamMenu()
        {
            var menu = new ContextMenu { PlacementTarget = LiveStream, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            MenuItem Check(string header, bool value, Action<bool> set, string tip = null)
            {
                var mi = new MenuItem { Header = header, IsCheckable = true, IsChecked = value, StaysOpenOnClick = true, ToolTip = tip };
                mi.Click += (s, e) => set(mi.IsChecked);
                menu.Items.Add(mi);
                return mi;
            }
            Check("Stream mode", _streamOn, SetStreamMode);
            menu.Items.Add(new Separator());
            Check("Blur account names", _s.StreamBlurNames, v => { _s.StreamBlurNames = v; _mgr.SaveSettings(); SetStreamMode(_streamOn); },
                  "In the tiles' headers, the side panel, and inside the games' pictures (leaderboards, chat, name tags): Windows' text reader finds your accounts' names and only those words are blurred. The tiles then refresh about five times a second.");
            Check("Hide the left panel", _s.StreamHideRail, v => { _s.StreamHideRail = v; _mgr.SaveSettings(); SetStreamMode(_streamOn); },
                  "Only on this page. Turn stream mode off to get it back.");
            Check("Hide the program name", _s.StreamHideTitle, v => { _s.StreamHideTitle = v; _mgr.SaveSettings(); SetStreamMode(_streamOn); },
                  "The title bar reads “Live view” with no icon, and so does Discord's list of windows to share.");
            menu.IsOpen = true;
        }

        // stream mode: the picture with the names blurred
        /// <summary>Up to 30 pictures a second per window; a game capped lower (20 fps in Balanced) simply delivers fewer.</summary>
        const int LiveCopyFrameMs = 33;
        bool _copyRunning, _ocrWarned;
        static readonly Random LiveStagger = new Random();
        /// <summary>The account names to look for, refreshed by the managing loop and read by every window's loop.</summary>
        volatile List<string> _copyNames = new List<string>();

        /// <summary>
        /// While stream mode blurs names, each window has its own loop (<see cref="PumpAsync"/>) feeding its
        /// <see cref="StreamCopier"/>; this one starts them, keeps the list of names current, and stops when stream mode does.
        /// </summary>
        async Task CopyLoopAsync()
        {
            _copyRunning = true;
            try
            {
                if (!await Task.Run(() => NameBlur.Available) && !_ocrWarned)
                {
                    _ocrWarned = true;
                    ShowToast("Windows' text reader isn't available here, so stream mode blurs the whole pictures. Adding a language with text recognition in Windows Settings fixes it.", true);
                }
                while (StreamCapture)
                {
                    _copyNames = NameBlur.CanonNames(_mgr.Accounts.SelectMany(a => new[] { a.Data.Username, a.Data.DisplayName, a.Data.Alias }));
                    var now = DateTime.UtcNow;
                    foreach (var t in _live.ToList())
                    {
                        if (t.Hwnd == IntPtr.Zero || now < t.SkipUntil || (t.Copier != null && t.Copier.Window == t.Hwnd)) continue;
                        if (t.Copier != null) ForgetCopy(t);
                        // made on a worker thread: the capture objects are then used from worker threads only, and the one-time
                        // graphics device setup doesn't hold up the window
                        var hwnd = t.Hwnd;
                        var offset = TimeSpan.FromMilliseconds(LiveStagger.Next(0, 1000));
                        var c = await Task.Run(() => new StreamCopier(hwnd, offset));
                        if (!StreamCapture || !_live.Contains(t) || t.Hwnd != hwnd || t.Copier != null) { _ = Task.Run(() => c.Dispose()); continue; }
                        t.Copier = c;
                        _ = PumpAsync(t, c);
                    }
                    await Task.Delay(250);
                }
            }
            catch (Exception ex) { Logger.Write("Live copy: " + ex.Message); }
            finally
            {
                _copyRunning = false;
                if (!StreamCapture) { foreach (var t in _live) if (t.Copier != null) ForgetCopy(t); BufferPool.Clear(); }
            }
        }

        /// <summary>
        /// One window's loop: a step about every 33 ms (copy the newest frame, hand out the next checked one) on a worker thread,
        /// the picture shown on the UI thread. Ends when stream mode ends, the tile goes, or the window was replaced; a window that
        /// doesn't answer within 2.5 s is left alone for 10 s and then starts over, blurred.
        /// </summary>
        async Task PumpAsync(LiveTile t, StreamCopier c)
        {
            try
            {
                while (StreamCapture && t.Copier == c && _live.Contains(t))
                {
                    var started = DateTime.UtcNow;
                    if (t.Paused || t.Card.Visibility != Visibility.Visible) { await Task.Delay(300); continue; }   // minimised, or hidden behind the zoomed tile: nothing to copy

                    double dpi = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
                    int tileWidth = Math.Max(64, (int)(t.Screen.ActualWidth * dpi));   // copies come at about the tile's size
                    var names = _copyNames;
                    var job = Task.Run(() => c.Step(names, tileWidth, DateTime.UtcNow));
                    if (await Task.WhenAny(job, Task.Delay(2500)) != job)
                    {
                        t.SkipUntil = DateTime.UtcNow.AddSeconds(10);
                        if (t.Copier == c) { t.Copier = null; t.Frame.Source = null; t.Bitmap = null; }
                        _ = job.ContinueWith(_ => c.Dispose(), TaskScheduler.Default);   // once the stuck step returns
                        return;
                    }
                    StreamFrame f = null;
                    try { f = job.Result; } catch (Exception ex) { Logger.Write("Live copy: " + ex.Message); }
                    if (f != null)
                    {
                        if (t.Copier == c && StreamCapture) { ShowCopy(t, f); t.Shown++; }
                        BufferPool.Return(f.Pixels);
                    }
                    int left = LiveCopyFrameMs - (int)(DateTime.UtcNow - started).TotalMilliseconds;
                    await Task.Delay(Math.Max(5, left));
                }
            }
            catch (Exception ex) { Logger.Write("Live pump: " + ex.Message); }
        }

        void ShowCopy(LiveTile t, StreamFrame f)
        {
            if (t.Bitmap == null || t.Bitmap.PixelWidth != f.Width || t.Bitmap.PixelHeight != f.Height)
            {
                t.Bitmap = new WriteableBitmap(f.Width, f.Height, 96, 96, PixelFormats.Bgr32, null);
                t.Frame.Source = t.Bitmap;
            }
            t.Bitmap.WritePixels(new Int32Rect(0, 0, f.Width, f.Height), f.Pixels, f.Width * 4, 0);
        }

        /// <summary>
        /// Drops a tile's copy and ends its capture, so it starts blurred again. The capture is released off the UI thread: a
        /// window that hangs could otherwise hold Lantern up while its last step finishes.
        /// </summary>
        static void ForgetCopy(LiveTile t)
        {
            var c = t.Copier;
            t.Frame.Source = null; t.Bitmap = null; t.Copier = null;
            if (c != null) Task.Run(() => c.Dispose());
        }

        // actions
        bool LiveDemoGuard()
        {
            if (!App.Demo) return false;
            ShowToast("Demo: windows aren't touched.", false);
            return true;
        }

        void PauseLive(LiveTile t)
        {
            if (t.Hwnd == IntPtr.Zero || LiveDemoGuard()) return;
            Native.MinimizeQuietly(t.Hwnd);
            t.Item.Log("Paused from Live");
        }

        void ResumeLive(LiveTile t)
        {
            if (t.Hwnd == IntPtr.Zero || LiveDemoGuard()) return;
            Native.RestoreBehind(t.Hwnd);
            t.Item.Log("Resumed from Live");
        }

        /// <summary>Lays the real window over the grid (or maximises it) and focuses it; clicking back into Lantern tucks it away.</summary>
        void PlayLive(LiveTile t, bool maximized)
        {
            if (t.Hwnd == IntPtr.Zero) { ShowToast(t.Item.Display + " has no window yet.", true); return; }
            if (LiveDemoGuard()) return;
            // where it was, to put it back (only the first time: playing another tile first returns the previous one)
            if (_livePlaying != IntPtr.Zero && _livePlaying != t.Hwnd && Native.WindowExists(_livePlaying) && _livePlayedFrom.HasValue)
                Native.PutBackBehind(_livePlaying, _livePlayedFrom.Value);
            if (_livePlaying != t.Hwnd) _livePlayedFrom = Native.NormalRect(t.Hwnd);
            if (maximized) Native.FocusMaximized(t.Hwnd);
            else
            {
                var tl = LiveArea.PointToScreen(new Point(0, 0));
                var br = LiveArea.PointToScreen(new Point(LiveArea.ActualWidth, LiveArea.ActualHeight));
                Native.PlaceOverAndFocus(t.Hwnd, new Native.RECT { Left = (int)tl.X, Top = (int)tl.Y, Right = (int)br.X, Bottom = (int)br.Y });
            }
            _livePlaying = t.Hwnd;
        }

        // the side panel: the Pulse numbers that matter
        void RefreshLiveSide()
        {
            if (PageLive.Visibility != Visibility.Visible || LiveSideBody == null) return;
            var now = DateTime.UtcNow;
            var n = PulseNow.From(_mgr.Accounts, now);
            if (_liveRep == null || now - _liveRepAt > TimeSpan.FromSeconds(30)) { _liveRep = BuildPulse("24h", now); _liveRepAt = now; }
            var rep = _liveRep;
            var p = LiveSideBody;
            p.Children.Clear();

            void Head(string text, double top)
            {
                var h = T(text, "BrushTextSecondary", 12, true); h.Margin = new Thickness(0, top, 0, 6);
                p.Children.Add(h);
            }
            void Row(string label, string value, string dot = null, string valueBrush = "BrushText", string tip = null)
            {
                var g = new Grid { Margin = new Thickness(0, 3, 0, 3), ToolTip = tip, Background = Brushes.Transparent };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var left = new StackPanel { Orientation = Orientation.Horizontal };
                if (dot != null)
                {
                    var e = new Dot { Width = 7, Height = 7, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                    e.SetResourceReference(Border.BackgroundProperty, dot);
                    left.Children.Add(e);
                }
                left.Children.Add(T(label, "BrushTextSecondary", 12.5));
                var v = T(value, valueBrush, 13, true); Grid.SetColumn(v, 1);
                g.Children.Add(left); g.Children.Add(v);
                p.Children.Add(g);
            }
            string Zone(string z) => z == "good" ? "BrushSuccess" : z == "warn" ? "BrushWarning" : z == "bad" ? "BrushDangerFill" : null;

            // in a game right now
            var count = new StackPanel { Orientation = Orientation.Horizontal };
            var big = T(n.InGame.ToString(CultureInfo.CurrentCulture), n.InGame > 0 ? "BrushAccentText" : "BrushTextTertiary", 34, true);
            big.SetResourceReference(TextBlock.FontFamilyProperty, "FontDisplay");
            var of = T(" / " + n.Running.ToString(CultureInfo.CurrentCulture), "BrushTextTertiary", 18); of.VerticalAlignment = VerticalAlignment.Bottom; of.Margin = new Thickness(0, 0, 0, 5);
            count.Children.Add(big); count.Children.Add(of);
            p.Children.Add(count);
            p.Children.Add(T(n.Running == 1 ? "running window in a game" : "running windows in a game", "BrushTextSecondary", 12.5));
            int off = Math.Max(0, n.Accounts - n.Running - n.Starting);
            p.Children.Add(T(n.Accounts + (n.Accounts == 1 ? " account" : " accounts") + (off > 0 ? "  ·  " + off + " not running" : ""), "BrushTextTertiary", 12));

            int trouble = n.Attention + n.Still + n.Mismatch + n.NotJoined;
            string status, dotBrush;
            if (n.Running == 0 && n.Starting == 0) { status = "Nothing running"; dotBrush = "BrushTextTertiary"; }
            else if (trouble > 0) { status = trouble == 1 ? "1 needs a look" : trouble + " need a look"; dotBrush = n.Attention > 0 || n.Still > 0 ? "BrushDangerFill" : "BrushWarning"; }
            else if (n.InGame == n.Running && n.Starting == 0) { status = "Every window is in a game"; dotBrush = "BrushSuccess"; }
            else { status = "Getting there"; dotBrush = "BrushWarning"; }
            var chip = StatusChip(status, dotBrush); chip.HorizontalAlignment = HorizontalAlignment.Left; chip.Margin = new Thickness(0, 10, 0, 4);
            p.Children.Add(chip);

            Head("Windows", 16);
            Row("In a game", n.InGame.ToString(CultureInfo.CurrentCulture), "BrushAccent");
            Row("Loading", (n.Loading + n.Unconfirmed).ToString(CultureInfo.CurrentCulture), "BrushWarning");
            Row("Starting / rejoining", n.Starting.ToString(CultureInfo.CurrentCulture), "BrushTextTertiary");
            Row("Paused", _live.Count(t => t.Paused).ToString(CultureInfo.CurrentCulture), "BrushStroke", tip: "Minimised: not drawing, its tile shows the last picture.");
            if (n.Attention > 0) Row("Needs you", n.Attention.ToString(CultureInfo.CurrentCulture), "BrushDangerFill", "BrushDanger");
            if (n.Still > 0) Row("Frozen", n.Still.ToString(CultureInfo.CurrentCulture), "BrushDangerFill", "BrushDanger", "Running but using no CPU at all for 90 s.");
            if (n.NotJoined > 0) Row("No join seen", n.NotJoined.ToString(CultureInfo.CurrentCulture), "BrushWarning");

            foreach (var (name, what) in n.Flags.Take(4))
            {
                var f = T((StreamBlurNames ? "" : name + ": ") + what, "BrushWarning", 12); f.TextWrapping = TextWrapping.Wrap; f.Margin = new Thickness(0, 4, 0, 0);
                p.Children.Add(f);
            }

            Head("This PC", 18);
            Native.Memory(out var total, out var avail);
            if (n.Running > 0)
            {
                Row("Roblox CPU", Fmt.Percent(n.Cpu));
                Row("Roblox RAM", Fmt.Memory(n.WorkingSet));
            }
            if (_s.TrimVisible)
            {
                long kept = _mgr.VisibleTrimKept;
                Row("Kept off by trimming", kept > 0 ? Fmt.Memory(kept) : "measuring…", valueBrush: kept > 0 ? "BrushText" : "BrushTextTertiary",
                    tip: "What the open windows have NOT taken back half a minute after their last trim, all added up: the real saving, not the dip right after a trim.");
            }
            if (total > 0)
            {
                double used = 1 - (double)avail / total;
                Row("Free RAM", Fmt.Memory((long)avail) + " of " + Fmt.Memory((long)total), valueBrush: used > 0.9 ? "BrushDanger" : "BrushText");
                var track = new Grid { Margin = new Thickness(0, 4, 0, 2) };
                track.SetResourceReference(HeightProperty, "MeterHeight");
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(used, 0.001), GridUnitType.Star) });
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - used, 0.001), GridUnitType.Star) });
                var rest = new Box("RadiusXs"); rest.SetResourceReference(Border.BackgroundProperty, "BrushStroke"); Grid.SetColumnSpan(rest, 2);
                var fill = new Box("RadiusXs"); fill.SetResourceReference(Border.BackgroundProperty, used > 0.9 ? "BrushDangerFill" : used > 0.8 ? "BrushWarning" : "BrushAccent");
                track.Children.Add(rest); track.Children.Add(fill);
                p.Children.Add(new Box("RadiusXs") { ClipToBounds = true, Child = track });
            }

            p = LiveSideBody2;
            p.Children.Clear();
            Head("Last 24 hours", 18);
            if (rep.Empty) p.Children.Add(T("Nothing recorded yet.", "BrushTextTertiary", 12.5));
            else
            {
                Row("In a game", Pct(rep.InGameShare), Zone(ZoneHigh(rep.InGameShare, 0.97, 0.9)), tip: "Share of window time spent inside a server.");
                Row("Launches that worked", Pct(rep.LaunchSuccess), Zone(ZoneHigh(rep.LaunchSuccess, 0.98, 0.9)));
                Row("Problems per 10 h", rep.ProblemsPer10h.HasValue ? rep.ProblemsPer10h.Value.ToString("0.0", CultureInfo.CurrentCulture) : "—", Zone(ZoneLow(rep.ProblemsPer10h, 0.5, 2)),
                    tip: "Crashes and drops per 10 hours of window time.");
                Row("Crashes", rep.Crashes.ToString(CultureInfo.CurrentCulture), rep.Crashes == 0 ? "BrushSuccess" : "BrushDangerFill");
                Row("Auto rejoins", rep.AutoRejoins.ToString(CultureInfo.CurrentCulture));
                Row("Lost to rejoins", Fmt.Duration(rep.LostToRejoins));
            }

            var open = new Button { Content = "Open Pulse", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 18, 0, 0) };
            open.Click += (s, e) => NavPulse.IsChecked = true;
            p.Children.Add(open);
        }
    }
}
