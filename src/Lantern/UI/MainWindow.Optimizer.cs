using System;
using System.Windows;
using System.Windows.Controls;
using Lantern.Core;

namespace Lantern.UI
{
    /// <summary>The adjustable side of low-resource mode: the Custom mode and the engine-flag switch.</summary>
    public partial class MainWindow
    {
        StackPanel _customPanel;      // everything that only matters in Custom mode
        StackPanel _flagRows;         // the engine tweaks inside it (disabled while flags are switched off)

        static readonly int[][] WindowSizes = { new[] { 320, 200 }, new[] { 480, 300 }, new[] { 640, 400 }, new[] { 960, 540 }, new[] { 1280, 720 } };
        static readonly string[] ApiValues = { "Default", "D3D11", "Vulkan", "OpenGL" };

        void WireOptimizer()
        {
            PerfCustom.IsChecked = _s.PerfMode == "Custom";
            PerfCustom.Checked += (s, e) => SetPerf("Custom");

            PerfExtras.Children.Add(Row("Use Roblox's allowed engine flags",
                "Minimal and Custom may also set engine tweaks from the list Roblox publishes as allowed: low textures, no grass, simpler meshes, no anti-aliasing, a frame-rate lock. " +
                "Lantern only writes flags from that list. They go into a ClientSettings file next to Roblox's player, and Lantern takes them out again when the windows close.",
                Switch(_s.UseEngineFlags, on => _s.UseEngineFlags = on)));

            var c = _s.Custom ?? (_s.Custom = new PerfCustom());
            _customPanel = new StackPanel { Visibility = _s.PerfMode == "Custom" ? Visibility.Visible : Visibility.Collapsed };

            var title = new TextBlock { Text = "Your custom settings", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0) };
            title.SetResourceReference(TextBlock.ForegroundProperty, "BrushAccentText");
            _customPanel.Children.Add(title);

            _customPanel.Children.Add(Row("Graphics level", "1 is the lowest. This is Roblox's own manual graphics setting.",
                Stepper(() => c.Quality, v => c.Quality = v, 1, 10, v => "Level " + v)));
            _customPanel.Children.Add(Row("Frame-rate lock",
                "How many frames per second each window may draw. Sent to Roblox two ways: its settings file and the engine's frame-rate target. Roblox decides whether values below 60 are honored; check with Shift+F5 inside a window.",
                Stepper(() => c.FrameRate, v => c.FrameRate = v, 5, 60, v => v + " fps")));

            int sizeIdx = Array.FindIndex(WindowSizes, s => s[0] == c.WindowWidth && s[1] == c.WindowHeight);
            _customPanel.Children.Add(Row("Window size", "The size a window starts at. Fewer pixels to draw is the biggest graphics saving.",
                Segments(Array.ConvertAll(WindowSizes, s => s[0] + "×" + s[1]), sizeIdx, i => { c.WindowWidth = WindowSizes[i][0]; c.WindowHeight = WindowSizes[i][1]; })));
            _customPanel.Children.Add(Row("Mute audio", "", Switch(c.Mute, on => c.Mute = on)));
            _customPanel.Children.Add(Row("Efficiency mode", "Windows runs each window on its power-saving cores at a low clock speed.", Switch(c.Eco, on => c.Eco = on)));

            _flagRows = new StackPanel();
            var head = new TextBlock { Text = "Engine tweaks (flags Roblox allows)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0) };
            head.SetResourceReference(TextBlock.ForegroundProperty, "BrushAccentText");
            _flagRows.Children.Add(head);
            _flagRows.Children.Add(Row("Low-quality textures", "The lowest texture detail. The main lever for graphics memory.", Switch(c.LowTextures, on => c.LowTextures = on)));
            _flagRows.Children.Add(Row("Turn off anti-aliasing", "No edge smoothing: less graphics work per frame.", Switch(c.NoAntiAliasing, on => c.NoAntiAliasing = on)));
            _flagRows.Children.Add(Row("No grass", "Grass isn't drawn at all.", Switch(c.NoGrass, on => c.NoGrass = on)));
            _flagRows.Children.Add(Row("Simpler distant meshes", "Union and mesh parts switch to their low-detail versions straight away.", Switch(c.LowDetailMeshes, on => c.LowDetailMeshes = on)));
            _flagRows.Children.Add(Row("Pause lighting updates", "Stops the voxel lighting from refreshing. Lighting looks frozen, but it costs less.", Switch(c.PauseLighting, on => c.PauseLighting = on)));
            _flagRows.Children.Add(Row("Force the lowest graphics level", "Applies the graphics level above even where a game would raise it.", Switch(c.ForceLowestLevel, on => c.ForceLowestLevel = on)));
            int apiIdx = Math.Max(0, Array.IndexOf(ApiValues, c.GraphicsApi));
            _flagRows.Children.Add(Row("Graphics engine",
                "Which renderer draws the window. Default is what Roblox picks. If a window won't start, set this back to Default.",
                Segments(new[] { "Default", "Direct3D 11", "Vulkan", "OpenGL" }, apiIdx, i => c.GraphicsApi = ApiValues[i])));
            _customPanel.Children.Add(_flagRows);

            PerfExtras.Children.Add(_customPanel);
            WireCap();
            UpdatePerfUi();
        }

