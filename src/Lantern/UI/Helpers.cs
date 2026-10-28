using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Lantern.UI
{
    /// <summary>Attached properties used by control templates.</summary>
    public static class Ui
    {
        public static readonly DependencyProperty PlaceholderProperty =
            DependencyProperty.RegisterAttached("Placeholder", typeof(string), typeof(Ui), new PropertyMetadata(""));
        /// <summary>Extra words a Settings row is found by (the search also reads the row's own texts).</summary>
        public static readonly DependencyProperty KeywordsProperty =
            DependencyProperty.RegisterAttached("Keywords", typeof(string), typeof(Ui), new PropertyMetadata(""));
        public static string GetKeywords(DependencyObject o) => (string)o.GetValue(KeywordsProperty);
        public static void SetKeywords(DependencyObject o, string v) => o.SetValue(KeywordsProperty, v);

        public static string GetPlaceholder(DependencyObject o) => (string)o.GetValue(PlaceholderProperty);
        public static void SetPlaceholder(DependencyObject o, string v) => o.SetValue(PlaceholderProperty, v);
    }

    public static class TokenExtensions
    {
        /// <summary>Sizes an icon from the <c>IconSize*</c> tokens (see <see cref="Theme.IconSizes"/>).</summary>
        public static T IconSize<T>(this T element, double size) where T : System.Windows.FrameworkElement
        {
            element.SetResourceReference(System.Windows.Documents.TextElement.FontSizeProperty, Theme.IconSizeKey(size));
            return element;
        }
    }

    /// <summary>A small round status dot (its corners are the <c>RadiusPill</c> token).</summary>
    public sealed class Dot : System.Windows.Controls.Border
    {
        public Dot() { SetResourceReference(CornerRadiusProperty, "RadiusPill"); }
    }

    /// <summary>A <see cref="System.Windows.Controls.Border"/> whose corners come from one of the theme's radius tokens.</summary>
    public sealed class Box : System.Windows.Controls.Border
    {
        public Box(string radiusKey) { SetResourceReference(CornerRadiusProperty, radiusKey); }
    }

    public sealed class BoolVis : IValueConverter
    {
        public bool Invert { get; set; }
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool b = value is bool v && v;
            if (Invert) b = !b;
            return b ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>
    /// Column widths / gutters that change with the window width. One instance, bound from templates,
    /// so the header row and every account row stay aligned.
    /// </summary>
    public sealed class LayoutState : INotifyPropertyChanged
    {
        public static readonly LayoutState Current = new LayoutState();
        public event PropertyChangedEventHandler PropertyChanged;

        GridLength _game = new GridLength(1, GridUnitType.Star);
        GridLength _status = new GridLength(200);
        double _statusText = 170, _usageText = 190;
        GridLength _action = new GridLength(148);
        Thickness _gutter = new Thickness(28, 0, 28, 0);
        Visibility _gameVis = Visibility.Visible;
        Visibility _statusVis = Visibility.Visible;
        Visibility _compactVis = Visibility.Collapsed;
        Visibility _labelVis = Visibility.Visible;
        double _actionWidth = 100;
        double _rowHeight = 60;

        public GridLength GameColumn => _game;
        public GridLength StatusColumn => _status;
        public GridLength ActionColumn => _action;
        public Thickness Gutter => _gutter;
        public Visibility GameVisibility => _gameVis;
        /// <summary>Status as its own column (wide/medium) …</summary>
        public Visibility StatusVisibility => _statusVis;
        /// <summary>… or folded under the name (narrow).</summary>
        public Visibility CompactVisibility => _compactVis;
        /// <summary>Launch/Close text next to the icon; dropped on very small windows.</summary>
        public Visibility LabelVisibility => _labelVis;
        public double ActionWidth => _actionWidth;
        /// <summary>How wide the status line and the CPU/GPU/memory line under it may get before they are cut with an ellipsis.</summary>
        public double StatusTextMax => _statusText;
        public double UsageTextMax => _usageText;
        public double RowHeight => _rowHeight;

        /// <summary>contentWidth = width of the page area right of the nav rail.</summary>
        public void Update(double contentWidth)
        {
            bool wide = contentWidth >= 880;
            bool narrow = contentWidth < 700;
            bool tiny = contentWidth < 560;
            _game = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            _gameVis = wide ? Visibility.Visible : Visibility.Collapsed;
            _status = narrow ? new GridLength(0) : new GridLength(200);
            _statusText = 170; _usageText = 190;
            _statusVis = narrow ? Visibility.Collapsed : Visibility.Visible;
            _compactVis = narrow ? Visibility.Visible : Visibility.Collapsed;
            _labelVis = tiny ? Visibility.Collapsed : Visibility.Visible;
            _actionWidth = tiny ? 32 : 100;
            _action = new GridLength(tiny ? 76 : 148);
            _rowHeight = narrow ? 66 : 60;
            _gutter = narrow ? new Thickness(16, 0, 16, 0) : new Thickness(28, 0, 28, 0);
            foreach (var n in new[] { nameof(GameColumn), nameof(StatusColumn), nameof(ActionColumn), nameof(Gutter), nameof(GameVisibility),
                                      nameof(StatusVisibility), nameof(CompactVisibility), nameof(LabelVisibility), nameof(ActionWidth), nameof(RowHeight), nameof(StatusTextMax), nameof(UsageTextMax) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }
    }
}


