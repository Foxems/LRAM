using System;
using System.Collections.Generic;
using System.Linq;

namespace Lantern.Core
{
    /// <summary>The whole machine at one moment, as far as Roblox windows are concerned.</summary>
    public struct UsageSnapshot
    {
        public DateTime Time;
        public int Windows;
        /// <summary>Roblox windows together, percent of the whole CPU.</summary>
        public double RobloxCpu;
        /// <summary>The whole PC, percent.</summary>
        public double SystemCpu;
        public double? RobloxGpu;
        public ulong RobloxBytes, AvailableBytes, TotalBytes;
    }

    public sealed class Advice
    {
        /// <summary>memory | cpu | cpu-limit | gpu | gpu-limit | headroom | ok</summary>
        public string Kind = "ok";
        public Level Level = Level.Info;
        public string Title = "";
        public string Detail = "";
        /// <summary>Off | Balanced | Minimal when switching mode would help, otherwise null.</summary>
        public string Mode;
    }

    /// <summary>
    /// Turns recent measurements into one plain-language suggestion: what is actually limiting this PC
    /// (memory, CPU or graphics) and whether another low-resource mode would help. Pure function: easy to test.
    /// </summary>
    public static class ModeAdvisor
    {
        public const int MinSnapshots = 6;

        public static Advice Evaluate(IList<UsageSnapshot> history, string mode, bool minimizeOn)
        {
            mode = string.IsNullOrEmpty(mode) ? "Off" : mode;
            bool custom = mode == "Custom";
            if (custom) mode = "Minimal";   // hand-tuned settings count as 'already as lean as you want': never suggest a preset over them
            string lean = custom ? "with your custom settings" : "in Minimal mode";
            if (history == null || history.Count < MinSnapshots) return null;

            var last = history[history.Count - 1];
            if (last.Windows < 1) return null;

            double sysCpu = history.Average(h => h.SystemCpu);
            double rbxCpu = history.Average(h => h.RobloxCpu);
            var gpus = history.Where(h => h.RobloxGpu.HasValue).Select(h => h.RobloxGpu.Value).ToList();
            double? gpu = gpus.Count > 0 ? gpus.Average() : (double?)null;
            double perWindow = history.Average(h => h.Windows > 0 ? (double)h.RobloxBytes / h.Windows : 0);
            double availFrac = last.TotalBytes > 0 ? (double)last.AvailableBytes / last.TotalBytes : 1;
            string perWindowText = Math.Round(perWindow / 1024 / 1024) + " MB";
            int fitMore = perWindow > 0 ? (int)Math.Max(0, (last.AvailableBytes - 1024.0 * 1024 * 1024) / perWindow) : 0;

            // 1. memory: the one low-resource modes barely help with
            if (availFrac < 0.12 || last.AvailableBytes < 2UL * 1024 * 1024 * 1024)
                return new Advice
                {
                    Kind = "memory", Level = Level.Warn, Title = "Memory is nearly full",
                    Detail = "Each window uses about " + perWindowText + " and roughly " + fitMore + " more would fit. Low-resource modes help only a little here: close the windows you need least, or add RAM."
                };

            // 2. CPU (only meaningful with a few windows competing)
            if (last.Windows >= 2 && sysCpu >= 85 && rbxCpu >= sysCpu * 0.5)
            {
                if (mode != "Minimal")
                    return new Advice
                    {
                        Kind = "cpu", Level = Level.Warn, Mode = mode == "Off" ? "Balanced" : "Minimal",
                        Title = "Your CPU is maxed out",
                        Detail = "The Roblox windows use about " + Math.Round(rbxCpu) + "% of it. " + (mode == "Off" ? "Balanced" : "Minimal") + " mode lowers the frame cap and priority; it applies to windows you launch next."
                    };
                return new Advice
                {
                    Kind = "cpu-limit", Level = Level.Warn,
                    Title = "CPU is maxed out even " + lean,
                    Detail = "These windows use about " + Math.Round(rbxCpu) + "% of the CPU. The only way to go further is fewer windows at once."
                };
            }

            // 3. GPU
            if (gpu.HasValue && gpu.Value >= 75)
            {
                if (mode != "Minimal")
                    return new Advice
                    {
                        Kind = "gpu", Level = Level.Warn, Mode = "Minimal",
                        Title = "The graphics card is the bottleneck",
                        Detail = "Roblox is using about " + Math.Round(gpu.Value) + "% of it. Minimal mode shrinks the windows and lowers graphics" + (minimizeOn ? "." : "; turning on “Minimize windows once the game has loaded” helps most.")
                    };
                return new Advice
                {
                    Kind = "gpu-limit", Level = Level.Warn,
                    Title = "The graphics card is maxed even " + lean,
                    Detail = minimizeOn ? "Roblox uses about " + Math.Round(gpu.Value) + "% of it. Fewer windows is the remaining option." : "Turn on “Minimize windows once the game has loaded”: a minimized window stops drawing."
                };
            }

            // 4. lots of room: a nicer mode (or more windows) would be fine
            if (last.Windows >= 2 && mode != "Off" && sysCpu < 45 && (!gpu.HasValue || gpu.Value < 45) && availFrac > 0.35)
                return new Advice
                {
                    Kind = "headroom", Level = Level.Info, Mode = mode == "Minimal" ? "Balanced" : "Off",
                    Title = "You have plenty of headroom",
                    Detail = "CPU is at " + Math.Round(sysCpu) + "% and memory is mostly free, so a lighter mode would look better, or you could run more windows."
                };

            return new Advice
            {
                Kind = "ok", Level = Level.Ok, Title = "Running comfortably",
                Detail = "CPU " + Math.Round(sysCpu) + "%" + (gpu.HasValue ? ", graphics " + Math.Round(gpu.Value) + "%" : "") + ", " + Math.Round((1 - availFrac) * 100) + "% of memory in use."
            };
        }
    }
}
