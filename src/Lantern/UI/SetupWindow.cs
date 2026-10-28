using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Lantern.Core;

namespace Lantern.UI
{
    /// <summary>
    /// The first-run setup: a thank-you with a short pause (and a nudge to star the project), what Lantern is and the disclaimer
    /// (which has to be accepted), the theme, how your Roblox sign-ins are kept safe, and two last switches. Closing the window
    /// before the end counts as declining.
    /// </summary>
    public sealed class SetupWindow : Window
    {
        const int PageCount = 5;
        /// <summary>How long the first page holds Continue back.</summary>
        const double ThanksSeconds = 5;
        const double RingSize = 84, RingStroke = 6;

        readonly SettingsData _s;
        readonly Grid _stage = new Grid();
        readonly ScrollViewer _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        readonly StackPanel _dots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        readonly Button _back = new Button { Content = "Back", MinWidth = 88 };
        readonly Button _next = new Button { MinWidth = 120 };
        readonly Dictionary<string, RadioButton> _themeTiles = new Dictionary<string, RadioButton>();
        readonly Dictionary<string, Border> _themeMocks = new Dictionary<string, Border>();
        readonly UIElement[] _pages = new UIElement[PageCount];
        CheckBox _accept;
        int _page;
        bool _building;

        // the first page's countdown
        readonly DispatcherTimer _thanksTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        readonly Stopwatch _thanksClock = new Stopwatch();
        Ellipse _ring;
        TextBlock _ringNumber, _ringCaption;
        bool _thanksDone;
        int _thanksLeft = (int)ThanksSeconds;

        /// <summary>True once the last page was confirmed.</summary>
        public bool Completed { get; private set; }

        public SetupWindow(SettingsData settings)
        {
            _s = settings;
            Title = "Welcome to Lantern";
            Width = 700; Height = Math.Min(780, SystemParameters.WorkArea.Height - 40); MinWidth = 560; MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            SetResourceReference(BackgroundProperty, "BrushSurface");
            SetResourceReference(ForegroundProperty, "BrushText");
            SetResourceReference(FontFamilyProperty, "FontText");
            SetResourceReference(FontSizeProperty, "BaseFontSize");
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Lantern;component/Assets/lantern.ico"));

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _scroll.Content = _stage;
            _stage.Margin = new Thickness(36, 32, 36, 16);
            root.Children.Add(_scroll);

            var footer = new Grid { Margin = new Thickness(36, 8, 36, 24) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.Children.Add(_dots);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            _next.SetResourceReference(StyleProperty, "AccentButton");
            _back.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(_back);
            buttons.Children.Add(_next);
            Grid.SetColumn(buttons, 1);
            footer.Children.Add(buttons);
            Grid.SetRow(footer, 1);
            root.Children.Add(footer);
            Content = root;

            for (int i = 0; i < PageCount; i++)
            {
                var dot = new Border { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0) };
                dot.SetResourceReference(Border.CornerRadiusProperty, "RadiusPill");
                _dots.Children.Add(dot);
            }

            _back.Click += (s, e) => Go(_page - 1);
            _next.Click += (s, e) =>
            {
                if (_page < PageCount - 1) { Go(_page + 1); return; }
                _s.SetupDone = true;
                Completed = true;
                Close();
            };
            SourceInitialized += (s, e) => Native.SetDarkTitleBar(new WindowInteropHelper(this).Handle, Theme.IsDark);
            Theme.Changed += OnThemeChanged;
            Closed += (s, e) => { Theme.Changed -= OnThemeChanged; _thanksTimer.Stop(); };
            _thanksTimer.Tick += (s, e) => TickThanks();
            ContentRendered += (s, e) => { if (!_thanksDone && !_thanksClock.IsRunning) { _thanksClock.Start(); _thanksTimer.Start(); } };
            Go(0);
        }

        void OnThemeChanged()
        {
            Native.SetDarkTitleBar(new WindowInteropHelper(this).Handle, Theme.IsDark);
            Refresh();
        }