        // memory cap for minimized windows
        void WireCap()
        {
            CapSwitch.IsChecked = _s.CapMinimized;
            CapSwitch.Checked += (s, e) => { _mgr.EnableCap(true); UpdateCapUi(); };
            CapSwitch.Unchecked += (s, e) => { _mgr.EnableCap(false); UpdateCapUi(); };
            _mgr.CapUnavailableChanged += () => { CapSwitch.IsChecked = false; UpdateCapUi(); };

            int idx = Array.IndexOf(InstanceManager.CapChoicesMb, _mgr.CapMbNow);
            CapExtras.Children.Add(Row("Memory ceiling", "Per minimized window. Lower fits more windows but is harder on each one.",
                Segments(Array.ConvertAll(InstanceManager.CapChoicesMb, mb => mb >= 1000 && mb % 1000 == 0 ? mb / 1000 + " GB" : mb >= 1000 ? (mb / 1000.0).ToString("0.0") + " GB" : mb + " MB"), idx,
                         i => { _mgr.SetCapMb(InstanceManager.CapChoicesMb[i]); UpdateCapUi(); })));
            UpdateCapUi();
        }

        void UpdateCapUi()
        {
            CapExtras.Visibility = _s.CapMinimized ? Visibility.Visible : Visibility.Collapsed;
            Native.Memory(out var total, out _);
            long cap = _mgr.CapMbNow * 1024L * 1024L;
            if (total == 0) { CapEstimate.Inlines.Clear(); return; }
            long fit = (long)(total * 0.75) / cap;
            Rich.Set(CapEstimate, "At **" + Fmt.Memory(cap) + "** each, roughly **" + fit + "** minimized windows fit in your **" + Fmt.Memory((long)total) +
                                  "** of RAM, leaving a quarter for Windows and your other programs. What Windows takes out is compressed or paged, so it adds a little more.");
        }

        void CommitOptimizer()
        {
            _mgr.SaveSettings();
            UpdatePerfUi();
        }

        // small control builders (same look as the XAML rows)
        Border Row(string title, string caption, FrameworkElement control)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
            left.Children.Add(new TextBlock { Text = title });
            if (!string.IsNullOrEmpty(caption))
            {
                var c = new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
                c.SetResourceReference(FrameworkElement.StyleProperty, "Caption");
                left.Children.Add(c);
            }
            g.Children.Add(left);
            Grid.SetColumn(control, 1);
            control.VerticalAlignment = VerticalAlignment.Top;
            g.Children.Add(control);

            var b = new Border { Child = g };
            b.SetResourceReference(FrameworkElement.StyleProperty, "SettingRow");
            return b;
        }

        CheckBox Switch(bool on, Action<bool> set)
        {
            var cb = new CheckBox { IsChecked = on, Margin = new Thickness(0, 2, 0, 0) };
            cb.SetResourceReference(FrameworkElement.StyleProperty, "Switch");
            cb.Checked += (s, e) => { set(true); CommitOptimizer(); };
            cb.Unchecked += (s, e) => { set(false); CommitOptimizer(); };
            return cb;
        }

        FrameworkElement Stepper(Func<int> get, Action<int> set, int min, int max, Func<int, string> fmt)
        {
            var label = new TextBlock { Width = 74, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Text = fmt(get()) };
            Button Make(string glyph, int delta)
            {
                var icon = new TextBlock { Text = glyph, FontSize = 11 };
                icon.SetResourceReference(FrameworkElement.StyleProperty, "Icon");
                var b = new Button { Content = icon };
                b.SetResourceReference(FrameworkElement.StyleProperty, "StepBtn");
                b.Click += (s, e) =>
                {
                    set(Math.Max(min, Math.Min(max, get() + delta)));
                    label.Text = fmt(get());
                    CommitOptimizer();
                };
                return b;
            }
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(Make("", -1));
            sp.Children.Add(label);
            sp.Children.Add(Make("", +1));
            return sp;
        }

        FrameworkElement Segments(string[] labels, int selected, Action<int> pick)
        {
            string group = "seg" + Guid.NewGuid().ToString("N");
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < labels.Length; i++)
            {
                int idx = i;
                var rb = new RadioButton { GroupName = group, Content = labels[i], IsChecked = i == selected, MinWidth = 60 };
                rb.SetResourceReference(FrameworkElement.StyleProperty, "Segment");
                rb.Checked += (s, e) => { pick(idx); CommitOptimizer(); };
                sp.Children.Add(rb);
            }
            var b = new Box("RadiusMd") { BorderThickness = new Thickness(1), Padding = new Thickness(2), Child = sp };
            b.SetResourceReference(Border.BackgroundProperty, "BrushControl");
            b.SetResourceReference(Border.BorderBrushProperty, "BrushControlStroke");
            return b;
        }
    }
}
