using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Lantern.Core
{
    /// <summary>One run of one Roblox window. Plain data, saved to stats.json (no cookies, no tickets).</summary>
    public class SessionRecord
    {
        public string AccountId { get; set; } = "";
        public long UserId { get; set; }
        /// <summary>The name shown for the account when the session started (kept even if the account is removed later).</summary>
        public string Name { get; set; } = "";
        public long PlaceId { get; set; }
        public string Game { get; set; } = "";
        public long StartTicks { get; set; }
        /// <summary>UTC ticks; 0 while the window is still running.</summary>
        public long EndTicks { get; set; }
        /// <summary>The last moment Lantern knew the window was alive (so a crash of Lantern itself loses at most a minute).</summary>
        public long LastSeenTicks { get; set; }
        /// <summary>Closed | Kicked | Replaced | ServerShutdown | LostConnection | Idle | Abrupt | Code | LanternClosed | Unknown ...</summary>
        public string EndKind { get; set; } = "";
        /// <summary>Not something the user did.</summary>
        public bool Unexpected { get; set; }
        /// <summary>The window was already open when Lantern started, so its true start is unknown.</summary>
        public bool PickedUp { get; set; }
        /// <summary>Working-set samples: sum and count (megabytes), plus the highest one.</summary>
        public long MemSumMb { get; set; }
        public int MemSamples { get; set; }
        public long MemPeakMb { get; set; }

        // Pulse (recorded since Pulse exists; older records leave them at their defaults, see Tracked)
        /// <summary>Written by a Lantern that records the fields below, so the Pulse page can tell "never joined" from "not recorded".</summary>
        public bool Tracked { get; set; }
        /// <summary>Launch | AutoRejoin | IdleRejoin: what started this window.</summary>
        public string Origin { get; set; } = "";
        /// <summary>UTC ticks when the launch began (before the window existed); 0 when unknown.</summary>
        public long LaunchTicks { get; set; }
        /// <summary>UTC ticks when Roblox's log first showed this account inside a server; 0 = not (yet) seen.</summary>
        public long JoinedTicks { get; set; }
        /// <summary>The largest memory ceiling (MB) Lantern put on this window; 0 = never capped.</summary>
        public int CapMb { get; set; }
        public bool Trimmed { get; set; }
        /// <summary>Private (committed) memory: a reading taken once the game has settled, and the latest one, to spot windows that keep growing.</summary>
        public long PrivFirstMb { get; set; }
        public long PrivFirstTicks { get; set; }
        public long PrivLastMb { get; set; }
        public long PrivLastTicks { get; set; }

        public DateTime Start => new DateTime(StartTicks, DateTimeKind.Utc);
        public bool Open => EndTicks == 0;
        public DateTime End(DateTime now) => EndTicks > 0 ? new DateTime(EndTicks, DateTimeKind.Utc) : now;
        public long AvgMb => MemSamples > 0 ? MemSumMb / MemSamples : 0;
        public SessionRecord Copy() => (SessionRecord)MemberwiseClone();
    }

    /// <summary>One launch attempt (or Lantern giving up on an account), for the Pulse page's success rates. Plain data, no cookies.</summary>
    public class LaunchEvent
    {
        public long Ticks { get; set; }
        public string AccountId { get; set; } = "";
        public long UserId { get; set; }
        public string Name { get; set; } = "";
        /// <summary>Launch | GaveUp</summary>
        public string Kind { get; set; } = "Launch";
        public bool Ok { get; set; }
        /// <summary>Launch | AutoRejoin | IdleRejoin</summary>
        public string Origin { get; set; } = "";
        /// <summary>Why it failed, in plain words (empty when it worked).</summary>
        public string Reason { get; set; } = "";
        public DateTime Time => new DateTime(Ticks, DateTimeKind.Utc);
        public LaunchEvent Copy() => (LaunchEvent)MemberwiseClone();
    }

    public class StatsFile
    {
        public List<SessionRecord> Sessions { get; set; } = new List<SessionRecord>();
        public List<LaunchEvent> Events { get; set; } = new List<LaunchEvent>();
    }

    /// <summary>
    /// The history of every window Lantern has watched: when it started, when and why it ended, how much memory it used.
    /// Written to <c>stats.json</c> as it goes. A session that was still open when Lantern (or the PC) stopped is closed
    /// at the last moment it was seen alive, the next time Lantern starts.
    /// </summary>
    public sealed class SessionLog
    {
        public const int MaxSessions = 8000;
        public const int MaxEvents = 20000;
        public static readonly TimeSpan MaxAge = TimeSpan.FromDays(400);

        readonly List<SessionRecord> _all = new List<SessionRecord>();
        readonly List<LaunchEvent> _events = new List<LaunchEvent>();
        readonly Dictionary<string, SessionRecord> _open = new Dictionary<string, SessionRecord>();
        readonly object _gate = new object();
        bool _dirty;

        /// <summary>False in --demo (sample data stays in memory).</summary>
        public bool Persist { get; set; } = true;
        public string PathOnDisk { get; set; }

        public SessionLog(string path = null) { PathOnDisk = path ?? Paths.Stats; }

        public int Count { get { lock (_gate) return _all.Count; } }

        /// <summary>A copy, oldest first. Safe to read from any thread.</summary>
        public List<SessionRecord> Snapshot()
        {
            lock (_gate)
                return _all.Select(r => r.Copy()).ToList();
        }

        /// <summary>Launch attempts and give-ups, oldest first. Safe to read from any thread.</summary>
        public List<LaunchEvent> EventsSnapshot()
        {
            lock (_gate)
                return _events.Select(e => e.Copy()).ToList();
        }

        // load / save
        public void Load(DateTime? nowUtc = null)
        {
            lock (_gate)
            {
                _all.Clear(); _open.Clear(); _events.Clear();
                var now = nowUtc ?? DateTime.UtcNow;
                var file = Store.LoadPlain<StatsFile>(PathOnDisk);
                foreach (var r in file.Sessions ?? new List<SessionRecord>())
                {
                    if (r == null || r.StartTicks <= 0) continue;
                    if (r.EndTicks == 0)
                    {
                        // Lantern stopped without closing it (crash, power cut): it was alive at least until LastSeen.
                        r.EndTicks = Math.Max(r.StartTicks, r.LastSeenTicks > 0 ? r.LastSeenTicks : r.StartTicks);
                        if (string.IsNullOrEmpty(r.EndKind)) r.EndKind = "Unknown";
                        _dirty = true;
                    }
                    _all.Add(r);
                }
                _all.Sort((a, b) => a.StartTicks.CompareTo(b.StartTicks));
                foreach (var e in file.Events ?? new List<LaunchEvent>()) if (e != null && e.Ticks > 0) _events.Add(e);
                _events.Sort((a, b) => a.Ticks.CompareTo(b.Ticks));
                if (Prune(now)) _dirty = true;
                SaveLocked();
            }
        }

        bool Prune(DateTime now)
        {
            int before = _all.Count;
            long cutoff = (now - MaxAge).Ticks;
            _all.RemoveAll(r => !r.Open && r.EndTicks < cutoff);
            while (_all.Count > MaxSessions)
            {
                var oldest = _all.FirstOrDefault(r => !r.Open);
                if (oldest == null) break;
                _all.Remove(oldest);
            }
            int eventsBefore = _events.Count;
            _events.RemoveAll(e => e.Ticks < cutoff);
            if (_events.Count > MaxEvents) _events.RemoveRange(0, _events.Count - MaxEvents);
            return _all.Count != before || _events.Count != eventsBefore;
        }

        void SaveLocked()
        {
            if (!Persist || !_dirty) return;
            try
            {
                Store.SavePlain(PathOnDisk, new StatsFile { Sessions = _all, Events = _events });
                _dirty = false;
            }
            catch (Exception ex) { Logger.Write("Stats save: " + ex.Message); }
        }

        public void Save() { lock (_gate) SaveLocked(); }

        // recording
        public void Start(string accountId, long userId, string name, long placeId, string game, DateTime nowUtc, bool pickedUp = false,
                          string origin = "", DateTime? launchedUtc = null)
        {
            lock (_gate)
            {
                if (_open.ContainsKey(accountId)) EndLocked(accountId, nowUtc, "Unknown", false);   // never two open sessions for one account
                var r = new SessionRecord
                {
                    AccountId = accountId, UserId = userId, Name = name ?? "", PlaceId = placeId, Game = game ?? "",
                    StartTicks = nowUtc.Ticks, LastSeenTicks = nowUtc.Ticks, PickedUp = pickedUp,
                    Tracked = true, Origin = origin ?? "", LaunchTicks = launchedUtc?.Ticks ?? 0
                };
                _all.Add(r);
                _open[accountId] = r;
                _dirty = true;
                Prune(nowUtc);
                SaveLocked();
            }
        }

        /// <summary>Remembers one memory reading (bytes) for the account's open session.</summary>
        public void Sample(string accountId, long workingSetBytes)
        {
            lock (_gate)
            {
                if (!_open.TryGetValue(accountId, out var r) || workingSetBytes <= 0) return;
                long mb = workingSetBytes / (1024 * 1024);
                r.MemSumMb += mb; r.MemSamples++;
                if (mb > r.MemPeakMb) r.MemPeakMb = mb;
                _dirty = true;
            }
        }

        /// <summary>Roblox's log shows the account inside a server: the first such moment is kept.</summary>
        public void MarkJoined(string accountId, DateTime nowUtc)
        {
            lock (_gate)
            {
                if (!_open.TryGetValue(accountId, out var r) || r.JoinedTicks > 0) return;
                r.JoinedTicks = Math.Max(r.StartTicks, nowUtc.Ticks);
                _dirty = true;
                SaveLocked();
            }
        }

        /// <summary>A memory ceiling of <paramref name="mb"/> was put on the account's window (the largest one is kept).</summary>
        public void MarkCap(string accountId, int mb)
        {
            lock (_gate)
            {
                if (!_open.TryGetValue(accountId, out var r) || mb <= r.CapMb) return;
                r.CapMb = mb; _dirty = true;
            }
        }

        public void MarkTrimmed(string accountId)
        {
            lock (_gate)
            {
                if (!_open.TryGetValue(accountId, out var r) || r.Trimmed) return;
                r.Trimmed = true; _dirty = true;
            }
        }

        /// <summary>Private memory reading: the first one 10 minutes into the session (loading is over by then), and always the latest.</summary>
        public void SamplePrivate(string accountId, long privateBytes, DateTime nowUtc)
        {
            lock (_gate)
            {
                if (!_open.TryGetValue(accountId, out var r) || privateBytes <= 0) return;
                long mb = privateBytes / (1024 * 1024);
                if (r.PrivFirstTicks == 0)
                {
                    if (nowUtc - r.Start < TimeSpan.FromMinutes(10)) return;
                    r.PrivFirstMb = mb; r.PrivFirstTicks = nowUtc.Ticks;
                }
                r.PrivLastMb = mb; r.PrivLastTicks = nowUtc.Ticks;
                _dirty = true;
            }
        }

        public void AddEvent(LaunchEvent e)
        {
            if (e == null) return;
            lock (_gate)
            {
                if (e.Ticks <= 0) e.Ticks = DateTime.UtcNow.Ticks;
                _events.Add(e);
                if (_events.Count > MaxEvents) _events.RemoveRange(0, _events.Count - MaxEvents);
                _dirty = true;
                SaveLocked();
            }
        }

        /// <summary>Called about once a minute: marks open sessions as alive now and writes the file if anything changed.</summary>
        public void Heartbeat(DateTime nowUtc)
        {
            lock (_gate)
            {
                foreach (var r in _open.Values) { r.LastSeenTicks = nowUtc.Ticks; _dirty = true; }
                SaveLocked();
            }
        }

        public void End(string accountId, DateTime nowUtc, string kind, bool unexpected)
        {
            lock (_gate) { EndLocked(accountId, nowUtc, kind, unexpected); SaveLocked(); }
        }

        void EndLocked(string accountId, DateTime nowUtc, string kind, bool unexpected)
        {
            if (!_open.TryGetValue(accountId, out var r)) return;
            _open.Remove(accountId);
            r.EndTicks = Math.Max(r.StartTicks, nowUtc.Ticks);
            r.LastSeenTicks = r.EndTicks;
            r.EndKind = kind ?? "";
            r.Unexpected = unexpected;
            _dirty = true;
        }

        /// <summary>Lantern is closing while windows are still open: they carry on unwatched, so their session ends here.</summary>
        public void EndAll(DateTime nowUtc, string kind)
        {
            lock (_gate)
            {
                foreach (var id in _open.Keys.ToList()) EndLocked(id, nowUtc, kind, false);
                SaveLocked();
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                var keep = _open.Values.ToList();     // windows that are open right now keep counting
                _all.Clear(); _all.AddRange(keep);
                _events.Clear();
                _dirty = true;
                SaveLocked();
            }
        }

        /// <summary>Demo / tests: replaces everything in memory with these records and never writes them.</summary>
        public void ReplaceInMemory(IEnumerable<SessionRecord> records, IEnumerable<LaunchEvent> events = null)
        {
            lock (_gate)
            {
                Persist = false;
                _all.Clear(); _open.Clear(); _events.Clear();
                foreach (var r in records) { _all.Add(r); if (r.Open) _open[r.AccountId] = r; }
                if (events != null) _events.AddRange(events.OrderBy(e => e.Ticks));
            }
        }

        // CSV
        public static string ToCsv(IEnumerable<SessionRecord> sessions, DateTime nowUtc)
        {
            string q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
            var sb = new StringBuilder();
            sb.AppendLine("account,user_id,game,place_id,start_local,end_local,minutes,ended,unplanned,avg_memory_mb,peak_memory_mb,picked_up");
            foreach (var r in sessions.OrderBy(s => s.StartTicks))
            {
                var end = r.End(nowUtc);
                sb.Append(q(r.Name)).Append(',').Append(r.UserId).Append(',').Append(q(r.Game)).Append(',').Append(r.PlaceId).Append(',')
                  .Append(q(r.Start.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))).Append(',')
                  .Append(r.Open ? "" : q(end.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))).Append(',')
                  .Append(Math.Round((end - r.Start).TotalMinutes, 1).ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(q(r.Open ? "still running" : StatsReport.EndText(r.EndKind))).Append(',')
                  .Append(r.Unexpected ? "yes" : "no").Append(',').Append(r.AvgMb).Append(',').Append(r.MemPeakMb).Append(',')
                  .Append(r.PickedUp ? "yes" : "no").AppendLine();
            }
            return sb.ToString();
        }
    }

    // ======================================================================== numbers for the Stats page

    public sealed class AccountStat
    {
        public long UserId;
        public string Name = "";
        public TimeSpan Time;
        public int Sessions, Unplanned;
        public TimeSpan Longest;
        public long AvgMb;
        public DateTime LastSeenUtc;
        public bool Online;
        public double Share;      // of the busiest account, 0..1 (for the bar)
        public TimeSpan Average => Sessions > 0 ? TimeSpan.FromTicks(Time.Ticks / Sessions) : TimeSpan.Zero;
    }

    public sealed class GameStat
    {
        public long PlaceId;
        public string Name = "";
        public TimeSpan Time;
        public int Sessions;
        public long AvgMb, PeakMb;
        public double Share;
    }

    public sealed class Bucket
    {
        public DateTime StartLocal, EndLocal;
        public TimeSpan Time;
        public string Label = "";
    }

    public sealed class StatsReport
    {
        public DateTime FromUtc, ToUtc;
        public TimeSpan Total;
        public int Sessions, Unplanned, PeakAtOnce;
        public TimeSpan Longest;
        public long AvgMb;
        public List<AccountStat> Accounts = new List<AccountStat>();
        public List<GameStat> Games = new List<GameStat>();
        public List<Bucket> Buckets = new List<Bucket>();
        public string BucketUnit = "day";     // hour | day | week
        public List<SessionRecord> Recent = new List<SessionRecord>();

        public TimeSpan Average => Sessions > 0 ? TimeSpan.FromTicks(Total.Ticks / Sessions) : TimeSpan.Zero;

        /// <summary>Plain words for how a session ended.</summary>
        public static string EndText(string kind)
        {
            switch (kind)
            {
                case "Closed": return "Closed by you";
                case "Kicked": return "Kicked";
                case "BadBehavior": return "Removed for behaviour";
                case "Replaced": return "Signed in elsewhere";
                case "ServerShutdown": return "Server shut down";
                case "Maintenance": return "Roblox maintenance";
                case "NoAccess": return "Not allowed in";
                case "Banned": return "Banned";
                case "LostConnection": return "Lost connection";
                case "Idle": return "Idle 20 min";
                case "FailedToConnect": return "Couldn't connect";
                case "OutOfMemory": return "Out of memory";
                case "Code": return "Disconnected";
                case "Updated": return "Roblox update";
                case "Abrupt": return "Crashed or force-closed";
                case "LanternClosed": return "Lantern closed";
                case "": return "";
                default: return "Ended";
            }
        }

        /// <summary>
        /// Everything the Stats page shows for the window [from, to]. Sessions are clipped to it, so a session that
        /// began yesterday counts only the part inside the range. Open sessions run up to <paramref name="now"/>.
        /// </summary>
        public static StatsReport Build(IEnumerable<SessionRecord> sessions, DateTime from, DateTime to, DateTime now)
        {
            var rep = new StatsReport { FromUtc = from, ToUtc = to };
            var inRange = new List<(SessionRecord r, DateTime s, DateTime e)>();
            foreach (var r in sessions)
            {
                var s = r.Start; var e = r.End(now);
                if (e <= from || s >= to) continue;
                var cs = s < from ? from : s; var ce = e > to ? to : e;
                if (ce <= cs) continue;
                inRange.Add((r, cs, ce));
            }

            var byAcc = new Dictionary<long, AccountStat>();
            var byGame = new Dictionary<long, GameStat>();
            var accMem = new Dictionary<long, (long sum, int n)>();
            var gameMem = new Dictionary<long, (long sum, int n)>();
            long memSum = 0; int memN = 0;

            foreach (var (r, s, e) in inRange)
            {
                var len = e - s;
                rep.Total += len; rep.Sessions++;
                if (r.Unexpected) rep.Unplanned++;
                if (len > rep.Longest) rep.Longest = len;

                long key = r.UserId != 0 ? r.UserId : Math.Abs((long)r.AccountId.GetHashCode());
                if (!byAcc.TryGetValue(key, out var a)) byAcc[key] = a = new AccountStat { UserId = key };
                a.Name = r.Name.Length > 0 ? r.Name : a.Name;
                a.Time += len; a.Sessions++;
                if (r.Unexpected) a.Unplanned++;
                if (len > a.Longest) a.Longest = len;
                if (e > a.LastSeenUtc) a.LastSeenUtc = e;
                if (r.Open) a.Online = true;

                long gk = r.PlaceId;
                if (!byGame.TryGetValue(gk, out var g)) byGame[gk] = g = new GameStat { PlaceId = gk };
                if (r.Game.Length > 0) g.Name = r.Game;
                g.Time += len; g.Sessions++;
                if (r.MemPeakMb > g.PeakMb) g.PeakMb = r.MemPeakMb;

                if (r.MemSamples > 0)
                {
                    memSum += r.AvgMb; memN++;
                    accMem.TryGetValue(key, out var am); accMem[key] = (am.sum + r.AvgMb, am.n + 1);
                    gameMem.TryGetValue(gk, out var gm); gameMem[gk] = (gm.sum + r.AvgMb, gm.n + 1);
                }
            }

            rep.AvgMb = memN > 0 ? memSum / memN : 0;
            foreach (var kv in accMem) byAcc[kv.Key].AvgMb = kv.Value.sum / kv.Value.n;
            foreach (var kv in gameMem) byGame[kv.Key].AvgMb = kv.Value.sum / kv.Value.n;

            rep.Accounts = byAcc.Values.OrderByDescending(x => x.Time).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
            double maxA = rep.Accounts.Count > 0 ? rep.Accounts[0].Time.TotalSeconds : 0;
            foreach (var a in rep.Accounts) a.Share = maxA > 0 ? a.Time.TotalSeconds / maxA : 0;

            foreach (var g in byGame.Values) if (g.Name.Length == 0) g.Name = g.PlaceId > 0 ? "Place " + g.PlaceId : "Unknown game";
            rep.Games = byGame.Values.OrderByDescending(x => x.Time).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
            double maxG = rep.Games.Count > 0 ? rep.Games[0].Time.TotalSeconds : 0;
            foreach (var g in rep.Games) g.Share = maxG > 0 ? g.Time.TotalSeconds / maxG : 0;

            rep.PeakAtOnce = PeakConcurrent(inRange.Select(x => (x.s, x.e)));
            BuildBuckets(rep, inRange.Select(x => (x.s, x.e)).ToList());
            rep.Recent = inRange.OrderByDescending(x => x.r.StartTicks).Take(15).Select(x => x.r).ToList();
            return rep;
        }

        static int PeakConcurrent(IEnumerable<(DateTime s, DateTime e)> spans)
        {
            var events = new List<(DateTime t, int d)>();
            foreach (var (s, e) in spans) { events.Add((s, 1)); events.Add((e, -1)); }
            // an end at the same instant as a start frees its slot first
            events.Sort((a, b) => a.t != b.t ? a.t.CompareTo(b.t) : a.d.CompareTo(b.d));
            int cur = 0, peak = 0;
            foreach (var ev in events) { cur += ev.d; if (cur > peak) peak = cur; }
            return peak;
        }

        static void BuildBuckets(StatsReport rep, List<(DateTime s, DateTime e)> spans)
        {
            var spanLen = rep.ToUtc - rep.FromUtc;
            string unit = spanLen <= TimeSpan.FromHours(36) ? "hour" : spanLen <= TimeSpan.FromDays(45) ? "day" : "week";
            rep.BucketUnit = unit;

            DateTime Floor(DateTime local)
            {
                if (unit == "hour") return new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0, DateTimeKind.Local);
                var d = new DateTime(local.Year, local.Month, local.Day, 0, 0, 0, DateTimeKind.Local);
                if (unit == "week") d = d.AddDays(-(((int)d.DayOfWeek + 6) % 7));    // weeks start on Monday
                return d;
            }
            DateTime Next(DateTime local) => unit == "hour" ? local.AddHours(1) : unit == "day" ? local.AddDays(1) : local.AddDays(7);

            var startLocal = Floor(rep.FromUtc.ToLocalTime());
            var endLocal = rep.ToUtc.ToLocalTime();
            var list = new List<Bucket>();
            for (var t = startLocal; t < endLocal && list.Count < 400; t = Next(t))
            {
                var b = new Bucket { StartLocal = t, EndLocal = Next(t) };
                b.Label = unit == "hour" ? t.ToString("HH:mm", CultureInfo.CurrentCulture) : t.ToString("d MMM", CultureInfo.CurrentCulture);
                list.Add(b);
            }
            if (list.Count == 0) { rep.Buckets = list; return; }

            foreach (var (s, e) in spans)
            {
                var sl = s.ToLocalTime(); var el = e.ToLocalTime();
                foreach (var b in list)
                {
                    if (b.EndLocal <= sl) continue;
                    if (b.StartLocal >= el) break;
                    var cs = sl > b.StartLocal ? sl : b.StartLocal; var ce = el < b.EndLocal ? el : b.EndLocal;
                    if (ce > cs) b.Time += ce - cs;
                }
            }
            rep.Buckets = list;
        }
    }
}
