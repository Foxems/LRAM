using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Lantern.UI
{
    /// <summary>
    /// Descriptions with their key words picked out. Write <c>**like this**</c> in a text and that part is drawn in the accent colour
    /// and a heavier weight; Pixel also lays a tint behind it. The look comes from three tokens (<c>BrushAccentText</c>, <c>BrushHiBg</c>,
    /// <c>HiWeight</c>), so a change of theme restyles every highlight that is already on screen.
    /// </summary>
    public static class Rich
    {
        public static void Set(TextBlock tb, string markup)
        {
            tb.Inlines.Clear();
            var parts = (markup ?? "").Split(new[] { "**" }, StringSplitOptions.None);
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) continue;
                tb.Inlines.Add(i % 2 == 1 ? Hi(parts[i]) : new Run(parts[i]));
            }
        }

        public static Run Hi(string text)
        {
            var r = new Run(text);
            r.SetResourceReference(TextElement.ForegroundProperty, "BrushAccentText");
            r.SetResourceReference(TextElement.BackgroundProperty, "BrushHiBg");
            r.SetResourceReference(TextElement.FontWeightProperty, "HiWeight");
            return r;
        }

        static readonly System.Text.RegularExpressions.Regex Measure =
            new System.Text.RegularExpressions.Regex(@"\d[\d.,]*\s?(?:%|[KMG]B\b)", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Marks every size and percentage in a text (<c>1.9 GB</c>, <c>12%</c>) as a highlight.</summary>
        public static string Values(string text) => Measure.Replace(text ?? "", "**$0**");

        /// <summary>The same text without the markers (for places that cannot show them).</summary>
        public static string Plain(string markup) => (markup ?? "").Replace("**", "");

        public static readonly DependencyProperty MarkupProperty =
            DependencyProperty.RegisterAttached("Markup", typeof(string), typeof(Rich),
                new PropertyMetadata(null, (d, e) => { if (d is TextBlock tb) Set(tb, (string)e.NewValue); }));
        public static string GetMarkup(DependencyObject o) => (string)o.GetValue(MarkupProperty);
        public static void SetMarkup(DependencyObject o, string v) => o.SetValue(MarkupProperty, v);
    }
}
