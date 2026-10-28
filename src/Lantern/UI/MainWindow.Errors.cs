using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lantern.Core;

namespace Lantern.UI
{
    /// <summary>
    /// The Errors page: every Roblox disconnect code Lantern knows, what it means and what Lantern does about it (from <see cref="Disconnects"/>).
    /// Laid out like Settings: sections in a sidebar (chips on a narrow window), a search through every entry, and an Overview that
    /// shows what is switched on and leads into each section.
    /// </summary>
    public partial class MainWindow
    {
        sealed class ErrorsSection
        {
            public string Key, Title, Caption, Glyph, Tone;
            public StackPanel Panel;
            public TextBlock Head;
            public RadioButton Nav;
            public TextBlock Count;
            public List<SearchUnit> Units = new List<SearchUnit>();
        }

        readonly List<ErrorsSection> _errSections = new List<ErrorsSection>();
        string _errSection = "Overview";
        bool _errNavReady, _errNarrow, _errLaidOut, _errWired;

        void WireErrors()
        {
            if (_errWired) return;
            _errWired = true;
            ErrorsSearch.TextChanged += (s, e) => { ErrorsSearchClear.Visibility = ErrorsSearch.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed; RefreshErrorsView(false); };
            ErrorsSearchClear.Click += (s, e) => { ErrorsSearch.Clear(); ErrorsSearch.Focus(); };
            ErrorsSearch.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && ErrorsSearch.Text.Length > 0) { ErrorsSearch.Clear(); e.Handled = true; }
            };
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && PageErrors.Visibility == Visibility.Visible)
                { ErrorsSearch.Focus(); ErrorsSearch.SelectAll(); e.Handled = true; }
            };
        }

        /// <summary>Opens the Errors page on a section.</summary>
        void OpenErrors(string section)
        {
            _errSection = section;
            if (NavErrors.IsChecked != true) NavErrors.IsChecked = true; else RenderErrors();
        }

        // building
        void RenderErrors()
        {
            WireErrors();
            _errNavReady = false;
            _errSections.Clear();
            ErrorsNav.Children.Clear();
            ErrorsBody.Children.Clear();

            var all = Disconnects.All;
            ErrorsSection Make(string key, string title, string glyph, string caption, string tone = null)
            {
                var sec = new ErrorsSection { Key = key, Title = title, Glyph = glyph, Caption = caption, Tone = tone, Panel = new StackPanel { Visibility = Visibility.Collapsed } };
                sec.Head = new TextBlock { Text = title, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 22, 0, 2) };
                sec.Head.SetResourceReference(FrameworkElement.StyleProperty, "SectionLabel");
                sec.Panel.Children.Add(sec.Head);
                _errSections.Add(sec);
                ErrorsBody.Children.Add(sec.Panel);
                return sec;
            }
            void Add(ErrorsSection sec, UIElement el)
            {
                var unit = new SearchUnit { Child = el };
                sec.Panel.Children.Add(unit);
                sec.Units.Add(unit);
            }

            var over = Make("Overview", "Overview", "\uE946", "When Roblox disconnects a window it usually stays open on an error screen with a code. Lantern reads that code from the window's own log (read-only) and acts on it.");
            var rejoin = Make("Rejoin", "Auto-rejoined", "\uE72C",
                "With **“Rejoin when closed”** on. Roblox leaves a disconnected window open on its error screen; Lantern reads the code from that window's own log, **closes it and joins again**. Backs off: each repeat waits longer (up to **10 min**) and never counts toward giving up. Fresh server: that server is gone, so it joins the game (or your private-server link) again.", "BrushAccent");
            var idle = Make("Idle", "Idle kick", "\uE769", "With **“Rejoin after an idle kick”** on. Only the **exact idle code** counts, never lag or another kick.", "BrushWarning");
            var stop = Make("Stop", "Never rejoined", "\uE72E", "Coming back would fight the cause or put the account at risk. The window is **left on its error screen** for you to see.", "BrushDangerFill");
            var normal = Make("Normal", "Normal endings", "\uE73E", "Nothing went wrong.", "BrushTextTertiary");
            var launch = Make("Launch", "Launch problems", "\uE768", "Before a window is in a game: what happens, and what Lantern does.");
            var other = Make("Other", "Other cases", "\uE7BA", "Crashes, codes Lantern doesn't know, and windows that keep failing.");

            void Codes(ErrorsSection sec, Recovery r) { foreach (var c in all.Where(x => x.Recovery == r)) Add(sec, ErrorCard(c, sec.Tone)); }
            Codes(rejoin, Recovery.Rejoin); Codes(idle, Recovery.IdleRejoin); Codes(stop, Recovery.Stop); Codes(normal, Recovery.None);

            foreach (var (t, b) in new[]
            {
                ("First private server is full",
                 "The first account waits up to **30 s** for Roblox to let it in (a full private server just never answers), then **moves to the backup link**. Every account after it goes straight to the backup: for **20 minutes**, and for as long as one of your accounts plays there. If the backup fills up too, they try the first link again. When the first server shuts down (maintenance, restart) it starts empty, so it is tried first again."),
                ("Not allowed into a private server, or the link didn't resolve",
                 "With a backup link the account **uses that one instead**. Without one, the launch **stops** with the reason and what to do."),
                ("A friend's private server by server id (code 524)",
                 "Roblox refuses joining a private server by its id. Lantern notices within **~20 s**, closes the window and **joins through the player** instead, like the Join button on their profile."),
                ("Couldn't reach Roblox, Roblox's servers failing, or “too many requests”",
                 "Rejoins wait longer each time (**30 s, 1, 2, 5, then 10 minutes**; at least **90 s** after a rate limit) and **never count toward giving up**, so an outage overnight doesn't leave accounts stopped."),
                ("Roblox updates itself (its installer opens)",
                 "When a launch needs another Roblox build (an update, or another release channel), the window that starts first hands over to **Roblox's installer** and closes. The installer **closes every open Roblox window**, swaps builds, then starts the client again. Roblox allows **one installer at a time**: a second launch meanwhile ends in its “another installer is running” **critical error**. So Lantern **starts nothing while the installer runs** (shown as “Waiting for Roblox to update…”, up to **5 min**), keeps waiting for the window the installer starts (the launch timeout pauses for the download), and doesn't take the short-lived first window for the real one. Windows the installer closed end as “Roblox update”: they are **rejoined once it is done** and **never count toward giving up**."),
                ("Many windows dropped at once",
                 "They rejoin **one at a time**, with the delay between launches, so Roblox isn't hit by all of them in the same second."),
                ("Sign-in expired",
                 "**Never retried**: the account shows “Sign-in expired” until you sign in again."),
                ("Roblox didn't open a window",
                 "After **150 s** with no window (longer while Roblox is updating) the launch fails with the reason. During a rejoin it counts as a failed try."),
            }) Add(launch, ErrorNote(t, b));

            foreach (var (t, b) in new[]
            {
                ("Crashed or force-closed (no code)",
                 "The log just stops. With “Rejoin when closed” on, the window is **rejoined**."),
                ("Any other code",
                 "Shown with its number. Lantern doesn't close a window over a code it doesn't know; if the window closes by itself, it is rejoined like a crash."),
                ("Keeps failing",
                 "A window that ends within **90 s** of starting **" + _s.MaxRejoins + "×** in a row stops rejoining (“Give up after” in Settings). Outages above never count toward this."),
            }) Add(other, ErrorNote(t, b));

            // Overview: what is switched on, then a way into each section (after the others so the counts are real)
            Add(over, ErrorsSettingsCard());
            Add(over, ErrorsGlance(new[] { rejoin, idle, stop, normal, launch, other }));

            // the sidebar
            foreach (var sec in _errSections)
            {
                var key = sec.Key;
                var icon = new TextBlock { Text = sec.Glyph, Margin = new Thickness(0, 0, 12, 0) };
                icon.SetResourceReference(FrameworkElement.StyleProperty, "Icon");
                icon.IconSize(14);
                var label = new TextBlock { Text = sec.Title, VerticalAlignment = VerticalAlignment.Center };
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(icon); row.Children.Add(label);
                if (key != "Overview")
                {
                    int n = sec.Units.Count;
                    sec.Count = new TextBlock { Text = n.ToString(CultureInfo.InvariantCulture), FontSize = 11, Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    sec.Count.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextTertiary");
                    row.Children.Add(sec.Count);
                }
                sec.Nav = new RadioButton { GroupName = "errnav", Content = row, Tag = key, ToolTip = sec.Title };
                sec.Nav.SetResourceReference(FrameworkElement.StyleProperty, _errNarrow ? "Chip" : "SettingsNav");
                if (_errNarrow) sec.Nav.Margin = new Thickness(6, 0, 0, 4);
                sec.Nav.Checked += (s, e) => { if (_errNavReady) SelectErrorsSection(key, true, clearSearch: true); };
                ErrorsNav.Children.Add(sec.Nav);
            }
            _errNavReady = true;
            if (_errSections.All(x => x.Key != _errSection)) _errSection = "Overview";
            SelectErrorsSection(_errSection, false, clearSearch: false);
        }

        // views
        ErrorsSection ErrSec(string key) => _errSections.FirstOrDefault(x => x.Key == key) ?? _errSections[0];

        void SelectErrorsSection(string key, bool animate, bool clearSearch)
        {
            var sec = ErrSec(key);
            _errSection = sec.Key;
            if (sec.Nav.IsChecked != true) { _errNavReady = false; sec.Nav.IsChecked = true; _errNavReady = true; }
            if (clearSearch && ErrorsSearch.Text.Length > 0) { ErrorsSearch.Clear(); return; }
            RefreshErrorsView(animate);
        }

        void RefreshErrorsView(bool animate)
        {
            if (_errSections.Count == 0) return;
            var words = ErrorsSearch.Text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            bool searching = words.Length > 0;
            int total = 0;
            foreach (var sec in _errSections)
            {
                int hits = 0;
                foreach (var u in sec.Units)
                {
                    // the Overview's own cards are not results: only codes and problems are
                    bool show = searching ? sec.Key != "Overview" && u.Matches(words) : true;
                    u.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                    if (show && u.IsAvailable) hits++;
                }
                total += searching && sec.Key != "Overview" ? hits : 0;
                sec.Head.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
                sec.Panel.Visibility = searching ? (hits > 0 && sec.Key != "Overview" ? Visibility.Visible : Visibility.Collapsed) : (sec.Key == _errSection ? Visibility.Visible : Visibility.Collapsed);
            }

            var cur = ErrSec(_errSection);
            if (searching)
            {
                ErrorsTitle.Text = "Search results";
                ErrorsCaption.Text = total == 0 ? "" : total + (total == 1 ? " entry matches" : " entries match") + " “" + ErrorsSearch.Text.Trim() + "”.";
                ErrorsHeaderCaption.Text = "Searching every section.";
            }
            else
            {
                ErrorsTitle.Text = cur.Title;
                Rich.Set(ErrorsCaption, cur.Caption);
                ErrorsHeaderCaption.Text = "What Roblox's disconnect codes mean and what Lantern does.";
            }
            ErrorsCaption.Visibility = ErrorsCaption.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ErrorsNoResults.Visibility = searching && total == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (searching && total == 0)
                ErrorsNoResultsText.Text = "No code or problem mentions “" + ErrorsSearch.Text.Trim() + "”. Try a number such as 277, or a word such as “kicked”.";
            ErrorsNav.Opacity = searching ? 0.55 : 1;

            if (animate && Motion.Enabled(_s) && !searching) GlideIn(ErrorsContent, ErrorsContentShift);
            if (!searching || !animate) ErrorsScroll.ScrollToTop();
        }

        /// <summary>Wide: sections on the left. Narrow: they become a row of chips above the content.</summary>
        void ApplyErrorsLayout(double contentWidth, double gutter)
        {
            if (ErrorsPageBody == null) return;
            bool narrow = contentWidth < 720;
            ErrorsHeader.Margin = new Thickness(gutter, 24, gutter, 0);
            bool stackHeader = contentWidth < 600;
            Grid.SetRow(ErrorsSearchHost, stackHeader ? 1 : 0);
            Grid.SetColumn(ErrorsSearchHost, stackHeader ? 0 : 1);
            Grid.SetColumnSpan(ErrorsSearchHost, stackHeader ? 2 : 1);
            ErrorsSearchHost.Width = stackHeader ? double.NaN : 260;
            ErrorsSearchHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            ErrorsSearchHost.Margin = stackHeader ? new Thickness(0, 12, 0, 0) : new Thickness(0);

            if (narrow == _errNarrow && _errLaidOut)
            {
                if (narrow) { ErrorsNavScroll.Margin = new Thickness(gutter - 6, 0, gutter - 6, 6); ErrorsContent.Margin = new Thickness(gutter, 8, gutter, 40); }
                else ErrorsNav.Margin = new Thickness(Math.Max(6, gutter - 14), 0, 8, 16);
                return;
            }
            _errNarrow = narrow;
            _errLaidOut = true;

            if (narrow)
            {
                ErrorsNavColumn.Width = new GridLength(0);
                Grid.SetRow(ErrorsNavScroll, 0); Grid.SetRowSpan(ErrorsNavScroll, 1); Grid.SetColumn(ErrorsNavScroll, 0); Grid.SetColumnSpan(ErrorsNavScroll, 2);
                Grid.SetRow(ErrorsScroll, 1); Grid.SetRowSpan(ErrorsScroll, 1); Grid.SetColumn(ErrorsScroll, 0); Grid.SetColumnSpan(ErrorsScroll, 2);
                ErrorsNavScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                ErrorsNavScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                ErrorsNav.Orientation = Orientation.Horizontal;
                ErrorsNav.Margin = new Thickness(0, 0, 0, 4);
                ErrorsNavScroll.Margin = new Thickness(gutter - 6, 0, gutter - 6, 6);
                ErrorsContent.Margin = new Thickness(gutter, 8, gutter, 40);
                foreach (var sec in _errSections) { sec.Nav.SetResourceReference(StyleProperty, "Chip"); sec.Nav.Margin = new Thickness(6, 0, 0, 4); }
            }
            else
            {
                ErrorsNavColumn.Width = new GridLength(212);
                Grid.SetRow(ErrorsNavScroll, 0); Grid.SetRowSpan(ErrorsNavScroll, 2); Grid.SetColumn(ErrorsNavScroll, 0); Grid.SetColumnSpan(ErrorsNavScroll, 1);
                Grid.SetRow(ErrorsScroll, 0); Grid.SetRowSpan(ErrorsScroll, 2); Grid.SetColumn(ErrorsScroll, 1); Grid.SetColumnSpan(ErrorsScroll, 1);
                ErrorsNavScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                ErrorsNavScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                ErrorsNav.Orientation = Orientation.Vertical;
                ErrorsNavScroll.Margin = new Thickness(0);
                ErrorsNav.Margin = new Thickness(Math.Max(6, gutter - 14), 0, 8, 16);
                ErrorsContent.Margin = new Thickness(14, 0, 28, 40);
                foreach (var sec in _errSections) { sec.Nav.SetResourceReference(StyleProperty, "SettingsNav"); sec.Nav.Margin = new Thickness(0, 1, 0, 1); }
            }
        }

        // pieces
        UIElement ErrorsSettingsCard()
        {
            var sp = new StackPanel();
            void Row(string label, bool on, string what)
            {
                var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
                var dot = new Dot { Width = 8, Height = 8, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                dot.SetResourceReference(Border.BackgroundProperty, on ? "BrushSuccess" : "BrushTextTertiary");
                row.Children.Add(dot);
                row.Children.Add(T(label + ": ", "BrushText", 13.5, true));
                row.Children.Add(T(on ? "on" : "off", on ? "BrushSuccess" : "BrushTextSecondary", 13.5, true));
                var w = T("  ·  " + what, "BrushTextTertiary", 12.5); w.TextTrimming = TextTrimming.CharacterEllipsis;
                row.Children.Add(w);
                sp.Children.Add(row);
            }
            int rejoined = Disconnects.All.Count(c => c.Recovery == Recovery.Rejoin);
            Row("Rejoin when closed", _s.AutoRejoin, rejoined + " codes and crashes");
            Row("Rejoin after an idle kick", _s.RejoinAfterIdleKick, "code 278");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(sp);
            if (!_s.AutoRejoin || !_s.RejoinAfterIdleKick)
            {
                var hint = T("Codes under a switch that is off are only explained, not acted on.", "BrushTextSecondary", 12.5);
                hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(18, 4, 0, 0);
                sp.Children.Add(hint);
            }
            var open = new Button { Content = "Open Settings", VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(16, 0, 0, 0) };
            open.Click += (s, e) => OpenSettings("Online", RejoinSwitch);
            Grid.SetColumn(open, 1);
            grid.Children.Add(open);
            return CardBox(grid, new Thickness(0, 12, 0, 0), 16);
        }

        /// <summary>The Overview's way in: one card per section with how many entries it holds. Clicking opens it.</summary>
        UIElement ErrorsGlance(ErrorsSection[] sections)
        {
            var wrap = new System.Windows.Controls.Primitives.UniformGrid { Columns = _errNarrow ? 1 : 2, Margin = new Thickness(0, 4, -10, 0) };
            foreach (var sec in sections)
            {
                var s = sec;
                int n = s.Units.Count;
                var head = new StackPanel { Orientation = Orientation.Horizontal };
                var icon = new TextBlock { Text = s.Glyph, Margin = new Thickness(0, 0, 10, 0) };
                icon.SetResourceReference(FrameworkElement.StyleProperty, "Icon");
                icon.IconSize(14);
                icon.SetResourceReference(TextBlock.ForegroundProperty, s.Tone == "BrushDangerFill" ? "BrushDanger" : s.Tone ?? "BrushAccentText");
                head.Children.Add(icon);
                head.Children.Add(T(s.Title, "BrushText", 13.5, true));
                var count = T(n + (s.Key == "Launch" || s.Key == "Other" ? (n == 1 ? " case" : " cases") : (n == 1 ? " code" : " codes")), "BrushTextSecondary", 12);
                count.Margin = new Thickness(0, 6, 0, 0);
                var body = new StackPanel(); body.Children.Add(head); body.Children.Add(count);
                var card = new Box("RadiusLg") { BorderThickness = new Thickness(1), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 10, 10), Child = body, Cursor = Cursors.Hand, ToolTip = "Open " + s.Title };
                card.SetResourceReference(Border.BackgroundProperty, "BrushControl");
                card.SetResourceReference(Border.BorderBrushProperty, "BrushControlStroke");
                card.MouseEnter += (a, b) => card.SetResourceReference(Border.BackgroundProperty, "BrushControlHover");
                card.MouseLeave += (a, b) => card.SetResourceReference(Border.BackgroundProperty, "BrushControl");
                card.MouseLeftButtonUp += (a, b) => SelectErrorsSection(s.Key, true, clearSearch: true);
                wrap.Children.Add(card);
            }
            var sp = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            var h = T("Jump to", "BrushTextSecondary", 12); h.Margin = new Thickness(1, 0, 0, 4);
            sp.Children.Add(h); sp.Children.Add(wrap);
            return sp;
        }

        UIElement ErrorCard(DisconnectCode c, string tone)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var badge = new Box("RadiusMd") { Padding = new Thickness(0, 5, 0, 5), Width = 46, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
            badge.SetResourceReference(Border.BackgroundProperty, tone);
            var num = T(c.Code.ToString(CultureInfo.InvariantCulture), tone == "BrushTextTertiary" ? "BrushLayer" : "BrushOnAccent", 14, true, TextAlignment.Center);
            num.HorizontalAlignment = HorizontalAlignment.Stretch;
            num.SetResourceReference(TextBlock.FontFamilyProperty, "FontDisplay");
            badge.Child = num;
            grid.Children.Add(badge);

            var body = new StackPanel();
            var head = new WrapPanel();
            var name = T(c.Short, "BrushText", 14, true); name.Margin = new Thickness(0, 0, 10, 2);
            head.Children.Add(name);
            if (c.Outage) head.Children.Add(ErrorTag("backs off", "Roblox's or the game's side: each try in a row waits longer, and it never counts toward giving up."));
            if (c.ServerGone && c.Recovery == Recovery.Rejoin) head.Children.Add(ErrorTag("fresh server", "That server is gone: it rejoins the game (or your private-server link), not the old server id."));
            body.Children.Add(head);

            var says = T("Roblox: “" + c.RobloxSays + "”", "BrushTextTertiary", 12); says.TextWrapping = TextWrapping.Wrap; says.FontStyle = FontStyles.Italic;
            body.Children.Add(says);
            var what = T(c.Meaning + " " + c.Handling, "BrushTextSecondary", 12.5); what.TextWrapping = TextWrapping.Wrap; what.Margin = new Thickness(0, 5, 0, 0);
            body.Children.Add(what);
            Grid.SetColumn(body, 1);
            grid.Children.Add(body);
            return CardBox(grid, new Thickness(0, 0, 0, 8), 14);
        }

        UIElement ErrorTag(string text, string tip)
        {
            var b = new Box("RadiusSm") { BorderThickness = new Thickness(1), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 1, 6, 2), VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
            b.SetResourceReference(Border.BorderBrushProperty, "BrushControlStroke");
            b.Child = T(text, "BrushTextSecondary", 11);
            return b;
        }

        UIElement ErrorNote(string title, string text)
        {
            var inner = new StackPanel();
            inner.Children.Add(T(title, "BrushText", 14, true));
            var b = T("", "BrushTextSecondary", 12.5); b.TextWrapping = TextWrapping.Wrap; b.Margin = new Thickness(0, 3, 0, 0);
            Rich.Set(b, text);
            inner.Children.Add(b);
            return CardBox(inner, new Thickness(0, 0, 0, 8), 14);
        }
    }
}
