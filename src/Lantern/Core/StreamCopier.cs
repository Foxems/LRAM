using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Lantern.Core
{
    /// <summary>A picture ready to show: BGRA pixels (from <see cref="BufferPool"/>), already blurred where names are.</summary>
    public sealed class StreamFrame
    {
        public byte[] Pixels;
        public int Width, Height;
        /// <summary>How much smaller than the window it is (1, 2, 4 …); name boxes are in the window's pixels and are divided by this.</summary>
        public int Scale = 1;
        public DateTime At;
    }

    /// <summary>
    /// Stream mode's picture of one window, held back about a second so nothing is shown before its text was checked: each copy
    /// waits until a reading of a copy taken at or after it has finished, and is then blurred wherever a name was seen in the
    /// last 10 s (which includes that later reading). So a name that pops up (a "You've joined …" message, a new leaderboard row,
    /// a chat line) is blurred from its very first shown frame, as long as it stays up for one reading interval; game messages
    /// stay several seconds. Until the window's text has been read once, nothing is shown at all. The copies for the tile come
    /// at about the tile's size (made smaller on the graphics card); only the one copy a second that is read comes at the
    /// window's full size. Readings run beside the copying (a few at a time across all windows), so they never hold up the
    /// frames. One instance per window: a rejoined account gets a new window and a new copier, so it starts over. <see cref="Step"/>
    /// is called for one window from one loop at a time; <see cref="Dispose"/> may come from anywhere.
    /// </summary>
    public sealed class StreamCopier : IDisposable
    {
        public static readonly TimeSpan ReadEvery = TimeSpan.FromSeconds(1), NameKeep = TimeSpan.FromSeconds(10);
        /// <summary>At 30 copies a second a reading interval plus a slow reading is ~40 frames; beyond that the oldest are dropped.</summary>
        const int MaxWaiting = 48, MaxBacklog = 36;
        static readonly SemaphoreSlim Readers = new SemaphoreSlim(Math.Max(1, Math.Min(4, Environment.ProcessorCount / 4)));

        public readonly IntPtr Window;
        readonly object _gate = new object();
        readonly WindowGrabber _grabber;
        readonly Func<IntPtr, int, (byte[] px, int w, int h, int scale)> _capture;
        readonly Func<byte[], int, int, List<ScreenWord>> _read;
        readonly bool _readInline;
        readonly Queue<StreamFrame> _waiting = new Queue<StreamFrame>();
        readonly List<(Rect Box, DateTime Seen)> _names = new List<(Rect, DateTime)>();
        readonly TimeSpan _offset;
        DateTime _readAt = DateTime.MinValue;
        bool _first = true, _disposed;
        /// <summary>Copies taken at or before this moment have had a reading after them and may be shown.</summary>
        DateTime _checkedUntil = DateTime.MinValue;
        Task<Reading> _reading;

        sealed class Reading
        {
            public byte[] Pixels;
            public int Width, Height;
            public DateTime At;
            public List<ScreenWord> Words;
        }

        /// <summary>A reading has worked at least once for this window.</summary>
        public bool EverRead { get; private set; }

        /// <summary>True while Windows' video capture delivers the frames (false: the screenshot-style fallback, or a test).</summary>
        public bool UsingVideo => _grabber?.UsingVideo ?? false;

        /// <param name="firstReadOffset">Spreads the windows' readings over the second (the first reading is always straight away).</param>
        /// <param name="capture">Tests only: where pictures come from, given a wanted width (default: <see cref="WindowGrabber"/>).</param>
        /// <param name="read">Tests only: the text reader (default: Windows' own).</param>
        /// <param name="readInline">Tests only: read inside <see cref="Step"/> instead of beside it, so the order is exact.</param>
        public StreamCopier(IntPtr window, TimeSpan firstReadOffset,
                            Func<IntPtr, int, (byte[] px, int w, int h, int scale)> capture = null, Func<byte[], int, int, List<ScreenWord>> read = null,
                            bool readInline = false)
        {
            Window = window;
            _offset = firstReadOffset;
            if (capture == null) { _grabber = new WindowGrabber(window); _capture = (_, max) => _grabber.Grab(max); }
            else _capture = capture;
            _read = read ?? NameBlur.Read;
            _readInline = readInline;
        }

        /// <summary>
        /// Takes the newest picture, starts a reading when one is due, and returns the next picture to show, or null (nothing is
        /// ready yet, or the window had nothing new). New copies are about <paramref name="tileWidth"/> pixels wide (0 = the
        /// window's size). The caller hands the returned frame's pixels back to <see cref="BufferPool"/> once shown.
        /// </summary>
        public StreamFrame Step(IList<string> canonNames, int tileWidth, DateTime now)
        {
            lock (_gate)
            {
                if (_disposed) return null;
                TakeReading(canonNames);

                bool due = _reading == null && now - _readAt >= ReadEvery;
                var (px, w, h, scale) = _capture(Window, due ? 0 : tileWidth);
                if (px != null && w > 0 && h > 0)
                {
                    if (due && scale != 1) due = false;   // (a capture that couldn't give the full size: read next time)
                    StreamFrame frame;
                    if (due)
                    {
                        // the full-size copy goes to the reader; the tile gets its own smaller copy of it
                        _readAt = _first ? now + _offset : now;   // (the first straight away, the second 1 s plus a part of a second later)
                        _first = false;
                        frame = Shrink(px, w, h, tileWidth, copy: true, now);
                        StartReading(px, w, h, now);
                        if (_readInline) TakeReading(canonNames);
                    }
                    else frame = Shrink(px, w, h, tileWidth, copy: false, now, scale);

                    if (EverRead)
                    {
                        _waiting.Enqueue(frame);
                        while (_waiting.Count > MaxWaiting) BufferPool.Return(_waiting.Dequeue().Pixels);
                    }
                    else BufferPool.Return(frame.Pixels);   // nothing is shown before the first reading has worked
                }
                _names.RemoveAll(n => now - n.Seen > NameKeep);

                // the oldest checked copy is shown next, one per step, which plays them back at the pace they were taken, about one
                // reading interval late; if they piled up, the oldest are skipped so the delay doesn't keep growing
                int ready = 0;
                foreach (var f in _waiting) if (f.At <= _checkedUntil) ready++;
                if (ready == 0) return null;
                while (ready > MaxBacklog) { BufferPool.Return(_waiting.Dequeue().Pixels); ready--; }
                var show = _waiting.Dequeue();
                double k = 1.0 / show.Scale;
                foreach (var n in _names)
                    NameBlur.BlurArea(show.Pixels, show.Width, show.Height, new Rect(n.Box.X * k, n.Box.Y * k, n.Box.Width * k, n.Box.Height * k));
                return show;
            }
        }

        /// <summary>
        /// A frame of about <paramref name="tileWidth"/> pixels from a picture: halved on the processor while it stays that wide.
        /// With <paramref name="copy"/> the picture itself is left untouched (it is being read); otherwise it becomes the frame or
        /// goes back to the pool.
        /// </summary>
        static StreamFrame Shrink(byte[] px, int w, int h, int tileWidth, bool copy, DateTime at, int scale = 1)
        {
            byte[] cur = px;
            bool owned = !copy;
            while (tileWidth > 0 && w / 2 >= tileWidth && h >= 16)
            {
                var half = NameBlur.Half(cur, w, h);
                if (owned) BufferPool.Return(cur);
                cur = half; owned = true; w /= 2; h /= 2; scale *= 2;
            }
            if (!owned) { var dup = BufferPool.Rent(w * h * 4); Buffer.BlockCopy(cur, 0, dup, 0, w * h * 4); cur = dup; }
            return new StreamFrame { Pixels = cur, Width = w, Height = h, Scale = scale, At = at };
        }

        void StartReading(byte[] px, int w, int h, DateTime at)
        {
            var r = new Reading { Pixels = px, Width = w, Height = h, At = at };
            if (_readInline) { r.Words = SafeRead(r); _reading = Task.FromResult(r); return; }
            _reading = Task.Run(async () =>
            {
                await Readers.WaitAsync().ConfigureAwait(false);
                try { r.Words = SafeRead(r); } finally { Readers.Release(); }
                return r;
            });
        }

        List<ScreenWord> SafeRead(Reading r)
        {
            try { return _read(r.Pixels, r.Width, r.Height); }
            catch (Exception ex) { Logger.Write("Stream read: " + ex.Message); return null; }
        }

        /// <summary>Applies a finished reading: where the names are, and that copies up to its moment may be shown.</summary>
        void TakeReading(IList<string> canonNames)
        {
            if (_reading == null || !_reading.IsCompleted) return;
            var r = _reading.Status == TaskStatus.RanToCompletion ? _reading.Result : null;
            _reading = null;
            if (r == null) return;
            BufferPool.Return(r.Pixels);
            if (r.Words == null) return;   // the reader failed: nothing is cleared, the waiting copies stay held back
            EverRead = true;
            foreach (var box in NameBlur.FindNames(r.Words, canonNames, r.Width, r.Height))
            {
                // the same name in the same place is seen every second: refresh that spot rather than piling up copies of it
                int same = _names.FindIndex(n => Overlap(n.Box, box) > 0.6);
                if (same >= 0) { var u = _names[same].Box; u.Union(box); _names[same] = (u, r.At); }
                else _names.Add((box, r.At));
            }
            if (r.At > _checkedUntil) _checkedUntil = r.At;
        }

        /// <summary>How much two boxes cover the same spot: the shared area over the smaller box's area (0 to 1).</summary>
        static double Overlap(Rect a, Rect b)
        {
            var i = Rect.Intersect(a, b);
            if (i.IsEmpty) return 0;
            double smaller = Math.Min(a.Width * a.Height, b.Width * b.Height);
            return smaller > 0 ? i.Width * i.Height / smaller : 0;
        }

        /// <summary>The places names were seen (for tests).</summary>
        public IReadOnlyList<Rect> NameBoxes { get { lock (_gate) return _names.Select(n => n.Box).ToList(); } }

        /// <summary>Tests only: waits for a reading that is under way.</summary>
        public void WaitForReading() { var r = _reading; try { r?.Wait(5000); } catch { } }

        /// <summary>Ends the capture and drops the held copies. Safe to call more than once, from any thread.</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _grabber?.Dispose();
                while (_waiting.Count > 0) BufferPool.Return(_waiting.Dequeue().Pixels);
                _names.Clear();
            }
        }
    }
}
