using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Lantern.UI
{
    /// <summary>
    /// The Settings page: seven sections in a sidebar (chips on a narrow window), a search that looks through every row, and a way to
    /// open a given row from elsewhere in the app (<see cref="OpenSettings"/>), which scrolls to it and lights it up for a moment.
    /// </summary>
    public partial class MainWindow
    {
        sealed class SettingsSection
        {
            public string Key, Title, Caption;
            public Panel Panel;
            public RadioButton Nav;
            public TextBlock Head;
            public List<SearchUnit> Units = new List<SearchUnit>();
        }

        readonly List<SettingsSection> _sections = new List<SettingsSection>();
        string _section = "Launching";
        bool _settingsNavReady, _settingsNarrow, _settingsLaidOut;

        static readonly string[,] SectionText =
        {
            { "Launching",   "Launching",       "How windows are opened: several at once, one after another." },
            { "Online",      "Staying online",  "What Lantern does when a window closes or Roblox disconnects it." },
            { "Performance", "Performance",     "Keep many windows light on the processor, graphics card and memory." },
            { "Appearance",  "Appearance",      "How Lantern looks and how it starts." },
            { "Privacy",     "Privacy & data",  "Checks, your saved sessions and where they live." },
            { "General",     "General",         "The system tray, and about Lantern." },
            { "Credits",     "Credits",         "Who made Lantern, and where to find the source and the author." },
        };

        void WireSettingsNav()
        {
            var panels = new Dictionary<string, (Panel p, RadioButton n)>
            {
                ["Launching"] = (SecLaunching, NavSetLaunching), ["Online"] = (SecOnline, NavSetOnline),
                ["Performance"] = (SecPerformance, NavSetPerformance), ["Appearance"] = (SecAppearance, NavSetAppearance),
                ["Privacy"] = (SecPrivacy, NavSetPrivacy), ["General"] = (SecGeneral, NavSetGeneral),
                ["Credits"] = (SecCredits, NavSetCredits),
            };
            for (int i = 0; i < SectionText.GetLength(0); i++)
            {
                var key = SectionText[i, 0];
                var (panel, nav) = panels[key];
                var sec = new SettingsSection { Key = key, Title = SectionText[i, 1], Caption = SectionText[i, 2], Panel = panel, Nav = nav };
                // every row goes into a SearchUnit, so searching can hide it without touching its own Visibility
                foreach (var child in panel.Children.Cast<UIElement>().ToList())
                {
                    if (child is TextBlock tb && (tb.Tag as string) == "head") { sec.Head = tb; continue; }
                    panel.Children.Remove(child);
                    var unit = new SearchUnit { Child = child };
                    panel.Children.Add(unit);
                    sec.Units.Add(unit);
                }
                _sections.Add(sec);
                nav.Checked += (s, e) => { if (_settingsNavReady) SelectSection(key, true, clearSearch: true); };
            }
            _settingsNavReady = true;

            SettingsSearch.TextChanged += (s, e) => { SettingsSearchClear.Visibility = SettingsSearch.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed; RefreshSettingsView(false); };
            SettingsSearchClear.Click += (s, e) => { SettingsSearch.Clear(); SettingsSearch.Focus(); };
            SettingsSearch.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && SettingsSearch.Text.Length > 0) { SettingsSearch.Clear(); e.Handled = true; }
            };
            // Ctrl+F on the page goes to the search
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && PageSettings.Visibility == Visibility.Visible)
                { SettingsSearch.Focus(); SettingsSearch.SelectAll(); e.Handled = true; }
            };

            var start = SectionText.Cast<string>().Where((x, i) => i % 3 == 0).Contains(_s.SettingsSection) ? _s.SettingsSection : "Launching";
            SelectSection(start, false, clearSearch: false);
        }

        SettingsSection Section(string key) => _sections.FirstOrDefault(x => x.Key == key) ?? _sections[0];

        /// <summary>Shows one section. With <paramref name="animate"/> the new content glides in.</summary>
        void SelectSection(string key, bool animate, bool clearSearch)
        {
            var sec = Section(key);
            _section = sec.Key;
            if (_s.SettingsSection != _section) { _s.SettingsSection = _section; _mgr.SaveSettings(); }
            if (sec.Nav.IsChecked != true) { _settingsNavReady = false; sec.Nav.IsChecked = true; _settingsNavReady = true; }
            if (clearSearch && SettingsSearch.Text.Length > 0) { SettingsSearch.Clear(); return; }   // the cleared search refreshes the view
            RefreshSettingsView(animate);
        }

        string[] SearchWords() => SettingsSearch.Text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

        /// <summary>Lays the page out for the current section, or for the search results.</summary>
        void RefreshSettingsView(bool animate)
        {
            var words = SearchWords();
            bool searching = words.Length > 0;
            int total = 0;
            foreach (var sec in _sections)
            {
                int hits = 0;
                foreach (var u in sec.Units)
                {
                    bool show = searching ? u.Matches(words) : true;
                    u.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                    if (show && u.IsAvailable) hits++;
                }
                total += hits;
                if (sec.Head != null) sec.Head.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
                sec.Panel.Visibility = searching ? (hits > 0 ? Visibility.Visible : Visibility.Collapsed) : (sec.Key == _section ? Visibility.Visible : Visibility.Collapsed);
            }

            var cur = Section(_section);
            if (searching)
            {
                SectionTitle.Text = "Search results";
                SectionCaption.Text = total == 0 ? "" : total + (total == 1 ? " setting matches" : " settings match") + " “" + SettingsSearch.Text.Trim() + "”.";
                SettingsCaption.Text = "Searching every section.";
            }
            else
            {
                SectionTitle.Text = cur.Title;
                SectionCaption.Text = cur.Caption;
                SettingsCaption.Text = "Everything stays on this PC.";
            }
            SectionCaption.Visibility = SectionCaption.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            SettingsNoResults.Visibility = searching && total == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (searching && total == 0)
                SettingsNoResultsText.Text = "No setting mentions “" + SettingsSearch.Text.Trim() + "”. Try a shorter word, or pick a section on the left.";
            SettingsNav.Opacity = searching ? 0.55 : 1;

            if (animate && Motion.Enabled(_s) && !searching) GlideIn(SettingsContent, SettingsContentShift);
            if (!searching || !animate) SettingsScroll.ScrollToTop();
        }

        /// <summary>Content eases in from slightly below, fading up: 160 ms.</summary>
        static void GlideIn(UIElement content, TranslateTransform shift)
        {
            content.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = Motion.Out });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(210)) { EasingFunction = Motion.Out });
        }

        // open a row from elsewhere
        /// <summary>
        /// Opens Settings on <paramref name="section"/> and, when given, scrolls to <paramref name="target"/> (any control inside the
        /// row) and lights its row up for a moment, so it is clear which one was meant.
        /// </summary>
        public void OpenSettings(string section, FrameworkElement target = null)
        {
            if (NavSettings.IsChecked != true) NavSettings.IsChecked = true;
            if (SettingsSearch.Text.Length > 0) SettingsSearch.Clear();
            SelectSection(section, true, clearSearch: false);
            if (target == null) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateLayout();
                var row = RowOf(target);
                (row ?? target).BringIntoView();
                if (row != null) Flash(row);
            }), DispatcherPriority.Loaded);
        }

        /// <summary>The Settings row (the bordered strip) a control sits in: the child of its <see cref="SearchUnit"/>.</summary>
        static FrameworkElement RowOf(DependencyObject d)
        {
            for (; d != null; d = LogicalTreeHelper.GetParent(d))
                if (d is SearchUnit u) return u.Child as FrameworkElement;
            return null;
        }

        /// <summary>The row's background glows in the accent colour and fades out (1.6 s).</summary>
        void Flash(FrameworkElement row)
        {
            if (!(row is Border b)) return;
            var accent = (Color)FindResource("ColorAccent");
            var from = Color.FromArgb(0x3C, accent.R, accent.G, accent.B);
            var brush = new SolidColorBrush(from);
            b.Background = brush;
            if (!Motion.Enabled(_s)) { Dispatcher.BeginInvoke(new Action(() => b.ClearValue(Border.BackgroundProperty)), DispatcherPriority.ApplicationIdle); return; }
            var anim = new ColorAnimation(from, Color.FromArgb(0, accent.R, accent.G, accent.B), TimeSpan.FromMilliseconds(1500)) { BeginTime = TimeSpan.FromMilliseconds(500), EasingFunction = Motion.InOut };
            anim.Completed += (s, e) => b.ClearValue(Border.BackgroundProperty);
            brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }

        // layout
        /// <summary>Wide: sections on the left. Narrow: they become a row of chips above the content.</summary>
        void ApplySettingsLayout(double contentWidth, double gutter)
        {
            if (SettingsBody == null) return;
            bool narrow = contentWidth < 720;
            SettingsHeader.Margin = new Thickness(gutter, 24, gutter, 0);

            // header: the search drops under the title when there is no room beside it
            bool stackHeader = contentWidth < 600;
            Grid.SetRow(SettingsSearchHost, stackHeader ? 1 : 0);
            Grid.SetColumn(SettingsSearchHost, stackHeader ? 0 : 1);
            Grid.SetColumnSpan(SettingsSearchHost, stackHeader ? 2 : 1);
            SettingsSearchHost.Width = stackHeader ? double.NaN : 260;
            SettingsSearchHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            SettingsSearchHost.Margin = stackHeader ? new Thickness(0, 12, 0, 0) : new Thickness(0);

            if (narrow == _settingsNarrow && _settingsLaidOut)
            {
                if (narrow) { SettingsNavScroll.Margin = new Thickness(gutter - 6, 0, gutter - 6, 6); SettingsContent.Margin = new Thickness(gutter, 8, gutter, 40); }
                else { SettingsNav.Margin = new Thickness(Math.Max(6, gutter - 14), 0, 8, 16); }
                return;
            }
            _settingsNarrow = narrow;
            _settingsLaidOut = true;

            if (narrow)
            {
                SettingsNavColumn.Width = new GridLength(0);
                Grid.SetRow(SettingsNavScroll, 0); Grid.SetRowSpan(SettingsNavScroll, 1); Grid.SetColumn(SettingsNavScroll, 0); Grid.SetColumnSpan(SettingsNavScroll, 2);
                Grid.SetRow(SettingsScroll, 1); Grid.SetRowSpan(SettingsScroll, 1); Grid.SetColumn(SettingsScroll, 0); Grid.SetColumnSpan(SettingsScroll, 2);
                SettingsNavScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                SettingsNavScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                SettingsNav.Orientation = Orientation.Horizontal;
                SettingsNav.Margin = new Thickness(0, 0, 0, 4);
                SettingsNavScroll.Margin = new Thickness(gutter - 6, 0, gutter - 6, 6);
                SettingsContent.Margin = new Thickness(gutter, 8, gutter, 40);
                foreach (var sec in _sections) { sec.Nav.SetResourceReference(StyleProperty, "Chip"); sec.Nav.Margin = new Thickness(6, 0, 0, 4); }
            }
            else
            {
                SettingsNavColumn.Width = new GridLength(212);
                Grid.SetRow(SettingsNavScroll, 0); Grid.SetRowSpan(SettingsNavScroll, 2); Grid.SetColumn(SettingsNavScroll, 0); Grid.SetColumnSpan(SettingsNavScroll, 1);
                Grid.SetRow(SettingsScroll, 0); Grid.SetRowSpan(SettingsScroll, 2); Grid.SetColumn(SettingsScroll, 1); Grid.SetColumnSpan(SettingsScroll, 1);
                SettingsNavScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                SettingsNavScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                SettingsNav.Orientation = Orientation.Vertical;
                SettingsNavScroll.Margin = new Thickness(0);
                SettingsNav.Margin = new Thickness(Math.Max(6, gutter - 14), 0, 8, 16);
                SettingsContent.Margin = new Thickness(14, 0, 28, 40);
                foreach (var sec in _sections) { sec.Nav.SetResourceReference(StyleProperty, "SettingsNav"); sec.Nav.Margin = new Thickness(0, 1, 0, 1); }
            }
        }
    }
}
