using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Lantern.Core
{
    public enum AuthStatus { Ok, Expired, Network }

    public class UserInfo
    {
        public long Id;
        public string Name = "";
        public string DisplayName = "";
    }

    public class GameInfo
    {
        public long UniverseId;
        public string Name = "";
        public string IconUrl = "";
    }

    public sealed class ApiResponse
    {
        public HttpStatusCode Status;
        public string Body = "";
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
        public bool IsOk => (int)Status >= 200 && (int)Status < 300;
    }

    /// <summary>
    /// Thin Roblox web client. The .ROBLOSECURITY cookie is attached only to https://*.roblox.com
    /// requests; <see cref="SendAsync"/> refuses (and never follows a redirect) anywhere else.
    /// </summary>
    public static class RobloxApi
    {
        const string Referer = "https://www.roblox.com/";
        static readonly HttpClient Http;
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        static RobloxApi()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072 | (SecurityProtocolType)12288; } catch { }
            ServicePointManager.DefaultConnectionLimit = 16;
            var handler = new HttpClientHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseProxy = true
            };
            Http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
            Http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/html;q=0.8, */*;q=0.5");
        }

        public static bool IsRobloxHost(Uri u) =>
            u.Scheme == Uri.UriSchemeHttps && (u.Host == "roblox.com" || u.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase));

        static bool IsCdnHost(Uri u) =>
            u.Scheme == Uri.UriSchemeHttps && (IsRobloxHost(u) || u.Host.EndsWith(".rbxcdn.com", StringComparison.OrdinalIgnoreCase));

        public static async Task<ApiResponse> SendAsync(HttpMethod method, string url, string cookie = null,
            string csrf = null, string json = null, string referer = null)
        {
            var uri = new Uri(url);
            if (cookie != null && !IsRobloxHost(uri))
                throw new InvalidOperationException("Refusing to send credentials to a non-Roblox host.");
            if (cookie != null && !InstanceManager.IsPlausibleCookie(cookie))
                throw new InvalidOperationException("Refusing to send a malformed cookie.");

            for (int hop = 0; hop < 5; hop++)
            {
                using (var req = new HttpRequestMessage(method, uri))
                {
                    if (cookie != null) req.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
                    if (csrf != null) req.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
                    req.Headers.TryAddWithoutValidation("Referer", referer ?? Referer);
                    if (method == HttpMethod.Post)
                    {
                        // Roblox answers a POST without a JSON content type with HTTP 415 (after the CSRF
                        // handshake has already passed), so always declare one - an empty object when there's no payload.
                        req.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");
                        req.Content = new StringContent(json ?? "{}", System.Text.Encoding.UTF8, "application/json");
                    }

                    using (var resp = await Http.SendAsync(req).ConfigureAwait(false))
                    {
                        int code = (int)resp.StatusCode;
                        if ((code == 301 || code == 302 || code == 303 || code == 307 || code == 308) && resp.Headers.Location != null)
                        {
                            var next = new Uri(uri, resp.Headers.Location);
                            if (cookie != null && !IsRobloxHost(next)) return Wrap(resp, "");
                            uri = next;
                            method = HttpMethod.Get;
                            continue;
                        }
                        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        return Wrap(resp, body);
                    }
                }
            }
            return new ApiResponse { Status = HttpStatusCode.BadGateway };
        }

        static ApiResponse Wrap(HttpResponseMessage resp, string body)
        {
            var r = new ApiResponse { Status = resp.StatusCode, Body = body ?? "" };
            foreach (var h in resp.Headers) r.Headers[h.Key] = string.Join(",", h.Value);
            return r;
        }

        // JSON helpers
        static object Parse(string body)
        {
            try { return Json.DeserializeObject(body); } catch { return null; }
        }

        static object Dig(object o, params object[] path)
        {
            foreach (var p in path)
            {
                if (o == null) return null;
                if (p is string key && o is IDictionary<string, object> d) o = d.TryGetValue(key, out var v) ? v : null;
                else if (p is int idx && o is object[] arr) o = idx < arr.Length ? arr[idx] : null;
                else return null;
            }
            return o;
        }

        static long ToLong(object o)
        {
            try { return o == null ? 0 : Convert.ToInt64(o); } catch { return 0; }
        }

        // Account
        public static async Task<(AuthStatus status, UserInfo user)> GetUserAsync(string cookie)
        {
            // A missing or damaged saved cookie can't be sent (SendAsync refuses it), which would otherwise read as "no connection".
            if (!InstanceManager.IsPlausibleCookie(cookie)) return (AuthStatus.Expired, null);
            try
            {
                var r = await SendAsync(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated", cookie).ConfigureAwait(false);
                if (r.Status == HttpStatusCode.Unauthorized) return (AuthStatus.Expired, null);
                if (!r.IsOk) return (AuthStatus.Network, null);
                var o = Parse(r.Body);
                long id = ToLong(Dig(o, "id"));
                if (id <= 0) return (AuthStatus.Expired, null);
                return (AuthStatus.Ok, new UserInfo
                {
                    Id = id,
                    Name = Dig(o, "name") as string ?? "",
                    DisplayName = Dig(o, "displayName") as string ?? ""
                });
            }
            catch (Exception ex)
            {
                Logger.Write("GetUser: " + ex.GetType().Name + " " + ex.Message);
                return (AuthStatus.Network, null);
            }
        }

        public static async Task<string> GetCsrfAsync(string cookie)
        {
            var r = await SendAsync(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket", cookie).ConfigureAwait(false);
            return r.Status == HttpStatusCode.Forbidden ? r.Header("x-csrf-token") : null;
        }

        public sealed class TicketResult
        {
            public string Ticket;
            public bool Expired;
            public string Error;
            /// <summary>HTTP status of the call that failed (0 = none).</summary>
            public int Http;
        }

        /// <summary>
        /// The launch ticket ends up on a command line, so it has to be a single plain token: printable ASCII, no spaces, no quotes or
        /// backslashes (which a command line would read as the end of the argument or an escape).
        /// </summary>
        public static bool IsPlausibleTicket(string ticket)
        {
            if (string.IsNullOrEmpty(ticket) || ticket.Length > 4096) return false;
            foreach (var c in ticket)
                if (c < 0x21 || c > 0x7E || c == '"' || c == '\\') return false;
            return true;
        }

        /// <summary>Single-use, ~1 minute lifetime. Redeemed by the Roblox client at launch.</summary>
        public static async Task<TicketResult> GetAuthTicketAsync(string cookie)
        {
            if (!InstanceManager.IsPlausibleCookie(cookie)) return new TicketResult { Expired = true };
            try
            {
                var first = await SendAsync(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket", cookie).ConfigureAwait(false);
                if (first.Status == HttpStatusCode.Unauthorized) return new TicketResult { Expired = true };
                var token = first.Status == HttpStatusCode.Forbidden ? first.Header("x-csrf-token") : null;
                if (token == null) return new TicketResult { Http = (int)first.Status, Error = "Roblox didn't accept the session (HTTP " + (int)first.Status + ")." };

                var second = await SendAsync(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket", cookie, csrf: token).ConfigureAwait(false);
                if (second.Status == HttpStatusCode.Unauthorized) return new TicketResult { Expired = true };
                var ticket = second.Header("rbx-authentication-ticket");
                if (string.IsNullOrEmpty(ticket)) return new TicketResult { Http = (int)second.Status, Error = "Roblox didn't issue a launch ticket (HTTP " + (int)second.Status + ")." };
                if (!IsPlausibleTicket(ticket)) return new TicketResult { Http = (int)second.Status, Error = "Roblox sent a launch ticket in a format Lantern doesn't accept." };
                return new TicketResult { Ticket = ticket };
            }
            catch (Exception ex)
            {
                Logger.Write("GetAuthTicket: " + ex.GetType().Name + " " + ex.Message);
                return new TicketResult { Error = "Couldn't reach Roblox. Check your connection." };
            }
        }

        // Private servers
        /// <summary>Resolves a roblox.com/share?code=...&amp;type=Server link to a place + link code.</summary>
        public static async Task<(long placeId, string linkCode)> ResolveShareLinkAsync(string cookie, string code)
        {
            try
            {
                var csrf = await GetCsrfAsync(cookie).ConfigureAwait(false);
                var body = Json.Serialize(new Dictionary<string, object> { { "linkId", code }, { "linkType", "Server" } });
                var r = await SendAsync(HttpMethod.Post, "https://apis.roblox.com/sharelinks/v1/resolve-link", cookie, csrf, body).ConfigureAwait(false);
                if (!r.IsOk) return (0, null);
                var o = Parse(r.Body);
                var data = Dig(o, "privateServerInviteData");
                return (ToLong(Dig(data, "placeId")), Dig(data, "linkCode") as string);
            }
            catch (Exception ex) { Logger.Write("ResolveShare: " + ex.Message); return (0, null); }
        }

        static readonly Regex AccessCodeRx = new Regex(@"joinPrivateGame\(\s*\d+\s*,\s*'([0-9a-fA-F-]{36})'", RegexOptions.Compiled);

        public static async Task<string> GetPrivateAccessCodeAsync(string cookie, long placeId, string linkCode)
        {
            try
            {
                var url = "https://www.roblox.com/games/" + placeId + "?privateServerLinkCode=" + Uri.EscapeDataString(linkCode);
                var r = await SendAsync(HttpMethod.Get, url, cookie, referer: "https://www.roblox.com/games/" + placeId).ConfigureAwait(false);
                var m = AccessCodeRx.Match(r.Body ?? "");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch (Exception ex) { Logger.Write("AccessCode: " + ex.Message); return null; }
        }

        // Presence
        public sealed class PresenceInfo
        {
            /// <summary>0 offline, 1 online, 2 in a game, 3 in Studio.</summary>
            public int Type;
            public string LastLocation = "";
            public long PlaceId;
            public string JobId = "";
            /// <summary>False when the request itself failed (so "offline" can't be claimed).</summary>
            public bool Ok;
        }

        /// <summary>
        /// Where the account says it is playing right now (null if offline, hidden, or Roblox withholds the
        /// server id). Uses the account's own session, so it only ever talks to presence.roblox.com.
        /// </summary>
        public static async Task<PresenceInfo> GetPresenceAsync(string cookie, long userId)
        {
            var p = await GetPresenceOfAsync(cookie, userId).ConfigureAwait(false);
            return p != null && p.Ok && !string.IsNullOrEmpty(p.JobId) && p.PlaceId > 0 ? p : null;
        }

        /// <summary>
        /// Presence of <paramref name="userId"/> as seen by the account behind <paramref name="cookie"/>. Roblox only reveals the
        /// server id (JobId) when the viewer may join that player (friends / joins on); otherwise it is empty.
        /// Null on a network failure.
        /// </summary>
        public static async Task<PresenceInfo> GetPresenceOfAsync(string cookie, long userId)
        {
            try
            {
                var csrf = await GetCsrfAsync(cookie).ConfigureAwait(false);
                if (csrf == null) return new PresenceInfo { Ok = false };   // the session isn't accepted: never present an anonymous answer as this account's view
                var body = Json.Serialize(new Dictionary<string, object> { { "userIds", new[] { userId } } });
                var r = await SendAsync(HttpMethod.Post, "https://presence.roblox.com/v1/presence/users", cookie, csrf, body).ConfigureAwait(false);
                if (!r.IsOk) return new PresenceInfo { Ok = false };
                var p = Dig(Parse(r.Body), "userPresences", 0);
                if (p == null) return new PresenceInfo { Ok = false };
                return new PresenceInfo
                {
                    Ok = true,
                    Type = (int)ToLong(Dig(p, "userPresenceType")),
                    LastLocation = Dig(p, "lastLocation") as string ?? "",
                    PlaceId = ToLong(Dig(p, "placeId")),
                    JobId = Dig(p, "gameId") as string ?? ""
                };
            }
            catch (Exception ex) { Logger.Write("Presence: " + ex.GetType().Name + " " + ex.Message); return null; }
        }

        /// <summary>
        /// Presence of several users in one request, as seen by the account behind <paramref name="cookie"/> (the same call the
        /// website's friends list makes). Users Roblox didn't answer for are missing; null on a failed request.
        /// </summary>
        public static async Task<Dictionary<long, PresenceInfo>> GetPresencesAsync(string cookie, IList<long> userIds)
        {
            try
            {
                if (userIds == null || userIds.Count == 0) return new Dictionary<long, PresenceInfo>();
                var csrf = await GetCsrfAsync(cookie).ConfigureAwait(false);
                if (csrf == null) return null;
                var body = Json.Serialize(new Dictionary<string, object> { { "userIds", userIds.Take(100).ToArray() } });
                var r = await SendAsync(HttpMethod.Post, "https://presence.roblox.com/v1/presence/users", cookie, csrf, body).ConfigureAwait(false);
                if (!r.IsOk) return null;
                var result = new Dictionary<long, PresenceInfo>();
                if (Dig(Parse(r.Body), "userPresences") is object[] list)
                    foreach (var p in list)
                    {
                        long id = ToLong(Dig(p, "userId"));
                        if (id <= 0) continue;
                        result[id] = new PresenceInfo
                        {
                            Ok = true,
                            Type = (int)ToLong(Dig(p, "userPresenceType")),
                            LastLocation = Dig(p, "lastLocation") as string ?? "",
                            PlaceId = ToLong(Dig(p, "placeId")),
                            JobId = Dig(p, "gameId") as string ?? ""
                        };
                    }
                return result;
            }
            catch (Exception ex) { Logger.Write("Presences: " + ex.GetType().Name + " " + ex.Message); return null; }
        }

        // Users + account security
        public sealed class FoundUser
        {
            public long Id;
            public string Name = "";
            public string DisplayName = "";
        }

        /// <summary>Looks a username up (public data; no account needed). Null = not found or couldn't reach Roblox; see <paramref name="networkError"/>.</summary>
        public static async Task<FoundUser> ResolveUsernameAsync(string username, Action<bool> networkError = null)
        {
            try
            {
                var body = Json.Serialize(new Dictionary<string, object> { { "usernames", new[] { username } }, { "excludeBannedUsers", true } });
                const string url = "https://users.roblox.com/v1/usernames/users";
                var r = await SendAsync(HttpMethod.Post, url, null, null, body).ConfigureAwait(false);
                if (r.Status == HttpStatusCode.Forbidden && r.Header("x-csrf-token") != null)
                    r = await SendAsync(HttpMethod.Post, url, null, r.Header("x-csrf-token"), body).ConfigureAwait(false);
                if (!r.IsOk) { networkError?.Invoke(true); return null; }
                var first = Dig(Parse(r.Body), "data", 0);
                long id = ToLong(Dig(first, "id"));
                if (id <= 0) return null;
                return new FoundUser { Id = id, Name = Dig(first, "name") as string ?? username, DisplayName = Dig(first, "displayName") as string ?? "" };
            }
            catch (Exception ex) { Logger.Write("ResolveUsername: " + ex.GetType().Name + " " + ex.Message); networkError?.Invoke(true); return null; }
        }

        /// <summary>Is 2-step verification on for this account? Null if Roblox didn't say.</summary>
        public static async Task<bool?> GetTwoStepEnabledAsync(string cookie, long userId)
        {
            try
            {
                var r = await SendAsync(HttpMethod.Get, "https://twostepverification.roblox.com/v1/users/" + userId + "/configuration", cookie).ConfigureAwait(false);
                if (!r.IsOk) return null;
                if (!(Dig(Parse(r.Body), "methods") is object[] methods)) return null;
                foreach (var m in methods) if (Dig(m, "enabled") is bool b && b) return true;
                return false;
            }
            catch (Exception ex) { Logger.Write("TwoStep: " + ex.Message); return null; }
        }

        /// <summary>Does the account have a verified e-mail address? Null if Roblox didn't say.</summary>
        public static async Task<bool?> GetEmailVerifiedAsync(string cookie)
        {
            try
            {
                var r = await SendAsync(HttpMethod.Get, "https://accountsettings.roblox.com/v1/email", cookie).ConfigureAwait(false);
                if (!r.IsOk) return null;
                var v = Dig(Parse(r.Body), "verified");
                return v is bool b ? b : (bool?)null;
            }
            catch (Exception ex) { Logger.Write("Email: " + ex.Message); return null; }
        }


        // Public info
        public static async Task<Dictionary<long, string>> GetHeadshotUrlsAsync(IEnumerable<long> userIds)
        {
            var map = new Dictionary<long, string>();
            var ids = userIds.Distinct().Take(100).ToList();
            if (ids.Count == 0) return map;
            try
            {
                var url = "https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds=" + string.Join(",", ids) +
                          "&size=150x150&format=Png&isCircular=false";
                var r = await SendAsync(HttpMethod.Get, url).ConfigureAwait(false);
                if (!r.IsOk) return map;
                if (Dig(Parse(r.Body), "data") is object[] arr)
                    foreach (var item in arr)
                    {
                        var u = Dig(item, "imageUrl") as string;
                        long id = ToLong(Dig(item, "targetId"));
                        if (id > 0 && !string.IsNullOrEmpty(u)) map[id] = u;
                    }
            }
            catch (Exception ex) { Logger.Write("Headshots: " + ex.Message); }
            return map;
        }

        public static async Task<byte[]> DownloadImageAsync(string url)
        {
            try
            {
                var uri = new Uri(url);
                if (!IsCdnHost(uri)) return null;
                using (var req = new HttpRequestMessage(HttpMethod.Get, uri))
                using (var resp = await Http.SendAsync(req).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return null;
                    var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    return bytes.Length > 0 && bytes.Length < 2 * 1024 * 1024 ? bytes : null;
                }
            }
            catch { return null; }
        }

        public static async Task<GameInfo> GetGameInfoAsync(long placeId)
        {
            try
            {
                var u = await SendAsync(HttpMethod.Get, "https://apis.roblox.com/universes/v1/places/" + placeId + "/universe").ConfigureAwait(false);
                long universe = u.IsOk ? ToLong(Dig(Parse(u.Body), "universeId")) : 0;
                if (universe <= 0) return null;

                var info = new GameInfo { UniverseId = universe };
                var g = await SendAsync(HttpMethod.Get, "https://games.roblox.com/v1/games?universeIds=" + universe).ConfigureAwait(false);
                if (g.IsOk) info.Name = Dig(Parse(g.Body), "data", 0, "name") as string ?? "";

                var i = await SendAsync(HttpMethod.Get,
                    "https://thumbnails.roblox.com/v1/games/icons?universeIds=" + universe + "&returnPolicy=PlaceHolder&size=128x128&format=Png&isCircular=false").ConfigureAwait(false);
                if (i.IsOk) info.IconUrl = Dig(Parse(i.Body), "data", 0, "imageUrl") as string ?? "";
                return info;
            }
            catch (Exception ex) { Logger.Write("GameInfo: " + ex.Message); return null; }
        }
    }
}
