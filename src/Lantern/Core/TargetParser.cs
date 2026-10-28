using System;
using System.Text.RegularExpressions;

namespace Lantern.Core
{
    public sealed class ParsedTarget
    {
        public LaunchTarget Target = new LaunchTarget();
        public bool IsEmpty;
        public string Error = "";
        public bool Ok => !IsEmpty && string.IsNullOrEmpty(Error) && Target.IsValid;

        /// <summary>Short human description shown under the input.</summary>
        public string Summary
        {
            get
            {
                if (IsEmpty) return "";
                if (!string.IsNullOrEmpty(Error)) return Error;
                if (!Target.IsValid) return "Not recognised";
                var kind = !string.IsNullOrEmpty(Target.JobId) ? "specific server"
                         : Target.IsPrivate ? (Target.Backup != null ? "private server + backup" : "private server") : "public server";
                return Target.PlaceId > 0 ? "Place " + Target.PlaceId + " · " + kind : "Private server link";
            }
        }
    }

    /// <summary>Turns a launch target back into a link anyone can paste into Lantern (or open on roblox.com).</summary>
    public static class ServerLink
    {
        public static string Build(LaunchTarget t)
        {
            if (t == null) return "";
            if (!string.IsNullOrEmpty(t.ShareCode)) return "https://www.roblox.com/share?code=" + t.ShareCode + "&type=Server";
            if (!string.IsNullOrEmpty(t.LinkCode) && t.PlaceId > 0) return "https://www.roblox.com/games/" + t.PlaceId + "?privateServerLinkCode=" + t.LinkCode;
            if (t.PlaceId <= 0) return "";
            if (!string.IsNullOrEmpty(t.JobId)) return "https://www.roblox.com/games/start?placeId=" + t.PlaceId + "&gameInstanceId=" + t.JobId;
            return "https://www.roblox.com/games/" + t.PlaceId;
        }

        /// <summary>
        /// Identifies the private server a target points at (empty for a public one). A share link is known by its share code alone:
        /// launching resolves it and adds the link code to the same target, and that must not turn it into a different server.
        /// </summary>
        public static string Key(LaunchTarget t) =>
            t == null ? "" : !string.IsNullOrEmpty(t.ShareCode) ? "share:" + t.ShareCode : !string.IsNullOrEmpty(t.LinkCode) ? "link:" + t.LinkCode : "";

        public static bool Same(LaunchTarget a, LaunchTarget b) => Key(a).Length > 0 && Key(a) == Key(b);

        /// <summary>Does this text look like a Roblox game / server link worth loading into the launch bar?</summary>
        public static bool LooksLikeLink(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 600 || text.IndexOf('\n') >= 0) return false;
            var t = text.Trim();
            if (t.StartsWith("roblox://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("roblox-player:", StringComparison.OrdinalIgnoreCase)) return t.IndexOf("placeId=", StringComparison.OrdinalIgnoreCase) >= 0;
            return Regex.IsMatch(t, @"^https?://(www\.)?roblox\.com/(games/|share\?)", RegexOptions.IgnoreCase);
        }
    }

    /// <summary>Accepts a bare place id, a roblox.com/games link, a private-server link or a share link.</summary>
    public static class TargetParser
    {
        static readonly Regex Digits = new Regex(@"^\d{3,}$", RegexOptions.Compiled);
        static readonly Regex GamesPath = new Regex(@"/games/(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex PlaceIdQs = new Regex(@"[?&]placeId=(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex LinkCode = new Regex(@"(?:privateServerLinkCode|linkCode)=([\w-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex ShareCode = new Regex(@"[?&]code=([\w-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex JobInUrl = new Regex(@"(?:gameInstanceId|jobId)=([0-9a-fA-F-]{36})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex Guid36 = new Regex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.Compiled);

        public static ParsedTarget Parse(string raw, string jobId)
        {
            var p = new ParsedTarget();
            raw = (raw ?? "").Trim();
            jobId = (jobId ?? "").Trim();

            if (raw.Length == 0) { p.IsEmpty = true; return p; }

            if (Digits.IsMatch(raw))
            {
                if (!long.TryParse(raw, out long id)) { p.Error = "That place id is too long"; return p; }
                p.Target.PlaceId = id;
            }
            else
            {
                Match m;
                if ((m = GamesPath.Match(raw)).Success || (m = PlaceIdQs.Match(raw)).Success)
                {
                    if (long.TryParse(m.Groups[1].Value, out long place)) p.Target.PlaceId = place;
                }

                if ((m = LinkCode.Match(raw)).Success) p.Target.LinkCode = m.Groups[1].Value;
                else if (raw.IndexOf("share", StringComparison.OrdinalIgnoreCase) >= 0 && (m = ShareCode.Match(raw)).Success)
                    p.Target.ShareCode = m.Groups[1].Value;

                if (jobId.Length == 0 && (m = JobInUrl.Match(raw)).Success) jobId = m.Groups[1].Value;
            }

            if (jobId.Length > 0)
            {
                if (!Guid36.IsMatch(jobId)) { p.Error = "Job id should look like 8f1c…-…-…-…-…"; return p; }
                if (p.Target.IsPrivate) { p.Error = "A job id can't be combined with a private server link"; return p; }
                p.Target.JobId = jobId;
            }

            if (!p.Target.IsValid) p.Error = "Paste a place id or a Roblox game link";
            return p;
        }

        /// <summary>Parses the launch bar including the optional backup private server.</summary>
        public static ParsedTarget Parse(string raw, string jobId, string backupRaw)
        {
            var p = Parse(raw, jobId);
            backupRaw = (backupRaw ?? "").Trim();
            if (backupRaw.Length == 0 || p.IsEmpty || !string.IsNullOrEmpty(p.Error)) return p;

            if (!p.Target.IsPrivate) { p.Error = "A backup server only works with a private server link"; return p; }
            var b = Parse(backupRaw, "");
            if (!b.Ok || !b.Target.IsPrivate) { p.Error = "The backup must be a private server link"; return p; }
            if (p.Target.PlaceId > 0 && b.Target.PlaceId > 0 && p.Target.PlaceId != b.Target.PlaceId) { p.Error = "The backup server is for a different game"; return p; }
            if (ServerLink.Same(p.Target, b.Target)) { p.Error = "The backup is the same server as the first one"; return p; }
            p.Target.Backup = b.Target;
            return p;
        }
    }
}
