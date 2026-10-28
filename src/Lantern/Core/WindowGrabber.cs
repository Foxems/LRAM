using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Lantern.Core
{
    /// <summary>
    /// Reuses the big pixel buffers stream mode goes through (a 800×600 window is 1.9 MB a picture, 20–30 times a second), so
    /// they aren't handed to the garbage collector one after another. Thread-safe.
    /// </summary>
    public static class BufferPool
    {
        static readonly Dictionary<int, Stack<byte[]>> Free = new Dictionary<int, Stack<byte[]>>();
        const int KeepPerSize = 24;

        public static byte[] Rent(int length)
        {
            lock (Free)
                if (Free.TryGetValue(length, out var s) && s.Count > 0) return s.Pop();
            return new byte[length];
        }

        public static void Return(byte[] buffer)
        {
            if (buffer == null || buffer.Length < 64 * 1024) return;
            lock (Free)
            {
                if (!Free.TryGetValue(buffer.Length, out var s)) Free[buffer.Length] = s = new Stack<byte[]>();
                // a few spares of the big full-size pictures (one a second per window is read), more of the small tile-sized ones
                if (s.Count < (buffer.Length > 4 << 20 ? 3 : KeepPerSize)) s.Push(buffer);
            }
        }

        /// <summary>Lets go of everything kept (stream mode turned off).</summary>
        public static void Clear() { lock (Free) Free.Clear(); }
    }

    /// <summary>
    /// Copies of one window's game area (its client area) for stream mode. First choice is Windows' own video capture
    /// (Windows.Graphics.Capture, what OBS and the Snipping Tool's recorder use): Windows hands over each frame the window
    /// presents, on the graphics card, and only the game area is copied back. If that can't start on this PC, or stops
    /// delivering frames, it falls back to the screenshot-style copy (<see cref="Native.CaptureClient"/>). Either way nothing is
    /// read from or sent to the window's process; the capture ends with <see cref="Dispose"/>.
    /// </summary>
    public sealed class WindowGrabber : IDisposable
    {
        // one Direct3D device for all windows
        static readonly object Device = new object();
        static IntPtr _d3d, _ctx;                 // ID3D11Device*, ID3D11DeviceContext*
        static IDirect3DDevice _device;           // the same device as Windows' capture wants it
        static bool _deviceTried;

        static readonly Guid IID_IDXGIDevice = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        static readonly Guid IID_ID3D10Multithread = new Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0");
        static readonly Guid IID_ID3D11Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        static readonly Guid IID_IGraphicsCaptureItem = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");

        [DllImport("d3d11.dll")]
        static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels, uint levels, uint sdkVersion,
                                            out IntPtr device, out int featureLevel, out IntPtr context);
        [DllImport("d3d11.dll")] static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IGraphicsCaptureItemInterop
        {
            IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
            IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
        }

        [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDirect3DDxgiInterfaceAccess
        {
            IntPtr GetInterface([In] ref Guid iid);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct TEXTURE2D_DESC
        {
            public uint Width, Height, MipLevels, ArraySize;
            public int Format;
            public uint SampleCount, SampleQuality;
            public int Usage;
            public uint BindFlags, CPUAccessFlags, MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)] struct BOX { public uint Left, Top, Front, Right, Bottom, Back; }
        [StructLayout(LayoutKind.Sequential)] struct MAPPED { public IntPtr Data; public uint RowPitch, DepthPitch; }

        // the few Direct3D methods needed, called through their slots in the COM tables (fixed by the Direct3D 11 headers)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateTexture2DFn(IntPtr self, ref TEXTURE2D_DESC desc, IntPtr init, out IntPtr tex);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int MapFn(IntPtr self, IntPtr res, uint sub, int type, uint flags, out MAPPED mapped);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void UnmapFn(IntPtr self, IntPtr res, uint sub);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void CopyRegionFn(IntPtr self, IntPtr dst, uint dstSub, uint x, uint y, uint z, IntPtr src, uint srcSub, ref BOX box);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void GetDescFn(IntPtr self, out TEXTURE2D_DESC desc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetProtectedFn(IntPtr self, int on);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateSrvFn(IntPtr self, IntPtr resource, IntPtr desc, out IntPtr view);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void GenerateMipsFn(IntPtr self, IntPtr view);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void CopyWholeFn(IntPtr self, IntPtr dst, uint dstSub, uint x, uint y, uint z, IntPtr src, uint srcSub, IntPtr box);

        const int SlotCreateTexture2D = 5, SlotCreateShaderResourceView = 7;      // ID3D11Device
        const int SlotGenerateMips = 54;                                          // ID3D11DeviceContext
        const int SlotMap = 14, SlotUnmap = 15, SlotCopySubresourceRegion = 46;   // ID3D11DeviceContext
        const int SlotGetDesc = 10;                                               // ID3D11Texture2D
        const int SlotSetMultithreadProtected = 5;                                // ID3D10Multithread

        static T Slot<T>(IntPtr com, int slot) where T : Delegate
        {
            var table = Marshal.ReadIntPtr(com);
            return (T)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(table, slot * IntPtr.Size), typeof(T));
        }

        static CreateTexture2DFn _createTexture;
        static MapFn _map;
        static UnmapFn _unmap;
        static CopyRegionFn _copyRegion;
        static CopyWholeFn _copyWhole;
        static CreateSrvFn _createSrv;
        static GenerateMipsFn _generateMips;

        /// <summary>The shared device, made once; false if this PC can't (then every window uses the fallback).</summary>
        static bool EnsureDevice()
        {
            lock (Device)
            {
                if (_deviceTried) return _device != null;
                _deviceTried = true;
                try
                {
                    if (!GraphicsCaptureSession.IsSupported()) return false;
                    const uint BGRA = 0x20, SDK = 7;
                    int hr = D3D11CreateDevice(IntPtr.Zero, 1 /* hardware */, IntPtr.Zero, BGRA, IntPtr.Zero, 0, SDK, out _d3d, out _, out _ctx);
                    if (hr < 0) hr = D3D11CreateDevice(IntPtr.Zero, 5 /* WARP */, IntPtr.Zero, BGRA, IntPtr.Zero, 0, SDK, out _d3d, out _, out _ctx);
                    if (hr < 0 || _d3d == IntPtr.Zero || _ctx == IntPtr.Zero) { Logger.Write("Capture: no Direct3D device (" + hr.ToString("X8") + ")"); return false; }

                    // Windows' capture works with the device on its own threads too: let Direct3D serialise the calls
                    var mtIid = IID_ID3D10Multithread;
                    if (Marshal.QueryInterface(_ctx, ref mtIid, out IntPtr mt) >= 0 && mt != IntPtr.Zero)
                    {
                        Slot<SetProtectedFn>(mt, SlotSetMultithreadProtected)(mt, 1);
                        Marshal.Release(mt);
                    }

                    var dxgiIid = IID_IDXGIDevice;
                    if (Marshal.QueryInterface(_d3d, ref dxgiIid, out IntPtr dxgi) < 0) return false;
                    try
                    {
                        if (CreateDirect3D11DeviceFromDXGIDevice(dxgi, out IntPtr insp) < 0 || insp == IntPtr.Zero) return false;
                        try { _device = Marshal.GetObjectForIUnknown(insp) as IDirect3DDevice; }
                        finally { Marshal.Release(insp); }
                    }
                    finally { Marshal.Release(dxgi); }

                    _createTexture = Slot<CreateTexture2DFn>(_d3d, SlotCreateTexture2D);
                    _map = Slot<MapFn>(_ctx, SlotMap);
                    _unmap = Slot<UnmapFn>(_ctx, SlotUnmap);
                    _copyRegion = Slot<CopyRegionFn>(_ctx, SlotCopySubresourceRegion);
                    _copyWhole = Slot<CopyWholeFn>(_ctx, SlotCopySubresourceRegion);
                    _createSrv = Slot<CreateSrvFn>(_d3d, SlotCreateShaderResourceView);
                    _generateMips = Slot<GenerateMipsFn>(_ctx, SlotGenerateMips);
                    return _device != null;
                }
                catch (Exception ex) { Logger.Write("Capture: device: " + ex.Message); _device = null; return false; }
            }
        }

        // one window
        public readonly IntPtr Window;
        readonly object _gate = new object();
        GraphicsCaptureItem _item;
        Direct3D11CaptureFramePool _pool;
        GraphicsCaptureSession _session;
        Windows.Graphics.SizeInt32 _poolSize;
        IntPtr _staging;
        int _stagingW, _stagingH;
        DateTime _since;            // when video capture started, or last delivered a frame
        bool _video, _disposed;

        /// <summary>True while Windows' video capture is in use (false: the screenshot-style fallback).</summary>
        public bool UsingVideo { get { lock (_gate) return _video; } }

        public WindowGrabber(IntPtr window)
        {
            Window = window;
            try { _video = StartVideo(); }
            catch (Exception ex) { Logger.Write("Capture: start: " + ex.Message); StopVideo(); _video = false; }
        }

        bool StartVideo()
        {
            if (!EnsureDevice() || Native.IsMinimized(Window)) return false;   // a minimised window has nothing to capture yet
            var factory = WindowsRuntimeMarshal.GetActivationFactory(typeof(GraphicsCaptureItem));
            var interop = (IGraphicsCaptureItemInterop)factory;
            var iid = IID_IGraphicsCaptureItem;
            IntPtr p = interop.CreateForWindow(Window, ref iid);
            if (p == IntPtr.Zero) return false;
            try { _item = Marshal.GetObjectForIUnknown(p) as GraphicsCaptureItem; }
            finally { Marshal.Release(p); }
            if (_item == null) return false;

            _poolSize = _item.Size;
            if (_poolSize.Width <= 0 || _poolSize.Height <= 0) return false;
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
            _session = _pool.CreateCaptureSession(_item);
            TryHideCursor(_session);
            TryHideBorder(_session);
            _session.StartCapture();
            _since = DateTime.UtcNow;
            return true;
        }

        // (newer Windows only; kept in their own methods so an older Windows only loses these two niceties)
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void TryHideCursor(GraphicsCaptureSession s) { try { s.IsCursorCaptureEnabled = false; } catch { } }
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void TryHideBorder(GraphicsCaptureSession s) { try { s.IsBorderRequired = false; } catch { } }

        void StopVideo()
        {
            try { _session?.Dispose(); } catch { }
            try { _pool?.Dispose(); } catch { }
            _session = null; _pool = null; _item = null;
            lock (Device)
            {
                foreach (var t in new[] { _staging, _stagingSmall, _mipSrv, _mipTex }) if (t != IntPtr.Zero) Marshal.Release(t);
                _staging = _stagingSmall = _mipSrv = _mipTex = IntPtr.Zero;
                _stagingW = _stagingH = _smallW = _smallH = _mipW = _mipH = _mipLevels = 0;
            }
        }

        /// <summary>
        /// The newest picture of the game area (BGRA, top row first, from <see cref="BufferPool"/>), or null when there is no new
        /// one: video capture delivers a frame only when the game draws one, and a minimised window draws none.
        /// <paramref name="maxWidth"/> above 0 asks for a smaller picture (for a tile): it is halved, on the graphics card, as long
        /// as it stays at least that wide, and <c>scale</c> says by how much (1, 2, 4 ...). 0 = the window's full size.
        /// </summary>
        public (byte[] px, int w, int h, int scale) Grab(int maxWidth = 0)
        {
            lock (_gate)
            {
                if (_disposed || !Native.WindowExists(Window)) return (null, 0, 0, 1);
                if (_video)
                {
                    try
                    {
                        var got = GrabVideo(maxWidth);
                        if (got.px != null) { _since = DateTime.UtcNow; return got; }
                        // no frame for a while although the window is up and drawing: video capture isn't working here
                        if (Native.IsMinimized(Window)) _since = DateTime.UtcNow;
                        else if (DateTime.UtcNow - _since > TimeSpan.FromSeconds(4)) { Logger.Write("Capture: no frames from video capture, falling back"); StopVideo(); _video = false; }
                        return (null, 0, 0, 1);
                    }
                    catch (Exception ex) { Logger.Write("Capture: " + ex.Message + ", falling back"); StopVideo(); _video = false; }
                }
                // fallback: a full-size copy, halved on the processor down to the size asked for
                var px = Native.CaptureClient(Window, out int w, out int h);
                int scale = 1;
                while (px != null && maxWidth > 0 && w / 2 >= maxWidth && h >= 16)
                {
                    var half = NameBlur.Half(px, w, h);
                    BufferPool.Return(px);
                    px = half; w /= 2; h /= 2; scale *= 2;
                }
                return (px, w, h, scale);
            }
        }

        static IntPtr _getDescPtr;
        static GetDescFn _getDesc;

        static TEXTURE2D_DESC DescOf(IntPtr tex)
        {
            var fn = Marshal.ReadIntPtr(Marshal.ReadIntPtr(tex), SlotGetDesc * IntPtr.Size);
            if (fn != _getDescPtr) { _getDesc = (GetDescFn)Marshal.GetDelegateForFunctionPointer(fn, typeof(GetDescFn)); _getDescPtr = fn; }
            _getDesc(tex, out var d);
            return d;
        }

        (byte[] px, int w, int h, int scale) GrabVideo(int maxWidth)
        {
            // the newest waiting frame; older ones are dropped
            Direct3D11CaptureFrame frame = null;
            for (var next = _pool.TryGetNextFrame(); next != null; next = _pool.TryGetNextFrame()) { frame?.Dispose(); frame = next; }
            if (frame == null) return (null, 0, 0, 1);
            using (frame)
            {
                var size = frame.ContentSize;
                if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
                {
                    _poolSize = size;   // the window was resized: later frames come at the new size
                    _pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                }
                if (!ClientArea(Window, size.Width, size.Height, out int cx, out int cy, out int cw, out int ch)) return (null, 0, 0, 1);

                var surface = frame.Surface;
                IntPtr tex = IntPtr.Zero;
                try
                {
                    var texIid = IID_ID3D11Texture2D;
                    tex = ((IDirect3DDxgiInterfaceAccess)surface).GetInterface(ref texIid);
                    if (tex == IntPtr.Zero) return (null, 0, 0, 1);
                    lock (Device)
                    {
                        var td = DescOf(tex);
                        cw = Math.Min(cw, (int)td.Width - cx); ch = Math.Min(ch, (int)td.Height - cy);
                        if (cw <= 0 || ch <= 0) return (null, 0, 0, 1);
                        var box = new BOX { Left = (uint)cx, Top = (uint)cy, Front = 0, Right = (uint)(cx + cw), Bottom = (uint)(cy + ch), Back = 1 };

                        // how many halvings still leave the picture at least maxWidth wide (at most 4: a sixteenth)
                        int k = 0;
                        while (maxWidth > 0 && k < 4 && (cw >> (k + 1)) >= maxWidth && (ch >> (k + 1)) >= 8) k++;

                        if (k == 0)
                        {
                            EnsureStaging(ref _staging, ref _stagingW, ref _stagingH, cw, ch);
                            _copyRegion(_ctx, _staging, 0, 0, 0, 0, tex, 0, ref box);
                            var full = ReadBack(_staging, cw, ch);
                            for (int i = 3; i < full.Length; i += 4) full[i] = 255;   // (the text reader wants an opaque picture)
                            return (full, cw, ch, 1);
                        }

                        // smaller: the game area goes into a texture with mip levels, the graphics card fills the halved levels,
                        // and only level k comes back to the processor
                        EnsureMips(cw, ch, k + 1);
                        _copyRegion(_ctx, _mipTex, 0, 0, 0, 0, tex, 0, ref box);
                        _generateMips(_ctx, _mipSrv);
                        int mw = Math.Max(1, cw >> k), mh = Math.Max(1, ch >> k);
                        EnsureStaging(ref _stagingSmall, ref _smallW, ref _smallH, mw, mh);
                        _copyWhole(_ctx, _stagingSmall, 0, 0, 0, 0, _mipTex, (uint)k, IntPtr.Zero);
                        return (ReadBack(_stagingSmall, mw, mh), mw, mh, 1 << k);
                    }
                }
                finally
                {
                    if (tex != IntPtr.Zero) Marshal.Release(tex);
                    try { Marshal.ReleaseComObject(surface); } catch { }   // now, not whenever the garbage collector gets to it
                }
            }
        }

        // textures (callers hold the Device lock)
        IntPtr _stagingSmall, _mipTex, _mipSrv;
        int _smallW, _smallH, _mipW, _mipH, _mipLevels;

        void EnsureStaging(ref IntPtr staging, ref int sw, ref int sh, int w, int h)
        {
            if (staging != IntPtr.Zero && sw == w && sh == h) return;
            if (staging != IntPtr.Zero) { Marshal.Release(staging); staging = IntPtr.Zero; }
            var desc = new TEXTURE2D_DESC
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = 87 /* B8G8R8A8_UNORM */,
                SampleCount = 1, Usage = 3 /* STAGING */, CPUAccessFlags = 0x20000 /* READ */
            };
            int hr = _createTexture(_d3d, ref desc, IntPtr.Zero, out staging);
            if (hr < 0 || staging == IntPtr.Zero) { staging = IntPtr.Zero; throw new InvalidOperationException("staging texture " + hr.ToString("X8")); }
            sw = w; sh = h;
        }

        void EnsureMips(int w, int h, int levels)
        {
            if (_mipTex != IntPtr.Zero && _mipW == w && _mipH == h && _mipLevels == levels) return;
            if (_mipSrv != IntPtr.Zero) { Marshal.Release(_mipSrv); _mipSrv = IntPtr.Zero; }
            if (_mipTex != IntPtr.Zero) { Marshal.Release(_mipTex); _mipTex = IntPtr.Zero; }
            var desc = new TEXTURE2D_DESC
            {
                Width = (uint)w, Height = (uint)h, MipLevels = (uint)levels, ArraySize = 1, Format = 87,
                SampleCount = 1, Usage = 0 /* DEFAULT */, BindFlags = 0x8 /* SHADER_RESOURCE */ | 0x20 /* RENDER_TARGET */,
                MiscFlags = 0x1 /* GENERATE_MIPS */
            };
            int hr = _createTexture(_d3d, ref desc, IntPtr.Zero, out _mipTex);
            if (hr < 0 || _mipTex == IntPtr.Zero) { _mipTex = IntPtr.Zero; throw new InvalidOperationException("mip texture " + hr.ToString("X8")); }
            hr = _createSrv(_d3d, _mipTex, IntPtr.Zero, out _mipSrv);
            if (hr < 0 || _mipSrv == IntPtr.Zero) { _mipSrv = IntPtr.Zero; throw new InvalidOperationException("mip view " + hr.ToString("X8")); }
            _mipW = w; _mipH = h; _mipLevels = levels;
        }

        static byte[] ReadBack(IntPtr staging, int w, int h)
        {
            int hr = _map(_ctx, staging, 0, 1 /* READ */, 0, out var m);
            if (hr < 0) throw new InvalidOperationException("map " + hr.ToString("X8"));
            try
            {
                var px = BufferPool.Rent(w * h * 4);
                int row = w * 4;
                for (int y = 0; y < h; y++) Marshal.Copy(m.Data + (int)(y * m.RowPitch), px, y * row, row);
                return px;
            }
            finally { _unmap(_ctx, staging, 0); }
        }

        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out Native.RECT r);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out Native.RECT val, int size);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

        /// <summary>
        /// Where the game area sits inside a captured frame: video capture takes the whole window as it appears (title bar
        /// included, its visible frame bounds), so the client area is cut out of it. All in physical pixels (Lantern is DPI aware).
        /// </summary>
        static bool ClientArea(IntPtr hwnd, int frameW, int frameH, out int x, out int y, out int w, out int h)
        {
            x = y = w = h = 0;
            if (DwmGetWindowAttribute(hwnd, 9 /* EXTENDED_FRAME_BOUNDS */, out var frame, Marshal.SizeOf(typeof(Native.RECT))) != 0) return false;
            var p = new POINT();
            if (!ClientToScreen(hwnd, ref p) || !GetClientRect(hwnd, out var rc)) return false;
            x = Math.Max(0, p.X - frame.Left); y = Math.Max(0, p.Y - frame.Top);
            w = Math.Min(rc.Right - rc.Left, frameW - x); h = Math.Min(rc.Bottom - rc.Top, frameH - y);
            return w > 0 && h > 0;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                StopVideo();
            }
        }
    }
}
