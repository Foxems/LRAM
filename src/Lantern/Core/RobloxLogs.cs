using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Lantern.Core
{
    /// <summary>Why a Roblox window ended, read from the client's own log.</summary>
    public sealed class ExitInfo
    {
        /// <summary>Closed | Kicked | BadBehavior | Replaced | ServerShutdown | Maintenance | LostConnection | Idle | FailedToConnect | OutOfMemory | NoAccess | Banned | Code | Updated | Abrupt | Unknown</summary>
        public string Kind = "Unknown";
        /// <summary>Roblox's disconnect code, 0 when none was logged.</summary>
        public int Code;
        /// <summary>Two or three words for the row ("Kicked", "Lost connection").</summary>
        public string Short = "";
        public string Title = "";
        public string Detail = "";
        /// <summary>Not something the user did, so worth flagging.</summary>
        public bool Unexpected;
        /// <summary>Rejoining would only fight the cause (e.g. the account was opened on another device).</summary>
        public bool StopRejoin;
        /// <summary>What Lantern does about it (see <see cref="Disconnects"/>); None for endings without a code.</summary>
        public Recovery Recovery = Recovery.None;
        /// <summary>Roblox's or the game's side: rejoins back off instead of counting toward giving up.</summary>
        public bool Outage;
        /// <summary>That server is gone, so a rejoin goes to the game (or private-server link), not to the old server id.</summary>
        public bool ServerGone;
    }

    /// <summary>
    /// Reads Roblox's own client logs (%LOCALAPPDATA%\Roblox\logs) to learn which server an account is in
    /// and why a window ended. Read-only, local files only - nothing is sent anywhere and Roblox itself
    /// is never touched.
    /// </summary>
    public static class RobloxLogs
    {
        static readonly Regex JoinRx = new Regex(@"Joining game '([0-9a-fA-F-]{36})' place (\d+)", RegexOptions.Compiled);
        static readonly Regex UserRx = new Regex(@"game_join_loadtime:[^\r\n]*?userid:(\d+)", RegexOptions.Compiled);
        static readonly Regex AnyUserRx = new Regex(@"(?:userid:|""UserId""%3a)(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex DisconnectRx = new Regex(
            @"(?:Disconnection Notification\. Reason: |Sending disconnect with reason: |Disconnected from server for reason: Player: )(\d+)", RegexOptions.Compiled);
        static readonly Regex LostRx = new Regex(@"Connection lost", RegexOptions.Compiled);
        static readonly Regex CleanRx = new Regex(
            @"AppPlatformQoSEmergencyHandler was destroyed|internal browser session end|SingleSurfaceApp\] setStage: \(stage:None\)", RegexOptions.Compiled);
        const int TailBytes = 1024 * 1024;

        /// <summary>Overridable so tests can point at a throw-away folder.</summary>
        public static string LogDir { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "logs");

        public sealed class Server
        {
            public long PlaceId;
            public string JobId = "";
        }

        // which log is whose
        /// <summary>
        /// Newest-first client logs (not crash-handler logs), optionally only those written since a moment. Not cut to a handful:
        /// with many windows open every log is being written at once, so "the newest few" would leave some running windows out.
        /// Callers skip other accounts' logs by their remembered owner (<see cref="OwnerOf"/>) before reading any tail.
        /// </summary>
        static IEnumerable<FileInfo> Candidates(DateTime? since)
        {
            if (!Directory.Exists(LogDir)) return Enumerable.Empty<FileInfo>();
            return new DirectoryInfo(LogDir).GetFiles("*_Player_*_last.log")
                .Where(f => f.Name.IndexOf("CrashHandler", StringComparison.OrdinalIgnoreCase) < 0)
                .Where(f => !since.HasValue || f.LastWriteTimeUtc >= since.Value.AddSeconds(-30))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(MaxCandidates);
        }

        /// <summary>A safety bound only, far above any number of windows one PC runs.</summary>
        const int MaxCandidates = 300;

        /// <summary>True when the log is known to belong to a different account (so its tail needn't be read).</summary>
        static bool SomeoneElses(FileInfo f, long userId)
        {
            long owner = OwnerOf(f);
            return owner != 0 && owner != userId;
        }

        public static bool BelongsTo(string text, long userId)
        {
            foreach (Match m in AnyUserRx.Matches(text))
                if (long.TryParse(m.Groups[1].Value, out var id) && id == userId) return true;
            return false;
        }

        // A client names its account near the start of its log (the first ~60 KB). In a long session that is far outside the tail
        // that is read for the ending, so the owner is read from the head once per file and remembered.
        const int HeadBytes = 256 * 1024;
        static readonly Dictionary<string, long> Owners = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The account a client log belongs to (0 while it hasn't named one yet).</summary>
        static long OwnerOf(FileInfo f)
        {
            lock (Owners) { if (Owners.TryGetValue(f.FullName, out var known)) return known; }
            long id = 0;
            using (var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var buf = new byte[(int)Math.Min(fs.Length, HeadBytes)];
                int n = 0, r;
                while (n < buf.Length && (r = fs.Read(buf, n, buf.Length - n)) > 0) n += r;
                var m = AnyUserRx.Match(Encoding.UTF8.GetString(buf, 0, n));
                if (m.Success) long.TryParse(m.Groups[1].Value, out id);
            }
            if (id > 0) lock (Owners) { if (Owners.Count > 256) Owners.Clear(); Owners[f.FullName] = id; }
            return id;
        }

        /// <summary>Text (tail) of the newest log that belongs to <paramref name="userId"/>, or null.</summary>
        public static string LogTextFor(long userId, DateTime? since)
        {
            try
            {
                if (userId <= 0) return null;
                foreach (var f in Candidates(since))
                {
                    if (SomeoneElses(f, userId)) continue;
                    var text = Tail(f.FullName);
                    if (OwnerOf(f) == userId || BelongsTo(text, userId)) return text;
                }
            }
            catch (Exception ex) { Logger.Write("RobloxLogs: " + ex.Message); }
            return null;
        }

        // current server
        /// <summary>
        /// The server the newest client log for <paramref name="userId"/> last joined (teleports included), or null.
        /// <paramref name="since"/> ignores logs from before that moment (stale sessions of the same account).
        /// </summary>
        public static Server CurrentServer(long userId, DateTime? since)
        {
            try
            {
                if (userId <= 0) return null;
                foreach (var f in Candidates(since))
                {
                    if (SomeoneElses(f, userId)) continue;
                    var hit = Parse(Tail(f.FullName), userId);
                    if (hit != null) return hit;
                }
            }
            catch (Exception ex) { Logger.Write("RobloxLogs: " + ex.Message); }
            return null;
        }

        /// <summary>True when a client log written since <paramref name="since"/> shows <paramref name="userId"/> joining a server (the join went through).</summary>
        public static bool HasJoinedSince(long userId, DateTime since)
        {
            try
            {
                if (userId <= 0) return false;
                foreach (var f in Candidates(null))
                {
                    if (f.LastWriteTimeUtc < since) continue;
                    if (SomeoneElses(f, userId)) continue;
                    if (Parse(Tail(f.FullName), userId) != null) return true;
                }
            }
            catch (Exception ex) { Logger.Write("RobloxLogs: " + ex.Message); }
            return false;
        }

        /// <summary>
        /// The cheap form of <see cref="HasJoinedSince"/> for watching many windows at once: only the log owned by
        /// <paramref name="userId"/> (owners are remembered, so other accounts' logs cost nothing after the first look) and
        /// created for this window is read. True when it shows the account inside a server.
        /// </summary>
        /// <param name="deep">Also look near the start of the log: in a session that has run for hours the join is far outside the tail.</param>
        public static bool InServerSince(long userId, DateTime since, bool deep = false)
        {
            try
            {
                if (userId <= 0) return false;
                foreach (var f in Candidates(null))
                {
                    // Only the creation time decides. The folder listing's "last written" time of a log that is still growing lags by
                    // seconds to minutes, so a log made a moment before the window was seen can look older than it and be skipped.
                    if (f.CreationTimeUtc < since.AddMinutes(-2)) continue;
                    if (OwnerOf(f) != userId) continue;
                    if (Parse(Tail(f.FullName), userId) != null) return true;
                    return deep && Parse(Head(f.FullName, DeepBytes), userId) != null;
                }
            }
            catch (Exception ex) { Logger.Write("RobloxLogs: " + ex.Message); }
            return false;
        }

        /// <summary>
        /// True only when the client log of the window started at <paramref name="since"/> for <paramref name="userId"/> ends in
        /// Roblox's idle-kick code (278, "no input for 20 minutes"). See <see cref="StuckOn"/>.
        /// </summary>
        public static bool IdleKickedSince(long userId, DateTime since) => StuckOn(userId, since)?.Kind == "Idle";

        /// <summary>How much of a log the regular error-screen check reads: a disconnect is logged seconds before it is looked for.</summary>
        const int StuckTailBytes = 256 * 1024;

        /// <summary>
        /// The disconnect the window started at <paramref name="since"/> for <paramref name="userId"/> is sitting on right now, or null.
        /// A disconnected Roblox window doesn't exit: it stays open on an error screen (kick, server shutdown, Roblox maintenance, lost
        /// connection, idle...), so its process looks perfectly alive. Deliberately narrow: the log must have been created for this very
        /// window (not an earlier session of the same account) and belong to the account, and the code must come after its last join
        /// (a teleport or reconnect that went on into a server is not an ending). 285, the client leaving on its own, never counts.
        /// </summary>
        /// <param name="deep">Read further back (once per window: one Lantern found already open may have been stuck for a while).</param>
        public static ExitInfo StuckOn(long userId, DateTime since, bool deep = false)
        {
            try
            {
                if (userId <= 0) return null;
                foreach (var f in Candidates(null))
                {
                    // Only the creation time decides. The folder listing's "last written" time of a log that is still growing lags by
                    // seconds to minutes, so a log made a moment before the window was seen can look older than it and be skipped.
                    if (f.CreationTimeUtc < since.AddMinutes(-2)) continue;
                    if (OwnerOf(f) != userId) continue;   // cheap for the logs of other accounts: they are remembered
                    return StuckIn(Tail(f.FullName, deep ? DeepBytes : StuckTailBytes));
                }
            }
            catch (Exception ex) { Logger.Write("RobloxLogs: " + ex.Message); }
            return null;
        }

        /// <summary>The disconnect a log ends on with no join after it (see <see cref="StuckOn"/>), or null.</summary>
        public static ExitInfo StuckIn(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int code = 0, codeAt = -1;
            foreach (Match m in DisconnectRx.Matches(text))
                if (int.TryParse(m.Groups[1].Value, out var c)) { code = c; codeAt = m.Index; }
            if (codeAt < 0 || code == 285) return null;
            var joins = JoinRx.Matches(text);
            if (joins.Count > 0 && joins[joins.Count - 1].Index > codeAt) return null;
            return Describe(KindOf(code), code);
        }

        /// <summary>Last "Joining game" in <paramref name="text"/> that belongs to <paramref name="userId"/>.</summary>
        public static Server Parse(string text, long userId)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var joins = JoinRx.Matches(text);
            for (int i = joins.Count - 1; i >= 0; i--)
            {
                var j = joins[i];
                // The load-time report follows the join line and names the user.
                var window = text.Substring(j.Index, Math.Min(2000, text.Length - j.Index));
                var u = UserRx.Match(window);
                if (!u.Success || !long.TryParse(u.Groups[1].Value, out var uid) || uid != userId) continue;
                if (!long.TryParse(j.Groups[2].Value, out var place) || place <= 0) continue;
                return new Server { PlaceId = place, JobId = j.Groups[1].Value };
            }
            return null;
        }

        // why a window ended
        /// <summary>Why the newest client log for <paramref name="userId"/> ended; null if no log could be found.</summary>
        public static ExitInfo ExitOf(long userId, DateTime? since)
        {
            var text = LogTextFor(userId, since);
            return text == null ? null : Analyze(text);
        }

        public static ExitInfo Analyze(string text)
        {
            if (string.IsNullOrEmpty(text)) return Describe("Unknown", 0);

            int code = 0, codeAt = -1;
            foreach (Match m in DisconnectRx.Matches(text))
                if (int.TryParse(m.Groups[1].Value, out var c)) { code = c; codeAt = m.Index; }

            var lost = LostRx.Matches(text);
            int lostAt = lost.Count > 0 ? lost[lost.Count - 1].Index : -1;
            bool clean = CleanRx.IsMatch(text);

            // A disconnect that logged a code is the most precise answer.
            // (285 "client left" is also logged on every teleport, so by itself it doesn't prove a clean exit.)
            if (codeAt >= 0 && codeAt >= lostAt - 2000 && (code != 285 || clean)) return Describe(KindOf(code), code);
            if (lostAt >= 0) return Describe("LostConnection", 277);
            if (clean) return Describe("Closed", 285);
            return Describe("Abrupt", 0);
        }

        static string KindOf(int code) => Disconnects.Find(code)?.Kind ?? "Code";

        public static ExitInfo Describe(string kind, int code)
        {
            var known = code > 0 ? Disconnects.Find(code) : null;
            if (known != null && known.Kind == kind && kind != "Closed") return FromCatalog(known);

            var e = new ExitInfo { Kind = kind, Code = code, Unexpected = true };
            switch (kind)
            {
                case "Closed":
                    e.Unexpected = false; e.Short = "Closed";
                    e.Title = "The window was closed normally.";
                    e.Detail = "Roblox shut down cleanly: the game was left or the window was closed."; break;
                case "Kicked":
                    e.Short = "Kicked"; e.Title = "Kicked from the game (code 267).";
                    e.Detail = "A script in the game removed this account. Games do that for moderation, anti-cheat, AFK rules or to enforce a rule. Rejoining usually works unless the game banned the account."; break;
                case "BadBehavior":
                    e.Short = "Removed by Roblox"; e.Title = "Disconnected for unexpected client behavior (code 268).";
                    e.Detail = "Roblox ended the session because the client behaved in a way it didn't expect. A normal Lantern launch doesn't touch the client; other tools, overlays or a corrupted install can cause this. Reinstalling Roblox often clears it."; break;
                case "Replaced":
                    e.Short = "Signed in elsewhere"; e.StopRejoin = true;
                    e.Title = "The same account joined from somewhere else (code 273).";
                    e.Detail = "Roblox allows one session per account per game. Another window, device or browser tab joined with this account and pushed this one out. Auto-rejoin is paused so the two don't fight."; break;
                case "ServerShutdown":
                    e.Short = "Server shut down"; e.Title = "The game server shut down (code 274).";
                    e.Detail = "The game restarted or closed that server (an update, or the server emptied). Joining again lands in a fresh server."; break;
                case "LostConnection":
                    e.Short = "Lost connection"; e.Title = "Lost connection to the server (code 277).";
                    e.Detail = "The link to the game server dropped: an unstable connection, the PC being overloaded, or the server itself crashing. If it happens often with many windows, try a calmer mode in Settings → Low-resource mode."; break;
                case "Idle":
                    e.Short = "Idle 20 min"; e.Title = "Removed for being idle (code 278).";
                    e.Detail = "Roblox disconnects a player after about 20 minutes without any input. Games with their own anti-AFK handling avoid this; Lantern doesn't send input."; break;
                case "FailedToConnect":
                    e.Short = "Couldn't connect"; e.Title = "Couldn't connect to the game server (code 279).";
                    e.Detail = "The server didn't answer the join. It may be full, restarting, or blocked by your network."; break;
                case "OutOfMemory":
                    e.Short = "Out of memory"; e.Title = "The PC ran out of memory (code 286).";
                    e.Detail = "There wasn't enough free RAM for another Roblox window. Close some windows, or use Low-resource mode, which also lowers memory use."; break;
                case "Code":
                    e.Short = "Disconnected (" + code + ")"; e.Title = "Disconnected by Roblox (code " + code + ").";
                    e.Detail = "Roblox gave disconnect code " + code + ", which Lantern doesn't have a description for. Searching that number on Roblox's support site usually explains it."; break;
                case "Updated":
                    e.Unexpected = false; e.Short = "Roblox update";
                    e.Title = "Roblox closed it to install another client build.";
                    e.Detail = "A launch needed a different Roblox build (an update, or another release channel), and Roblox's installer closes every open window while it swaps builds. Lantern waits for the installer to finish before starting anything again; this doesn't count toward giving up."; break;
                case "Abrupt":
                    e.Short = "Ended abruptly"; e.Title = "The window ended without a normal shutdown.";
                    e.Detail = "The log stops with no shutdown or disconnect message, which means the Roblox process crashed or was force-closed (Task Manager, antivirus, power loss, or Windows ending it)."; break;
                default:
                    e.Short = ""; e.Title = "Couldn't tell why the window ended.";
                    e.Detail = "No client log was found for this session, so there is nothing to read the reason from."; e.Unexpected = false; break;
            }
            return e;
        }

        static ExitInfo FromCatalog(DisconnectCode c) => new ExitInfo
        {
            Kind = c.Kind, Code = c.Code, Short = c.Short, Unexpected = true,
            Title = c.Short + " (code " + c.Code + ").",
            Detail = c.Meaning + " Roblox says: “" + c.RobloxSays + "” " + c.Handling +
                     (c.Recovery == Recovery.Rejoin && c.ServerGone ? " It goes to a fresh server of the same game (or the same private-server link)." : "") +
                     (c.Recovery == Recovery.Rejoin && c.Outage ? " Repeated tries wait longer each time (up to 10 min) and never count toward giving up." : ""),
            StopRejoin = c.Recovery == Recovery.Stop, Recovery = c.Recovery, Outage = c.Outage, ServerGone = c.ServerGone
        };

        /// <summary>How far into a log the deep join check reads: a client joins within its first minutes, long before this.</summary>
        const int DeepBytes = 8 * 1024 * 1024;

        static string Head(string path, int bytes)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var buf = new byte[(int)Math.Min(fs.Length, bytes)];
                int n = 0, r;
                while (n < buf.Length && (r = fs.Read(buf, n, buf.Length - n)) > 0) n += r;
                return Encoding.UTF8.GetString(buf, 0, n);
            }
        }

        static string Tail(string path, int bytes = TailBytes)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (fs.Length > bytes) fs.Seek(-bytes, SeekOrigin.End);
                using (var sr = new StreamReader(fs, Encoding.UTF8)) return sr.ReadToEnd();
            }
        }
    }
}
