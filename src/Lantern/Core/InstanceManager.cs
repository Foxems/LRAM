using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Lantern.Core
{
    /// <summary>What <see cref="InstanceManager.FindPlayerAsync"/> learned about a player.</summary>
    public sealed class PlayerFind
    {
        public RobloxApi.FoundUser User;
        /// <summary>The best view any of your accounts had of them.</summary>
        public RobloxApi.PresenceInfo Presence;
        /// <summary>Set when a server one of your accounts can be sent to was found.</summary>
        public LaunchTarget Target;
        public string ViewedBy = "";
        /// <summary>Why nothing joinable was found (empty when <see cref="Target"/> is set).</summary>
        public string Problem = "";
    }

    /// <summary>
    /// Owns the account list and every running Roblox window: launching (staggered), tracking,
    /// auto-rejoin, closing. Lives on the UI thread; network/WMI work is pushed to the thread pool.
    /// </summary>
    public sealed class InstanceManager
    {
        public readonly ObservableCollection<AccountItem> Accounts = new ObservableCollection<AccountItem>();
        public readonly SettingsData Settings;

        /// <summary>(message, isError)</summary>
        public event Action<string, bool> Message;
        public event Action BatchChanged;

        readonly SemaphoreSlim _launchGate = new SemaphoreSlim(1, 1);
        readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        readonly Random _rng = new Random();
        CancellationTokenSource _batchCts;
        int _tick;
        int _perfIdle;
        readonly UsageMonitor _usage = new UsageMonitor();
        readonly List<UsageSnapshot> _hist = new List<UsageSnapshot>();
        readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
        bool _sampling;

        /// <summary>"CPU 6% · GPU 12% · 1.9 GB" for all Lantern's windows together; empty when none are running.</summary>
        public string UsageSummary { get; private set; } = "";
        /// <summary>The current suggestion (what is limiting this PC), or null while there isn't enough data.</summary>
        public Advice Advice { get; private set; }
        public event Action UsageChanged;
        /// <summary>Average memory a running window uses right now, or null when none is running.</summary>
        public double? MeasuredBytesPerWindow => _hist.Count > 0 && _hist[_hist.Count - 1].Windows > 0 ? (double)_hist[_hist.Count - 1].RobloxBytes / _hist[_hist.Count - 1].Windows : (double?)null;

        public bool BatchRunning { get; private set; }

        /// <summary>When and why every window started and ended (the Stats page).</summary>
        public readonly SessionLog Sessions = new SessionLog();
        int _trimFails;
        bool _trimming;
        /// <summary>Set after Windows refused memory trimming several times in a row; the opt-in is then switched off.</summary>
        public bool TrimUnavailable { get; private set; }
        public event Action TrimUnavailableChanged;

        /// <summary>--demo: sample data for screenshots/design review. Nothing is saved or sent anywhere.</summary>
        public bool Demo { get; set; }

        public InstanceManager(SettingsData settings)
        {
            Settings = settings;
            _timer.Tick += (s, e) => OnTick();
        }

        void Say(string text, bool error = false) => Message?.Invoke(text, error);

        public void SaveAccounts()
        {
            if (Demo) return;
            try { Store.SaveAccounts(Accounts.Select(a => a.Data).ToList()); }
            catch (Exception ex) { Logger.Write("SaveAccounts: " + ex.Message); Say("Couldn't save your accounts: " + ex.Message, true); }
        }

        public void SaveSettings()
        {
            if (Demo) return;
            try { Store.SavePlain(Paths.Settings, Settings); }
            catch (Exception ex) { Logger.Write("SaveSettings: " + ex.Message); }
        }

        // load / add / remove
        /// <param name="progress">After each account's session check: (checked so far, all accounts). On the UI thread.</param>
        public async Task LoadAsync(Action<int, int> progress = null)
        {
            foreach (var d in Store.LoadAccounts())
                Accounts.Add(new AccountItem(d));
            Sessions.Load();

            _timer.Start();

            // Pick up windows that are still open from a previous Lantern session. (Skip the WMI query
            // - and the memory it costs - when no Roblox client exists at all.)
            var scan = RobloxProcesses.LivePids().Count == 0
                ? new List<RbxProc>()
                : await Task.Run(() => RobloxProcesses.Scan());
            foreach (var item in Accounts)
            {
                var hit = scan.FirstOrDefault(p => p.TrackerId.Length > 0 && p.TrackerId == item.Data.TrackerId);
                if (hit == null) continue;
                item.Pid = hit.Pid;
                // the window's real start, so its own log (made when it started) is recognised for joins, idle kicks and exit reasons
                item.StartedAt = Native.ProcessStartUtc(hit.Pid) ?? DateTime.UtcNow;
                item.SetState(AccountState.Running);
                item.Log("Picked up again (window was already open)");
                item.JoinedAt = null;
                item.PickedUp = true;
                item.JoinDeepChecked = false;
                item.PerfMode = Settings.PerfMode ?? "Off";
                if (item.Data.LastTarget != null && item.Data.LastTarget.IsValid)
                {
                    // Lantern started this window earlier, so it may rejoin it like any other (auto-rejoin and idle-kick rejoin still
                    // follow their settings). Back into the game itself, not into a server id or a friend that may be gone.
                    item.Target = ForRejoin(item.Data.LastTarget);
                    item.WantRunning = true;
                }
                Sessions.Start(item.Data.Id, item.Data.UserId, item.Display, item.Data.LastPlaceId, item.Data.LastGame, DateTime.UtcNow, pickedUp: true, origin: "PickedUp");
            }

            int checkedCount = 0, total = Accounts.Count;
            using (var gate = new SemaphoreSlim(4))
            {
                await Task.WhenAll(Accounts.ToList().Select(async a =>
                {
                    await gate.WaitAsync();
                    try { await RefreshAsync(a); } finally { gate.Release(); }
                    progress?.Invoke(Interlocked.Increment(ref checkedCount), total);
                }));
            }
        }

        public void LoadDemo()
        {
            Demo = true;
            AccountItem Make(string user, string display, AccountState st, string game, int minutes, string detail = "", string group = "")
            {
                var a = new AccountItem(new AccountData { UserId = 1000 + Accounts.Count, Username = user, DisplayName = display, LastGame = game, Group = group });
                if (st == AccountState.Running) a.StartedAt = DateTime.UtcNow.AddMinutes(-minutes);
                if (st == AccountState.Rejoining) a.RejoinAt = DateTime.UtcNow.AddSeconds(11);
                a.SetState(st, detail);
                Accounts.Add(a);
                return a;
            }
            Make("mossgrove_farm", "Moss", AccountState.Running, "Grow a Garden", 134, group: "Farm");
            Make("pinewren", "Wren", AccountState.Running, "Grow a Garden", 133, group: "Farm");
            Make("kettle.and.co", "Kettle", AccountState.Running, "Grow a Garden", 131, group: "Farm");
            Make("lowtide_04", "Lowtide", AccountState.Launching, "Blox Fruits", 0, group: "Alts");
            Make("ashbell", "Ashbell", AccountState.Rejoining, "Grow a Garden", 0, group: "Farm");
            var sel = Make("juniper_9", "Juniper", AccountState.Idle, "Brookhaven", 0, group: "Alts"); sel.IsSelected = true;
            var sel2 = Make("oat_milk_alt", "Oat", AccountState.Idle, "—", 0, group: "Alts"); sel2.IsSelected = true;
            Make("tern.runs", "Tern", AccountState.Expired, "Adopt Me!", 0);
            var err = Make("quillfeather", "Quill", AccountState.Error, "Brookhaven", 0, "This account can't open that private server.");
            err.Error = LaunchErrors.PrivateDenied();
            err.Log("Launch started → place 4924922222");
            err.Log("Failed: " + err.Error.Title);

            // sample numbers so screenshots show the live-usage readout
            var up = Accounts.Where(a => a.State == AccountState.Running).ToList();
            double[] cpu = { 2.4, 1.9, 2.8 }; double[] gpu = { 4.2, 3.1, 5.0 };
            for (int i = 0; i < up.Count; i++)
                up[i].SetUsage(new UsageSample { Pid = 1, Cpu = cpu[i % 3], Gpu = gpu[i % 3], WorkingSet = (610L + 55 * i) * 1024 * 1024 });
            UsageSummary = "CPU 7%  \u00b7  GPU 12%  \u00b7  1.9 GB";
            Advice = new Advice
            {
                Kind = "cpu", Level = Level.Warn, Mode = "Minimal", Title = "Your CPU is maxed out",
                Detail = "The Roblox windows use about 61% of it. Minimal mode lowers the frame cap and priority; it applies to windows you launch next."
            };
            foreach (var a in up) { a.JoinedAt = a.StartedAt?.AddSeconds(28); a.PresenceType = 2; a.PresenceAt = DateTime.UtcNow.AddSeconds(-40); a.CapMb = 800; }
            PresenceCheckedAt = DateTime.UtcNow.AddSeconds(-40);
            var demoEvents = new List<LaunchEvent>();
            Sessions.ReplaceInMemory(DemoSessions(demoEvents), demoEvents);
            _timer.Start();
        }

        /// <summary>Two weeks of believable history for --demo screenshots and tests (with Pulse's launches and rejoins in <paramref name="events"/>).</summary>
        List<SessionRecord> DemoSessions(List<LaunchEvent> events)
        {
            var now = DateTime.UtcNow;
            var rng = new Random(7);
            var list = new List<SessionRecord>();
            string[] games = { "Grow a Garden", "Blox Fruits", "Brookhaven RP", "Adopt Me!" };
            long[] places = { 126884695634066L, 2753915549L, 4924922222L, 920587237L };
            string[] enders = { "Closed", "Closed", "Closed", "Idle", "LostConnection", "Kicked", "ServerShutdown" };
            foreach (var a in Accounts)
            {
                bool farm = a.Group == "Farm";
                for (int day = 13; day >= 0; day--)
                {
                    int runs = farm ? 2 : (rng.Next(0, 3) == 0 ? 1 : 0);
                    for (int k = 0; k < runs; k++)
                    {
                        var start = now.Date.AddDays(-day).AddHours(rng.Next(6, 20)).AddMinutes(rng.Next(0, 60));
                        var len = TimeSpan.FromMinutes(farm ? rng.Next(90, 420) : rng.Next(25, 150));
                        var end = start + len;
                        if (end > now.AddMinutes(-5)) continue;
                        int g = farm ? 0 : rng.Next(games.Length);
                        string kind = enders[rng.Next(enders.Length)];
                        if (farm && day <= 3 && rng.Next(0, 9) == 0) kind = "Abrupt";
                        int cap = day <= 3 ? 800 : day <= 6 ? 1000 : 0;
                        var r = new SessionRecord
                        {
                            AccountId = a.Data.Id, UserId = a.Data.UserId, Name = a.Display, PlaceId = places[g], Game = games[g],
                            StartTicks = start.Ticks, EndTicks = end.Ticks, LastSeenTicks = end.Ticks, EndKind = kind,
                            Unexpected = kind != "Closed" && kind != "Idle" && kind != "ServerShutdown",
                            MemSumMb = (g == 0 ? 640 : 1100 + g * 120) * 20, MemSamples = 20, MemPeakMb = (g == 0 ? 700 : 1300 + g * 120),
                            Tracked = day <= 9, Origin = "Launch", LaunchTicks = start.AddSeconds(-9).Ticks, CapMb = cap,
                            JoinedTicks = start.AddSeconds(14 + rng.Next(0, 30)).Ticks
                        };
                        list.Add(r);
                        if (r.Tracked) events.Add(new LaunchEvent { Ticks = start.Ticks, AccountId = a.Data.Id, UserId = a.Data.UserId, Name = a.Display, Ok = true, Origin = "Launch" });
                        if (r.Tracked && rng.Next(0, 14) == 0)
                            events.Add(new LaunchEvent { Ticks = start.AddMinutes(-2).Ticks, AccountId = a.Data.Id, UserId = a.Data.UserId, Name = a.Display, Ok = false, Origin = "Launch", Reason = "Roblox didn't open a window in time." });

                        // an unexpected end on a farm account is rejoined a minute or so later
                        if (farm && r.Tracked && kind != "Closed")
                        {
                            var back = end.AddSeconds(25 + rng.Next(0, 70));
                            var backEnd = back.AddMinutes(rng.Next(40, 200));
                            if (backEnd > now.AddMinutes(-5)) continue;
                            string origin = kind == "Idle" ? "IdleRejoin" : "AutoRejoin";
                            list.Add(new SessionRecord
                            {
                                AccountId = a.Data.Id, UserId = a.Data.UserId, Name = a.Display, PlaceId = places[0], Game = games[0],
                                StartTicks = back.Ticks, EndTicks = backEnd.Ticks, LastSeenTicks = backEnd.Ticks, EndKind = "Closed",
                                MemSumMb = 640 * 20, MemSamples = 20, MemPeakMb = 700, Tracked = true, Origin = origin, CapMb = cap,
                                LaunchTicks = back.AddSeconds(-8).Ticks, JoinedTicks = back.AddSeconds(16 + rng.Next(0, 25)).Ticks
                            });
                            events.Add(new LaunchEvent { Ticks = back.Ticks, AccountId = a.Data.Id, UserId = a.Data.UserId, Name = a.Display, Ok = true, Origin = origin });
                        }
                    }
                }
                if (a.State == AccountState.Running && a.StartedAt.HasValue)
                {
                    bool leaky = a.Data.Username == "kettle.and.co";
                    list.Add(new SessionRecord
                    {
                        AccountId = a.Data.Id, UserId = a.Data.UserId, Name = a.Display, PlaceId = places[0], Game = games[0],
                        StartTicks = a.StartedAt.Value.Ticks, LastSeenTicks = now.Ticks, MemSumMb = 640 * 20, MemSamples = 20, MemPeakMb = 700,
                        Tracked = true, Origin = "Launch", LaunchTicks = a.StartedAt.Value.AddSeconds(-9).Ticks, JoinedTicks = a.StartedAt.Value.AddSeconds(28).Ticks, CapMb = 800,
                        PrivFirstMb = 2010, PrivFirstTicks = a.StartedAt.Value.AddMinutes(10).Ticks,
                        PrivLastMb = leaky ? 2010 + 290 * 2 : 2040, PrivLastTicks = a.StartedAt.Value.AddMinutes(130).Ticks
                    });
                    events.Add(new LaunchEvent { Ticks = a.StartedAt.Value.Ticks, AccountId = a.Data.Id, UserId = a.Data.UserId, Name = a.Display, Ok = true, Origin = "Launch" });
                }
                if (a.Data.Username == "ashbell")
                    events.Add(new LaunchEvent { Ticks = now.AddHours(-5).Ticks, AccountId = a.Data.Id, UserId = a.Data.UserId, Name = a.Display, Kind = "GaveUp", Origin = "AutoRejoin", Reason = "Lost connection to the server (code 277)." });
            }
            return list;
        }

        public static string SanitizeCookie(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var s = raw.Trim().Trim('"', '\'');
            var i = s.IndexOf(".ROBLOSECURITY=", StringComparison.OrdinalIgnoreCase);
            if (i >= 0) s = s.Substring(i + ".ROBLOSECURITY=".Length);
            var semi = s.IndexOf(';');
            if (semi >= 0) s = s.Substring(0, semi);
            return s.Trim().Trim('"', '\'');
        }

        /// <summary>
        /// A cheap shape check before a cookie is stored or sent anywhere: printable ASCII only, no whitespace, quotes or separators
        /// (so it can't smuggle a second header or cookie into a request), and a sane length.
        /// </summary>
        public static bool IsPlausibleCookie(string cookie)
        {
            if (string.IsNullOrEmpty(cookie) || cookie.Length < 80 || cookie.Length > 4096) return false;
            foreach (var c in cookie)
                if (c < 0x21 || c > 0x7E || c == ';' || c == ',' || c == '"' || c == '\\') return false;
            return true;
        }

        static readonly Regex CookieRx = new Regex("_\\|WARNING:-DO-NOT-SHARE-THIS[^\\s;\"',]+", RegexOptions.Compiled);

        /// <summary>
        /// Pulls every cookie out of pasted text: one per line, a cookie-jar export, a line that carries other
        /// fields around the cookie... anything containing the standard .ROBLOSECURITY value. Only the cookie is
        /// kept. Falls back to treating each long token-looking line as a cookie.
        /// </summary>
        public static List<string> ExtractCookies(string text)
        {
            var found = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return found;
            foreach (Match m in CookieRx.Matches(text))
                if (!found.Contains(m.Value)) found.Add(m.Value);
            if (found.Count > 0) return found;

            foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var c = SanitizeCookie(line);
                if (c.Length >= 100 && !c.Any(char.IsWhiteSpace) && !found.Contains(c)) found.Add(c);
            }
            return found;
        }

        public sealed class AddSummary
        {
            public int Added, Updated;
            public readonly List<string> Errors = new List<string>();
            public AccountItem Last;
            public int Ok => Added + Updated;
        }

        public async Task<AddSummary> AddManyAsync(IList<string> cookies, Action<int, int> progress = null)
        {
            var sum = new AddSummary();
            for (int i = 0; i < cookies.Count; i++)
            {
                progress?.Invoke(i + 1, cookies.Count);
                var (item, error, updated) = await AddAsync(cookies[i]);
                if (item == null) { sum.Errors.Add((cookies.Count > 1 ? "#" + (i + 1) + ": " : "") + error); continue; }
                sum.Last = item;
                if (updated) sum.Updated++; else sum.Added++;
            }
            return sum;
        }

        public async Task<(AccountItem item, string error, bool updated)> AddAsync(string rawCookie)
        {
            var cookie = SanitizeCookie(rawCookie);
            if (!IsPlausibleCookie(cookie))
                return (null, "That doesn't look like a .ROBLOSECURITY cookie.", false);

            var (status, user) = await RobloxApi.GetUserAsync(cookie);
            if (status == AuthStatus.Expired) return (null, "Roblox rejected that cookie — it's invalid or has expired.", false);
            if (status == AuthStatus.Network) return (null, "Couldn't reach Roblox. Check your connection and try again.", false);

            var existing = Accounts.FirstOrDefault(a => a.Data.UserId == user.Id);
            if (existing != null)
            {
                existing.Data.Cookie = cookie;
                existing.Data.AddedTicks = DateTime.UtcNow.Ticks;   // the *session* is new, so its age restarts
                existing.Data.Username = user.Name;
                existing.Data.DisplayName = user.DisplayName;
                if (!existing.IsActive) existing.SetState(AccountState.Idle);
                existing.Log("Session refreshed");
                existing.RefreshIdentity();
                SaveAccounts();
                _ = LoadAvatarAsync(existing);
                return (existing, null, true);
            }

            var item = new AccountItem(new AccountData
            {
                UserId = user.Id,
                Username = user.Name,
                DisplayName = user.DisplayName,
                Cookie = cookie
            });
            item.SetState(AccountState.Idle);
            item.Log("Added");
            Accounts.Add(item);
            SaveAccounts();
            _ = LoadAvatarAsync(item);
            return (item, null, false);
        }

        public void Remove(AccountItem item)
        {
            if (item.IsActive) Close(item);
            Accounts.Remove(item);
            SaveAccounts();
            try { File.Delete(AvatarPath(item.Data.UserId)); } catch { }
        }

        public async Task RefreshAsync(AccountItem item)
        {
            var wasActive = item.IsActive;
            if (!wasActive) item.SetState(AccountState.Checking);

            var (status, user) = await RobloxApi.GetUserAsync(item.Data.Cookie);
            switch (status)
            {
                case AuthStatus.Ok:
                    item.Data.UserId = user.Id;
                    item.Data.Username = user.Name;
                    item.Data.DisplayName = user.DisplayName;
                    item.RefreshIdentity();
                    if (!wasActive) item.SetState(AccountState.Idle);
                    break;
                case AuthStatus.Expired:
                    if (!wasActive) item.SetState(AccountState.Expired);
                    item.Log("Sign-in expired");
                    break;
                default:
                    if (!wasActive) item.SetState(AccountState.Idle); // offline: don't mislabel a good account as expired
                    break;
            }
            SaveAccounts();
            await LoadAvatarAsync(item);
        }

        // groups
        /// <summary>Distinct group names, alphabetical.</summary>
        public List<string> GroupNames() =>
            Accounts.Select(a => a.Group).Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>Puts accounts into <paramref name="group"/> (empty = none). Reuses an existing spelling of the same name.</summary>
        public void SetGroup(IEnumerable<AccountItem> items, string group)
        {
            group = (group ?? "").Trim();
            if (group.Length > 40) group = group.Substring(0, 40);
            var match = GroupNames().FirstOrDefault(g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase));
            if (match != null) group = match;
            foreach (var a in items.ToList()) { a.Data.Group = group; a.RefreshIdentity(); }
            SaveAccounts();
        }

        public void RenameGroup(string from, string to) =>
            SetGroup(Accounts.Where(a => string.Equals(a.Group, from, StringComparison.OrdinalIgnoreCase)), to);

        public void DeleteGroup(string name) => RenameGroup(name, "");

        public IEnumerable<AccountItem> InGroup(string name) =>
            Accounts.Where(a => string.Equals(a.Group, name, StringComparison.OrdinalIgnoreCase));

        // order
        /// <summary>
        /// Moves <paramref name="item"/> to sit just before (or after) <paramref name="anchor"/>, in the saved order.
        /// Works on the whole list, so it behaves the same while a group or search filter hides some rows.
        /// </summary>
        public bool MoveAccount(AccountItem item, AccountItem anchor, bool before)
        {
            int from = Accounts.IndexOf(item), at = Accounts.IndexOf(anchor);
            if (from < 0 || at < 0 || ReferenceEquals(item, anchor)) return false;
            int to = before ? at : at + 1;
            if (from < to) to--;                       // the item leaves its old slot first
            if (to == from) return false;
            Accounts.Move(from, to);
            SaveAccounts();
            return true;
        }

        /// <summary>One step up (-1) or down (+1) among the rows currently shown (<paramref name="visible"/>, in display order).</summary>
        public bool Nudge(AccountItem item, int delta, IList<AccountItem> visible)
        {
            int i = visible.IndexOf(item), j = i + delta;
            if (i < 0 || j < 0 || j >= visible.Count) return false;
            return MoveAccount(item, visible[j], before: delta < 0);
        }

        public bool MoveToEdge(AccountItem item, bool top)
        {
            int from = Accounts.IndexOf(item), to = top ? 0 : Accounts.Count - 1;
            if (from < 0 || from == to) return false;
            Accounts.Move(from, to);
            SaveAccounts();
            return true;
        }

        public static readonly string[] SortKeys = { "name", "recent", "running", "group", "uptime" };

        /// <summary>Re-orders the list once (it stays in that order, ready to be dragged around). Ties keep their current order.</summary>
        public void Sort(string key)
        {
            IEnumerable<AccountItem> q = Accounts.ToList();
            switch (key)
            {
                case "name": q = q.OrderBy(a => a.Display, StringComparer.CurrentCultureIgnoreCase); break;
                case "recent": q = q.OrderByDescending(a => a.Data.LastLaunchTicks); break;
                case "running": q = q.OrderByDescending(a => a.IsActive ? 1 : 0); break;
                case "group": q = q.OrderBy(a => a.HasGroup ? 0 : 1).ThenBy(a => a.Group, StringComparer.CurrentCultureIgnoreCase); break;
                case "uptime":
                    var now = DateTime.UtcNow;
                    var rep = StatsReport.Build(Sessions.Snapshot(), now.AddDays(-7), now, now);
                    var time = rep.Accounts.ToDictionary(x => x.UserId, x => x.Time);
                    q = q.OrderByDescending(a => time.TryGetValue(a.Data.UserId, out var t) ? t : TimeSpan.Zero);
                    break;
                default: return;
            }
            var order = q.ToList();
            for (int i = 0; i < order.Count; i++)
            {
                int cur = Accounts.IndexOf(order[i]);
                if (cur != i) Accounts.Move(cur, i);
            }
            SaveAccounts();
        }

        // memory trim (opt-in)
        /// <summary>The Settings switch. Turning it on gives Windows another chance after an earlier refusal.</summary>
        public void EnableTrim(bool on)
        {
            Settings.TrimMinimized = on;
            if (on) { TrimUnavailable = false; _trimFails = 0; }
            else foreach (var a in Accounts) a.TrimmedAt = null;
            SaveSettings();
        }

        static long WorkingSetOf(int pid)
        {
            try { using (var p = Process.GetProcessById(pid)) return p.WorkingSet64; } catch { return 0; }
        }

        /// <summary>
        /// Hands a window's idle pages back to Windows. The window keeps running; pages it still needs come back as it
        /// touches them, so the figure creeps up again. Returns the working set before and after.
        /// </summary>
        public async Task<(bool ok, long before, long after)> TrimAsync(AccountItem a)
        {
            int pid = a.Pid;
            if (pid <= 0 || Demo) return (false, 0, 0);
            return await Task.Run(() =>
            {
                long before = WorkingSetOf(pid);
                if (!Native.TrimWorkingSet(pid)) return (false, before, before);
                return (true, before, WorkingSetOf(pid));
            });
        }

        async Task TrimMinimizedAsync()
        {
            if (_trimming || Demo || TrimUnavailable) return;
            _trimming = true;
            try
            {
                foreach (var a in Accounts.Where(x => x.State == AccountState.Running && x.Pid > 0).ToList())
                {
                    int pid = a.Pid;
                    if (a.CapMb > 0) continue;   // a capped window is already held down; trimming it again would only add hitches
                    if (!await Task.Run(() => Native.AllMinimized(pid))) { a.TrimmedAt = null; continue; }   // only windows nobody is looking at

                    bool first = a.TrimmedAt == null;
                    var (ok, before, after) = await TrimAsync(a);
                    if (ok)
                    {
                        _trimFails = 0;
                        a.TrimmedAt = DateTime.UtcNow;
                        Sessions.MarkTrimmed(a.Data.Id);
                        if (first && before > after) a.Log("Trimmed memory: " + Fmt.Memory(before) + " → " + Fmt.Memory(after));
                    }
                    else if (++_trimFails >= 3)
                    {
                        TrimUnavailable = true;
                        Settings.TrimMinimized = false;
                        SaveSettings();
                        Say("Windows wouldn't let Lantern trim Roblox's memory, so that option was switched off.", true);
                        TrimUnavailableChanged?.Invoke();
                        return;
                    }
                }
            }
            catch (Exception ex) { Logger.Write("Trim: " + ex.Message); }
            finally { _trimming = false; }
        }

        // memory trim of open windows (opt-in, Live page)
        /// <summary>How often each open window is trimmed, and when its effect is measured afterwards.</summary>
        public static readonly TimeSpan VisibleTrimEvery = TimeSpan.FromMinutes(1), VisibleTrimMeasure = TimeSpan.FromSeconds(30);
        bool _trimmingVisible;

        /// <summary>The Live switch. Turning it on gives Windows another chance after an earlier refusal.</summary>
        public void EnableTrimVisible(bool on)
        {
            Settings.TrimVisible = on;
            if (on) { TrimUnavailable = false; _trimFails = 0; }
            else foreach (var a in Accounts) { a.VisibleTrimAt = null; a.VisibleTrimKept = 0; a.VisibleTrimMeasured = true; }
            SaveSettings();
        }

        /// <summary>What trimming keeps off right now, all open windows added up (measured 30 s after each trim).</summary>
        public long VisibleTrimKept => Accounts.Where(a => a.State == AccountState.Running && a.Pid > 0).Sum(a => a.VisibleTrimKept);

        /// <summary>
        /// Every 2 s, the open (not minimised) window trimmed longest ago gets its idle pages handed back to Windows, so each one
        /// comes round about once a minute and never two at the same moment. There is no ceiling: whatever the game still uses it
        /// takes straight back (a short hitch right after), and only what it really doesn't use stays off. Half a minute later
        /// the window is measured again, and that is what <see cref="VisibleTrimKept"/> reports. Left alone: the window you are
        /// playing (it has the focus), windows still loading (first 90 s), capped ones, and minimised ones (the minimised-window
        /// trim's job).
        /// </summary>
        async Task TrimVisibleAsync()
        {
            if (_trimmingVisible || Demo || TrimUnavailable || !Settings.TrimVisible) return;
            _trimmingVisible = true;
            try
            {
                var now = DateTime.UtcNow;
                foreach (var a in Accounts.Where(x => x.State == AccountState.Running && x.Pid > 0 && !x.VisibleTrimMeasured && x.VisibleTrimAt.HasValue && now - x.VisibleTrimAt.Value >= VisibleTrimMeasure).ToList())
                {
                    int mpid = a.Pid;
                    long ws = await Task.Run(() => WorkingSetOf(mpid));
                    a.VisibleTrimMeasured = true;
                    if (ws > 0) a.VisibleTrimKept = Math.Max(0, a.VisibleTrimBefore - ws);
                }

                int focused = Native.ForegroundPid();
                var next = Accounts.Where(x => x.State == AccountState.Running && x.Pid > 0 && x.Pid != focused && x.CapMb == 0
                                               && x.StartedAt.HasValue && now - x.StartedAt.Value >= CapGrace
                                               && (!x.VisibleTrimAt.HasValue || now - x.VisibleTrimAt.Value >= VisibleTrimEvery))
                                   .OrderBy(x => x.VisibleTrimAt ?? DateTime.MinValue).FirstOrDefault();
                if (next == null) return;
                int pid = next.Pid;
                next.VisibleTrimAt = now;
                if (await Task.Run(() => Native.AllMinimized(pid))) { next.VisibleTrimKept = 0; return; }

                var (ok, before, after) = await TrimAsync(next);
                if (ok)
                {
                    _trimFails = 0;
                    bool first = next.VisibleTrimBefore == 0;
                    next.VisibleTrimBefore = before;
                    next.VisibleTrimMeasured = false;
                    Sessions.MarkTrimmed(next.Data.Id);
                    if (first && before > after) next.Log("Trimmed open window: " + Fmt.Memory(before) + " → " + Fmt.Memory(after));
                }
                else if (++_trimFails >= 3)
                {
                    TrimUnavailable = true;
                    Settings.TrimVisible = false;
                    Settings.TrimMinimized = false;
                    SaveSettings();
                    Say("Windows wouldn't let Lantern trim Roblox's memory, so trimming was switched off.", true);
                    TrimUnavailableChanged?.Invoke();
                }
            }
            catch (Exception ex) { Logger.Write("Trim open: " + ex.Message); }
            finally { _trimmingVisible = false; }
        }

        // memory cap (opt-in)
        public static readonly int[] CapChoicesMb = { 600, 800, 1000, 1500 };
        /// <summary>A window that has only just started is still loading the game; it is left alone until this much time has passed.</summary>
        static readonly TimeSpan CapGrace = TimeSpan.FromSeconds(90);
        const long MB = 1024 * 1024;

        int _capFails;
        bool _capping;
        readonly HashSet<int> _capChecked = new HashSet<int>();   // windows already checked for a ceiling left by an earlier run
        /// <summary>Set after Windows refused the memory cap several times in a row; the opt-in is then switched off.</summary>
        public bool CapUnavailable { get; private set; }
        public event Action CapUnavailableChanged;

        /// <summary>The Settings switch. Turning it off takes the ceiling off every window straight away.</summary>
        public void EnableCap(bool on)
        {
            Settings.CapMinimized = on;
            if (on) { CapUnavailable = false; _capFails = 0; }
            else LiftAllCaps();
            SaveSettings();
        }

        /// <summary>The ceiling for minimised windows. A window that is already capped follows within a few seconds.</summary>
        public void SetCapMb(int mb)
        {
            Settings.CapMb = mb;
            SaveSettings();
        }

        /// <summary>The ceiling in use: the saved value, or the nearest offered one if the settings file holds something else.</summary>
        public int CapMbNow => CapChoicesMb.Contains(Settings.CapMb) ? Settings.CapMb : CapChoicesMb.OrderBy(c => Math.Abs(c - Settings.CapMb)).First();

        /// <summary>Takes every ceiling off (switch turned off, or Lantern closing). Windows that are already gone are skipped.</summary>
        public void LiftAllCaps()
        {
            foreach (var a in Accounts.Where(x => x.CapMb > 0).ToList())
            {
                if (a.Pid > 0 && !Demo && !Native.LiftWorkingSetCap(a.Pid, a.CapOrigMin)) Logger.Write("Cap: couldn't lift for pid " + a.Pid);
                a.CapMb = 0;
            }
        }

        /// <summary>
        /// Every second: a capped window that was brought back is released at once; every fourth second: a window that is
        /// minimised, past its loading phase, and not yet capped gets its ceiling. Lantern never touches a window you are looking at.
        /// </summary>
        async Task WatchCapsAsync(bool lookForNew)
        {
            if (_capping || Demo) return;
            var candidates = Accounts.Where(x => x.State == AccountState.Running && x.Pid > 0 &&
                                                 (x.CapMb > 0 || !_capChecked.Contains(x.Pid) || (lookForNew && Settings.CapMinimized && !CapUnavailable))).ToList();
            if (candidates.Count == 0) return;
            _capping = true;
            try
            {
                var minimized = await Task.Run(() => Native.MinimizedPids(candidates.Select(x => x.Pid).ToList()));
                int mb = CapMbNow;
                var now = DateTime.UtcNow;

                foreach (var a in candidates)
                {
                    int pid = a.Pid;
                    if (pid <= 0 || a.State != AccountState.Running) continue;

                    // A window seen for the first time may still carry a ceiling from a Lantern run that ended abruptly: take it off.
                    if (a.CapMb == 0 && _capChecked.Add(pid))
                    {
                        long strayMin = 0;
                        if (await Task.Run(() => Native.HasHardCap(pid, out strayMin)) && await Task.Run(() => Native.LiftWorkingSetCap(pid, strayMin)))
                            a.Log("Memory cap left by an earlier run lifted");
                    }

                    bool want = Settings.CapMinimized && !CapUnavailable && minimized.Contains(pid) && a.StartedAt.HasValue && now - a.StartedAt.Value >= CapGrace;

                    if (!want && a.CapMb > 0)
                    {
                        long orig = a.CapOrigMin;
                        if (await Task.Run(() => Native.LiftWorkingSetCap(pid, orig))) { a.CapMb = 0; a.Log("Memory cap lifted"); }
                    }
                    else if (want && a.CapMb != mb)
                    {
                        long before = WorkingSetOf(pid);
                        long orig = 0;
                        bool ok = await Task.Run(() => Native.CapWorkingSet(pid, mb * MB, out orig));
                        if (ok)
                        {
                            _capFails = 0;
                            if (a.CapMb == 0) { a.CapOrigMin = orig; a.TrimmedAt = null; }
                            a.CapMb = mb;
                            Sessions.MarkCap(a.Data.Id, mb);
                            long after = WorkingSetOf(pid);
                            a.Log("Memory capped at " + Fmt.Memory(mb * MB) + (before > after ? ": " + Fmt.Memory(before) + " → " + Fmt.Memory(after) : ""));
                        }
                        else if (++_capFails >= 3)
                        {
                            CapUnavailable = true;
                            Settings.CapMinimized = false;
                            SaveSettings();
                            Say("Windows wouldn't let Lantern cap Roblox's memory, so that option was switched off.", true);
                            CapUnavailableChanged?.Invoke();
                            return;
                        }
                    }
                }
            }
            catch (Exception ex) { Logger.Write("Cap: " + ex.Message); }
            finally { _capping = false; }
        }

        // avatars
        static string AvatarPath(long userId) => Path.Combine(Paths.Cache, "avatar-" + userId + ".png");

        async Task LoadAvatarAsync(AccountItem item)
        {
            var id = item.Data.UserId;
            if (id <= 0) return;
            var path = AvatarPath(id);
            try
            {
                var stale = !File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromHours(12);
                if (stale)
                {
                    var urls = await RobloxApi.GetHeadshotUrlsAsync(new[] { id });
                    if (urls.TryGetValue(id, out var url))
                    {
                        var bytes = await RobloxApi.DownloadImageAsync(url);
                        if (bytes != null) { Paths.Ensure(); File.WriteAllBytes(path, bytes); }
                    }
                }
                if (File.Exists(path))
                {
                    var img = await Task.Run(() => LoadBitmap(path));
                    if (img != null) item.Avatar = img;
                }
            }
            catch (Exception ex) { Logger.Write("Avatar: " + ex.Message); }
        }

        public static BitmapSource LoadBitmap(string path, int decodeWidth = 96)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.DecodePixelWidth = decodeWidth;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        // launching
        bool Prepare(int count, out string error)
        {
            error = null;
            if (App.Demo) { error = "Demo mode never starts Roblox."; return false; }
            if (RobloxProcesses.FindLauncher() == null)
            {
                error = "Roblox doesn't seem to be installed. Install it from roblox.com, then try again.";
                return false;
            }
            if (Settings.MultiInstance)
            {
                if (MultiInstance.Held) return true;
                return MultiInstance.Enable(out error);
            }
            // (Roblox's idle tray process doesn't count: only a real game window blocks a single launch.)
            if (count > 1 || RobloxProcesses.AnyGameClient())
            {
                error = "Turn on “Allow multiple Roblox windows” in Settings to run more than one account.";
                return false;
            }
            return true;
        }

        /// <param name="perfOverride">Off | Balanced | Minimal for this batch only (a profile's own mode); null = the global setting.</param>
        public async Task LaunchAsync(IList<AccountItem> items, LaunchTarget target, string perfOverride = null)
        {
            var queue = items.Where(i => !i.IsActive && i.State != AccountState.Expired).ToList();
            if (queue.Count == 0) return;

            if (!Prepare(queue.Count + Accounts.Count(a => a.State == AccountState.Running), out var err))
            {
                Say(err, true);
                foreach (var q in queue) q.Log("Not started: " + err);
                return;
            }

            // Low-resource mode: write Roblox's lean settings once, before the first window of the batch starts.
            var perf = string.IsNullOrEmpty(perfOverride) ? (Settings.PerfMode ?? "Off") : perfOverride;
            if (!Demo)
            {
                if (perf != "Off")
                {
                    if (!Perf.Apply(perf, Settings, out var pnote)) Say(pnote, true);
                }
                else if (Perf.Pending && !Accounts.Any(a => a.IsActive) && !RobloxProcesses.AnyGameClient())
                {
                    Perf.Restore(out _);   // an earlier run left Roblox's settings lowered; this batch should run normally
                }
            }

            _batchCts = new CancellationTokenSource();
            var ct = _batchCts.Token;
            BatchRunning = true;
            BatchChanged?.Invoke();

            foreach (var item in queue)
            {
                item.CancelRequested = false;
                item.WantRunning = true;
                item.Target = target.Clone();
                item.PerfMode = perf;
                item.Failures = 0;
                item.OutageStreak = 0;
                item.PrimaryKey = ServerLink.Key(target);
                item.Error = null;
                item.NextOrigin = "Launch";
                item.SetState(AccountState.Queued);
            }

            int ok = 0;
            try
            {
                for (int i = 0; i < queue.Count; i++)
                {
                    var item = queue[i];
                    if (ct.IsCancellationRequested || item.CancelRequested) { if (item.State == AccountState.Queued) item.SetState(AccountState.Idle); continue; }

                    await _launchGate.WaitAsync();
                    bool launched;
                    try { launched = await LaunchOneAsync(item, item.Target, ct); }
                    finally { _launchGate.Release(); }
                    if (launched) ok++;

                    bool more = queue.Skip(i + 1).Any(q => q.State == AccountState.Queued);
                    if (more && !ct.IsCancellationRequested)
                    {
                        try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, Settings.StaggerSeconds)), ct); }
                        catch (OperationCanceledException) { }
                    }
                }
            }
            finally
            {
                foreach (var q in queue.Where(q => q.State == AccountState.Queued)) q.SetState(AccountState.Idle);
                BatchRunning = false;
                BatchChanged?.Invoke();
            }

            if (ok > 0 && ok == queue.Count) Say(ok == 1 ? "Launched " + queue[0].Display + "." : "Launched " + ok + " accounts.");
            else if (ok > 0) Say("Launched " + ok + " of " + queue.Count + " accounts.", true);
        }

        // join an alt's server
        /// <summary>
        /// Works out where a running account is right now, as a target other accounts can be sent to.
        /// Private servers reuse the original link (each account needs its own access code); public ones
        /// are pinned to the exact server via the job id, read from Roblox's local log, then presence.
        /// Null when it can't be determined.
        /// </summary>
        public async Task<LaunchTarget> ResolveServerAsync(AccountItem src)
        {
            if (src.Target != null && src.Target.IsPrivate) return src.Target.Clone();

            long userId = src.Data.UserId;
            // Only trust logs written since this session's launch; a window re-attached from an earlier
            // Lantern run has no launch time, so the newest log for the account wins.
            DateTime? since = src.Target != null ? src.StartedAt : null;
            var log = await Task.Run(() => RobloxLogs.CurrentServer(userId, since));

            long place = 0; string job = "";
            if (log != null) { place = log.PlaceId; job = log.JobId; }
            else if (!string.IsNullOrEmpty(src.Data.Cookie))
            {
                var p = await RobloxApi.GetPresenceAsync(src.Data.Cookie, userId);
                if (p != null) { place = p.PlaceId; job = p.JobId; }
            }
            if (place <= 0 || string.IsNullOrEmpty(job)) return null;

            var t = new LaunchTarget { PlaceId = place, JobId = job };
            if (src.Data.LastPlaceId == place && !string.IsNullOrEmpty(src.Data.LastGame)) t.GameName = src.Data.LastGame;
            return t;
        }

        /// <summary>Sends <paramref name="alts"/> into the server <paramref name="src"/> is currently in.</summary>
        public async Task JoinServerOfAsync(AccountItem src, IList<AccountItem> alts)
        {
            var queue = alts.Where(a => a != src && !a.IsActive && a.State != AccountState.Expired).ToList();
            if (queue.Count == 0) { Say("No other account is free to join " + src.Display + ".", true); return; }
            if (src.State != AccountState.Running) { Say(src.Display + " isn't running yet.", true); return; }

            var target = await ResolveServerAsync(src);
            if (target == null)
            {
                Say("Couldn't tell which server " + src.Display + " is in. Wait until the game has loaded, then try again.", true);
                return;
            }
            if (!target.IsPrivate) target.FollowUserId = src.Data.UserId;   // fallback if Roblox refuses the server id (a private server)
            foreach (var a in queue) a.Log("Joining " + src.Display + "'s server");
            await LaunchAsync(queue, target);
        }

        /// <summary>
        /// Looks a player up by username and, if one of your accounts is allowed to see their server
        /// (friends, joins on), returns a target that puts other accounts in it.
        /// </summary>
        public async Task<PlayerFind> FindPlayerAsync(string username)
        {
            var res = new PlayerFind();
            username = (username ?? "").Trim().TrimStart('@');
            if (username.Length < 3 || !Regex.IsMatch(username, "^[A-Za-z0-9_]+$"))
            {
                res.Problem = "That doesn't look like a Roblox username.";
                return res;
            }

            bool netErr = false;
            var user = await RobloxApi.ResolveUsernameAsync(username, e => netErr = e);
            if (user == null)
            {
                res.Problem = netErr ? "Couldn't reach Roblox to look that username up." : "No Roblox user is called “" + username + "”.";
                return res;
            }
            res.User = user;

            // One of your own running accounts? Its own client log knows exactly where it is.
            var own = Accounts.FirstOrDefault(a => a.Data.UserId == user.Id && a.State == AccountState.Running);
            if (own != null)
            {
                var t = await ResolveServerAsync(own);
                if (t != null) { t.FollowUserId = user.Id; res.Target = t; res.ViewedBy = "Lantern"; return res; }
            }

            var viewers = Accounts.Where(a => a.State != AccountState.Expired && !string.IsNullOrEmpty(a.Data.Cookie)).ToList();
            if (viewers.Count == 0)
            {
                res.Problem = "Add an account first: Roblox only shows where a player is to signed-in accounts.";
                return res;
            }

            foreach (var v in viewers.Take(8))
            {
                var p = await RobloxApi.GetPresenceOfAsync(v.Data.Cookie, user.Id);
                if (p == null || !p.Ok) continue;
                if (res.Presence == null || p.Type > res.Presence.Type || !string.IsNullOrEmpty(p.JobId)) res.Presence = p;
                if (p.Type == 2 && p.PlaceId > 0 && !string.IsNullOrEmpty(p.JobId))
                {
                    res.Target = new LaunchTarget { PlaceId = p.PlaceId, JobId = p.JobId, GameName = p.LastLocation, FollowUserId = user.Id };
                    res.ViewedBy = v.Display;
                    return res;
                }
            }

            var pr = res.Presence;
            string who = user.DisplayName.Length > 0 ? user.DisplayName : user.Name;
            if (pr == null) res.Problem = "Couldn't reach Roblox to see where " + who + " is.";
            else if (pr.Type == 0) res.Problem = who + " is offline.";
            else if (pr.Type == 1) res.Problem = who + " is online but not in a game.";
            else if (pr.Type == 3) res.Problem = who + " is in Roblox Studio.";
            else res.Problem = who + " is playing" + (pr.LastLocation.Length > 0 ? " “" + pr.LastLocation + "”" : "") +
                               ", but none of your accounts can join them. Roblox only shares the server with friends who allow joins.";
            return res;
        }

        // profiles
        /// <summary>Starts a saved profile: its accounts (those still here and free) in its game.</summary>
        public async Task<string> LaunchProfileAsync(ProfileData p)
        {
            var parsed = TargetParser.Parse(p.TargetRaw, p.JobId, p.BackupRaw);
            if (!parsed.Ok) return "This profile's game link isn't valid anymore. Update the profile from the launch bar.";

            var chosen = p.AccountIds.Select(id => Accounts.FirstOrDefault(a => a.Data.Id == id)).Where(a => a != null).ToList();
            if (chosen.Count == 0) return "None of this profile's accounts exist anymore.";
            var ready = chosen.Where(a => !a.IsActive && a.State != AccountState.Expired).ToList();
            if (ready.Count == 0) return "Its accounts are already running or need signing in again.";

            parsed.Target.GameName = p.GameName;
            await LaunchAsync(ready, parsed.Target, string.IsNullOrEmpty(p.PerfMode) ? null : p.PerfMode);
            return "";
        }

        public void CancelBatch()
        {
            _batchCts?.Cancel();
            foreach (var a in Accounts.Where(a => a.State == AccountState.Queued))
            {
                a.CancelRequested = true;
                a.WantRunning = false;
                a.SetState(AccountState.Idle);
            }
        }

        // Private servers seen to be full (by ServerLink.Key), so the accounts after the first one don't each wait 30 s to find out. The mark
        // lasts 20 minutes, and beyond that for as long as one of our accounts is playing on the backup it sent them to: once one account
        // has moved over, every account after it goes straight there. A server that shut down (maintenance, restart) is empty again, so
        // its mark is dropped then (see OnExited).
        readonly Dictionary<string, (DateTime Until, string Backup)> _fullServers = new Dictionary<string, (DateTime Until, string Backup)>();

        bool KnownFull(LaunchTarget t)
        {
            if (!_fullServers.TryGetValue(ServerLink.Key(t), out var f)) return false;
            return f.Until > DateTime.UtcNow || Accounts.Any(a => a.State == AccountState.Running && ServerLink.Key(a.Target) == f.Backup);
        }

        void MarkFull(LaunchTarget t)
        {
            var key = ServerLink.Key(t);
            bool news = !_fullServers.ContainsKey(key);
            _fullServers[key] = (DateTime.UtcNow.AddMinutes(20), ServerLink.Key(t.Backup));
            if (news) Say("That private server is full: the next accounts go straight to the other link.");
        }

        /// <summary>
        /// Moves the account over to its backup private server. The first server becomes the backup of the new target, so a later rejoin
        /// tries the server that worked first. Then launches the backup alone, or (<paramref name="keepFallback"/>, when the first server
        /// is only remembered as full) with the first one still behind it, in case the backup has filled up meanwhile.
        /// </summary>
        async Task<bool> UseBackupAsync(AccountItem item, LaunchTarget t, string why, CancellationToken ct, bool keepFallback = false)
        {
            item.Log(why + ", trying the other private server link");
            var first = t.Clone(); first.Backup = null;
            var now = t.Backup.Clone(); now.Backup = null; now.GameName = t.GameName;
            var next = now.Clone(); next.Backup = first;
            item.Target = next;
            return keepFallback ? await LaunchOneAsync(item, next, ct, switched: true) : await LaunchOneAsync(item, now, ct, true);
        }

        /// <param name="onBackup">This attempt is already the backup server, so there is nothing left to fall back to.</param>
        /// <param name="switched">Already sent here from the first server because that one is full: don't switch back without trying.</param>
        async Task<bool> LaunchOneAsync(AccountItem item, LaunchTarget requested, CancellationToken ct, bool onBackup = false, bool switched = false)
        {
            var t = requested.Clone();
            item.Pid = 0;
            if (item.State != AccountState.Launching || !item.LaunchBeganAt.HasValue) item.LaunchBeganAt = DateTime.UtcNow;
            item.SetState(AccountState.Launching);
            item.Log("Launching → " + (t.FollowUserId > 0 && string.IsNullOrEmpty(t.LinkCode) ? "joining a player" : t.PlaceId > 0 ? "place " + t.PlaceId : "private server link"));
            if (t.Backup != null && !switched && KnownFull(t) && !KnownFull(t.Backup))
                return await UseBackupAsync(item, t, "The first private server is full", ct, keepFallback: true);
            try
            {
                // 1. Work out where we're going (private servers need the account's own session).
                string accessCode = null;
                if (!string.IsNullOrEmpty(t.ShareCode))
                {
                    var (place, link) = await RobloxApi.ResolveShareLinkAsync(item.Data.Cookie, t.ShareCode);
                    if (place <= 0 || string.IsNullOrEmpty(link))
                        return t.Backup != null ? await UseBackupAsync(item, t, "The first private server link didn't resolve", ct) : Fail(item, LaunchErrors.ShareUnresolved());
                    t.PlaceId = place; t.LinkCode = link;
                }
                if (!string.IsNullOrEmpty(t.LinkCode))
                {
                    accessCode = await RobloxApi.GetPrivateAccessCodeAsync(item.Data.Cookie, t.PlaceId, t.LinkCode);
                    if (accessCode == null)
                        return t.Backup != null ? await UseBackupAsync(item, t, "This account can't open the first private server", ct) : Fail(item, LaunchErrors.PrivateDenied());
                }
                if (t.PlaceId <= 0) return Fail(item, LaunchErrors.NoPlace());
                if (ct.IsCancellationRequested || item.CancelRequested) { item.SetState(AccountState.Idle); return false; }

                // 2. Single-use launch ticket.
                var ticket = await RobloxApi.GetAuthTicketAsync(item.Data.Cookie);
                if (ticket.Expired)
                {
                    item.WantRunning = false;
                    item.SetState(AccountState.Expired);
                    item.Log("Sign-in expired");
                    Say(item.Display + "'s sign-in has expired. Sign in again to use it.", true);
                    return false;
                }
                if (ticket.Ticket == null)
                    return Fail(item, ticket.Http == 0 ? LaunchErrors.Network() : LaunchErrors.Ticket(ticket.Http, ticket.Error));

                // 3. Start the client. Lantern's own build (no installer beside it, so it can't swap builds and close the other
                // windows) when it has one; otherwise the official launcher, never while Roblox's installer is at work.
                string exe = null;
                if (!Demo)
                {
                    if (RobloxBuild.Current == null) { item.WaitNote = "Preparing Roblox…"; item.RefreshStatus(); }
                    exe = await RobloxBuild.EnsureAsync();
                    item.WaitNote = "";
                    if (ct.IsCancellationRequested || item.CancelRequested) { item.SetState(AccountState.Idle); return false; }
                }
                if (exe == null && !await WaitForInstallerAsync(item, ct)) { item.SetState(AccountState.Idle); item.WantRunning = false; return false; }
                var tracker = _rng.Next(100000, 175000).ToString() + _rng.Next(100000, 900000);
                item.Data.TrackerId = tracker;
                var known = new HashSet<int>(RobloxProcesses.LivePids());
                var uri = BuildUri(ticket.Ticket, t, accessCode, tracker);
                var launchedAt = DateTime.UtcNow;
                if (exe != null)
                {
                    // the same link the official launcher would be handed, given straight to the client
                    Process.Start(new ProcessStartInfo(exe, uri) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
                    item.Log("Started from Lantern's Roblox build " + RobloxBuild.Current);
                }
                else Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });

                // 4. Wait for the client to appear and bind it to this account via its tracker id.
                var deadline = DateTime.UtcNow.AddSeconds(150);
                bool updating = false;
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(1000);
                    if (ct.IsCancellationRequested || item.CancelRequested) { item.SetState(AccountState.Idle); item.WantRunning = false; return false; }

                    // The client that starts first may be the wrong build: it hands over to Roblox's installer and closes. The installer
                    // starts the right build with this same link (same tracker id) once it is done, so wait for that one, however long
                    // the download takes.
                    if (exe == null && await Task.Run(() => RobloxProcesses.InstallerRunning()))
                    {
                        if (!updating)
                        {
                            updating = true;
                            item.Log("Roblox is installing another client build: waiting for it");
                            item.WaitNote = "Roblox is updating…";
                            item.RefreshStatus();
                        }
                        var later = DateTime.UtcNow.AddSeconds(90);
                        if (deadline < later) deadline = later;
                        continue;
                    }

                    var hit = (await Task.Run(() => RobloxProcesses.Scan())).FirstOrDefault(p => p.TrackerId == tracker && !known.Contains(p.Pid));
                    if (hit == null) continue;

                    // A client about to hand over to the installer looks just like a real one for its first moment: look again
                    // shortly. If the installer started meanwhile, or the client already went, this isn't the window to keep.
                    if (!Demo)
                    {
                        await Task.Delay(3000);
                        if (await Task.Run(() => (exe == null && RobloxProcesses.InstallerRunning()) || !RobloxProcesses.LivePids().Contains(hit.Pid)))
                        {
                            known.Add(hit.Pid);
                            continue;
                        }
                    }

                    // A private server at its player limit never answers the join, and the client never logs joining a server. With a
                    // second server to go to (or already on it), silence for 30 s means "full": close this window and use the other one.
                    if (t.IsPrivate && (t.Backup != null || onBackup) && !Demo
                        && !await JoinWentThroughAsync(item.Data.UserId, launchedAt, ct, 30))
                    {
                        RobloxProcesses.Kill(hit.Pid);
                        if (t.Backup == null) return Fail(item, LaunchErrors.PrivateFull());
                        MarkFull(t);
                        return await UseBackupAsync(item, t, "No room in that private server", ct);
                    }

                    // Joining a player by server id works for public servers but Roblox refuses it for a private one (error 524).
                    // The client's log says whether the join happened; if not, close this window and join through the player instead,
                    // which is what the Join button on their profile asks Roblox for.
                    if (!string.IsNullOrEmpty(t.JobId) && t.FollowUserId > 0 && string.IsNullOrEmpty(t.LinkCode) && !Demo
                        && !await JoinWentThroughAsync(item.Data.UserId, launchedAt, ct))
                    {
                        item.Log("Roblox didn't accept the server id (a private server?), joining the player instead");
                        RobloxProcesses.Kill(hit.Pid);
                        var viaPlayer = t.Clone();
                        viaPlayer.JobId = "";
                        item.Target = viaPlayer;   // rejoining later uses the way that works
                        return await LaunchOneAsync(item, viaPlayer, ct);
                    }

                    item.Pid = hit.Pid;
                    item.StartedAt = DateTime.UtcNow;
                    item.JoinedAt = null;
                    item.PickedUp = false;
                    item.JoinDeepChecked = false;
                    item.StillSince = null;
                    item.Exit = null;
                    item.Error = null;
                    item.StuckExit = null; item.StuckSeenAt = null; item.StuckDeepChecked = false;
                    item.OnBackup = t.IsPrivate && item.PrimaryKey.Length > 0 && ServerLink.Key(t) != item.PrimaryKey;
                    item.Data.LastPlaceId = t.PlaceId;
                    item.Data.LastGame = string.IsNullOrEmpty(t.GameName) ? "Place " + t.PlaceId : t.GameName;
                    item.Data.LastTarget = (item.Target ?? t).Clone();
                    item.Data.LastLaunchTicks = DateTime.UtcNow.Ticks;
                    item.SetState(AccountState.Running);
                    item.Log("Roblox window is up");
                    item.RefreshIdentity();
                    SaveAccounts();
                    Sessions.Start(item.Data.Id, item.Data.UserId, item.Display, t.PlaceId, item.Data.LastGame, item.StartedAt.Value,
                                   origin: item.NextOrigin, launchedUtc: item.LaunchBeganAt);
                    Record(item, true, "");
                    item.LaunchBeganAt = null;
                    RobloxProcesses.SetPriority(hit.Pid, Settings.Priority);
                    _ = TuneAsync(item, hit.Pid);
                    return true;
                }
                return Fail(item, LaunchErrors.Timeout(known.Count > 0 && Settings.MultiInstance));
            }
            catch (Exception ex)
            {
                Logger.Write("Launch: " + ex.GetType().Name + " " + ex.Message);
                return Fail(item, LaunchErrors.Exception(ex));
            }
        }

        /// <summary>
        /// Roblox's installer runs one at a time; a launch started while it works makes the second one fail with a critical error
        /// (and the first closes every open window anyway). Waits for it to finish (up to 5 min), plus a few quiet seconds so it can
        /// hand over to the client it starts. False when the launch was cancelled meanwhile.
        /// </summary>
        async Task<bool> WaitForInstallerAsync(AccountItem item, CancellationToken ct)
        {
            if (Demo || !await Task.Run(() => RobloxProcesses.InstallerRunning())) return true;
            item.Log("Roblox is updating: waiting for it to finish before launching");
            item.WaitNote = "Waiting for Roblox to update…";
            item.RefreshStatus();
            var end = DateTime.UtcNow.AddMinutes(5);
            int quiet = 0;
            while (quiet < 3 && DateTime.UtcNow < end)
            {
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { }
                if (ct.IsCancellationRequested || item.CancelRequested) return false;
                quiet = await Task.Run(() => RobloxProcesses.InstallerRunning()) ? 0 : quiet + 1;
            }
            item.WaitNote = "";
            item.RefreshStatus();
            return true;
        }

        /// <summary>
        /// Waits (up to 20 s) for the new client's log to show the account joining a server; a client logs that within a couple of
        /// seconds of starting, so silence means Roblox refused the join. True also if the launch was cancelled meanwhile.
        /// </summary>
        async Task<bool> JoinWentThroughAsync(long userId, DateTime since, CancellationToken ct, int seconds = 20)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end)
            {
                if (ct.IsCancellationRequested) return true;
                if (await Task.Run(() => RobloxLogs.HasJoinedSince(userId, since))) return true;
                try { await Task.Delay(1500, ct); } catch (OperationCanceledException) { return true; }
            }
            return false;
        }

        /// <summary>One launch attempt for the Pulse page (an attempt that moved on to a backup server or another route counts once, when it settles).</summary>
        void Record(AccountItem item, bool ok, string reason, string kind = "Launch")
        {
            if (Demo) return;
            Sessions.AddEvent(new LaunchEvent
            {
                Ticks = DateTime.UtcNow.Ticks, AccountId = item.Data.Id, UserId = item.Data.UserId, Name = item.Display,
                Kind = kind, Ok = ok, Origin = item.NextOrigin ?? "", Reason = reason ?? ""
            });
        }

        bool Fail(AccountItem item, ErrorInfo why)
        {
            Record(item, false, why.Title);
            item.LaunchBeganAt = null;
            item.Error = why;
            item.Log("Failed: " + why.Title);
            item.SetState(AccountState.Error, why.Title);
            Say(item.Display + ": " + why.Title, true);
            return false;
        }

        /// <summary>Mirrors the URI the roblox.com Play button hands to the registered roblox-player handler.</summary>
        static string BuildUri(string ticket, LaunchTarget t, string accessCode, string tracker)
        {
            const string baseUrl = "https://www.roblox.com/Game/PlaceLauncher.ashx?";
            string attempt = "&referredByPlayerId=0&joinAttemptId=" + Guid.NewGuid().ToString() + "&joinAttemptOrigin=PlayButton";
            string place;
            if (accessCode != null)
                place = baseUrl + "request=RequestPrivateGame&browserTrackerId=" + tracker + "&placeId=" + t.PlaceId +
                        "&accessCode=" + accessCode + "&linkCode=" + t.LinkCode + attempt;
            else if (!string.IsNullOrEmpty(t.JobId))
                place = baseUrl + "request=RequestGameJob&browserTrackerId=" + tracker + "&placeId=" + t.PlaceId +
                        "&gameId=" + t.JobId + "&isPlayTogetherGame=false" + attempt;
            else if (t.FollowUserId > 0)
                place = baseUrl + "request=RequestFollowUser&browserTrackerId=" + tracker + "&userId=" + t.FollowUserId + attempt;
            else
                place = baseUrl + "request=RequestGame&browserTrackerId=" + tracker + "&placeId=" + t.PlaceId +
                        "&isPlayTogetherGame=false" + attempt;

            long launchTime = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            // No channel: every account runs the released build (see RobloxBuild). Naming a test channel here is what made the
            // client want another build and swap it in, closing every open window.
            return "roblox-player:1+launchmode:play+gameinfo:" + ticket + "+launchtime:" + launchTime +
                   "+placelauncherurl:" + Uri.EscapeDataString(place) + "+browsertrackerid:" + tracker +
                   "+robloxLocale:en_us+gameLocale:en_us+LaunchExp:InApp";
        }

        // low-resource mode, per window
        /// <summary>
        /// After a window is up: efficiency mode + a lower CPU priority, and (optionally) minimise it once the
        /// game has loaded so it stops drawing altogether. All of it is ordinary Windows scheduling; the
        /// Roblox client itself is never read or modified.
        /// </summary>
        async Task TuneAsync(AccountItem item, int pid)
        {
            var p = Perf.Preset(item.PerfMode, Settings);
            if (p == null || Demo) return;
            try
            {
                await Task.Delay(1500);
                if (item.Pid != pid) return;
                if (p.Eco && Native.SetEfficiencyMode(pid, true)) item.Log("Efficiency mode on");
                RobloxProcesses.SetPriority(pid, Perf.Lower(Settings.Priority, p.Priority));

                if (!Settings.MinimizeAfterLoad) return;
                var deadline = DateTime.UtcNow.AddSeconds(150);
                while (DateTime.UtcNow < deadline && item.Pid == pid)
                {
                    await Task.Delay(3000);
                    var joined = await Task.Run(() => RobloxLogs.CurrentServer(item.Data.UserId, item.StartedAt));
                    if (joined == null) continue;
                    await Task.Delay(12000);   // let the world finish loading before it stops drawing
                    if (item.Pid == pid && Native.MinimizeWindowsOf(new HashSet<int> { pid }) > 0) item.Log("Minimized once the game loaded");
                    return;
                }
            }
            catch (Exception ex) { Logger.Write("Tune: " + ex.Message); }
        }

        /// <summary>Puts Roblox's settings back once no window is left (called on a timer, never while one is open).</summary>
        void WatchPerf()
        {
            if (Demo || !Perf.Pending) { _perfIdle = 0; return; }
            if (BatchRunning || Accounts.Any(a => a.IsActive)) { _perfIdle = 0; return; }
            if (++_perfIdle < 8) return;     // give Roblox time to finish writing its own settings on exit
            _perfIdle = 0;
            if (RobloxProcesses.AnyGameClient()) { _perfIdle = -30; return; }   // someone else's window is open; look again later
            if (Perf.Restore(out var note)) Say("Roblox's graphics settings are back to normal.");
            else Say(note, true);
        }

        // live usage
        /// <summary>Every few seconds: how much CPU, graphics and memory each running window costs, plus a suggestion.</summary>
        async Task SampleUsageAsync()
        {
            if (_sampling || Demo) return;
            var running = Accounts.Where(a => a.State == AccountState.Running && a.Pid > 0).ToList();
            if (running.Count == 0)
            {
                if (_hist.Count > 0 || UsageSummary.Length > 0)
                {
                    _hist.Clear(); UsageSummary = ""; Advice = null;
                    foreach (var a in Accounts) a.ClearUsage();
                    UsageChanged?.Invoke();
                }
                return;
            }

            _sampling = true;
            try
            {
                var pids = new HashSet<int>(running.Select(a => a.Pid));
                var (samples, sys) = await Task.Run(() => (_usage.Sample(pids), _usage.SystemCpu()));
                _dispatcher.Invoke(new Action(() => ApplyUsage(running, samples, sys)));
            }
            catch (Exception ex) { Logger.Write("SampleUsage: " + ex.Message); }
            finally { _sampling = false; }
        }

        void ApplyUsage(List<AccountItem> running, Dictionary<int, UsageSample> samples, double sysCpu)
        {
            double cpu = 0, gpu = 0; bool anyGpu = false; ulong bytes = 0; int n = 0;
            foreach (var a in running)
            {
                if (a.State == AccountState.Running && a.Pid > 0 && samples.TryGetValue(a.Pid, out var s))
                {
                    a.SetUsage(s);
                    if (a.TrimmedAt == null && a.CapMb == 0) Sessions.Sample(a.Data.Id, s.WorkingSet);   // a trimmed or capped window's figure says nothing about the game
                    Sessions.SamplePrivate(a.Data.Id, s.Private, DateTime.UtcNow);
                    if (s.Cpu < StillCpu) { if (!a.StillSince.HasValue) a.StillSince = DateTime.UtcNow; }
                    else a.StillSince = null;
                    cpu += s.Cpu; bytes += (ulong)s.WorkingSet; n++;
                    if (s.Gpu.HasValue) { gpu += s.Gpu.Value; anyGpu = true; }
                }
                else a.ClearUsage();
            }
            foreach (var a in Accounts) if (a.State != AccountState.Running) a.ClearUsage();

            Native.Memory(out var total, out var avail);
            _hist.Add(new UsageSnapshot
            {
                Time = DateTime.UtcNow, Windows = n, RobloxCpu = cpu, SystemCpu = sysCpu,
                RobloxGpu = anyGpu ? Math.Min(100, gpu) : (double?)null,
                RobloxBytes = bytes, AvailableBytes = avail, TotalBytes = total
            });
            if (_hist.Count > 20) _hist.RemoveAt(0);

            UsageSummary = n == 0 ? "" : "CPU " + Fmt.Percent(cpu) + (anyGpu ? "  \u00b7  GPU " + Fmt.Percent(Math.Min(100, gpu)) : "") + "  \u00b7  " + Fmt.Memory(bytes);
            Advice = ModeAdvisor.Evaluate(_hist, Settings.PerfMode, Settings.MinimizeAfterLoad);
            UsageChanged?.Invoke();
        }

        // closing
        public void Close(AccountItem item)
        {
            item.WantRunning = false;
            item.RejoinAt = null;
            item.CancelRequested = true;

            if (item.Pid > 0)
            {
                int pid = item.Pid;
                item.SetState(AccountState.Closing);
                item.Log("Closing");
                Native.RequestClose(new HashSet<int> { pid });
                _ = KillIfStillAlive(pid);
            }
            else if (item.State != AccountState.Launching)
            {
                item.SetState(AccountState.Idle);
            }
            // Launching with no pid yet: the launch loop sees CancelRequested and settles itself.
        }

        static async Task KillIfStillAlive(int pid)
        {
            await Task.Delay(4000);
            if (RobloxProcesses.LivePids().Contains(pid)) RobloxProcesses.Kill(pid);
        }

        public void CloseAll()
        {
            CancelBatch();
            foreach (var a in Accounts.Where(a => a.IsActive && a.State != AccountState.Closing).ToList()) Close(a);
        }

        public int RunningCount => Accounts.Count(a => a.State == AccountState.Running);

        public int TileWindows(IntPtr anchor)
        {
            var windows = new List<IntPtr>();
            foreach (var a in Accounts.Where(a => a.State == AccountState.Running && a.Pid > 0))
                windows.AddRange(Native.TopWindowsOf(new HashSet<int> { a.Pid }));
            return Native.Tile(windows, anchor);
        }

        // watchdog
        void OnTick()
        {
            _tick++;
            var live = RobloxProcesses.LivePids();
            var now = DateTime.UtcNow;

            foreach (var a in Accounts.ToList())
            {
                if (a.Pid > 0 && !live.Contains(a.Pid)) OnExited(a, now);

                if (a.State == AccountState.Rejoining && a.RejoinAt.HasValue && a.RejoinAt.Value <= now)
                {
                    a.RejoinAt = null;
                    _ = RejoinAsync(a);
                }

                if (a.State == AccountState.Running || a.State == AccountState.Rejoining) a.RefreshStatus();
            }
            if (_tick % 3 == 0) _ = SampleUsageAsync();
            if (_tick % 60 == 0 && !Demo) Sessions.Heartbeat(now);
            if (_tick % 60 == 30 && Settings.TrimMinimized && !Demo) _ = TrimMinimizedAsync();
            if (_tick % 2 == 1 && Settings.TrimVisible && !Demo) _ = TrimVisibleAsync();
            _ = WatchCapsAsync(_tick % 4 == 1);
            if (_tick % 5 == 4 && !Demo) _ = WatchStuckAsync();
            if (_tick % 5 == 2 && !Demo) _ = WatchJoinsAsync();
            WatchPerf();
            if (!Demo)
            {
                // Lantern's Roblox build: ready soon after start, and checked against Roblox's release every 10 minutes, so a new
                // build is in place before anything needs it (open windows aren't touched)
                if (_tick % 600 == 5) _ = Task.Run(async () => { try { await RobloxBuild.EnsureAsync(); } catch (Exception ex) { Logger.Write("Roblox build: " + ex.Message); } });
                _guardWanted = Accounts.Any(a => a.IsActive) && RobloxBuild.Current != null;
                if (_guardWanted && !_guarding) GuardInstaller();
            }
        }

        // Roblox's installer
        volatile bool _guardWanted;
        volatile bool _guarding;
        DateTime _guardToldAt = DateTime.MinValue;

        /// <summary>
        /// While accounts run from Lantern's build, Roblox's installer has nothing to do for them, and the first thing it does is
        /// close every Roblox window (it starts from Roblox's tray icon when it finds an update, or from a game started in the browser).
        /// It is stopped the moment it appears: looked for four times a second, which costs next to nothing.
        /// </summary>
        void GuardInstaller()
        {
            _guarding = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (_guardWanted)
                    {
                        if (RobloxProcesses.StopInstallers() > 0)
                        {
                            Logger.Write("Stopped Roblox's installer before it could close the running windows");
                            _ = _dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (DateTime.UtcNow - _guardToldAt < TimeSpan.FromMinutes(10)) return;
                                _guardToldAt = DateTime.UtcNow;
                                Say("Roblox's installer started and would have closed every Roblox window, so Lantern stopped it. Lantern keeps its own Roblox up to date.");
                            }));
                        }
                        await Task.Delay(250);
                    }
                }
                catch (Exception ex) { Logger.Write("Installer guard: " + ex.Message); }
                finally { _guarding = false; }
            });
        }

        // Pulse: in a server or not
        /// <summary>A live Roblox client always uses some CPU (a minimised, capped one about 0.8% of this kind of PC); below this share of the whole PC it does nothing at all.</summary>
        public const double StillCpu = 0.03;
        /// <summary>How long a window may load before "never got into a server" is the likelier story.</summary>
        public static readonly TimeSpan JoinWatchFor = TimeSpan.FromMinutes(5);
        bool _watchingJoins;

        /// <summary>
        /// Every 5 s: windows that are up but not yet seen inside a server have their own client log read (only the log that
        /// belongs to that account) for the join. Read-only, local files; nothing is sent and Roblox itself is not touched.
        /// </summary>
        async Task WatchJoinsAsync()
        {
            if (_watchingJoins) return;
            var now = DateTime.UtcNow;
            var due = Accounts.Where(a => a.State == AccountState.Running && a.Pid > 0 && !a.JoinedAt.HasValue && a.Data.UserId > 0 &&
                                          a.StartedAt.HasValue && (now - a.StartedAt.Value <= JoinWatchFor || !a.JoinDeepChecked || _tick % 30 == 2)).ToList();
            if (due.Count == 0) return;
            _watchingJoins = true;
            try
            {
                foreach (var a in due)
                {
                    int pid = a.Pid;
                    var since = a.StartedAt.Value;
                    long userId = a.Data.UserId;
                    bool picked = a.PickedUp;
                    // a window Lantern found already open may have joined hours ago: look near the start of its log too, once
                    // past the first minutes (or found already open) the join may be outside the log's tail: look near its start, once
                    bool deep = !a.JoinDeepChecked && (picked || now - since > JoinWatchFor);
                    bool joined = await Task.Run(() => RobloxLogs.InServerSince(userId, since, deep));
                    if (deep) a.JoinDeepChecked = true;
                    if (!joined || a.Pid != pid || a.JoinedAt.HasValue) continue;
                    MarkInServer(a, picked ? "In a server (found already open)" : null);
                }
            }
            catch (Exception ex) { Logger.Write("Joins: " + ex.Message); }
            finally { _watchingJoins = false; }
        }

        void MarkInServer(AccountItem a, string note)
        {
            a.JoinedAt = DateTime.UtcNow;
            Sessions.MarkJoined(a.Data.Id, a.JoinedAt.Value);
            a.Log(note ?? "In a server (" + Math.Max(1, (int)(a.JoinedAt.Value - (a.LaunchBeganAt ?? a.StartedAt ?? a.JoinedAt.Value)).TotalSeconds) + " s after the launch)");
        }

        /// <summary>When Roblox's presence service was last asked about the running windows (null = never, or it didn't answer).</summary>
        public DateTime? PresenceCheckedAt { get; private set; }
        bool _askingPresence;

        /// <summary>
        /// Asks Roblox's presence service, in one request and through one of your own running accounts, whether the running
        /// accounts are in a game: the same call the website's friends list makes. Only called while the Pulse page is open.
        /// </summary>
        public async Task RefreshPresenceAsync()
        {
            if (_askingPresence || Demo) return;
            var running = Accounts.Where(a => a.State == AccountState.Running && a.Data.UserId > 0).ToList();
            var viewer = running.FirstOrDefault(a => !string.IsNullOrEmpty(a.Data.Cookie));
            if (viewer == null) return;
            _askingPresence = true;
            try
            {
                var answer = await RobloxApi.GetPresencesAsync(viewer.Data.Cookie, running.Select(a => a.Data.UserId).Distinct().ToList());
                if (answer == null) return;
                var now = DateTime.UtcNow;
                foreach (var a in running)
                {
                    if (!answer.TryGetValue(a.Data.UserId, out var p)) continue;
                    a.PresenceType = p.Type; a.PresenceAt = now;
                    // a window whose join Lantern couldn't read (found already open, or its join is too far back in the log): Roblox's word is enough
                    if (p.Type == 2 && !a.JoinedAt.HasValue && a.State == AccountState.Running) MarkInServer(a, "In a game, as Roblox's presence service says");
                }
                PresenceCheckedAt = now;
            }
            catch (Exception ex) { Logger.Write("Presence: " + ex.Message); }
            finally { _askingPresence = false; }
        }

        // stuck on Roblox's error screen
        /// <summary>A window this young is still starting; its log has nothing to say yet.</summary>
        static readonly TimeSpan StuckMinRun = TimeSpan.FromSeconds(15);
        /// <summary>The same code must still be showing this much later (a second look) before the window is closed; a reconnect that goes through in between wins.</summary>
        static readonly TimeSpan StuckGrace = TimeSpan.FromSeconds(8);
        bool _watchingStuck;

        /// <summary>Does this disconnect get rejoined with the current settings?</summary>
        bool RejoinsAfter(ExitInfo e) =>
            e != null && (e.Recovery == Recovery.Rejoin ? Settings.AutoRejoin : e.Recovery == Recovery.IdleRejoin && Settings.RejoinAfterIdleKick);

        /// <summary>
        /// A disconnected Roblox window does not exit: kicked, server shut down, Roblox maintenance (275), lost connection, idle (278)…
        /// it sits on Roblox's error screen, so the exit watchdog never fires and "Rejoin when closed" would never see it. Every 5 s,
        /// each running window has its own client log checked for a disconnect code after its last join (see RobloxLogs.StuckOn). A code
        /// that the settings rejoin (see <see cref="Disconnects"/>), still showing a few seconds later, gets the window closed the ordinary
        /// way; the normal exit path then rejoins it with that reason. Codes that must not be rejoined (signed in elsewhere, bans) are left
        /// on screen for you to see.
        /// </summary>
        async Task WatchStuckAsync()
        {
            if (_watchingStuck || !(Settings.AutoRejoin || Settings.RejoinAfterIdleKick)) return;
            var now = DateTime.UtcNow;
            var due = Accounts.Where(a => a.State == AccountState.Running && a.Pid > 0 && a.WantRunning && a.StuckExit == null && a.Target != null &&
                                          a.Data.UserId > 0 && a.StartedAt.HasValue && now - a.StartedAt.Value >= StuckMinRun).ToList();
            if (due.Count == 0) return;
            _watchingStuck = true;
            try
            {
                foreach (var a in due)
                {
                    int pid = a.Pid;
                    var since = a.StartedAt.Value;
                    long userId = a.Data.UserId;
                    bool deep = !a.StuckDeepChecked;
                    var e = await Task.Run(() => RobloxLogs.StuckOn(userId, since, deep));
                    // Things may have changed while the log was read: only act on the very same, still-wanted window.
                    if (a.Pid != pid || a.State != AccountState.Running || !a.WantRunning || a.StuckExit != null) continue;
                    a.StuckDeepChecked = true;
                    if (!RejoinsAfter(e)) { a.StuckSeenAt = null; continue; }
                    if (!a.StuckSeenAt.HasValue || a.StuckSeenCode != e.Code) { a.StuckSeenCode = e.Code; a.StuckSeenAt = DateTime.UtcNow; continue; }
                    if (DateTime.UtcNow - a.StuckSeenAt.Value < StuckGrace) continue;
                    OnStuck(a, pid, e);
                }
            }
            catch (Exception ex) { Logger.Write("Stuck: " + ex.Message); }
            finally { _watchingStuck = false; }
        }

        void OnStuck(AccountItem a, int pid, ExitInfo e)
        {
            a.StuckExit = e;
            a.StuckSeenAt = null;
            if (e.Kind == "Idle")
            {
                a.Log("Roblox removed this account for being idle (code 278): closing the window to rejoin");
                Say(a.Display + " was removed for being idle. Rejoining.");
            }
            else
            {
                a.Log("Roblox disconnected this account: " + e.Title.TrimEnd('.') + ". Closing the window to rejoin");
                Say(a.Display + ": " + e.Short + " (code " + e.Code + "). Rejoining.");
            }

            // Back into the game itself, not into a friend's server or a server id that is gone. A private-server link is kept.
            if (e.Kind == "Idle" || e.ServerGone) a.Target = ForRejoin(a.Target);

            Native.RequestClose(new HashSet<int> { pid });
            _ = KillIfStillAlive(pid);
        }

        /// <summary>The same place for a rejoin: a public server id or "follow this player" is dropped (it may be gone); a private-server link is kept.</summary>
        static LaunchTarget ForRejoin(LaunchTarget t)
        {
            if (t == null || t.IsPrivate || t.PlaceId <= 0 || (t.FollowUserId <= 0 && string.IsNullOrEmpty(t.JobId))) return t?.Clone();
            var c = t.Clone();
            c.JobId = ""; c.FollowUserId = 0;
            return c;
        }

        void OnExited(AccountItem a, DateTime now)
        {
            var started = a.StartedAt;
            bool byUs = a.CancelRequested || a.State == AccountState.Closing;
            a.Pid = 0;
            var ranFor = started.HasValue ? now - started.Value : TimeSpan.Zero;
            a.StartedAt = null;
            a.JoinedAt = null;
            a.StillSince = null;
            a.PresenceType = -1; a.PresenceAt = null;

            // Why did it end? Roblox's own log knows (kick, lost connection, crash...). A window Lantern closed from Roblox's error
            // screen already said why.
            var stuck = a.StuckExit;
            a.StuckExit = null; a.StuckSeenAt = null;
            ExitInfo exit = null;
            if (byUs)
            {
                exit = RobloxLogs.Describe("Closed", 285);
                exit.Title = "You closed it from Lantern.";
            }
            else if (stuck != null) exit = stuck;
            // Roblox's installer closes every open window while it swaps builds: not the account's fault, and its rejoin waits for it
            else if (!Demo && RobloxProcesses.InstallerRunning()) exit = RobloxLogs.Describe("Updated", 0);
            else if (!Demo)
            {
                try { exit = RobloxLogs.ExitOf(a.Data.UserId, a.Target != null ? started : null); }
                catch (Exception ex) { Logger.Write("ExitOf: " + ex.Message); }
            }
            a.Exit = exit;
            a.TrimmedAt = null;
            a.CapMb = 0;   // the ceiling went with the process
            Sessions.End(a.Data.Id, now, byUs ? "Closed" : exit != null ? exit.Kind : "Unknown", !byUs && exit != null && exit.Unexpected);
            if (exit != null) a.Log("Ended: " + exit.Title);
            else a.Log("Ended");
            if (exit != null && exit.Unexpected && stuck == null) Say(a.Display + ": " + exit.Title, true);

            // A private server that shut down (maintenance, restart) starts empty: it is no longer full.
            if (!byUs && exit != null && exit.ServerGone && a.Target != null && a.Target.IsPrivate) _fullServers.Remove(ServerLink.Key(a.Target));

            bool idleRejoin = stuck != null && stuck.Kind == "Idle" && Settings.RejoinAfterIdleKick;
            if (!a.WantRunning || !(Settings.AutoRejoin || idleRejoin))
            {
                a.WantRunning = false;
                a.SetState(AccountState.Idle);
                return;
            }

            if (exit != null && exit.StopRejoin)
            {
                a.WantRunning = false;
                a.SetState(AccountState.Idle);
                a.Log("Auto-rejoin paused (" + exit.Short + ")");
                return;
            }

            // A server going away or the network failing is not the account's fault: back off and keep trying, but don't give up.
            if (ranFor >= TimeSpan.FromMinutes(10)) a.OutageStreak = 0;
            TimeSpan? wait = null;
            if (exit != null && exit.Outage) wait = TimeSpan.FromSeconds(Disconnects.OutageWaitSeconds(++a.OutageStreak));
            else if (exit == null || exit.Kind != "Updated") a.Failures = ranFor < TimeSpan.FromSeconds(90) ? a.Failures + 1 : 0;
            if (exit != null && exit.ServerGone) a.Target = ForRejoin(a.Target);
            a.NextOrigin = idleRejoin ? "IdleRejoin" : "AutoRejoin";
            ScheduleRejoin(a, now, wait);
        }

        void ScheduleRejoin(AccountItem a, DateTime now, TimeSpan? wait = null)
        {
            if (a.Failures > Settings.MaxRejoins)
            {
                a.WantRunning = false;
                a.Error = new ErrorInfo
                {
                    Title = "Kept closing — stopped rejoining",
                    Why = "The window ended again and again within a minute or two of starting" + (a.Exit != null ? " (last reason: " + a.Exit.Title.TrimEnd('.') + ")" : "") + ".",
                    Fix = "Look at the reason above, fix that, then launch again. Raise “Give up after” in Settings if you want it to keep trying."
                };
                a.SetState(AccountState.Error, a.Error.Title);
                a.Log("Gave up rejoining");
                Record(a, false, a.Exit != null ? a.Exit.Title : "", "GaveUp");
                Say(a.Display + " keeps closing, so auto-rejoin gave up.", true);
                return;
            }
            int secs = Math.Max(5, Settings.RejoinDelaySeconds);
            if (wait.HasValue) secs = Math.Max(secs, (int)wait.Value.TotalSeconds);
            a.RejoinAt = now.AddSeconds(secs);
            a.Log("Rejoining in " + secs + " s");
            a.SetState(AccountState.Rejoining);
        }

        async Task RejoinAsync(AccountItem a)
        {
            if (!a.WantRunning || a.Target == null) { a.SetState(AccountState.Idle); return; }
            if (!Prepare(1, out var err))
            {
                Record(a, false, err);
                a.WantRunning = false;
                a.Error = LaunchErrors.Generic(err);
                a.SetState(AccountState.Error, err);
                Say(err, true);
                return;
            }

            if (_launchGate.CurrentCount == 0)
            {
                a.WaitNote = RobloxProcesses.InstallerRunning() ? "Waiting for Roblox to update…" : "Rejoining · waiting its turn";
                a.RefreshStatus();
            }
            await _launchGate.WaitAsync();
            bool ok;
            try
            {
                ok = await LaunchOneAsync(a, a.Target, CancellationToken.None);
                // Hold the gate a while, like the gap between launches in a batch: when many windows drop at once (a server
                // shutdown, a network blip) they then rejoin one by one instead of all hitting Roblox within seconds.
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(3, Settings.StaggerSeconds)));
            }
            finally { _launchGate.Release(); }

            if (!ok && a.WantRunning && a.State != AccountState.Expired)
            {
                // Roblox said "too many requests", its servers failed, or the network is down: not the account's fault, so it doesn't
                // count toward giving up; wait clearly longer instead, longer each time in a row
                if (a.Error != null && a.Error.Transient)
                {
                    int secs = Math.Max(a.Error.Http == 429 ? 90 : 0, Disconnects.OutageWaitSeconds(++a.OutageStreak));
                    ScheduleRejoin(a, DateTime.UtcNow, TimeSpan.FromSeconds(secs));
                }
                else
                {
                    a.Failures++;
                    ScheduleRejoin(a, DateTime.UtcNow);
                }
            }
        }

        public void Shutdown()
        {
            _timer.Stop();
            LiftAllCaps();   // a window that outlives Lantern must not stay held down
            _usage.Dispose();
            if (!Demo) Sessions.EndAll(DateTime.UtcNow, "LanternClosed");
            // Leave Roblox's settings as we found them, unless a window we started is still open.
            if (!Demo && Perf.Pending && !Accounts.Any(a => a.IsActive)) Perf.Restore(out _);
        }
    }
}