        void Go(int page)
        {
            _page = Math.Max(0, Math.Min(PageCount - 1, page));
            if (_pages[_page] == null) _pages[_page] = BuildPage(_page);
            _stage.Children.Clear();
            _stage.Children.Add(_pages[_page]);
            Refresh();
            Dispatcher.BeginInvoke(new Action(() => _scroll.ScrollToTop()), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        void Refresh()
        {
            for (int i = 0; i < _dots.Children.Count; i++)
                ((Border)_dots.Children[i]).SetResourceReference(Border.BackgroundProperty, i == _page ? "BrushAccent" : "BrushStroke");
            _back.Visibility = _page == 0 ? Visibility.Collapsed : Visibility.Visible;
            _next.Content = _page == PageCount - 1 ? "Start using Lantern"
                : _page == 0 && !_thanksDone ? "Continue in " + _thanksLeft + " s" : "Continue";
            _next.IsEnabled = _page == 0 ? _thanksDone : _page != 1 || (_accept != null && _accept.IsChecked == true);
        }

        UIElement BuildPage(int page)
        {
            switch (page)
            {
                case 0: return ThanksPage();
                case 1: return WelcomePage();
                case 2: return ThemePage();
                case 3: return SecurityPage();
                default: return FinishPage();
            }
        }

        // building blocks
        TextBlock Text(string text, string brush = "BrushText", double size = 0, bool bold = false, Thickness? margin = null)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0) };
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            if (size > 0) t.FontSize = size;
            if (bold) t.FontWeight = FontWeights.SemiBold;
            return t;
        }

        TextBlock Heading(string text)
        {
            var t = Text(text, size: 26, bold: true);
            t.SetResourceReference(TextBlock.FontFamilyProperty, "FontDisplay");
            return t;
        }

        /// <summary>A rounded panel in the app's card colours.</summary>
        Border Card(UIElement child, string background = "BrushControl", string stroke = "BrushControlStroke", Thickness? margin = null)
        {
            var b = new Border { Child = child, Padding = new Thickness(16, 14, 16, 14), BorderThickness = new Thickness(1), Margin = margin ?? new Thickness(0, 0, 0, 10) };
            b.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
            b.SetResourceReference(Border.BackgroundProperty, background);
            b.SetResourceReference(Border.BorderBrushProperty, stroke);
            return b;
        }

