using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace Lantern.UI
{
    /// <summary>
    /// Wraps one Settings row so the search can hide it without touching the row's own Visibility (some rows show and hide
    /// themselves, e.g. "Wait before rejoining" while rejoining is off). It also reads out everything a person could search for.
    /// </summary>
    public sealed class SearchUnit : Decorator
    {
        /// <summary>The row is currently meant to be there at all (its own Visibility); a row that hides itself is never a result.</summary>
        public bool IsAvailable => Child == null || Child.Visibility == Visibility.Visible;

        /// <summary>All the words of the row: titles, captions, button labels, the keywords added in markup.</summary>
        public string SearchText()
        {
            var sb = new StringBuilder();
            if (Child != null) Collect(Child, sb);
            return sb.ToString();
        }

        static void Collect(DependencyObject d, StringBuilder sb)
        {
            if (d is TextBlock tb) sb.Append(tb.Text).Append(' ');
            else if (d is ContentControl cc && cc.Content is string s) sb.Append(s).Append(' ');
            if (d is FrameworkElement fe)
            {
                var kw = Ui.GetKeywords(fe);
                if (!string.IsNullOrEmpty(kw)) sb.Append(kw).Append(' ');
                if (fe.ToolTip is string tip) sb.Append(tip).Append(' ');
            }
            foreach (var child in LogicalTreeHelperChildren(d)) Collect(child, sb);
        }

        static IEnumerable<DependencyObject> LogicalTreeHelperChildren(DependencyObject d)
        {
            foreach (var o in System.Windows.LogicalTreeHelper.GetChildren(d))
                if (o is DependencyObject dep) yield return dep;
        }

        /// <summary>Every word typed has to be found (ignoring case and accents).</summary>
        public bool Matches(string[] words)
        {
            if (!IsAvailable) return false;
            if (words.Length == 0) return true;
            var text = SearchText();
            var cmp = CultureInfo.CurrentCulture.CompareInfo;
            foreach (var w in words)
                if (cmp.IndexOf(text, w, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) < 0) return false;
            return true;
        }
    }
}
