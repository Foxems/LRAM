using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace Lantern.Core
{
    public sealed class RbxProc
    {
        public int Pid;
        public string TrackerId = "";
        /// <summary>Roblox's windowless background process (--launch-to-tray): no game, no tracker id.</summary>
        public bool IsTray;
        /// <summary>The client file it runs from (which build); null if Windows wouldn't say.</summary>
        public string ExePath;
    }

    public static class RobloxProcesses
    {
        const string ImageName = "RobloxPlayerBeta";
        const string InstallerName = "RobloxPlayerInstaller";
        // Current clients carry the whole roblox-player: URI on their command line ("+browsertrackerid:123..."),
        // older ones used "-b 123...". Accept both.
        static readonly Regex TrackerRx = new Regex(@"(?:browsertrackerid:|(?:^|\s)-b\s+)(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Process ids only. Uses the system process list - no handle to Roblox is opened.</summary>
        public static HashSet<int> LivePids()
        {
            var set = new HashSet<int>();
            Process[] all = null;
            try
            {
                all = Process.GetProcessesByName(ImageName);
                foreach (var p in all) set.Add(p.Id);
            }
            catch { }
            finally { if (all != null) foreach (var p in all) p.Dispose(); }
            return set;
        }

        public static bool AnyRunning() => LivePids().Count > 0;

        /// <summary>
        /// Roblox's installer is at work: a launch needed another client build (an update or another channel), so the client that
        /// started handed over to it. It closes the open windows, swaps builds and then starts the client again with the same launch
        /// link. It refuses to run twice ("another RobloxPlayerInstaller is running", a critical error), so no launch may start meanwhile.
        /// </summary>
        public static bool InstallerRunning()
        {
            Process[] all = null;
            try { all = Process.GetProcessesByName(InstallerName); return all.Length > 0; }
            catch { return false; }
            finally { if (all != null) foreach (var p in all) p.Dispose(); }
        }

        /// <summary>
        /// Stops Roblox's installer. Only used while Lantern has windows running from its own build: none of Lantern's launches need
        /// the installer then, and the installer's first act is closing every Roblox window. Returns how many it stopped.
        /// </summary>
        public static int StopInstallers()
        {
            if (App.Demo) return 0;
            int n = 0;
            Process[] all = null;
            try
            {
                all = Process.GetProcessesByName(InstallerName);
                foreach (var p in all) { try { p.Kill(); n++; } catch { /* already gone */ } }
            }
            catch { }
            finally { if (all != null) foreach (var p in all) p.Dispose(); }
            return n;
        }

        /// <summary>True when a Roblox window (not just its tray process) is running. Only asks WMI when something is running at all.</summary>
        public static bool AnyGameClient() => LivePids().Count > 0 && Scan().Any(p => !p.IsTray);

        /// <summary>Closes Roblox's idle tray process(es) so they release the single-instance lock. Never touches a game window.</summary>
        public static int KillTrayProcesses()
        {
            int n = 0;
            foreach (var p in Scan().Where(p => p.IsTray)) { Kill(p.Pid); n++; }
            if (n > 0) Logger.Write("Closed " + n + " Roblox tray process(es) to free the single-instance lock.");
            return n;
        }

        /// <summary>Pids plus the browser-tracker id from each command line (WMI; read-only, no process handle).</summary>
        public static List<RbxProc> Scan()
        {
            var list = new List<RbxProc>();
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT ProcessId, CommandLine, ExecutablePath FROM Win32_Process WHERE Name = 'RobloxPlayerBeta.exe'"))
                using (var results = s.Get())
                {
                    foreach (ManagementBaseObject o in results)
                    {
                        using (o)
                        {
                            var cmd = o["CommandLine"] as string ?? "";
                            var m = TrackerRx.Match(cmd);
                            list.Add(new RbxProc
                            {
                                Pid = Convert.ToInt32(o["ProcessId"]),
                                TrackerId = m.Success ? m.Groups[1].Value : "",
                                IsTray = cmd.IndexOf("--launch-to-tray", StringComparison.OrdinalIgnoreCase) >= 0,
                                ExePath = o["ExecutablePath"] as string,
                            });
                        }
                    }
                }
            }
            catch (Exception ex) { Logger.Write("Scan: " + ex.Message); }
            return list;
        }

        public static void SetPriority(int pid, string priority)
        {
            ProcessPriorityClass cls;
            switch (priority)
            {
                case "BelowNormal": cls = ProcessPriorityClass.BelowNormal; break;
                case "Idle": cls = ProcessPriorityClass.Idle; break;
                default: cls = ProcessPriorityClass.Normal; break;
            }
            try { using (var p = Process.GetProcessById(pid)) p.PriorityClass = cls; }
            catch (Exception ex) { Logger.Write("SetPriority: " + ex.Message); }
        }

        public static void Kill(int pid)
        {
            if (App.Demo) return;
            try { using (var p = Process.GetProcessById(pid)) p.Kill(); }
            catch { /* already gone */ }
        }

        /// <summary>Path to the roblox-player protocol handler, or null if Roblox isn't installed the standard way.</summary>
        public static string FindLauncher()
        {
            try
            {
                foreach (var hive in new[] { Registry.CurrentUser, Registry.ClassesRoot })
                {
                    var path = hive == Registry.CurrentUser ? @"Software\Classes\roblox-player\shell\open\command" : @"roblox-player\shell\open\command";
                    using (var k = hive.OpenSubKey(path))
                    {
                        var cmd = k?.GetValue(null) as string;
                        if (string.IsNullOrWhiteSpace(cmd)) continue;
                        var m = Regex.Match(cmd, "^\\s*\"([^\"]+)\"");
                        var exe = m.Success ? m.Groups[1].Value : cmd.Split(' ')[0];
                        if (File.Exists(exe)) return exe;
                    }
                }
            }
            catch { }

            try
            {
                var versions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions");
                if (Directory.Exists(versions))
                    return Directory.GetFiles(versions, "RobloxPlayerLauncher.exe", SearchOption.AllDirectories).FirstOrDefault()
                           ?? Directory.GetFiles(versions, "RobloxPlayerBeta.exe", SearchOption.AllDirectories).FirstOrDefault();
            }
            catch { }
            return null;
        }
    }

    /// <summary>
    /// Roblox refuses to start a second client while a global lock named ROBLOX_singletonMutex exists
    /// and is not ours. Creating that lock first (and keeping it) is what lets several clients run.
    /// It only creates a named kernel object - Roblox itself is never touched.
    /// </summary>
    public static class MultiInstance
    {
        // Names/hook are fields (not constants) only so the self-test can exercise this exact code on
        // throw-away names without touching a Roblox client that is already running.
        public static string MutexName = "ROBLOX_singletonMutex";
        public static string EventName = "ROBLOX_singletonEvent";
        public static Func<bool> IsRobloxRunning = RobloxProcesses.AnyGameClient;
        /// <summary>Frees the lock from Roblox's idle tray process. Returns how many it closed. A hook so tests never touch a real client.</summary>
        public static Func<int> ReleaseTray = RobloxProcesses.KillTrayProcesses;
        static Mutex _mutex;
        static EventWaitHandle _event;

        public static bool Held => _mutex != null;

        /// <summary>
        /// Takes the lock. It does not matter whether Roblox windows are already open: windows Lantern started earlier (before Lantern
        /// was closed or restarted) don't own the lock, so it is free and is simply taken back, which is what lets Lantern launch and
        /// rejoin again. Only a lock someone else owns is refused: a Roblox window started the normal way, or another tool.
        /// </summary>
        public static bool Enable(out string error)
        {
            error = null;
            if (App.Demo) { error = "Demo mode never takes Roblox's lock."; return false; }
            if (_mutex != null) return true;

            Mutex m = null;
            try
            {
                m = new Mutex(true, MutexName, out bool created);
                if (created || TryTake(m)) { Keep(m); return true; }
                m.Dispose(); m = null;

                // Roblox keeps an idle background (tray) process that can own the lock; closing it is harmless.
                if (ReleaseTray() > 0)
                {
                    Thread.Sleep(700);
                    m = new Mutex(true, MutexName, out created);
                    if (created || TryTake(m)) { Keep(m); return true; }
                    m.Dispose(); m = null;
                }
                error = IsRobloxRunning()
                    ? "Roblox is already running and owns its lock: a Roblox window that Lantern didn't start. Close that window, then try again."
                    : "Another program is holding Roblox's lock (a different multi-instance tool or bootstrapper?). Close it and try again.";
                return false;
            }
            catch (Exception ex)
            {
                m?.Dispose();
                Logger.Write("MultiInstance.Enable: " + ex.Message);
                error = "Couldn't create the lock: " + ex.Message;
                return false;
            }
        }

        /// <summary>Owns the existing lock if nobody holds it. A lock left behind by a Lantern that crashed is "abandoned": Windows hands it over, and that counts as taken.</summary>
        static bool TryTake(Mutex m)
        {
            try { return m.WaitOne(0, true); }
            catch (AbandonedMutexException) { return true; }
        }

        static void Keep(Mutex m)
        {
            _mutex = m;
            try { _event = new EventWaitHandle(false, EventResetMode.ManualReset, EventName); } catch { /* best effort */ }
        }

        public static void Disable()
        {
            try { _event?.Dispose(); } catch { }
            _event = null;
            if (_mutex == null) return;
            try { _mutex.ReleaseMutex(); } catch { }
            try { _mutex.Dispose(); } catch { }
            _mutex = null;
        }
    }
}
