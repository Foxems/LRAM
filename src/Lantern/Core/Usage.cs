using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace Lantern.Core
{
    public static class Fmt
    {
        public static string Percent(double v) => (v < 10 ? Math.Round(v, 1).ToString("0.0") : Math.Round(v).ToString("0")) + "%";
        public static string Memory(long bytes) => bytes >= 1024L * 1024 * 1024 ? (bytes / 1024.0 / 1024 / 1024).ToString("0.0") + " GB" : Math.Round(bytes / 1024.0 / 1024) + " MB";
        public static string Memory(ulong bytes) => Memory((long)bytes);

        /// <summary>"42m", "3h 05m", "5d 10h": how long something ran, in the two most useful units.</summary>
        public static string Duration(TimeSpan t)
        {
            if (t.TotalSeconds < 1) return "0m";
            if (t.TotalMinutes < 1) return "<1m";
            if (t.TotalHours < 1) return (int)t.TotalMinutes + "m";
            if (t.TotalHours < 100) return (int)t.TotalHours + "h " + t.Minutes.ToString("00") + "m";
            return (int)t.TotalDays + "d " + t.Hours + "h";
        }
    }

    /// <summary>What one window is costing right now.</summary>
    public sealed class UsageSample
    {
        public int Pid;
        /// <summary>Percent of the whole PC's CPU (Task Manager style: 100 = every core busy).</summary>
        public double Cpu;
        public long WorkingSet;
        /// <summary>Committed private memory (Task Manager's "Commit size"), from the same process list; 0 when unknown.</summary>
        public long Private;
        /// <summary>Percent of the GPU's 3D engine this process used; null when Windows has no GPU counters.</summary>
        public double? Gpu;
    }

    /// <summary>
    /// Cheap, read-only usage numbers for a handful of processes. CPU comes from the process's own CPU-time
    /// counter (lowest query right, as Task Manager uses), memory from the system process list (no process
    /// handle at all), GPU from Windows' "GPU Engine" performance counters. Nothing inside the process is read.
    /// </summary>
    public sealed class UsageMonitor : IDisposable
    {
        readonly string _name;
        // CPU ticks and the moment they were read, per process: the interval must be measured between the two
        // readings themselves, not around slower work (like the first GPU lookup) that happens in between.
        readonly Dictionary<int, KeyValuePair<long, long>> _prevCpu = new Dictionary<int, KeyValuePair<long, long>>();
        long _prevSysBusy, _prevSysTotal;
        readonly GpuCounters _gpu = new GpuCounters();

        public UsageMonitor(string processName = "RobloxPlayerBeta") { _name = processName; }

        /// <summary>One reading for each of <paramref name="pids"/> that is still running. The first call only primes the CPU numbers.</summary>
        public Dictionary<int, UsageSample> Sample(ICollection<int> pids)
        {
            var result = new Dictionary<int, UsageSample>();
            int cores = Math.Max(1, Environment.ProcessorCount);
            var gpu = _gpu.Read(pids);

            Process[] procs = null;
            try
            {
                procs = Process.GetProcessesByName(_name);
                foreach (var p in procs)
                {
                    if (!pids.Contains(p.Id)) continue;
                    var s = new UsageSample { Pid = p.Id };
                    try { s.WorkingSet = p.WorkingSet64; } catch { }
                    try { s.Private = p.PrivateMemorySize64; } catch { }

                    long stamp = Stopwatch.GetTimestamp();
                    if (Native.ProcessCpuTicks(p.Id, out long ticks))
                    {
                        if (_prevCpu.TryGetValue(p.Id, out var before))
                        {
                            double wallTicks = (stamp - before.Value) * 10000000.0 / Stopwatch.Frequency;   // 100 ns units
                            if (wallTicks > 0) s.Cpu = Math.Max(0, Math.Min(100, (ticks - before.Key) / wallTicks / cores * 100.0));
                        }
                        _prevCpu[p.Id] = new KeyValuePair<long, long>(ticks, stamp);
                    }
                    if (gpu.TryGetValue(p.Id, out var g)) s.Gpu = Math.Min(100, g);
                    result[p.Id] = s;
                }
            }
            catch (Exception ex) { Logger.Write("Usage: " + ex.Message); }
            finally { if (procs != null) foreach (var p in procs) p.Dispose(); }

            foreach (var gone in _prevCpu.Keys.Where(k => !pids.Contains(k)).ToList()) _prevCpu.Remove(gone);
            return result;
        }

        /// <summary>How busy the whole PC's CPU has been since the previous call, 0..100 (0 on the first call).</summary>
        public double SystemCpu()
        {
            if (!Native.SystemCpuTicks(out long busy, out long total)) return 0;
            double v = 0;
            if (_prevSysTotal > 0 && total > _prevSysTotal) v = (busy - _prevSysBusy) * 100.0 / (total - _prevSysTotal);
            _prevSysBusy = busy; _prevSysTotal = total;
            return Math.Max(0, Math.Min(100, v));
        }

        public void Dispose() => _gpu.Dispose();
    }

    /// <summary>Per-process GPU use from Windows' "GPU Engine" counters (Windows 10 1709+). Quietly absent where unsupported.</summary>
    sealed class GpuCounters : IDisposable
    {
        static readonly Regex PidRx = new Regex(@"^pid_(\d+)_", RegexOptions.Compiled);
        readonly Dictionary<int, List<PerformanceCounter>> _map = new Dictionary<int, List<PerformanceCounter>>();
        HashSet<int> _pids = new HashSet<int>();
        DateTime _built = DateTime.MinValue;
        bool _unavailable;

        public Dictionary<int, double> Read(ICollection<int> pids)
        {
            var result = new Dictionary<int, double>();
            if (_unavailable || pids.Count == 0) return result;
            try
            {
                if (!_pids.SetEquals(pids) || DateTime.UtcNow - _built > TimeSpan.FromSeconds(30)) Rebuild(pids);
                foreach (var kv in _map)
                {
                    double sum = 0;
                    foreach (var c in kv.Value.ToList())
                    {
                        try { sum += c.NextValue(); }
                        catch (InvalidOperationException) { kv.Value.Remove(c); c.Dispose(); }   // that engine instance went away
                    }
                    result[kv.Key] = sum;
                }
            }
            catch (Exception ex)
            {
                _unavailable = true;   // no GPU counters on this PC: stop trying
                Logger.Write("GPU counters unavailable: " + ex.GetType().Name);
            }
            return result;
        }

        void Rebuild(ICollection<int> pids)
        {
            foreach (var l in _map.Values) foreach (var c in l) c.Dispose();
            _map.Clear();
            _pids = new HashSet<int>(pids);
            _built = DateTime.UtcNow;

            var cat = new PerformanceCounterCategory("GPU Engine");
            foreach (var inst in cat.GetInstanceNames())
            {
                var m = PidRx.Match(inst);
                if (!m.Success || inst.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) < 0) continue;
                int pid = int.Parse(m.Groups[1].Value);
                if (!_pids.Contains(pid)) continue;
                if (!_map.TryGetValue(pid, out var list)) _map[pid] = list = new List<PerformanceCounter>();
                try { var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, true); c.NextValue(); list.Add(c); }
                catch (InvalidOperationException) { }
            }
        }

        public void Dispose()
        {
            foreach (var l in _map.Values) foreach (var c in l) c.Dispose();
            _map.Clear();
        }
    }
}
