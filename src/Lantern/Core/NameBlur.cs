using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Lantern.Core
{
    /// <summary>A word Windows' text reader found in a picture, in that picture's pixels.</summary>
    public struct ScreenWord
    {
        public string Text;
        public Rect Box;
        public int Line;
    }

    /// <summary>
    /// Stream mode's name blur: Windows' own built-in text reader (Windows.Media.Ocr, offline) finds where your account names
    /// appear in a window's picture, and only those words are blurred. The reader misreads lookalike characters in game fonts
    /// ("moonfox42" comes back as "moonfoxq2", "Quillbert2" as "Quillbert02"), so names are compared loosely.
    /// </summary>
    public static class NameBlur
    {
        /// <summary>One reader per thread, so several windows can be read at the same time on different cores.</summary>
        [ThreadStatic] static OcrEngine _engine;
        static bool? _available;

        /// <summary>False when Windows has no text-reading language installed (then whole pictures are blurred instead).</summary>
        public static bool Available => _available ?? (_available = Engine() != null).Value;

        static OcrEngine Engine()
        {
            if (_engine != null || _available == false) return _engine;
            try
            {
                _engine = OcrEngine.TryCreateFromUserProfileLanguages();
                if (_engine == null)
                    foreach (var lang in OcrEngine.AvailableRecognizerLanguages)
                        if ((_engine = OcrEngine.TryCreateFromLanguage(lang)) != null) break;
            }
            catch (Exception ex) { Logger.Write("Ocr: " + ex.Message); _engine = null; }
            return _engine;
        }

        /// <summary>The words in a BGRA picture; null if the reader isn't available or failed. Blocks for ~70 ms on an 800×600 picture.</summary>
        public static List<ScreenWord> Read(byte[] bgra, int width, int height)
        {
            var engine = Engine();
            if (engine == null || bgra == null) return null;
            try
            {
                // Game fonts at ~10 px are read far more reliably at double size (measured: every leaderboard name instead of most
                // of them, 70 ms instead of 40). The reader has a size limit; a picture too big for it is read at half size instead.
                // A window up to ~1000 px wide is read at double size (game text there is ~10 px high, which the reader often
                // misreads at its own size); a bigger one at its own size, where the same text is bigger. Above the reader's
                // limit, half size.
                int max = (int)OcrEngine.MaxImageDimension;
                double scale = width <= 1000 && Math.Max(width, height) * 2 <= max ? 2 : Math.Max(width, height) <= max ? 1 : 0.5;
                byte[] px = scale == 2 ? Upscale2(bgra, width, height) : scale == 1 ? bgra : Downscale(bgra, width, height, 2);
                int w = (int)(width * scale), h = (int)(height * scale);
                SoftwareBitmap bmp;
                try { bmp = SoftwareBitmap.CreateCopyFromBuffer(px.AsBuffer(0, w * h * 4), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied); }
                finally { if (px != bgra) BufferPool.Return(px); }   // (the bitmap holds its own copy)
                using (bmp)
                {
                    OcrResult res;
                    res = engine.RecognizeAsync(bmp).AsTask().GetAwaiter().GetResult();
                    var words = new List<ScreenWord>();
                    int line = 0;
                    foreach (var l in res.Lines)
                    {
                        foreach (var word in l.Words)
                        {
                            var b = word.BoundingRect;
                            words.Add(new ScreenWord { Text = word.Text, Line = line, Box = new Rect(b.X / scale, b.Y / scale, b.Width / scale, b.Height / scale) });
                        }
                        line++;
                    }
                    return words;
                }
            }
            catch (Exception ex) { Logger.Write("Ocr: " + ex.Message); return null; }
        }

        /// <summary>Double size with bilinear filtering (smooth edges read better than doubled pixels).</summary>
        static byte[] Upscale2(byte[] px, int w, int h)
        {
            int nw = w * 2, nh = h * 2;
            var o = BufferPool.Rent(nw * nh * 4);   // every byte is written below
            for (int y = 0; y < nh; y++)
            {
                // source position of this row's centre: (y + 0.5) / 2 - 0.5, split into a row and a 0 or 0.5 weight
                int sy = Math.Max(0, (y - 1) / 2), sy2 = Math.Min(h - 1, sy + 1);
                int wy = y == 0 || y == nh - 1 ? 0 : (y % 2 == 0 ? 3 : 1);   // quarters towards the next row
                for (int x = 0; x < nw; x++)
                {
                    int sx = Math.Max(0, (x - 1) / 2), sx2 = Math.Min(w - 1, sx + 1);
                    int wx = x == 0 || x == nw - 1 ? 0 : (x % 2 == 0 ? 3 : 1);
                    int a = (sy * w + sx) * 4, b = (sy * w + sx2) * 4, c = (sy2 * w + sx) * 4, d = (sy2 * w + sx2) * 4, t = (y * nw + x) * 4;
                    for (int k = 0; k < 4; k++)
                    {
                        int top = px[a + k] * (4 - wx) + px[b + k] * wx, bottom = px[c + k] * (4 - wx) + px[d + k] * wx;
                        o[t + k] = (byte)((top * (4 - wy) + bottom * wy + 8) / 16);
                    }
                }
            }
            return o;
        }

        /// <summary>Half size, each new pixel the average of four (smooth, unlike skipping pixels).</summary>
        public static byte[] Half(byte[] px, int w, int h)
        {
            int nw = w / 2, nh = h / 2;
            var o = BufferPool.Rent(nw * nh * 4);   // every byte is written below
            for (int y = 0; y < nh; y++)
            {
                int r0 = (2 * y) * w * 4, r1 = (2 * y + 1) * w * 4, t = y * nw * 4;
                for (int x = 0; x < nw; x++, t += 4)
                {
                    int a = r0 + x * 8, b = r1 + x * 8;
                    for (int k = 0; k < 4; k++) o[t + k] = (byte)((px[a + k] + px[a + 4 + k] + px[b + k] + px[b + 4 + k] + 2) / 4);
                }
            }
            return o;
        }

        static byte[] Downscale(byte[] px, int w, int h, int f)
        {
            int nw = w / f, nh = h / f;
            var o = BufferPool.Rent(nw * nh * 4);   // every byte is written below
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                    Buffer.BlockCopy(px, ((y * f) * w + x * f) * 4, o, (y * nw + x) * 4, 4);
            return o;
        }

        // matching
        /// <summary>
        /// A name reduced to what survives a misread: lower case, lookalike characters folded together (0/o, 1/l/i, 4/q/9/g, 5/s,
        /// 8/b, 3/e, 7/t, 2/z), and anything that isn't a letter or digit dropped.
        /// </summary>
        public static string Canon(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char ch in s.ToLowerInvariant())
            {
                char c;
                switch (ch)
                {
                    case '0': case 'o': case 'ö': case 'ó': c = 'o'; break;
                    case '1': case 'l': case 'i': case '|': case '!': case 'í': c = 'i'; break;
                    case '4': case 'q': case '9': case 'g': c = 'g'; break;
                    case '5': case 's': case '$': c = 's'; break;
                    case '8': case 'b': c = 'b'; break;
                    case '3': case 'e': case 'é': case 'ě': c = 'e'; break;
                    case '7': case 't': c = 't'; break;
                    case '2': case 'z': case 'ž': c = 'z'; break;
                    case 'u': case 'v': case 'ú': case 'ů': c = 'v'; break;
                    case 'c': case 'č': c = 'c'; break;
                    case 'r': case 'ř': c = 'r'; break;
                    case 'a': case 'á': case '@': c = 'a'; break;
                    case 'y': case 'ý': c = 'y'; break;
                    default: c = char.IsLetterOrDigit(ch) ? ch : '\0'; break;
                }
                if (c != '\0') sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Canonical forms of every name an account can show up as in a game (username and display name), duplicates and very short ones left out.</summary>
        public static List<string> CanonNames(IEnumerable<string> names) =>
            names.Select(Canon).Where(n => n.Length >= 3).Distinct().OrderByDescending(n => n.Length).ToList();

        static int Distance(string a, string b)
        {
            var d = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) d[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                int prev = d[0]; d[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cur = d[j];
                    d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                    prev = cur;
                }
            }
            return d[b.Length];
        }

        /// <summary>
        /// Does this piece of text show one of the names? Exact for short names (4 characters or fewer), otherwise allowing about
        /// one wrong character in five, or the name sitting inside longer text ("[VIP] moonfox42", "@moonfox42:").
        /// </summary>
        public static bool IsName(string text, IList<string> canonNames)
        {
            var t = Canon(text);
            if (t.Length < 3) return false;
            foreach (var n in canonNames)
            {
                if (t == n) return true;
                if (n.Length <= 4) continue;
                if (t.Length >= n.Length && t.Contains(n)) return true;
                int allowed = Math.Max(1, n.Length / 5);
                if (Math.Abs(t.Length - n.Length) <= allowed && Distance(t, n) <= allowed) return true;
                // a name the reader only got the start of ("Petr'e" for Petrierio): 4+ leading characters in common, at least 40% of it
                int common = 0;
                while (common < t.Length && common < n.Length && t[common] == n[common]) common++;
                if (common >= 4 && common * 5 >= n.Length * 2) return true;
            }
            return false;
        }

        /// <summary>
        /// The boxes to blur: every word, or two neighbouring words on one line read together (a name the reader split), that is
        /// one of the names. Boxes are padded so a name that moves a little between readings stays covered.
        /// </summary>
        public static List<Rect> FindNames(IList<ScreenWord> words, IList<string> canonNames, int width, int height)
        {
            var hits = new List<Rect>();
            if (words == null || canonNames.Count == 0) return hits;
            for (int i = 0; i < words.Count; i++)
            {
                var w = words[i];
                // the next word joined on: a name the reader split ("Wren" "field3") is covered whole, even when its first half
                // already counts as the start of a name on its own
                // (only across a gap narrower than the text is tall: a name next to its score is two things, not one split word)
                bool joined = i + 1 < words.Count && words[i + 1].Line == w.Line
                              && words[i + 1].Box.Left - w.Box.Right < Math.Max(w.Box.Height, words[i + 1].Box.Height)
                              && IsName(w.Text + words[i + 1].Text, canonNames);
                if (joined)
                {
                    var both = w.Box; both.Union(words[i + 1].Box);
                    hits.Add(Pad(both, width, height));
                    i++;
                }
                else if (IsName(w.Text, canonNames)) hits.Add(Pad(w.Box, width, height));
            }
            return hits;
        }

        static Rect Pad(Rect r, int width, int height)
        {
            double px = Math.Max(4, r.Height * 0.45), py = Math.Max(3, r.Height * 0.35);
            var p = new Rect(r.X - px, r.Y - py, r.Width + 2 * px, r.Height + 2 * py);
            p.Intersect(new Rect(0, 0, width, height));
            return p.IsEmpty ? r : p;
        }

        // blurring
        /// <summary>
        /// Makes one area of a BGRA picture unreadable: averaged into blocks about half the text height, then softened so it reads
        /// as a blur rather than a mosaic. The original pixels in that area are gone from the copy.
        /// </summary>
        public static void BlurArea(byte[] px, int width, int height, Rect area, int block = 0)
        {
            int x0 = Math.Max(0, (int)Math.Floor(area.X)), y0 = Math.Max(0, (int)Math.Floor(area.Y));
            int x1 = Math.Min(width, (int)Math.Ceiling(area.Right)), y1 = Math.Min(height, (int)Math.Ceiling(area.Bottom));
            if (x1 - x0 < 2 || y1 - y0 < 2) return;
            if (block <= 0) block = Math.Max(5, (y1 - y0) / 3);

            // 1. block averages: nothing finer than a block survives
            for (int by = y0; by < y1; by += block)
                for (int bx = x0; bx < x1; bx += block)
                {
                    int ex = Math.Min(x1, bx + block), ey = Math.Min(y1, by + block);
                    long b = 0, g = 0, r = 0; int n = 0;
                    for (int y = by; y < ey; y++)
                        for (int x = bx, i = (y * width + bx) * 4; x < ex; x++, i += 4) { b += px[i]; g += px[i + 1]; r += px[i + 2]; n++; }
                    byte B = (byte)(b / n), G = (byte)(g / n), R = (byte)(r / n);
                    for (int y = by; y < ey; y++)
                        for (int x = bx, i = (y * width + bx) * 4; x < ex; x++, i += 4) { px[i] = B; px[i + 1] = G; px[i + 2] = R; }
                }

            // 2. two box-blur passes over the area so the blocks melt together
            int rad = Math.Max(2, block / 2);
            for (int pass = 0; pass < 2; pass++) { BoxPass(px, width, x0, y0, x1, y1, rad, true); BoxPass(px, width, x0, y0, x1, y1, rad, false); }
        }

        static void BoxPass(byte[] px, int width, int x0, int y0, int x1, int y1, int rad, bool horizontal)
        {
            // each output is the average of the 2*rad+1 neighbours along the line (fewer at the ends), taken from running sums,
            // so the cost per pixel doesn't grow with the radius
            int len = horizontal ? x1 - x0 : y1 - y0;
            var sum = new int[(len + 1) * 3];
            int outer0 = horizontal ? y0 : x0, outer1 = horizontal ? y1 : x1;
            for (int o = outer0; o < outer1; o++)
            {
                for (int k = 0; k < len; k++)
                {
                    int i = horizontal ? (o * width + x0 + k) * 4 : ((y0 + k) * width + o) * 4;
                    int t = (k + 1) * 3, f = k * 3;
                    sum[t] = sum[f] + px[i]; sum[t + 1] = sum[f + 1] + px[i + 1]; sum[t + 2] = sum[f + 2] + px[i + 2];
                }
                for (int k = 0; k < len; k++)
                {
                    int lo = Math.Max(0, k - rad), hi = Math.Min(len - 1, k + rad), n = hi - lo + 1;
                    int a = lo * 3, z = (hi + 1) * 3;
                    int i = horizontal ? (o * width + x0 + k) * 4 : ((y0 + k) * width + o) * 4;
                    px[i] = (byte)((sum[z] - sum[a]) / n); px[i + 1] = (byte)((sum[z + 1] - sum[a + 1]) / n); px[i + 2] = (byte)((sum[z + 2] - sum[a + 2]) / n);
                }
            }
        }
    }
}
