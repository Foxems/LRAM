using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Lantern.Core
{
    /// <summary>How a window ended, sorted by what it says about the farm's health.</summary>
    public static class EndClass
    {
        /// <summary>The client died: crashed, ran out of memory, or Roblox removed it for unexpected client behaviour.</summary>
        public const string Crash = "Crash";
        /// <summary>The window lost the server: connection lost, couldn't connect, kicked, signed in elsewhere, other codes.</summary>
        public const string Drop = "Drop";
        /// <summary>Roblox's 20-minute idle kick.</summary>
        public const string Idle = "Idle";
        /// <summary>The game or Roblox closed the server (updates, restarts, maintenance).</summary>
        public const string Server = "Server";
        /// <summary>Closed by you or by Lantern, or Lantern stopped watching.</summary>
        public const string Planned = "Planned";

        public static string Of(string kind)
        {
            switch (kind)
            {
                case "Abrupt": case "OutOfMemory": case "BadBehavior": return Crash;
                case "LostConnection": case "FailedToConnect": case "Kicked": case "Replaced": case "NoAccess": case "Banned": case "Code": return Drop;
                case "Idle": return Idle;
                case "ServerShutdown": case "Maintenance": return Server;
                default: return Planned;   // Closed, LanternClosed, Unknown, ""
            }
        }

        /// <summary>Counts against stability (crashes and drops; idle kicks and server restarts are the game's doing).</summary>
        public static bool IsProblem(string cls) => cls == Crash || cls == Drop;
    }

    public sealed class PulseReason
    {
        public string Kind = "";
        public string Class = "";
        public string Text = "";
        public int Count;
        public double Share;     // of all non-planned ends, 0..1
    }

    /// <summary>One row of "does the setup hurt?": a memory ceiling, or how many windows ran together.</summary>
    public sealed class PulseGroup
    {
        public string Label = "";
        public int SortKey;
        public TimeSpan WindowTime;
        public TimeSpan Tracked, InGame;
        public int Problems, Crashes, IdleKicks;
        public double? MedianJoinSeconds;
        public double WindowHours => WindowTime.TotalHours;
        public double? InGameShare => Tracked.TotalSeconds >= 60 ? InGame.TotalSeconds / Tracked.TotalSeconds : (double?)null;
        public double? ProblemsPer10h => WindowHours >= 0.25 ? Problems / WindowHours * 10 : (double?)null;
        public double? CrashesPer10h => WindowHours >= 0.25 ? Crashes / WindowHours * 10 : (double?)null;
        /// <summary>Under two window-hours the rates mean little.</summary>
        public bool LittleData => WindowHours < 2;
    }

    public sealed class PulseAccount
    {
        public string AccountId = "";
        public long UserId;
        public string Name = "";
        public TimeSpan WindowTime, Tracked, InGame;
        public int Problems, Crashes, IdleKicks, Rejoins, FailedLaunches, GaveUp;
        /// <summary>How fast the latest long session's committed memory grew, MB per hour (null without two readings 30+ minutes apart).</summary>
        public double? GrowthMbPerHour;
        public DateTime? LastProblemUtc;
        public string LastProblem = "";
        public double? InGameShare => Tracked.TotalSeconds >= 60 ? InGame.TotalSeconds / Tracked.TotalSeconds : (double?)null;
        public int Score => Problems * 3 + Crashes * 3 + FailedLaunches * 2 + GaveUp * 4 + Rejoins + (GrowthMbPerHour >= PulseReport.LeakMbPerHour ? 3 : 0);
    }

    public sealed class PulseIncident
    {
        public DateTime Utc;
        public string Name = "";
        /// <summary>Crash | Drop | Idle | Server | Failed | GaveUp</summary>
        public string Class = "";
        public string Text = "";
    }

    public sealed class PulseBucket
    {
        public DateTime StartLocal, EndLocal;
        public string Label = "";
        public int Problems, IdleKicks, Failed;
        public TimeSpan WindowTime;
    }

    /// <summary>
    /// The numbers behind the Pulse page: how much of the time windows were really inside a game, how often launches work, how
    /// often windows drop or crash (per window-hour, so 30 windows aren't judged against 15), what Lantern had to fix, and the same
    /// rates split by memory ceiling and by how many windows ran together. Built from stats.json only.
    /// </summary>
    public sealed class PulseReport
    {
        /// <summary>Committed memory growing faster than this (MB per hour) after the game settled looks like a leak.</summary>
        public const double LeakMbPerHour = 120;

        public DateTime FromUtc, ToUtc;
        public TimeSpan WindowTime;
        /// <summary>Window time of sessions recorded with join tracking (the base for <see cref="InGameShare"/>).</summary>
        public TimeSpan Tracked, InGame;
        public int Sessions;
        public int Launches, LaunchesOk, GaveUp;
        public int AutoRejoins, IdleRejoins;
        public double? MedianJoinSeconds;
        public int Problems, Crashes, Drops, IdleKicks, ServerRestarts, Planned;
        /// <summary>Sessions that ran 5+ minutes without ever showing up in a server.</summary>
        public int NeverJoined;
        /// <summary>From an unexpected end until the account was back in a server, all accounts added up.</summary>
        public TimeSpan LostToRejoins;
        public List<PulseReason> Reasons = new List<PulseReason>();
        public List<PulseGroup> ByCap = new List<PulseGroup>();
        public List<PulseGroup> ByWindows = new List<PulseGroup>();
        public List<PulseAccount> Attention = new List<PulseAccount>();
        public List<PulseAccount> Accounts = new List<PulseAccount>();
        public List<PulseIncident> Incidents = new List<PulseIncident>();
        public List<PulseBucket> Buckets = new List<PulseBucket>();
        public string BucketUnit = "hour";

        public double WindowHours => WindowTime.TotalHours;
        public double? InGameShare => Tracked.TotalSeconds >= 60 ? InGame.TotalSeconds / Tracked.TotalSeconds : (double?)null;
        public double? LaunchSuccess => Launches > 0 ? (double)LaunchesOk / Launches : (double?)null;
        public double? ProblemsPer10h => WindowHours >= 0.25 ? Problems / WindowHours * 10 : (double?)null;
        /// <summary>Window time between two problems on average (null when there were none).</summary>
        public TimeSpan? BetweenProblems => Problems > 0 ? TimeSpan.FromTicks(WindowTime.Ticks / Problems) : (TimeSpan?)null;
        public double? LostShare => WindowTime.TotalSeconds > 0 ? LostToRejoins.TotalSeconds / (WindowTime + LostToRejoins).TotalSeconds : (double?)null;
        public bool Empty => Sessions == 0 && Launches == 0 && GaveUp == 0;

        static TimeSpan Overlap(DateTime s, DateTime e, DateTime from, DateTime to)
        {
            var a = s > from ? s : from; var b = e < to ? e : to;
            return b > a ? b - a : TimeSpan.Zero;
        }

        static double? Median(List<double> v)
        {
            if (v.Count == 0) return null;
            v.Sort();
            return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
        }

        public static string CapLabel(int mb) => mb <= 0 ? "No cap" : mb >= 1000 ? (mb / 1000.0).ToString("0.#", CultureInfo.CurrentCulture) + " GB cap" : mb + " MB cap";

        static readonly int[] WindowEdges = { 5, 10, 15, 20, 25, 30, 40 };
        static (string label, int key) WindowsBucket(int n)
        {
            int lo = 1;
            foreach (var hi in WindowEdges)
            {
                if (n <= hi) return (lo == hi ? hi.ToString(CultureInfo.CurrentCulture) : lo + "–" + hi, hi);
                lo = hi + 1;
            }
            return ("41+", 999);
        }

        public static PulseReport Build(IEnumerable<SessionRecord> sessionsIn, IEnumerable<LaunchEvent> eventsIn, DateTime from, DateTime to, DateTime now)
        {
            var rep = new PulseReport { FromUtc = from, ToUtc = to };
            var all = sessionsIn.ToList();
            var events = (eventsIn ?? Enumerable.Empty<LaunchEvent>()).Where(e => e.Time >= from && e.Time <= to).ToList();
            var accounts = new Dictionary<string, PulseAccount>();
            var caps = new Dictionary<int, PulseGroup>();
            var joinAll = new List<double>();
            var joinByCap = new Dictionary<int, List<double>>();
            var reasons = new Dictionary<string, PulseReason>();

            PulseAccount Acc(string id, long userId, string name)
            {
                if (!accounts.TryGetValue(id ?? "", out var a)) accounts[id ?? ""] = a = new PulseAccount { AccountId = id ?? "", UserId = userId };
                if (!string.IsNullOrEmpty(name)) a.Name = name;
                return a;
            }

            foreach (var r in all)
            {
                var s = r.Start; var e = r.End(now);
                var span = Overlap(s, e, from, to);
                bool endsInRange = !r.Open && e >= from && e <= to;
                if (span <= TimeSpan.Zero && !endsInRange) continue;

                rep.Sessions++;
                rep.WindowTime += span;
                var acc = Acc(r.AccountId, r.UserId, r.Name);
                acc.WindowTime += span;

                if (!caps.TryGetValue(r.CapMb, out var cap)) caps[r.CapMb] = cap = new PulseGroup { Label = CapLabel(r.CapMb), SortKey = r.CapMb <= 0 ? int.MaxValue : r.CapMb };
                cap.WindowTime += span;

                if (r.Tracked)
                {
                    var inGame = r.JoinedTicks > 0 ? Overlap(new DateTime(r.JoinedTicks, DateTimeKind.Utc), e, from, to) : TimeSpan.Zero;
                    rep.Tracked += span; rep.InGame += inGame;
                    acc.Tracked += span; acc.InGame += inGame;
                    cap.Tracked += span; cap.InGame += inGame;

                    if (r.JoinedTicks > 0 && r.LaunchTicks > 0 && r.Start >= from && r.Start <= to && r.Origin != "PickedUp")
                    {
                        double secs = (r.JoinedTicks - r.LaunchTicks) / (double)TimeSpan.TicksPerSecond;
                        if (secs > 0 && secs < 3600)
                        {
                            joinAll.Add(secs);
                            if (!joinByCap.TryGetValue(r.CapMb, out var jl)) joinByCap[r.CapMb] = jl = new List<double>();
                            jl.Add(secs);
                        }
                    }
                    if (r.JoinedTicks == 0 && r.Origin != "PickedUp" && e - s >= TimeSpan.FromMinutes(5) && endsInRange) rep.NeverJoined++;
                }

                if (endsInRange)
                {
                    string cls = EndClass.Of(r.EndKind);
                    if (cls == EndClass.Planned) { rep.Planned++; }
                    else
                    {
                        if (!reasons.TryGetValue(r.EndKind, out var rr)) reasons[r.EndKind] = rr = new PulseReason { Kind = r.EndKind, Class = cls, Text = StatsReport.EndText(r.EndKind) };
                        rr.Count++;
                        if (cls == EndClass.Crash) { rep.Crashes++; acc.Crashes++; cap.Crashes++; }
                        else if (cls == EndClass.Drop) { rep.Drops++; }
                        else if (cls == EndClass.Idle) { rep.IdleKicks++; acc.IdleKicks++; cap.IdleKicks++; }
                        else if (cls == EndClass.Server) rep.ServerRestarts++;
                        if (EndClass.IsProblem(cls))
                        {
                            rep.Problems++; acc.Problems++; cap.Problems++;
                            if (!acc.LastProblemUtc.HasValue || e > acc.LastProblemUtc.Value) { acc.LastProblemUtc = e; acc.LastProblem = StatsReport.EndText(r.EndKind); }
                        }
                        rep.Incidents.Add(new PulseIncident { Utc = e, Name = r.Name, Class = cls, Text = StatsReport.EndText(r.EndKind) });
                    }
                }

                // committed memory that keeps growing (the latest session with two readings far enough apart)
                if (r.PrivFirstTicks > 0 && r.PrivLastTicks - r.PrivFirstTicks >= TimeSpan.FromMinutes(30).Ticks)
                {
                    double hours = (r.PrivLastTicks - r.PrivFirstTicks) / (double)TimeSpan.TicksPerHour;
                    acc.GrowthMbPerHour = (r.PrivLastMb - r.PrivFirstMb) / hours;
                }
            }

            // time from an unexpected end until the account was back inside a server
            foreach (var g in all.Where(x => x.Tracked).GroupBy(x => x.AccountId))
            {
                var list = g.OrderBy(x => x.StartTicks).ToList();
                for (int i = 1; i < list.Count; i++)
                {
                    var prev = list[i - 1]; var next = list[i];
                    if (prev.Open || (next.Origin != "AutoRejoin" && next.Origin != "IdleRejoin")) continue;
                    var gapEnd = next.JoinedTicks > 0 ? new DateTime(next.JoinedTicks, DateTimeKind.Utc) : next.Start;
                    var gap = Overlap(prev.End(now), gapEnd, from, to);
                    if (gap > TimeSpan.FromHours(2)) continue;   // a gap that long is a pause, not a rejoin
                    rep.LostToRejoins += gap;
                }
            }

            foreach (var ev in events)
            {
                var acc = Acc(ev.AccountId, ev.UserId, ev.Name);
                if (ev.Kind == "GaveUp")
                {
                    rep.GaveUp++; acc.GaveUp++;
                    rep.Incidents.Add(new PulseIncident { Utc = ev.Time, Name = ev.Name, Class = "GaveUp", Text = "Lantern gave up rejoining" + (ev.Reason.Length > 0 ? " · " + ev.Reason.TrimEnd('.') : "") });
                    continue;
                }
                rep.Launches++;
                if (ev.Ok)
                {
                    rep.LaunchesOk++;
                    if (ev.Origin == "AutoRejoin") { rep.AutoRejoins++; acc.Rejoins++; }
                    else if (ev.Origin == "IdleRejoin") { rep.IdleRejoins++; acc.Rejoins++; }
                }
                else
                {
                    acc.FailedLaunches++;
                    rep.Incidents.Add(new PulseIncident { Utc = ev.Time, Name = ev.Name, Class = "Failed", Text = "Launch failed" + (ev.Reason.Length > 0 ? " · " + ev.Reason.TrimEnd('.') : "") });
                }
            }

            rep.MedianJoinSeconds = Median(joinAll);
            int unplannedEnds = reasons.Values.Sum(x => x.Count);
            foreach (var r in reasons.Values) r.Share = unplannedEnds > 0 ? (double)r.Count / unplannedEnds : 0;
            int Rank(string cls) => cls == EndClass.Crash ? 0 : cls == EndClass.Drop ? 1 : cls == EndClass.Idle ? 2 : 3;
            rep.Reasons = reasons.Values.OrderBy(x => Rank(x.Class)).ThenByDescending(x => x.Count).ToList();

            foreach (var kv in caps) if (joinByCap.TryGetValue(kv.Key, out var jl)) kv.Value.MedianJoinSeconds = Median(jl);
            rep.ByCap = caps.Values.Where(c => c.WindowTime > TimeSpan.Zero).OrderBy(c => c.SortKey).ToList();
            rep.ByWindows = ByConcurrency(all, from, to, now);

            rep.Accounts = accounts.Values.Where(a => a.WindowTime > TimeSpan.Zero || a.FailedLaunches > 0 || a.GaveUp > 0).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
            rep.Attention = rep.Accounts.Where(a => a.Score > 0).OrderByDescending(a => a.Score).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Take(8).ToList();
            rep.Incidents = rep.Incidents.OrderByDescending(x => x.Utc).Take(40).ToList();
            BuildBuckets(rep, all, events, now);
            return rep;
        }

        /// <summary>
        /// Window time and problems split by how many windows were running at that moment: the timeline is cut wherever a window
        /// starts or ends, each piece counts toward its own level, and an end counts toward the level just before it.
        /// </summary>
        static List<PulseGroup> ByConcurrency(List<SessionRecord> all, DateTime from, DateTime to, DateTime now)
        {
            var marks = new List<(DateTime t, int d, SessionRecord r)>();
            foreach (var r in all)
            {
                var s = r.Start; var e = r.End(now);
                if (e <= s) continue;
                marks.Add((s, +1, null));
                marks.Add((e, -1, r));
            }
            marks.Sort((a, b) => a.t != b.t ? a.t.CompareTo(b.t) : b.d.CompareTo(a.d));   // a start at the same instant counts first, so an end sees it

            var groups = new Dictionary<int, PulseGroup>();
            PulseGroup G(int n)
            {
                var (label, key) = WindowsBucket(n);
                if (!groups.TryGetValue(key, out var g)) groups[key] = g = new PulseGroup { Label = label + " windows", SortKey = key };
                return g;
            }

            int cur = 0;
            DateTime? last = null;
            foreach (var m in marks)
            {
                if (last.HasValue && cur > 0)
                {
                    var piece = Overlap(last.Value, m.t, from, to);
                    if (piece > TimeSpan.Zero) G(cur).WindowTime += TimeSpan.FromTicks(piece.Ticks * cur);
                }
                if (m.d < 0 && m.r != null && !m.r.Open && m.t >= from && m.t <= to)
                {
                    string cls = EndClass.Of(m.r.EndKind);
                    var g = G(Math.Max(1, cur));
                    if (EndClass.IsProblem(cls)) g.Problems++;
                    if (cls == EndClass.Crash) g.Crashes++;
                    if (cls == EndClass.Idle) g.IdleKicks++;
                }
                cur += m.d;
                last = m.t;
            }
            return groups.Values.Where(g => g.WindowTime > TimeSpan.Zero).OrderBy(g => g.SortKey).ToList();
        }

        static void BuildBuckets(PulseReport rep, List<SessionRecord> all, List<LaunchEvent> events, DateTime now)
        {
            var span = rep.ToUtc - rep.FromUtc;
            string unit = span <= TimeSpan.FromHours(2) ? "5min" : span <= TimeSpan.FromHours(36) ? "hour" : "day";
            rep.BucketUnit = unit;
            DateTime Floor(DateTime l)
            {
                if (unit == "5min") return new DateTime(l.Year, l.Month, l.Day, l.Hour, l.Minute / 5 * 5, 0, DateTimeKind.Local);
                if (unit == "hour") return new DateTime(l.Year, l.Month, l.Day, l.Hour, 0, 0, DateTimeKind.Local);
                return new DateTime(l.Year, l.Month, l.Day, 0, 0, 0, DateTimeKind.Local);
            }
            DateTime Next(DateTime l) => unit == "5min" ? l.AddMinutes(5) : unit == "hour" ? l.AddHours(1) : l.AddDays(1);

            var list = new List<PulseBucket>();
            var endLocal = rep.ToUtc.ToLocalTime();
            for (var t = Floor(rep.FromUtc.ToLocalTime()); t < endLocal && list.Count < 400; t = Next(t))
                list.Add(new PulseBucket
                {
                    StartLocal = t, EndLocal = Next(t),
                    Label = unit == "day" ? t.ToString("d MMM", CultureInfo.CurrentCulture) : t.ToString("HH:mm", CultureInfo.CurrentCulture)
                });
            if (list.Count == 0) { rep.Buckets = list; return; }

            PulseBucket At(DateTime utc)
            {
                var l = utc.ToLocalTime();
                foreach (var b in list) if (l >= b.StartLocal && l < b.EndLocal) return b;
                return null;
            }

            foreach (var r in all)
            {
                var sl = r.Start.ToLocalTime(); var el = r.End(now).ToLocalTime();
                foreach (var b in list)
                {
                    if (b.EndLocal <= sl) continue;
                    if (b.StartLocal >= el) break;
                    var cs = sl > b.StartLocal ? sl : b.StartLocal; var ce = el < b.EndLocal ? el : b.EndLocal;
                    if (ce > cs) b.WindowTime += ce - cs;
                }
                if (r.Open) continue;
                var cls = EndClass.Of(r.EndKind);
                var hit = At(r.End(now));
                if (hit == null) continue;
                if (EndClass.IsProblem(cls)) hit.Problems++;
                else if (cls == EndClass.Idle) hit.IdleKicks++;
            }
            foreach (var ev in events.Where(e => e.Kind == "Launch" && !e.Ok)) { var hit = At(ev.Time); if (hit != null) hit.Failed++; }
            rep.Buckets = list;
        }
    }

    /// <summary>A one-line reading of the "does the setup hurt?" tables.</summary>
    public sealed class PulseVerdict
    {
        /// <summary>ok | warn | info</summary>
        public string Level = "info";
        public string Text = "";

        static string Rate(double? v) => (v ?? 0).ToString("0.0", CultureInfo.CurrentCulture);
        /// <summary>Clearly worse: half again as many problems plus a little slack, so 0.1 against 0.0 isn't called a trend.</summary>
        static bool Worse(double a, double b) => a > b * 1.5 + 0.3;

        /// <summary>Do capped windows drop or crash more than uncapped (or less tightly capped) ones?</summary>
        public static PulseVerdict Cap(PulseReport rep)
        {
            var rows = rep.ByCap.Where(g => !g.LittleData).ToList();
            if (rep.ByCap.Count == 0) return null;
            if (rep.ByCap.Count == 1)
                return new PulseVerdict { Text = "Every window in this range ran with the " + rep.ByCap[0].Label.ToLowerInvariant() + ". Run a while with another setting and the two can be compared here." };
            if (rows.Count < 2)
                return new PulseVerdict { Text = "Not enough data yet to compare memory caps: each setting needs about 2 window-hours." };
            var baseRow = rows.FirstOrDefault(g => g.SortKey == int.MaxValue) ?? rows.OrderByDescending(g => g.SortKey).First();
            double b = baseRow.ProblemsPer10h ?? 0;
            var worst = rows.Where(g => g != baseRow).OrderByDescending(g => g.ProblemsPer10h ?? 0).First();
            double w = worst.ProblemsPer10h ?? 0;
            bool lessInGame = worst.InGameShare.HasValue && baseRow.InGameShare.HasValue && worst.InGameShare.Value < baseRow.InGameShare.Value - 0.02;
            if (Worse(w, b))
                return new PulseVerdict { Level = "warn", Text = "Windows with the " + worst.Label + " drop or crash more often: " + Rate(w) + " against " + Rate(b) + " problems per 10 window-hours with the " + baseRow.Label.ToLowerInvariant() + "." };
            if (lessInGame)
                return new PulseVerdict { Level = "warn", Text = "Windows with the " + worst.Label + " spend less time in a game (" + Math.Round(worst.InGameShare.Value * 100) + "% against " + Math.Round(baseRow.InGameShare.Value * 100) + "%)." };
            return new PulseVerdict { Level = "ok", Text = "No sign that the " + worst.Label + " hurts stability: " + Rate(w) + " problems per 10 window-hours, against " + Rate(b) + " with the " + baseRow.Label.ToLowerInvariant() + "." };
        }

        /// <summary>Do problems climb when more windows run together?</summary>
        public static PulseVerdict Windows(PulseReport rep)
        {
            var rows = rep.ByWindows.Where(g => !g.LittleData).OrderBy(g => g.SortKey).ToList();
            if (rows.Count < 2) return null;
            var top = rows[rows.Count - 1];
            var lower = rows.Take(rows.Count - 1).ToList();
            double lowHours = lower.Sum(g => g.WindowHours);
            double low = lowHours > 0 ? lower.Sum(g => g.Problems) / lowHours * 10 : 0;
            double hi = top.ProblemsPer10h ?? 0;
            if (Worse(hi, low))
                return new PulseVerdict { Level = "warn", Text = "Problems climb with more windows: " + Rate(hi) + " per 10 window-hours at " + top.Label + ", against " + Rate(low) + " with fewer." };
            return new PulseVerdict { Level = "ok", Text = "Stability holds up at " + top.Label + ": " + Rate(hi) + " problems per 10 window-hours, against " + Rate(low) + " with fewer." };
        }
    }

    /// <summary>What every window is doing right now, for the top of the Pulse page.</summary>
    public sealed class PulseNow
    {
        public int Accounts, Running, InGame, Loading, Starting, Attention, Still, NotJoined;
        /// <summary>Found already open when Lantern started, and neither its log nor Roblox has confirmed a server yet.</summary>
        public int Unconfirmed;
        /// <summary>Running windows Roblox's presence service confirms are in a game, and those it was asked about recently.</summary>
        public int Confirmed, PresenceKnown;
        /// <summary>Running, seen in a server, but Roblox says the account isn't in a game (stuck on an error screen, frozen, or between servers).</summary>
        public int Mismatch;
        public long WorkingSet, Private;
        public double Cpu;
        public List<(string Name, string What)> Flags = new List<(string, string)>();

        public static readonly TimeSpan StillFor = TimeSpan.FromSeconds(90);
        public static readonly TimeSpan PresenceFresh = TimeSpan.FromMinutes(3);

        public static PulseNow From(IEnumerable<AccountItem> accounts, DateTime now)
        {
            var n = new PulseNow();
            foreach (var a in accounts)
            {
                n.Accounts++;
                switch (a.State)
                {
                    case AccountState.Running:
                        n.Running++;
                        n.WorkingSet += a.WorkingSet; n.Private += a.PrivateBytes; n.Cpu += a.Cpu;
                        if (a.JoinedAt.HasValue) n.InGame++;
                        else if (a.PickedUp) n.Unconfirmed++;
                        else if (a.StartedAt.HasValue && now - a.StartedAt.Value > InstanceManager.JoinWatchFor) { n.NotJoined++; n.Flags.Add((a.Display, "No server join seen in " + (int)InstanceManager.JoinWatchFor.TotalMinutes + " min")); }
                        else n.Loading++;
                        if (a.StillSince.HasValue && now - a.StillSince.Value >= StillFor) { n.Still++; n.Flags.Add((a.Display, "Uses no CPU: frozen?")); }
                        if (a.PresenceAt.HasValue && now - a.PresenceAt.Value <= PresenceFresh)
                        {
                            n.PresenceKnown++;
                            if (a.PresenceType == 2) n.Confirmed++;
                            else if (a.JoinedAt.HasValue) { n.Mismatch++; n.Flags.Add((a.Display, "Roblox says it isn't in a game")); }
                        }
                        break;
                    case AccountState.Queued: case AccountState.Launching: case AccountState.Rejoining: case AccountState.Closing: case AccountState.Checking:
                        n.Starting++; break;
                    case AccountState.Error: case AccountState.Expired:
                        n.Attention++; n.Flags.Add((a.Display, a.StatusText)); break;
                }
            }
            return n;
        }
    }

    /// <summary>
    /// A plain-JSON copy of the Pulse numbers (account names and counts, never cookies), so they can be read outside Lantern -
    /// by a spreadsheet, a script, or an assistant helping to tune the farm.
    /// </summary>
    public static class PulseExport
    {
        static double? R(double? v, int digits = 1) => v.HasValue ? Math.Round(v.Value, digits) : (double?)null;
        static double? Pct(double? share) => share.HasValue ? Math.Round(share.Value * 100, 1) : (double?)null;
        static string Iso(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        static Dictionary<string, object> Group(PulseGroup g) => new Dictionary<string, object>
        {
            { "label", g.Label }, { "window_hours", R(g.WindowHours, 2) }, { "in_game_pct", Pct(g.InGameShare) },
            { "problems", g.Problems }, { "problems_per_10h", R(g.ProblemsPer10h, 2) }, { "crashes", g.Crashes }, { "crashes_per_10h", R(g.CrashesPer10h, 2) },
            { "idle_kicks", g.IdleKicks }, { "median_join_s", R(g.MedianJoinSeconds) }, { "little_data", g.LittleData }
        };

        /// <summary>The numbers of one range.</summary>
        public static Dictionary<string, object> Range(PulseReport rep) => new Dictionary<string, object>
        {
            { "from", Iso(rep.FromUtc) }, { "to", Iso(rep.ToUtc) },
            { "totals", new Dictionary<string, object> {
                { "window_hours", R(rep.WindowHours, 2) }, { "sessions", rep.Sessions }, { "in_game_pct", Pct(rep.InGameShare) },
                { "launches", rep.Launches }, { "launch_success_pct", Pct(rep.LaunchSuccess) }, { "median_join_s", R(rep.MedianJoinSeconds) },
                { "problems", rep.Problems }, { "problems_per_10h", R(rep.ProblemsPer10h, 2) }, { "hours_between_problems", rep.BetweenProblems.HasValue ? R(rep.BetweenProblems.Value.TotalHours, 2) : null },
                { "crashes", rep.Crashes }, { "drops", rep.Drops }, { "idle_kicks", rep.IdleKicks }, { "server_restarts", rep.ServerRestarts }, { "planned_ends", rep.Planned },
                { "never_joined", rep.NeverJoined }, { "auto_rejoins", rep.AutoRejoins }, { "idle_rejoins", rep.IdleRejoins }, { "gave_up", rep.GaveUp },
                { "minutes_lost_to_rejoins", R(rep.LostToRejoins.TotalMinutes) }, { "lost_pct", Pct(rep.LostShare) } } },
            { "verdicts", new[] { PulseVerdict.Cap(rep), PulseVerdict.Windows(rep) }.Where(v => v != null).Select(v => v.Text).ToArray() },
            { "end_reasons", rep.Reasons.Select(r => new Dictionary<string, object> { { "reason", r.Text }, { "class", r.Class }, { "count", r.Count }, { "pct", Pct(r.Share) } }).ToArray() },
            { "by_cap", rep.ByCap.Select(Group).ToArray() },
            { "by_windows", rep.ByWindows.Select(Group).ToArray() },
            { "accounts", rep.Accounts.Select(a => new Dictionary<string, object> {
                { "name", a.Name }, { "window_hours", R(a.WindowTime.TotalHours, 2) }, { "in_game_pct", Pct(a.InGameShare) }, { "problems", a.Problems },
                { "crashes", a.Crashes }, { "idle_kicks", a.IdleKicks }, { "rejoins", a.Rejoins }, { "failed_launches", a.FailedLaunches }, { "gave_up", a.GaveUp },
                { "commit_growth_mb_per_h", R(a.GrowthMbPerHour, 0) } }).ToArray() },
            { "incidents", rep.Incidents.Take(25).Select(i => new Dictionary<string, object> { { "time", Iso(i.Utc) }, { "account", i.Name }, { "class", i.Class }, { "what", i.Text } }).ToArray() }
        };

        /// <summary>The whole file: right now, the setup, and each range by name ("last_hour", "last_24h", ...).</summary>
        public static Dictionary<string, object> Build(PulseNow now, SettingsData s, long ramTotal, long ramFree, IEnumerable<KeyValuePair<string, PulseReport>> ranges)
        {
            var d = new Dictionary<string, object>
            {
                { "generated", Iso(DateTime.UtcNow) },
                { "note", "Written by Lantern's Pulse page. Account names and numbers only; no cookies or tickets." },
                { "setup", new Dictionary<string, object> {
                    { "perf_mode", s.PerfMode }, { "minimize_after_load", s.MinimizeAfterLoad }, { "trim", s.TrimMinimized },
                    { "cap_mb", s.CapMinimized ? s.CapMb : 0 }, { "priority", s.Priority }, { "auto_rejoin", s.AutoRejoin }, { "idle_rejoin", s.RejoinAfterIdleKick },
                    { "ram_total_gb", Math.Round(ramTotal / 1073741824.0, 1) }, { "ram_free_gb", Math.Round(ramFree / 1073741824.0, 2) } } },
                { "now", new Dictionary<string, object> {
                    { "accounts", now.Accounts }, { "running", now.Running }, { "in_game", now.InGame }, { "loading", now.Loading }, { "starting", now.Starting },
                    { "needs_you", now.Attention }, { "no_join_seen", now.NotJoined }, { "unconfirmed", now.Unconfirmed }, { "frozen", now.Still },
                    { "presence_confirmed", now.Confirmed }, { "presence_known", now.PresenceKnown }, { "presence_mismatch", now.Mismatch },
                    { "roblox_cpu_pct", R(now.Cpu) }, { "roblox_ram_gb", Math.Round(now.WorkingSet / 1073741824.0, 2) }, { "roblox_commit_gb", Math.Round(now.Private / 1073741824.0, 2) },
                    { "flags", now.Flags.Select(f => f.Name + ": " + f.What).ToArray() } } }
            };
            foreach (var kv in ranges) d[kv.Key] = Range(kv.Value);
            return d;
        }
    }
}