        /// <summary>A line of icon + text, used for the lists on the first and third page.</summary>
        UIElement Point(string glyph, string brush, string title, string body)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = new TextBlock { Text = glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "FontIcons");
            icon.SetResourceReference(TextBlock.ForegroundProperty, brush);
            row.Children.Add(icon);
            var col = new StackPanel();
            col.Children.Add(Text(title, bold: true));
            col.Children.Add(Text(body, "BrushTextSecondary", 13, margin: new Thickness(0, 2, 0, 0)));
            Grid.SetColumn(col, 1);
            row.Children.Add(col);
            return row;
        }

        static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Logger.Write("Open link: " + ex.Message); }
        }

        static void OpenFolder(string path)
        {
            try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { Logger.Write("Open folder: " + ex.Message); }
        }

        // page 1: thanks, a short pause, and a nudge to star the project
        UIElement ThanksPage()
        {
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };

            // the lantern, with its light behind it
            Color accent = (Application.Current.TryFindResource("BrushAccent") as SolidColorBrush)?.Color ?? Color.FromRgb(0xff, 0xa0, 0x4a);
            var logo = new Grid { Width = 190, Height = 150, HorizontalAlignment = HorizontalAlignment.Center };
            logo.Children.Add(new Ellipse
            {
                Width = 190, Height = 150,
                Fill = new RadialGradientBrush(new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0x70, accent.R, accent.G, accent.B), 0),
                    new GradientStop(Color.FromArgb(0x28, accent.R, accent.G, accent.B), 0.55),
                    new GradientStop(Color.FromArgb(0x00, accent.R, accent.G, accent.B), 1)
                })
            });
            logo.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri("pack://application:,,,/Lantern;component/Assets/lantern-256.png")),
                Width = 96, Height = 96, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            });
            sp.Children.Add(logo);

            var title = Heading("Thank you for using Lantern");
            title.TextAlignment = TextAlignment.Center;
            title.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(title);

            var line = Text("Lantern is free, with no ads, no accounts and no telemetry. I make it in my spare time, and it is nice to know somebody uses it.",
                "BrushTextSecondary", 14, margin: new Thickness(0, 10, 0, 0));
            line.TextAlignment = TextAlignment.Center;
            line.MaxWidth = 470;
            sp.Children.Add(line);

            var ask = Text("If you like it, a star on GitHub is the best thank-you there is. It takes two clicks and helps other people find it.",
                margin: new Thickness(0, 14, 0, 0));
            ask.TextAlignment = TextAlignment.Center;
            ask.MaxWidth = 470;
            sp.Children.Add(ask);

            var star = new Button { Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(18, 8, 18, 8) };
            var starRow = new StackPanel { Orientation = Orientation.Horizontal };
            var glyph = new TextBlock { Text = "", FontSize = 17, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            glyph.SetResourceReference(TextBlock.FontFamilyProperty, "FontIcons");
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "BrushWarning");
            starRow.Children.Add(glyph);
            starRow.Children.Add(new TextBlock { Text = "Star Lantern on GitHub", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            star.Content = starRow;
            star.ToolTip = About.RepositoryUrl;
            star.Click += (s, e) => OpenUrl(About.RepositoryUrl);
            sp.Children.Add(star);

            // the countdown: a ring that fills as the seconds go by
            var ringHost = new Grid { Width = RingSize, Height = RingSize, Margin = new Thickness(0, 26, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            var track = new Ellipse { Width = RingSize, Height = RingSize, StrokeThickness = RingStroke };
            track.SetResourceReference(Shape.StrokeProperty, "BrushStroke");
            ringHost.Children.Add(track);
            _ring = new Ellipse
            {
                Width = RingSize, Height = RingSize, StrokeThickness = RingStroke,
                StrokeStartLineCap = PenLineCap.Flat, StrokeEndLineCap = PenLineCap.Flat,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(-90)
            };
            _ring.SetResourceReference(Shape.StrokeProperty, "BrushAccent");
            ringHost.Children.Add(_ring);
            _ringNumber = new TextBlock { FontSize = 28, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _ringNumber.SetResourceReference(TextBlock.FontFamilyProperty, "FontDisplay");
            _ringNumber.SetResourceReference(TextBlock.ForegroundProperty, "BrushText");
            ringHost.Children.Add(_ringNumber);
            sp.Children.Add(ringHost);

            _ringCaption = Text("", "BrushTextTertiary", 12, margin: new Thickness(0, 10, 0, 0));
            _ringCaption.TextAlignment = TextAlignment.Center;
            sp.Children.Add(_ringCaption);

            UpdateThanks(_thanksClock.Elapsed.TotalSeconds);
            return sp;
        }

        void TickThanks()
        {
            UpdateThanks(_thanksClock.Elapsed.TotalSeconds);
            if (_thanksDone) _thanksTimer.Stop();
            Refresh();
        }

        void UpdateThanks(double elapsed)
        {
            double p = Math.Max(0, Math.Min(1, elapsed / ThanksSeconds));
            if (p >= 1) _thanksDone = true;
            _thanksLeft = _thanksDone ? 0 : (int)Math.Ceiling(ThanksSeconds - elapsed);
            if (_ring == null) return;
            // dash lengths are in stroke widths; the circle's length is pi * (diameter - stroke)
            double circ = Math.PI * (RingSize - RingStroke) / RingStroke;
            _ring.StrokeDashArray = new DoubleCollection { p * circ, circ + 4 };
            if (_thanksDone)
            {
                _ringNumber.Text = "";
                _ringNumber.FontSize = 30;
                _ringNumber.SetResourceReference(TextBlock.FontFamilyProperty, "FontIcons");
                _ringNumber.SetResourceReference(TextBlock.ForegroundProperty, "BrushSuccess");
                _ringCaption.Text = "Thanks for waiting. Press Continue whenever you like.";
            }
            else
            {
                _ringNumber.Text = _thanksLeft.ToString();
                _ringCaption.Text = "Continue unlocks in a moment. The star is optional.";
            }
        }

        // page 2: welcome and disclaimer
        UIElement WelcomePage()
        {
            var sp = new StackPanel();
            sp.Children.Add(Heading("Welcome to Lantern"));
            sp.Children.Add(Text("A Roblox account manager. Sign in once, tick the accounts you want, pick a game and press Launch.",
                "BrushTextSecondary", 14, margin: new Thickness(0, 6, 0, 22)));

            var warn = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var warnIcon = new TextBlock { Text = "", FontSize = 18, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            warnIcon.SetResourceReference(TextBlock.FontFamilyProperty, "FontIcons");
            warnIcon.SetResourceReference(TextBlock.ForegroundProperty, "BrushWarning");
            head.Children.Add(warnIcon);
            head.Children.Add(Text("Please read this first", size: 16, bold: true));
            warn.Children.Add(head);
            warn.Children.Add(Text("Lantern is an unofficial, independent project. Roblox did not make it, doesn't endorse it and doesn't support it.", margin: new Thickness(0, 0, 0, 8)));
            warn.Children.Add(Text("You use it at your own risk. I (" + About.Author + ") take no responsibility for your Roblox accounts: bans, locked or stolen accounts, " +
                "lost items or Robux, or anything else that comes of running several accounts. Roblox's rules can change, and so can what they allow.", margin: new Thickness(0, 0, 0, 8)));
            warn.Children.Add(Text("I have no access to your accounts and never see them. Nothing is sent to me; there is no server of mine to send it to.", "BrushTextSecondary", 13));
            sp.Children.Add(Card(warn, "BrushAccentSubtle", "BrushAccent", new Thickness(0, 0, 0, 10)));
            var accept = SwitchRow("I have read this and I accept it", null, false, v => Refresh(), out _accept);
            accept.Margin = new Thickness(0, 0, 0, 22);
            sp.Children.Add(accept);

            sp.Children.Add(Text("What it does", size: 15, bold: true, margin: new Thickness(0, 0, 0, 10)));
            sp.Children.Add(Point("", "BrushAccentText", "Keeps your accounts together", "Add them by signing in or by pasting a cookie, group them, and launch any mix in one click."));
            sp.Children.Add(Point("", "BrushAccentText", "Opens a Roblox window for each", "One after another, with a delay, into the game, private server or friend's server you choose."));
            sp.Children.Add(Point("", "BrushAccentText", "Rejoins when a window drops", "If a window crashes or Roblox disconnects it, Lantern can close it and join again. It is off until you switch it on."));
            sp.Children.Add(Point("", "BrushAccentText", "Keeps many windows light", "Optional low-resource modes, live CPU and memory numbers, and a grid that shows every window at once."));

            return sp;
        }

        // page 3: theme
        UIElement ThemePage()
        {
            var sp = new StackPanel();
            sp.Children.Add(Heading("Pick a theme"));
            sp.Children.Add(Text("It changes as you click, so you can see it. You can change it any time in Settings → Appearance.",
                "BrushTextSecondary", 14, margin: new Thickness(0, 6, 0, 22)));

            var tiles = new WrapPanel();
            foreach (var choice in MainWindow.ThemeChoices)
            {
                var mock = new Border();
                var tile = MainWindow.ThemeTile(choice.Name, choice.Caption, mock);
                tile.Width = 194;
                tile.Margin = new Thickness(0, 0, 6, 10);
                var value = choice.Value;
                tile.Checked += (s, e) =>
                {
                    if (_building) return;
                    _s.Theme = value;
                    Theme.Apply(value);
                    RefreshThemeMocks();
                };
                _themeTiles[value] = tile;
                _themeMocks[value] = mock;
                tiles.Children.Add(tile);
            }
            sp.Children.Add(tiles);
            _building = true;
            _themeTiles[Theme.Normalize(_s.Theme)].IsChecked = true;
            _building = false;
            RefreshThemeMocks();

            sp.Children.Add(Text("Lantern always uses your Windows accent colour, so it sits in with the rest of your desktop.", "BrushTextTertiary", 12, margin: new Thickness(0, 10, 0, 0)));
            return sp;
        }

        void RefreshThemeMocks()
        {
            foreach (var kv in _themeMocks) kv.Value.Child = MainWindow.Mock(Theme.Preview(kv.Key), 176, 104);
        }

        // page 4: how cookies are kept safe
        UIElement SecurityPage()
        {
            var sp = new StackPanel();
            sp.Children.Add(Heading("How your sign-ins are kept safe"));
            sp.Children.Add(Text("Lantern never stores a password. It keeps the cookie Roblox gives your browser after you sign in, and a cookie is as good as a password, so it is treated like one.",
                "BrushTextSecondary", 14, margin: new Thickness(0, 6, 0, 22)));

            sp.Children.Add(Point("", "BrushSuccess", "Encrypted on this PC", "Saved in accounts.dat, locked with Windows' own data protection (DPAPI) under your Windows login. Other users and other PCs can't open it."));

            // real ciphertext, made with the same call the account file uses
            var demo = new StackPanel();
            demo.Children.Add(Text("This is how a made-up cookie would look in that file:", "BrushTextSecondary", 12));
            string cipher;
            try { cipher = Store.ProtectedPreview("not-a-real-cookie", 40) + "…"; }
            catch { cipher = "(not available on this PC)"; }
            var mono = Text(cipher, "BrushTextTertiary", 12, margin: new Thickness(0, 6, 0, 0));
            mono.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            mono.TextWrapping = TextWrapping.Wrap;
            demo.Children.Add(mono);
            var open = new Button { Content = "Open the data folder", Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            open.Click += (s, e) => OpenFolder(Paths.Root);
            demo.Children.Add(open);
            demo.Children.Add(Text(Paths.Root, "BrushTextTertiary", 12, margin: new Thickness(0, 8, 0, 0)));
            sp.Children.Add(Card(demo, margin: new Thickness(30, 0, 0, 14)));

            sp.Children.Add(Point("", "BrushSuccess", "Only ever sent to roblox.com", "The code refuses to attach a cookie to any other address and never follows a redirect off Roblox. There is no server, no telemetry and no updater."));
            sp.Children.Add(Point("", "BrushSuccess", "The sign-in window is disposable", "It is a private browser window with a temporary profile that is cleared and deleted when it closes. It can only visit roblox.com, and only the one cookie is taken."));
            sp.Children.Add(Point("", "BrushSuccess", "Kept out of logs, reports and the clipboard", "The log and every report leave cookies out. Cookies you paste are wiped from the clipboard once they have been added (you can turn that off)."));
            sp.Children.Add(Point("", "BrushWarning", "The honest limit", "Windows protects the file from other people and other PCs, not from malware already running as you. Keep Windows and your antivirus up to date, only run copies of Lantern you trust, and if you ever suspect a leak, use \"Sign out of all other sessions\" on Roblox."));

            var read = new Button { Content = "Read the full security notes", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(30, 4, 0, 0) };
            read.Click += (s, e) => OpenUrl(About.SecurityUrl);
            sp.Children.Add(read);
            return sp;
        }

        // page 5: last switches
        UIElement FinishPage()
        {
            var sp = new StackPanel();
            sp.Children.Add(Heading("You're all set"));
            sp.Children.Add(Text("Two last choices. Both can be changed later in Settings.", "BrushTextSecondary", 14, margin: new Thickness(0, 6, 0, 22)));

            sp.Children.Add(SwitchRow("Allow multiple Roblox windows",
                "Needed to run more than one account at the same time. Lantern holds the lock Roblox uses to stay single-instance, so close Roblox before you switch it on.",
                _s.MultiInstance, v => _s.MultiInstance = v, out _));
            sp.Children.Add(SwitchRow("Show the start screen",
                "A short animation while Lantern starts and checks your accounts.",
                _s.ShowSplash, v => _s.ShowSplash = v, out _));

            var next = new StackPanel();
            next.Children.Add(Text("Next: press Add account", bold: true));
            next.Children.Add(Text("Sign in in a private window (2-step verification works), or paste a cookie. Then tick the accounts, pick a game and press Launch.",
                "BrushTextSecondary", 13, margin: new Thickness(0, 4, 0, 0)));
            sp.Children.Add(Card(next, "BrushAccentSubtle", "BrushAccent", new Thickness(0, 14, 0, 0)));
            return sp;
        }

        Border SwitchRow(string title, string body, bool value, Action<bool> set, out CheckBox toggle)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var col = new StackPanel { Margin = new Thickness(0, 0, 20, 0) };
            col.Children.Add(Text(title, bold: true));
            if (body != null) col.Children.Add(Text(body, "BrushTextSecondary", 13, margin: new Thickness(0, 3, 0, 0)));
            grid.Children.Add(col);
            var sw = new CheckBox { IsChecked = value, VerticalAlignment = body == null ? VerticalAlignment.Center : VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
            sw.SetResourceReference(StyleProperty, "Switch");
            sw.Checked += (s, e) => set(true);
            sw.Unchecked += (s, e) => set(false);
            Grid.SetColumn(sw, 1);
            grid.Children.Add(sw);
            toggle = sw;
            return Card(grid);
        }
    }
}
