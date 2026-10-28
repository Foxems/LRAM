using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Lantern.Core
{
    public enum Level { Ok, Info, Warn, Bad }

    public sealed class Finding
    {
        public Level Level;
        public string Title = "";
        public string Detail = "";
        public Finding(Level level, string title, string detail = "") { Level = level; Title = title; Detail = detail; }
    }

    /// <summary>
    /// Read-only checks: "is this PC set up so Lantern works?" and "are these saved sessions in good shape?".
    /// Reports never contain cookies, tickets or passwords, so they are safe to copy and share.
    /// </summary>
    public static class Health
    {
        public static string ToText(string title, IEnumerable<Finding> findings)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Lantern " + title + " — " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            sb.AppendLine("(no cookies or tickets are included in this report)");
            sb.AppendLine();
            foreach (var f in findings)
            {
                var tag = f.Level == Level.Ok ? "OK  " : f.Level == Level.Info ? "INFO" : f.Level == Level.Warn ? "WARN" : "FAIL";
                sb.AppendLine("[" + tag + "] " + f.Title);
                if (!string.IsNullOrWhiteSpace(f.Detail))
                    foreach (var line in f.Detail.Split('\n')) sb.AppendLine("       " + line.TrimEnd());
            }
            return sb.ToString();
        }

        // ================================================================== this PC

        public static async Task<List<Finding>> SystemAsync(SettingsData s, IList<AccountItem> accounts, double? measuredBytesPerWindow = null)
        {
            var r = new List<Finding>();

            // Roblox installed
            var launcher = RobloxProcesses.FindLauncher();
            if (launcher == null)
                r.Add(new Finding(Level.Bad, "Roblox isn't installed", "Install it the normal way from roblox.com. Lantern starts the official client and can't work without it."));
            else if (launcher.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0)
                r.Add(new Finding(Level.Warn, "This looks like the Microsoft Store version of Roblox", "Lantern needs the version from roblox.com. Uninstall the Store one and install the website version."));
            else
                r.Add(new Finding(Level.Ok, "Roblox is installed", Path.GetFileName(Path.GetDirectoryName(launcher) ?? "") + "  (" + launcher + ")"));

            // Lantern's own build, which accounts run from so Roblox can't swap builds and close the windows (RobloxBuild)
            var build = RobloxBuild.Current;
            if (RobloxBuild.Status.Length > 0)
                r.Add(new Finding(build == null ? Level.Warn : Level.Info, "Lantern's copy of Roblox", RobloxBuild.Status));
            else if (build != null)
                r.Add(new Finding(Level.Ok, "Accounts run from Lantern's copy of Roblox", build + ". It has no installer beside it, so a launch or rejoin can't swap Roblox builds and close the other windows. Lantern puts each new Roblox release beside it as it comes out."));
            else if (launcher != null)
                r.Add(new Finding(Level.Info, "Lantern's copy of Roblox isn't ready yet", "It's made at the first launch. Until then accounts start through Roblox's own launcher, which can close every window when it swaps builds."));

            // Roblox processes
            var scan = await Task.Run(() => RobloxProcesses.LivePids().Count == 0 ? new List<RbxProc>() : RobloxProcesses.Scan());
            int tray = scan.Count(p => p.IsTray), games = scan.Count - tray;
            if (games > 0) r.Add(new Finding(Level.Info, games + (games == 1 ? " Roblox window is running" : " Roblox windows are running")));
            else if (tray > 0) r.Add(new Finding(Level.Info, "Roblox is idling in the background (system tray)", "Harmless. Lantern closes it for you when it needs Roblox's lock to run several windows."));
            else r.Add(new Finding(Level.Ok, "No Roblox processes are running"));

            // multi-window
            if (s.MultiInstance)
                r.Add(MultiInstance.Held
                    ? new Finding(Level.Ok, "Multiple Roblox windows are enabled", "Lantern is holding Roblox's single-instance lock.")
                    : new Finding(Level.Warn, "Multiple windows are switched on but not active", "Close every Roblox window (and the Roblox tray icon), then switch the setting off and on again."));
            else
                r.Add(new Finding(Level.Info, "Multiple Roblox windows are off", "Only one account can run at a time. Turn on “Allow multiple Roblox windows” in Settings to run several."));

            // WebView2 (sign-in window)
            string wv = null;
            try { wv = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch { }
            r.Add(string.IsNullOrEmpty(wv)
                ? new Finding(Level.Warn, "Microsoft Edge WebView2 isn't installed", "The sign-in window needs it. Install the WebView2 runtime from Microsoft, or add accounts with “Paste cookies”.")
                : new Finding(Level.Ok, "Sign-in window is available", "WebView2 " + wv));

            // Roblox reachable + clock
            var net = await CheckNetworkAsync();
            r.AddRange(net);

            // data folder
            try
            {
                Paths.Ensure();
                var probe = Path.Combine(Paths.Root, ".write-test");
                File.WriteAllText(probe, "x"); File.Delete(probe);
                var acc = Paths.Accounts;
                string det = File.Exists(acc) ? "accounts.dat · " + new FileInfo(acc).Length + " bytes · encrypted with Windows (DPAPI)" : "No accounts saved yet.";
                if (Store.LoadWarning != null) r.Add(new Finding(Level.Warn, "Saved accounts needed repair", Store.LoadWarning));
                else r.Add(new Finding(Level.Ok, "Lantern's data folder is writable", det + "\n" + Paths.Root));
            }
            catch (Exception ex) { r.Add(new Finding(Level.Bad, "Lantern can't write its data folder", ex.Message + "\n" + Paths.Root)); }

            // accounts
            int expired = accounts.Count(a => a.State == AccountState.Expired);
            if (accounts.Count == 0) r.Add(new Finding(Level.Info, "No accounts added yet"));
            else if (expired > 0)
                r.Add(new Finding(Level.Warn, expired + (expired == 1 ? " account needs" : " accounts need") + " signing in again",
                    string.Join(", ", accounts.Where(a => a.State == AccountState.Expired).Select(a => a.Display))));
            else r.Add(new Finding(Level.Ok, accounts.Count + (accounts.Count == 1 ? " account" : " accounts") + " saved, none expired"));

            // memory
            Native.Memory(out var total, out var avail);
            if (total > 0)
            {
                double gb(ulong b) => b / 1024.0 / 1024 / 1024;
                bool measured = measuredBytesPerWindow.HasValue && measuredBytesPerWindow.Value > 50 * 1024 * 1024;
                int perWindow = measured ? (int)(measuredBytesPerWindow.Value / 1024 / 1024) : s.PerfMode == "Minimal" ? 700 : s.PerfMode == "Balanced" ? 950 : 1300;
                int more = (int)Math.Max(0, (avail / 1024.0 / 1024 - 1500) / perWindow);
                r.Add(new Finding(avail < 1500UL * 1024 * 1024 ? Level.Warn : Level.Ok,
                    gb(avail).ToString("0.0") + " GB of " + gb(total).ToString("0.0") + " GB memory free",
                    "Room for roughly " + more + " more Roblox " + (more == 1 ? "window" : "windows") + " (about " + perWindow + " MB each, " +
                    (measured ? "measured from your running windows" : "a typical estimate; Lantern measures the real figure once windows are running") + ")."));
            }

            // low-resource mode
            if (s.PerfMode != "Off")
            {
                if (!Perf.SettingsFileExists)
                    r.Add(new Finding(Level.Warn, "Low-resource mode can't apply yet", "Roblox hasn't created its settings file. Start Roblox once, then launch from Lantern."));
                else if (Perf.Pending)
                    r.Add(new Finding(Level.Info, "Roblox's graphics settings are currently lowered", "Lantern restores your originals when its windows close (or press “Restore Roblox's original settings”)."));
                else
                    r.Add(new Finding(Level.Ok, "Low-resource mode is ready (" + s.PerfMode + ")", Perf.Describe(s.PerfMode, s)));
            }
            else if (Perf.Pending)
                r.Add(new Finding(Level.Info, "Roblox's graphics settings are still lowered from an earlier run", "They are restored automatically once no Roblox window is open."));

            // elevation
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    if (new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator))
                        r.Add(new Finding(Level.Warn, "Lantern is running as administrator", "Roblox windows it starts would inherit that. Run Lantern normally."));
            }
            catch { }

            return r;
        }

        static async Task<List<Finding>> CheckNetworkAsync()
        {
            var r = new List<Finding>();
            try
            {
                var sw = Stopwatch.StartNew();
                var resp = await RobloxApi.SendAsync(System.Net.Http.HttpMethod.Get, "https://users.roblox.com/v1/users/1");
                sw.Stop();
                if (!resp.IsOk)
                {
                    r.Add(new Finding((int)resp.Status == 429 ? Level.Warn : Level.Bad, "Roblox answered with an error (HTTP " + (int)resp.Status + ")",
                        (int)resp.Status == 429 ? "Roblox is rate-limiting this PC right now. Wait a minute." : "Check status.roblox.com, or your VPN / firewall."));
                    return r;
                }
                r.Add(new Finding(sw.ElapsedMilliseconds > 1500 ? Level.Warn : Level.Ok, "Roblox is reachable", "Answered in " + sw.ElapsedMilliseconds + " ms."));

                var date = resp.Header("Date");
                if (date != null && DateTimeOffset.TryParse(date, out var server))
                {
                    var skew = (DateTimeOffset.UtcNow - server).Duration();
                    if (skew > TimeSpan.FromMinutes(2))
                        r.Add(new Finding(Level.Warn, "Your PC's clock is off by " + (int)skew.TotalMinutes + " minutes", "A wrong clock can make Roblox reject sign-ins. Turn on “Set time automatically” in Windows."));
                    else r.Add(new Finding(Level.Ok, "PC clock is accurate", "Within " + Math.Max(1, (int)skew.TotalSeconds) + " s of Roblox's."));
                }
            }
            catch (Exception ex)
            {
                r.Add(new Finding(Level.Bad, "Can't reach Roblox", ex.GetType().Name + ": " + ex.Message + "\nCheck your connection, VPN or firewall."));
            }
            return r;
        }

        // ================================================================== saved sessions

        static readonly string[] PlainTextSuspects = { "*.json", "*.log", "*.old", "*.xml", "*.txt", "*.bak" };

        /// <summary>
        /// Is each saved session healthy, and is anything leaking? Read-only, apart from wiping the clipboard
        /// when it still holds a cookie. Talks only to roblox.com, with each account's own session.
        /// </summary>
        public static async Task<List<Finding>> SecurityAsync(IList<AccountItem> accounts, Func<string> clipboardText, Action clearClipboard)
        {
            var r = new List<Finding>();

            // 1. nothing stored in plain text
            var leaks = new List<string>();
            try
            {
                if (Directory.Exists(Paths.Root))
                    foreach (var pattern in PlainTextSuspects)
                        foreach (var f in Directory.GetFiles(Paths.Root, pattern, SearchOption.AllDirectories))
                        {
                            if (new FileInfo(f).Length > 8 * 1024 * 1024) continue;
                            if (File.ReadAllText(f).IndexOf("_|WARNING:-DO-NOT-SHARE-THIS", StringComparison.Ordinal) >= 0) leaks.Add(Path.GetFileName(f));
                        }
            }
            catch (Exception ex) { Logger.Write("Health plaintext scan: " + ex.Message); }
            r.Add(leaks.Count == 0
                ? new Finding(Level.Ok, "No cookie is stored in plain text", "Sessions live only in accounts.dat, encrypted with Windows (DPAPI) for your user.")
                : new Finding(Level.Bad, "A cookie was found in plain text", "In: " + string.Join(", ", leaks.Distinct()) + "\nDelete those files (Settings → Open folder)."));

            // 2. clipboard
            string clip = "";
            try { clip = clipboardText?.Invoke() ?? ""; } catch { }
            bool onClipboard = clip.IndexOf("_|WARNING:-DO-NOT-SHARE-THIS", StringComparison.Ordinal) >= 0 ||
                               accounts.Any(a => a.Data.Cookie.Length > 80 && clip.Contains(a.Data.Cookie));
            if (onClipboard)
            {
                try { clearClipboard?.Invoke(); } catch { }
                r.Add(new Finding(Level.Warn, "A Roblox cookie was sitting on your clipboard", "Any program can read the clipboard, so Lantern cleared it just now."));
            }
            else r.Add(new Finding(Level.Ok, "Your clipboard doesn't hold a cookie"));

            // 3. duplicates
            var dup = accounts.Where(a => a.Data.Cookie.Length > 0).GroupBy(a => a.Data.Cookie).Where(g => g.Count() > 1).ToList();
            if (dup.Count > 0)
                r.Add(new Finding(Level.Warn, "Two entries share one session", string.Join("\n", dup.Select(g => string.Join(" + ", g.Select(a => a.Display))))));

            // 4. each account
            var results = new Finding[accounts.Count];
            using (var gate = new SemaphoreSlim(3))
            {
                await Task.WhenAll(accounts.Select(async (a, i) =>
                {
                    await gate.WaitAsync();
                    try { results[i] = await CheckAccountAsync(a); }
                    catch (Exception ex) { results[i] = new Finding(Level.Info, a.Display, "Couldn't check: " + ex.Message); }
                    finally { gate.Release(); }
                }));
            }
            r.AddRange(results.Where(x => x != null).OrderByDescending(x => (int)x.Level));
            return r;
        }

        static async Task<Finding> CheckAccountAsync(AccountItem a)
        {
            var issues = new List<string>(); var goods = new List<string>();
            var worst = Level.Ok;
            void Add(Level l, string text) { issues.Add("• " + text); if ((int)l > (int)worst) worst = l; }

            var cookie = a.Data.Cookie ?? "";
            string title = a.Display + (string.IsNullOrEmpty(a.Handle) ? "" : "  " + a.Handle);
            if (cookie.Length == 0) return new Finding(Level.Bad, title, "• No session is saved for this account.");

            if (!cookie.StartsWith("_|WARNING:-DO-NOT-SHARE-THIS", StringComparison.Ordinal))
                Add(Level.Info, "The saved session doesn't start with Roblox's usual warning prefix (fine if it was added from a cookie manager).");

            var age = DateTime.UtcNow - new DateTime(Math.Max(a.Data.AddedTicks, DateTime.MinValue.Ticks), DateTimeKind.Utc);
            if (a.Data.AddedTicks > 0)
            {
                if (age.TotalDays > 180) Add(Level.Warn, "This session was saved " + (int)age.TotalDays + " days ago. Sign in again to rotate it.");
                else if (age.TotalDays > 90) Add(Level.Info, "This session is " + (int)age.TotalDays + " days old; signing in again every few months is good practice.");
                else goods.Add("saved " + Math.Max(0, (int)age.TotalDays) + " days ago");
            }

            var (status, user) = await RobloxApi.GetUserAsync(cookie);
            if (status == AuthStatus.Expired) { Add(Level.Bad, "The session has expired or was signed out. Sign in again."); return Done(); }
            if (status == AuthStatus.Network) { Add(Level.Info, "Couldn't reach Roblox to check the session."); return Done(); }
            goods.Insert(0, "session valid");

            var two = await RobloxApi.GetTwoStepEnabledAsync(cookie, user.Id);
            if (two == false) Add(Level.Warn, "2-step verification is off. Turn it on in Roblox's security settings; it protects the account if its password leaks.");
            else if (two == true) goods.Add("2-step verification on");

            var mail = await RobloxApi.GetEmailVerifiedAsync(cookie);
            if (mail == false) Add(Level.Warn, "No verified e-mail address, so the account can't be recovered if it's compromised.");
            else if (mail == true) goods.Add("e-mail verified");

            return Done();

            Finding Done() => new Finding(worst, title, issues.Count > 0 ? string.Join("\n", issues) : string.Join(" · ", goods));
        }

    }
}
