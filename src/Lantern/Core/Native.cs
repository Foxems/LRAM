using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

// Every native library Lantern calls (user32, kernel32, dwmapi, gdi32, psapi, d3d11, wintrust) is part of Windows. Looking only in
// System32 means a DLL of the same name dropped next to Lantern.exe (say, in an unzipped Downloads folder) is never loaded instead.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Lantern.Core
{
    /// <summary>
    /// Win32 helpers. Window-level (move, close, query) and scheduling hints (priority, efficiency mode,
    /// and the opt-in working-set trim and cap) - nothing reads Roblox's memory or injects into it.
    /// </summary>
    public static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct MARGINS { public int Left, Right, Top, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_POWER_THROTTLING_STATE { public uint Version; public uint ControlMask; public uint StateMask; }

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORY_PRIORITY_INFORMATION { public uint MemoryPriority; }

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr hProcess);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetProcessInformation(IntPtr h, int cls, ref PROCESS_POWER_THROTTLING_STATE info, int size);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessWorkingSetSizeEx(IntPtr h, out UIntPtr min, out UIntPtr max, out uint flags);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetProcessWorkingSetSizeEx(IntPtr h, UIntPtr min, UIntPtr max, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetProcessInformation(IntPtr h, int cls, ref MEMORY_PRIORITY_INFORMATION info, int size);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT val, int size);
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);

        const uint WM_CLOSE = 0x0010;
        const uint GW_OWNER = 4;
        const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        const int SW_RESTORE = 9;
        const int SW_SHOWMINNOACTIVE = 7;
        const uint MONITOR_DEFAULTTONEAREST = 2;

        /// <summary>Hands unused pages of Lantern's own memory back to Windows (our process only).</summary>
        public static void TrimOwnWorkingSet()
        {
            try
            {
                GC.Collect(2, GCCollectionMode.Forced, true);
                using (var me = System.Diagnostics.Process.GetCurrentProcess()) EmptyWorkingSet(me.Handle);
            }
            catch { }
        }

        // Windows 11 look: Mica + dark title bar
        public static void ApplyMica(IntPtr hwnd, bool dark)
        {
            try
            {
                int d = dark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, 20, ref d, 4);          // DWMWA_USE_IMMERSIVE_DARK_MODE
                int mica = 2;                                       // DWMSBT_MAINWINDOW
                DwmSetWindowAttribute(hwnd, 38, ref mica, 4);       // DWMWA_SYSTEMBACKDROP_TYPE
                var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref m);
            }
            catch { /* older Windows: plain background, still fine */ }
        }

        /// <summary>
        /// Switches a window between a Mica backdrop and a plain one, keeping the title bar's light/dark in step. Takes effect at once, on a window that is already showing.
        /// </summary>
        public static void ApplyBackdrop(IntPtr hwnd, bool dark, bool mica)
        {
            try
            {
                int d = dark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, 20, ref d, 4);                  // DWMWA_USE_IMMERSIVE_DARK_MODE
                int type = mica ? 2 : 1;                                    // DWMSBT_MAINWINDOW | DWMSBT_NONE
                DwmSetWindowAttribute(hwnd, 38, ref type, 4);               // DWMWA_SYSTEMBACKDROP_TYPE
                var m = mica ? new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 } : new MARGINS();
                DwmExtendFrameIntoClientArea(hwnd, ref m);
            }
            catch { /* older Windows: plain background, still fine */ }
        }

        public static void SetDarkTitleBar(IntPtr hwnd, bool dark)
        {
            try { int d = dark ? 1 : 0; DwmSetWindowAttribute(hwnd, 20, ref d, 4); } catch { }
        }

        /// <summary>
        /// Hides (or shows again) a window while it stays "open": it is laid out, rendered and receives its Loaded event, the
        /// screen just doesn't show it (DWMWA_CLOAK). Used to finish starting up behind the start-up picture. False if refused.
        /// </summary>
        public static bool Cloak(IntPtr hwnd, bool cloak)
        {
            try { int v = cloak ? 1 : 0; return DwmSetWindowAttribute(hwnd, 13, ref v, 4) == 0; } catch { return false; }
        }

        /// <summary>The part of a window that is actually drawn, in screen pixels (without the invisible resize borders).</summary>
        public static bool FrameBounds(IntPtr hwnd, out RECT r)
        {
            r = default;
            try { if (DwmGetWindowAttribute(hwnd, 9, out r, Marshal.SizeOf(typeof(RECT))) == 0 && r.Right > r.Left) return true; } catch { }   // DWMWA_EXTENDED_FRAME_BOUNDS
            return GetWindowRect(hwnd, out r);
        }

        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);

        /// <summary>Moves and sizes a window in screen pixels, without activating it or changing its stacking.</summary>
        public static void Place(IntPtr hwnd, int x, int y, int w, int h) => SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, 0x0004 | 0x0010);   // NOZORDER | NOACTIVATE

        /// <summary>The display scaling of the monitor a window is on (1.0 = 100 %).</summary>
        public static double ScaleOf(IntPtr hwnd)
        {
            try { uint d = GetDpiForWindow(hwnd); if (d > 0) return d / 96.0; } catch { }
            return 1;
        }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

        /// <summary>The process whose window has the keyboard focus right now (0 if none).</summary>
        public static int ForegroundPid()
        {
            try { var h = GetForegroundWindow(); if (h == IntPtr.Zero) return 0; GetWindowThreadProcessId(h, out uint pid); return (int)pid; }
            catch { return 0; }
        }

        public static void Foreground(IntPtr hwnd)
        {
            try { ShowWindow(hwnd, SW_RESTORE); SetForegroundWindow(hwnd); } catch { }
        }

        // gentle resource limits (scheduling hints only; no memory access, no injection)
        /// <summary>
        /// Windows "efficiency mode" (EcoQoS) for one process: it runs on the power-efficient cores at low clock speed.
        /// Needs only the right to change scheduling info, the same level as setting the process priority.
        /// </summary>
        public static bool SetEfficiencyMode(int pid, bool on)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(0x0200 /* PROCESS_SET_INFORMATION */, false, pid);
                if (h == IntPtr.Zero) return false;
                var st = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = 1 /* EXECUTION_SPEED */, StateMask = on ? 1u : 0u };
                return SetProcessInformation(h, 4 /* ProcessPowerThrottling */, ref st, Marshal.SizeOf(typeof(PROCESS_POWER_THROTTLING_STATE)));
            }
            catch { return false; }
            finally { if (h != IntPtr.Zero) CloseHandle(h); }
        }

        /// <summary>
        /// CPU time a process has used so far, in 100 ns ticks. Needs only the lowest query right
        /// (PROCESS_QUERY_LIMITED_INFORMATION, what Task Manager uses): no memory access, nothing written.
        /// </summary>
        public static bool ProcessCpuTicks(int pid, out long ticks)
        {
            ticks = 0;
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
                if (h == IntPtr.Zero) return false;
                if (!GetProcessTimes(h, out _, out _, out long kernel, out long user)) return false;
                ticks = kernel + user;
                return true;
            }
            catch { return false; }
            finally { if (h != IntPtr.Zero) CloseHandle(h); }
        }

        /// <summary>When a process started (UTC), with the same lowest query right as <see cref="ProcessCpuTicks"/>; null if Windows won't say.</summary>
        public static DateTime? ProcessStartUtc(int pid)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
                if (h == IntPtr.Zero || !GetProcessTimes(h, out long create, out _, out _, out _) || create <= 0) return null;
                return DateTime.FromFileTimeUtc(create);
            }
            catch { return null; }
            finally { if (h != IntPtr.Zero) CloseHandle(h); }
        }

        /// <summary>Whole-PC CPU time: (busy, total) in 100 ns ticks summed over all cores since boot.</summary>
        public static bool SystemCpuTicks(out long busy, out long total)
        {
            busy = total = 0;
            try
            {
                if (!GetSystemTimes(out long idle, out long kernel, out long user)) return false;
                total = kernel + user;                 // kernel time already includes idle time
                busy = total - idle;
                return true;
            }
            catch { return false; }
        }

        /// <summary>Minimises the windows of these processes without stealing focus.</summary>
        public static int MinimizeWindowsOf(ICollection<int> pids)
        {
            var w = TopWindowsOf(pids);
            foreach (var h in w) ShowWindow(h, SW_SHOWMINNOACTIVE);
            return w.Count;
        }

        /// <summary>True when the process has a window and every one of its top-level windows is minimised.</summary>
        public static bool AllMinimized(int pid)
        {
            var w = TopWindowsOf(new HashSet<int> { pid });
            if (w.Count == 0) return false;
            foreach (var h in w) if (!IsIconic(h)) return false;
            return true;
        }

        /// <summary>
        /// Asks Windows to move a process's idle pages out of its working set (the same call Task Manager-style memory
        /// cleaners use). Nothing in the process is read or changed: pages that are still needed are simply faulted back in.
        /// Needs the "set quota" right, which Roblox's protection may refuse; false then.
        /// </summary>
        public static bool TrimWorkingSet(int pid)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(0x0100 /* PROCESS_SET_QUOTA */ | 0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
                if (h == IntPtr.Zero) return false;
                return EmptyWorkingSet(h);
            }
            catch { return false; }
            finally { if (h != IntPtr.Zero) CloseHandle(h); }
        }

        const uint QUOTA_LIMITS_HARDWS_MIN_DISABLE = 0x2, QUOTA_LIMITS_HARDWS_MAX_ENABLE = 0x4, QUOTA_LIMITS_HARDWS_MAX_DISABLE = 0x8;
        const uint MEMORY_PRIORITY_VERY_LOW = 1, MEMORY_PRIORITY_NORMAL = 5;
        const int ProcessMemoryPriority = 0;
        const ulong DefaultMinWorkingSet = 204800;   // Windows' own default (50 pages)

        /// <summary>
        /// Puts a hard ceiling on how much of a process may sit in physical memory (its working set). Windows trims it to the
        /// ceiling straight away and keeps it there; what is taken out waits in compressed memory or the paging file and comes
        /// back page by page if the game touches it. The process's reserved (committed) memory does not change, nothing inside it
        /// is read or written, and the limit disappears with the process. Also marks the process's pages as the first ones to
        /// give up under memory pressure. <paramref name="originalMin"/> is what <see cref="LiftWorkingSetCap"/> needs later.
        /// </summary>
        public static bool CapWorkingSet(int pid, long capBytes, out long originalMin)
        {
            originalMin = 0;
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(0x0100 /* SET_QUOTA */ | 0x0200 /* SET_INFORMATION */ | 0x1000 /* QUERY_LIMITED */, false, pid);
                if (h == IntPtr.Zero) return false;
                originalMin = GetProcessWorkingSetSizeEx(h, out var min, out _, out _) ? (long)min.ToUInt64() : (long)DefaultMinWorkingSet;
                // The minimum only has to sit below the ceiling, but Windows refuses some small values for a process that has pinned
                // memory (Roblox's client does: 200 KB is rejected, 1 MB or more is fine). So try a few, small ones first.
                foreach (ulong lo in new[] { 16UL << 20, 64UL << 20, (ulong)Math.Max(originalMin, 0), DefaultMinWorkingSet })
                {
                    if (lo >= (ulong)capBytes) continue;
                    if (!SetProcessWorkingSetSizeEx(h, (UIntPtr)lo, (UIntPtr)(ulong)capBytes, QUOTA_LIMITS_HARDWS_MIN_DISABLE | QUOTA_LIMITS_HARDWS_MAX_ENABLE)) continue;
                    SetMemoryPriority(h, MEMORY_PRIORITY_VERY_LOW);
                    return true;
                }
                return false;
            }
            catch { return false; }
            finally { if (h != IntPtr.Zero) CloseHandle(h); }
        }

        /// <summary>
        /// Takes the ceiling off again. The limit is replaced by a soft one as large as the PC's memory (Windows needs it below the
        /// pages currently available, so it tries smaller ones if that is refused) instead of the process's old tiny soft limit,
        /// because Windows trims a process to its soft limit the moment it is set. The window then fills back up in a second or two.
        /// </summary>
        public static bool LiftWorkingSetCap(int pid, long originalMin)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(0x0100 | 0x0200 | 0x1000, false, pid);
                if (h == IntPtr.Zero) return false;
                Memory(out var total, out _);
                bool ok = false;
                foreach (ulong min in new[] { (ulong)Math.Max(originalMin, 0), 16UL << 20, DefaultMinWorkingSet })
                {
                    for (ulong max = total > 0 ? total : 8UL << 30; max >= 1UL << 30 && !ok; max /= 2)
                        ok = min > 0 && SetProcessWorkingSetSizeEx(h, (UIntPtr)min, (UIntPtr)max, QUOTA_LIMITS_HARDWS_MIN_DISABLE | QUOTA_LIMITS_HARDWS_MAX_DISABLE);
                    if (ok) break;
                }
                if (ok) SetMemoryPriority(h, MEMORY_PRIORITY_NORMAL);
                return ok;
            }
            catch { return false; }
            finally { if (h != IntPtr.Zero) CloseHandle(h); }
        }

        /// <summary>True when the process carries a hard working-set ceiling (one a Lantern run that ended abruptly may have left); <paramref name="min"/> is its current minimum.</summary>
        public static bool HasHardCap(int pid, out long min)
        {
            min = 0;
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(0x1000 /* QUERY_LIMITED */, false, pid);
                if (h == IntPtr.Zero || !GetProcessWorkingSetSizeEx(h, out var lo, out _, out uint flags)) return false;
                min = (long)lo.ToUInt64();
                return (flags & QUOTA_LIMITS_HARDWS_MAX_ENABLE) != 0;
            }
            catch { return false; }
            finally { if (h != IntPtr.Zero) CloseHandle(h); }
        }

        static void SetMemoryPriority(IntPtr h, uint priority)
        {
            var mp = new MEMORY_PRIORITY_INFORMATION { MemoryPriority = priority };
            SetProcessInformation(h, ProcessMemoryPriority, ref mp, Marshal.SizeOf(typeof(MEMORY_PRIORITY_INFORMATION)));   // a hint; fine if refused
        }

        /// <summary>The processes among <paramref name="pids"/> that have a window and whose windows are all minimised (one pass over the desktop).</summary>
        public static HashSet<int> MinimizedPids(ICollection<int> pids)
        {
            var shown = new HashSet<int>();      // has a minimised top-level window
            var open = new HashSet<int>();       // has a window that is not minimised
            if (pids.Count == 0) return shown;
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (!pids.Contains((int)pid)) return true;
                if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero || GetWindowTextLength(h) == 0) return true;
                if (IsIconic(h)) shown.Add((int)pid); else open.Add((int)pid);
                return true;
            }, IntPtr.Zero);
            shown.ExceptWith(open);
            return shown;
        }

        /// <summary>Physical memory in bytes: (total, available), or (0,0) if Windows won't say.</summary>
        public static void Memory(out ulong total, out ulong available)
        {
            total = available = 0;
            try
            {
                var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
                if (GlobalMemoryStatusEx(ref m)) { total = m.ullTotalPhys; available = m.ullAvailPhys; }
            }
            catch { }
        }

        // Roblox window handling
        public static List<IntPtr> TopWindowsOf(ICollection<int> pids)
        {
            var found = new List<IntPtr>();
            var seen = new HashSet<int>();
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (!pids.Contains((int)pid) || seen.Contains((int)pid)) return true;
                if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero || GetWindowTextLength(h) == 0) return true;
                seen.Add((int)pid);
                found.Add(h);
                return true;
            }, IntPtr.Zero);
            return found;
        }

        public static void RequestClose(ICollection<int> pids)
        {
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pids.Contains((int)pid)) PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
        }

        // Live page: thumbnails and window placement (window-level only, the same as the taskbar's previews)
        [StructLayout(LayoutKind.Sequential)]
        struct DWM_THUMBNAIL_PROPERTIES
        {
            public uint dwFlags;
            public RECT rcDestination, rcSource;
            public byte opacity;
            public bool fVisible, fSourceClientAreaOnly;
        }

        [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);

        const uint DWM_TNP_RECTDESTINATION = 0x1, DWM_TNP_OPACITY = 0x4, DWM_TNP_VISIBLE = 0x8, DWM_TNP_SOURCECLIENTAREAONLY = 0x10;
        static readonly IntPtr HWND_TOP = IntPtr.Zero, HWND_BOTTOM = new IntPtr(1);
        const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002;
        const int SW_SHOWNOACTIVATE = 4;

        /// <summary>
        /// Asks Windows to draw a live copy of <paramref name="source"/> inside <paramref name="dest"/> (what the taskbar previews
        /// use). Windows composes it on the graphics card; the source window is not read, hooked or changed. Zero if refused.
        /// </summary>
        public static IntPtr RegisterThumbnail(IntPtr dest, IntPtr source)
        {
            try { return DwmRegisterThumbnail(dest, source, out var t) == 0 ? t : IntPtr.Zero; } catch { return IntPtr.Zero; }
        }

        public static void UnregisterThumbnail(IntPtr thumb)
        {
            try { if (thumb != IntPtr.Zero) DwmUnregisterThumbnail(thumb); } catch { }
        }

        /// <summary>Where the copy is drawn, in pixels of the destination window's client area; false once the source is gone.</summary>
        public static bool UpdateThumbnail(IntPtr thumb, RECT dest, bool visible, byte opacity = 255)
        {
            try
            {
                var p = new DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
                    rcDestination = dest, opacity = opacity, fVisible = visible, fSourceClientAreaOnly = true
                };
                return DwmUpdateThumbnailProperties(thumb, ref p) == 0;
            }
            catch { return false; }
        }

        /// <summary>The size of a window's client area in pixels (what its thumbnail shows); (0,0) if gone.</summary>
        public static (int w, int h) ClientSize(IntPtr hwnd)
        {
            try { return GetClientRect(hwnd, out var r) ? (r.Right - r.Left, r.Bottom - r.Top) : (0, 0); } catch { return (0, 0); }
        }

        public static bool WindowExists(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);
        public static bool IsMinimized(IntPtr hwnd) => IsIconic(hwnd);

        /// <summary>Minimises one window without taking focus: it stops drawing, and the opt-in trim and cap may then apply.</summary>
        public static void MinimizeQuietly(IntPtr hwnd)
        {
            try { ShowWindow(hwnd, SW_SHOWMINNOACTIVE); } catch { }
        }

        /// <summary>Brings a minimised window back without taking focus and tucks it behind everything else, so it draws again for its tile.</summary>
        public static void RestoreBehind(IntPtr hwnd)
        {
            try
            {
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                SendToBack(hwnd);
            }
            catch { }
        }

        /// <summary>Puts a window at the bottom of the desktop's stack without moving, resizing or activating it.</summary>
        public static void SendToBack(IntPtr hwnd)
        {
            try { SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); } catch { }
        }

        /// <summary>Restores a window, lays its visible frame exactly over <paramref name="screen"/> (pixels) and gives it focus.</summary>
        public static void PlaceOverAndFocus(IntPtr hwnd, RECT screen)
        {
            try
            {
                if (IsZoomed(hwnd) || IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                int l = 0, t = 0, r = 0, b = 0;
                if (GetWindowRect(hwnd, out RECT outer) && DwmGetWindowAttribute(hwnd, 9, out RECT vis, Marshal.SizeOf(typeof(RECT))) == 0)
                {
                    l = vis.Left - outer.Left; t = vis.Top - outer.Top;
                    r = outer.Right - vis.Right; b = outer.Bottom - vis.Bottom;
                }
                SetWindowPos(hwnd, HWND_TOP, screen.Left - l, screen.Top - t, screen.Right - screen.Left + l + r, screen.Bottom - screen.Top + t + b, 0);
                SetForegroundWindow(hwnd);
            }
            catch { }
        }

        /// <summary>A window's position and size when it is neither minimised nor maximised (null if Windows won't say).</summary>
        public static RECT? NormalRect(IntPtr hwnd)
        {
            try
            {
                if (IsIconic(hwnd) || IsZoomed(hwnd)) return null;
                return GetWindowRect(hwnd, out var r) && r.Right > r.Left && r.Bottom > r.Top ? r : (RECT?)null;
            }
            catch { return null; }
        }

        /// <summary>Puts a window back where it was (out of maximised if need be) and behind everything else, without focusing it.</summary>
        public static void PutBackBehind(IntPtr hwnd, RECT r)
        {
            try
            {
                if (IsZoomed(hwnd)) ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                SetWindowPos(hwnd, HWND_BOTTOM, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SWP_NOACTIVATE);
            }
            catch { }
        }

        /// <summary>
        /// Stretches a window over its whole monitor for a moment, without focusing it or changing its place in the stack, so the
        /// game draws its interface full size. Returns where the window was (for <see cref="PutBack"/>), or null if it is
        /// minimised, already maximised or couldn't be moved.
        /// </summary>
        public static RECT? EnlargeQuietly(IntPtr hwnd)
        {
            try
            {
                if (IsIconic(hwnd) || IsZoomed(hwnd) || !GetWindowRect(hwnd, out var was)) return null;
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (!GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref mi)) return null;
                var area = mi.rcMonitor;
                int l = 0, t = 0, r = 0, b = 0;
                if (DwmGetWindowAttribute(hwnd, 9, out RECT vis, Marshal.SizeOf(typeof(RECT))) == 0)
                {
                    l = vis.Left - was.Left; t = vis.Top - was.Top;
                    r = was.Right - vis.Right; b = was.Bottom - vis.Bottom;
                }
                return SetWindowPos(hwnd, IntPtr.Zero, area.Left - l, area.Top - t, area.Right - area.Left + l + r, area.Bottom - area.Top + t + b,
                                    SWP_NOZORDER | SWP_NOACTIVATE) ? was : (RECT?)null;
            }
            catch { return null; }
        }

        /// <summary>Puts a window back exactly where <see cref="EnlargeQuietly"/> found it, without focusing or raising it.</summary>
        public static void PutBack(IntPtr hwnd, RECT r)
        {
            try { SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SWP_NOZORDER | SWP_NOACTIVATE); } catch { }
        }

        /// <summary>Restores (or maximises) a window and gives it focus.</summary>
        public static void FocusMaximized(IntPtr hwnd)
        {
            try { ShowWindow(hwnd, 3 /* SW_MAXIMIZE */); SetForegroundWindow(hwnd); } catch { }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);

        /// <summary>
        /// A copy of a window's client area as BGRA pixels (top row first), the way screenshot tools take one: Windows hands over
        /// what the window last drew (PW_RENDERFULLCONTENT works for DirectX games). Nothing is read from the process. Null if the
        /// window is gone or minimised (a minimised window has nothing to hand over).
        /// </summary>
        public static byte[] CaptureClient(IntPtr hwnd, out int width, out int height)
        {
            width = height = 0;
            if (!IsWindow(hwnd) || IsIconic(hwnd) || !GetClientRect(hwnd, out var rc)) return null;
            int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
            if (w <= 0 || h <= 0 || w > 8192 || h > 8192) return null;

            IntPtr screen = IntPtr.Zero, mem = IntPtr.Zero, dib = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                screen = GetDC(IntPtr.Zero);
                mem = CreateCompatibleDC(screen);
                var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                dib = CreateDIBSection(screen, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero) return null;
                old = SelectObject(mem, dib);
                if (!PrintWindow(hwnd, mem, 0x1 /* PW_CLIENTONLY */ | 0x2 /* PW_RENDERFULLCONTENT */)) return null;
                var px = BufferPool.Rent(w * h * 4);   // fully overwritten below
                Marshal.Copy(bits, px, 0, px.Length);
                for (int i = 3; i < px.Length; i += 4) px[i] = 255;   // GDI leaves alpha undefined
                width = w; height = h;
                return px;
            }
            catch { return null; }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(mem, old);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                if (mem != IntPtr.Zero) DeleteDC(mem);
                if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
            }
        }

        [ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ITaskbarList
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
        }

        [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
        class TaskbarListClass { }

        static ITaskbarList _taskbar;

        /// <summary>
        /// Shows or removes a window's taskbar button through the shell's own taskbar interface (Explorer's side only: the window
        /// itself, its style and its process are untouched; Alt+Tab still lists it). Call from the UI thread.
        /// </summary>
        public static bool SetTaskbarButton(IntPtr hwnd, bool shown)
        {
            try
            {
                if (_taskbar == null) { var t = (ITaskbarList)new TaskbarListClass(); t.HrInit(); _taskbar = t; }
                if (shown) _taskbar.AddTab(hwnd); else _taskbar.DeleteTab(hwnd);
                return true;
            }
            catch { _taskbar = null; return false; }   // Explorer restarting: tried again next time
        }

        /// <summary>Arranges windows in an even grid over the work area of the monitor <paramref name="anchor"/> is on.</summary>
        public static int Tile(List<IntPtr> windows, IntPtr anchor)
        {
            if (windows.Count == 0) return 0;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            GetMonitorInfo(MonitorFromWindow(anchor, MONITOR_DEFAULTTONEAREST), ref mi);
            var area = mi.rcWork;
            int w = area.Right - area.Left, h = area.Bottom - area.Top;

            int n = windows.Count;
            int cols = (int)Math.Ceiling(Math.Sqrt(n * (double)w / h));
            cols = Math.Max(1, Math.Min(cols, n));
            int rows = (int)Math.Ceiling(n / (double)cols);
            int cw = w / cols, ch = h / rows;

            for (int i = 0; i < n; i++)
            {
                var hwnd = windows[i];
                if (IsZoomed(hwnd) || IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

                // Windows 10/11 frames have an invisible border; compensate so tiles sit flush.
                int l = 0, t = 0, r = 0, b = 0;
                if (GetWindowRect(hwnd, out RECT outer) && DwmGetWindowAttribute(hwnd, 9, out RECT vis, Marshal.SizeOf(typeof(RECT))) == 0)
                {
                    l = vis.Left - outer.Left; t = vis.Top - outer.Top;
                    r = outer.Right - vis.Right; b = outer.Bottom - vis.Bottom;
                }
                int col = i % cols, row = i / cols;
                SetWindowPos(hwnd, IntPtr.Zero,
                    area.Left + col * cw - l, area.Top + row * ch - t,
                    cw + l + r, ch + t + b, SWP_NOZORDER | SWP_NOACTIVATE);
            }
            return n;
        }
    }
}
