using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using Lantern.Core;

/// <summary>
/// Checks for Lantern's core: link parsing, cookie handling, encrypted storage, the rule that cookies only go to roblox.com,
/// the multi-window lock and the launch URI. Needs no Roblox account: anything that would need a real session is covered up to
/// the point where Roblox itself would answer. <c>--offline</c> skips the few checks that talk to Roblox.
/// </summary>
static class Program
{
    static int pass, fail, skip;
    static bool holdOk;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { pass++; Console.WriteLine("  PASS  " + name); }
        else { fail++; Console.WriteLine("  FAIL  " + name + (detail.Length > 0 ? "  -> " + detail : "")); }
    }

    static void Skip(string name, string why) { skip++; Console.WriteLine("  SKIP  " + name + "  (" + why + ")"); }

    [STAThread]
    static int Main(string[] args)
    {
        var home = Path.Combine(Path.GetTempPath(), "Lantern-selftest-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("LANTERN_HOME", home);
        Console.WriteLine("data dir: " + Paths.Root);
        Check("LANTERN_HOME override respected", Paths.Root == home);

        Console.WriteLine("\n[TargetParser]");
        var t = TargetParser.Parse("920587237", "");
        Check("bare place id", t.Ok && t.Target.PlaceId == 920587237 && !t.Target.IsPrivate);
        t = TargetParser.Parse("https://www.roblox.com/games/920587237/Adopt-Me", "");
        Check("game url", t.Ok && t.Target.PlaceId == 920587237);
        t = TargetParser.Parse("https://www.roblox.com/games/4924922222/Brookhaven?privateServerLinkCode=12345678901234567890", "");
        Check("private server link", t.Ok && t.Target.PlaceId == 4924922222 && t.Target.LinkCode == "12345678901234567890" && t.Target.IsPrivate);
        t = TargetParser.Parse("https://www.roblox.com/share?code=abcdef0123456789&type=Server", "");
        Check("share link", t.Ok && t.Target.ShareCode == "abcdef0123456789" && t.Target.PlaceId == 0);
        t = TargetParser.Parse("920587237", "8f1c2d3e-1111-2222-3333-444455556666");
        Check("job id accepted", t.Ok && t.Target.JobId == "8f1c2d3e-1111-2222-3333-444455556666");
        t = TargetParser.Parse("920587237", "nonsense");
        Check("bad job id rejected", !t.Ok && t.Error.Length > 0);
        t = TargetParser.Parse("https://www.roblox.com/games/1234/x?privateServerLinkCode=abc", "8f1c2d3e-1111-2222-3333-444455556666");
        Check("job + private rejected", !t.Ok);
        t = TargetParser.Parse("hello world", "");
        Check("garbage rejected", !t.Ok);
        t = TargetParser.Parse("", "");
        Check("empty is empty", t.IsEmpty && !t.Ok);

        Console.WriteLine("\n[Backup private server]");
        const string vip1 = "https://www.roblox.com/games/4924922222/Brookhaven?privateServerLinkCode=11111111111111111111";
        const string vip2 = "https://www.roblox.com/games/4924922222/Brookhaven?privateServerLinkCode=22222222222222222222";
        t = TargetParser.Parse(vip1, "", vip2);
        Check("backup attached", t.Ok && t.Target.Backup != null && t.Target.Backup.LinkCode == "22222222222222222222" && t.Target.Backup.Backup == null);
        t = TargetParser.Parse(vip1, "", "  ");
        Check("blank backup ignored", t.Ok && t.Target.Backup == null);
        t = TargetParser.Parse("4924922222", "", vip2);
        Check("backup needs a private first server", !t.Ok && t.Error.Length > 0);
        t = TargetParser.Parse(vip1, "", "4924922222");
        Check("backup must be private", !t.Ok);
        t = TargetParser.Parse(vip1, "", "https://www.roblox.com/games/920587237/Adopt-Me?privateServerLinkCode=33333333333333333333");
        Check("backup for another game rejected", !t.Ok);
        t = TargetParser.Parse(vip1, "", vip1);
        Check("same server twice rejected", !t.Ok);
        t = TargetParser.Parse("https://www.roblox.com/share?code=abcdef0123456789&type=Server", "", vip2);
        Check("share link + link backup", t.Ok && t.Target.Backup != null);

        Console.WriteLine("\n[Cookie sanitising]");
        var raw = "_|WARNING:-DO-NOT-SHARE-THIS." + new string('A', 120);
        Check("plain", InstanceManager.SanitizeCookie(raw) == raw);
        Check("quoted + prefix + semicolon", InstanceManager.SanitizeCookie("\".ROBLOSECURITY=" + raw + "; path=/\"") == raw);

        Console.WriteLine("\n[Cookie extraction]");
        string ck(char c) => "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_" + new string(c, 90);
        var many = InstanceManager.ExtractCookies("account1: " + ck('A') + "\r\n\r\n" + ck('B') + "; path=/\n" + ck('A'));
        Check("cookie after other fields, plain lines, duplicates", many.Count == 2 && many[0] == ck('A') && many[1] == ck('B'), many.Count.ToString());
        Check("cookie-jar style (.ROBLOSECURITY=...)", InstanceManager.ExtractCookies(".ROBLOSECURITY=" + ck('Q') + "; Path=/; Secure").Count == 1);
        Check("bare token without the warning prefix", InstanceManager.ExtractCookies(new string('x', 150)).Count == 1);
        Check("ordinary text yields nothing", InstanceManager.ExtractCookies("hello\nthis is not a cookie").Count == 0);
        Check("empty yields nothing", InstanceManager.ExtractCookies("").Count == 0 && InstanceManager.ExtractCookies(null).Count == 0);

        Console.WriteLine("\n[Cookie shape]");
        Check("a real-looking cookie passes", InstanceManager.IsPlausibleCookie(ck('A')));
        Check("too short is refused", !InstanceManager.IsPlausibleCookie("_|WARNING:-DO-NOT-SHARE-THIS.abc"));
        Check("a line break is refused (no header smuggling)", !InstanceManager.IsPlausibleCookie(ck('A') + "\r\nX-Evil: 1"));
        Check("a second cookie is refused", !InstanceManager.IsPlausibleCookie(ck('A') + "; other=1"));
        Check("non-ASCII is refused", !InstanceManager.IsPlausibleCookie(ck('A') + "\u00e9"));
        Check("absurdly long is refused", !InstanceManager.IsPlausibleCookie(new string('A', 5000)));

        Console.WriteLine("\n[Log scrubbing]");
        Check("a cookie in a message is removed", !Logger.Scrub("failed for " + ck('A') + " again").Contains("DO-NOT-SHARE"));
        Check("a .ROBLOSECURITY header is removed", !Logger.Scrub("Cookie: .ROBLOSECURITY=abc123def456 ok").Contains("abc123"));
        Check("ordinary text is left alone", Logger.Scrub("GetUser: HttpRequestException") == "GetUser: HttpRequestException");

        Console.WriteLine("\n[Encrypted storage]");
        var acc = new AccountData { UserId = 42, Username = "tester", Cookie = "SECRET_COOKIE_VALUE_" + new string('Z', 100) };
        Store.SaveAccounts(new[] { acc });
        var bytes = File.ReadAllBytes(Paths.Accounts);
        Check("file written", bytes.Length > 0);
        Check("cookie not readable in file",
            Encoding.UTF8.GetString(bytes).IndexOf("SECRET_COOKIE", StringComparison.Ordinal) < 0 &&
            Encoding.Unicode.GetString(bytes).IndexOf("SECRET", StringComparison.Ordinal) < 0);
        Check("username not readable in file", Encoding.UTF8.GetString(bytes).IndexOf("tester", StringComparison.Ordinal) < 0);
        var back = Store.LoadAccounts();
        Check("round trip", back.Count == 1 && back[0].Cookie == acc.Cookie && back[0].UserId == 42);
        // the remembered destination (with a backup private server) must load back, or the whole account file would be set aside
        var withTarget = new AccountData { UserId = 43, Username = "t2", Cookie = acc.Cookie,
            LastTarget = new LaunchTarget { PlaceId = 7, LinkCode = "LC", GameName = "Pets", Backup = new LaunchTarget { PlaceId = 7, ShareCode = "SC" } } };
        Store.SaveAccounts(new[] { acc, withTarget });
        back = Store.LoadAccounts();
        var lt = back.FirstOrDefault(a => a.UserId == 43)?.LastTarget;
        Check("remembered destination survives a restart", Store.LoadWarning == null && back.Count == 2 && lt != null && lt.PlaceId == 7 && lt.LinkCode == "LC" && lt.IsPrivate && lt.Backup?.ShareCode == "SC" && back[0].LastTarget == null);
        Store.SaveAccounts(new[] { acc });
        Store.SaveAccounts(new[] { acc, new AccountData { Username = "second", Cookie = "x" } }); // produces a .bak of the previous good file
        File.WriteAllBytes(Paths.Accounts, new byte[] { 1, 2, 3, 4 });
        back = Store.LoadAccounts();
        Check("corrupt file falls back to backup", back.Count == 1 && Store.LoadWarning != null, "count=" + back.Count);
        Store.SavePlain(Paths.Settings, new SettingsData { MultiInstance = true, StaggerSeconds = 9 });
        var s2 = Store.LoadPlain<SettingsData>(Paths.Settings);
        Check("settings round trip", s2.MultiInstance && s2.StaggerSeconds == 9);

        Console.WriteLine("\n[Credential guard]");
        foreach (var bad in new[] { "https://evil.example.com/steal", "https://roblox.com.evil.example/x", "http://www.roblox.com/" })
        {
            try
            {
                RobloxApi.SendAsync(HttpMethod.Get, bad, "cookie").GetAwaiter().GetResult();
                Check("refuses " + bad, false, "no exception");
            }
            catch (InvalidOperationException) { Check("refuses " + bad, true); }
        }
        Check("accepts users.roblox.com", RobloxApi.IsRobloxHost(new Uri("https://users.roblox.com/x")));

        Console.WriteLine("\n[POST requests]  (local listener; Roblox answers HTTP 415 to a POST without a JSON content type)");
        {
            int port = 40000 + new Random().Next(0, 20000);
            string seenType = null, seenBody = null;
            using (var listener = new System.Net.HttpListener())
            {
                listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                listener.Start();
                var serve = System.Threading.Tasks.Task.Run(() =>
                {
                    var ctx = listener.GetContext();
                    seenType = ctx.Request.ContentType;
                    using (var rd = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) seenBody = rd.ReadToEnd();
                    ctx.Response.StatusCode = 200;
                    ctx.Response.Close();
                });
                RobloxApi.SendAsync(HttpMethod.Post, "http://127.0.0.1:" + port + "/x").GetAwaiter().GetResult();
                serve.Wait(5000);
                Check("empty POST declares application/json", seenType != null && seenType.StartsWith("application/json"), seenType ?? "null");
                Check("empty POST sends a valid JSON body", seenBody == "{}", seenBody ?? "null");
            }
        }

        Console.WriteLine("\n[Roblox client log -> current server]");
        {
            const string job1 = "a5c89b1f-81d2-47bb-8cff-da6c308e6e47", job2 = "0aedcdfd-2905-42a7-bb00-c00becfed559";
            string log =
                "2026-10-20T12:05:20.174Z,1.17,2a5c,6 [FLog::Output] ! Joining game '" + job1 + "' place 111 at 10.0.0.1\n" +
                "2026-10-20T12:05:20.174Z,1.17,2a5c,6 [FLog::GameJoinLoadTime] Report game_join_loadtime: placeid:111, join_time:0.5, sid:x, userid:100000001, \n" +
                "2026-10-20T14:13:52.111Z,7713.1,227c,6 [FLog::Output] ! Joining game '" + job2 + "' place 222 at 10.0.0.2\n" +
                "2026-10-20T14:13:52.111Z,7713.1,227c,6 [FLog::GameJoinLoadTime] Report game_join_loadtime: placeid:222, join_time:0, sid:y, userid:100000001, \n";
            var s = RobloxLogs.Parse(log, 100000001);
            Check("latest join wins (teleports)", s != null && s.JobId == job2 && s.PlaceId == 222);
            Check("a different account's log is not matched", RobloxLogs.Parse(log, 42) == null);
            Check("empty / unrelated text", RobloxLogs.Parse("", 1) == null && RobloxLogs.Parse("nothing here", 1) == null);
        }

        Console.WriteLine("\n[Lantern's Roblox build]");
        {
            var m = RobloxBuild.ParseManifest("v0\r\nRobloxApp.zip\r\nABCDEF0123\r\n137795773\r\n174128171\r\ncontent-fonts.zip\r\n00ff\r\n5944830\r\n12192571\r\nRobloxPlayerInstaller.exe\r\n11\r\n13795280\r\n13795280\r\n");
            Check("Roblox's package list is read (name, checksum, size)", m.Count == 3 && m[0].Name == "RobloxApp.zip" && m[0].Md5 == "abcdef0123" && m[0].Size == 137795773 && m[2].Name == "RobloxPlayerInstaller.exe");
            Check("a package list of another format is refused", RobloxBuild.ParseManifest("v1\r\nRobloxApp.zip\r\n1\r\n2\r\n3\r\n").Count == 0);
            Check("packages go where Roblox's installer puts them",
                  RobloxBuild.FolderFor("RobloxApp.zip") == "" && RobloxBuild.FolderFor("content-textures2.zip") == @"content\textures"
                  && RobloxBuild.FolderFor("content-platform-dictionaries.zip") == @"PlatformContent\pc\shared_compression_dictionaries"
                  && RobloxBuild.FolderFor("something-new.zip") == null);
            Check("only clients in Lantern's folder count as its own",
                  RobloxBuild.IsOurs(Path.Combine(RobloxBuild.Dir, "version-0123456789abcdef", "RobloxPlayerBeta.exe")) && !RobloxBuild.IsOurs(RobloxBuild.Dir + "Evil\\RobloxPlayerBeta.exe"));
        }

        Console.WriteLine("\n[Launch URI]");
        var bu = typeof(InstanceManager).GetMethod("BuildUri", BindingFlags.NonPublic | BindingFlags.Static);
        var uri = (string)bu.Invoke(null, new object[] { "TICKET123", new LaunchTarget { PlaceId = 920587237 }, null, "111222333444" });
        Console.WriteLine("    " + uri);
        Check("protocol + mode", uri.StartsWith("roblox-player:1+launchmode:play+gameinfo:TICKET123+launchtime:"));
        Check("tracker id present twice", uri.Contains("browsertrackerid:111222333444") && uri.Contains("browserTrackerId%3D111222333444"));
        Check("place launcher url encoded", uri.Contains("placelauncherurl:https%3A%2F%2Fwww.roblox.com%2FGame%2FPlaceLauncher.ashx%3Frequest%3DRequestGame%26"));
        Check("no test channel is asked for (it made Roblox swap builds and close every window)", !uri.Contains("+channel:"));
        uri = (string)bu.Invoke(null, new object[] { "T", new LaunchTarget { PlaceId = 5, JobId = "8f1c2d3e-1111-2222-3333-444455556666" }, null, "1" });
        Check("job join uses RequestGameJob", uri.Contains("RequestGameJob") && uri.Contains("gameId%3D8f1c2d3e"));
        uri = (string)bu.Invoke(null, new object[] { "T", new LaunchTarget { PlaceId = 5, LinkCode = "LC" }, "11111111-2222-3333-4444-555555555555", "1" });
        Check("private uses RequestPrivateGame", uri.Contains("RequestPrivateGame") && uri.Contains("accessCode%3D11111111") && uri.Contains("linkCode%3DLC"));

        Console.WriteLine("\n[Multi-instance lock]  (real code path on throw-away names; a running Roblox is never touched)");
        var tag = Guid.NewGuid().ToString("N");
        MultiInstance.MutexName = "Lantern_selftest_mutex_" + tag;
        MultiInstance.EventName = "Lantern_selftest_event_" + tag;
        MultiInstance.IsRobloxRunning = () => false;
        MultiInstance.ReleaseTray = () => 0;
        {
            // Another program already holds the lock: we must refuse, not steal it.
            var held = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            var holder = new Thread(() =>
            {
                using (var m = new Mutex(true, MultiInstance.MutexName))
                {
                    held.Set();
                    release.Wait();
                    try { m.ReleaseMutex(); } catch { }
                }
            });
            holder.Start();
            held.Wait();
            bool ok = MultiInstance.Enable(out var err);
            Check("refuses when another program holds the lock", !ok && !string.IsNullOrEmpty(err), err);
            Check("not Held after refusal", !MultiInstance.Held);
            release.Set();
            holder.Join();

            ok = MultiInstance.Enable(out err);
            Check("acquires when free", ok && MultiInstance.Held, err);
            Check("idempotent when already held", MultiInstance.Enable(out _) && MultiInstance.Held);
            Check("lock visible system-wide", Mutex.TryOpenExisting(MultiInstance.MutexName, out var probe));
            probe?.Dispose();
            Check("event object exists", EventWaitHandle.TryOpenExisting(MultiInstance.EventName, out var ev));
            ev?.Dispose();

            // Nobody else can take it while Lantern holds it; that is what lets extra clients start.
            var other = new Thread(() =>
            {
                using (var m = new Mutex(false, MultiInstance.MutexName))
                {
                    holdOk = m.WaitOne(0);
                    if (holdOk) m.ReleaseMutex();
                }
            });
            other.Start();
            other.Join();
            Check("others cannot acquire while Lantern holds it", !holdOk);

            MultiInstance.Disable();
            Check("released on disable", !Mutex.TryOpenExisting(MultiInstance.MutexName, out probe) && !MultiInstance.Held);

            // Lantern restarted while windows it launched are still open: they don't own the lock, so it is taken back
            MultiInstance.IsRobloxRunning = () => true;
            ok = MultiInstance.Enable(out err);
            Check("takes a free lock back while its own windows are still open (Lantern restarted)", ok && MultiInstance.Held, err);
            MultiInstance.Disable();

            // ...but a running Roblox that owns the lock (started the normal way) is still refused, with a clear reason
            var held2 = new ManualResetEventSlim(); var release2 = new ManualResetEventSlim();
            var owner = new Thread(() => { using (var m = new Mutex(true, MultiInstance.MutexName)) { held2.Set(); release2.Wait(); try { m.ReleaseMutex(); } catch { } } });
            owner.Start(); held2.Wait();
            ok = MultiInstance.Enable(out err);
            Check("refuses a lock a running Roblox owns", !ok && err != null && err.Contains("already running") && !MultiInstance.Held, err);
            release2.Set(); owner.Join();

            // a Lantern that crashed leaves the lock "abandoned": Windows hands it over and that counts as taken
            var crasher = new Thread(() => { var m = new Mutex(true, MultiInstance.MutexName); GC.KeepAlive(m); });   // exits without releasing
            crasher.Start(); crasher.Join();
            ok = MultiInstance.Enable(out err);
            Check("takes over a lock abandoned by a crashed Lantern", ok && MultiInstance.Held, err);
            MultiInstance.Disable();
            MultiInstance.IsRobloxRunning = () => false;
        }

        Console.WriteLine("\n[Processes]");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var scan = RobloxProcesses.Scan();
        sw.Stop();
        Console.WriteLine("    WMI scan: " + scan.Count + " Roblox processes, " + sw.ElapsedMilliseconds + " ms");
        sw.Restart();
        for (int i = 0; i < 20; i++) RobloxProcesses.LivePids();
        sw.Stop();
        Console.WriteLine("    LivePids x20: " + sw.ElapsedMilliseconds + " ms total");
        var launcher = RobloxProcesses.FindLauncher();
        Console.WriteLine("    launcher: " + (launcher ?? "(not found)"));
        if (launcher == null) Skip("roblox-player handler found", "Roblox isn't installed on this machine");
        else Check("roblox-player handler found", File.Exists(launcher));
        // Roblox also keeps a windowless background process (--launch-to-tray) that has no tracker id; only game clients do.
        var withTracker = scan.FindAll(p => p.TrackerId.Length > 0);
        if (withTracker.Count > 0)
            Check("tracker id parsed from a real running client's command line", withTracker.TrueForAll(p => p.TrackerId.Length >= 10), string.Join(",", withTracker.ConvertAll(p => p.Pid + ":" + p.TrackerId)));
        else
            Skip("real-client tracker parse", scan.Count > 0 ? "only Roblox's background tray process is running" : "no Roblox client running");

        Console.WriteLine("\n[Damaged account entries]  (a hand-edited or half-written file must not crash start-up)");
        {
            var odd = new AccountData { UserId = 77, Id = null, Username = null, DisplayName = null, Alias = null, Group = null, Cookie = null, TrackerId = null, LastGame = null };
            Store.SaveAccounts(new AccountData[] { null, odd });
            var loaded = Store.LoadAccounts();
            Check("an empty entry is skipped instead of loaded as null", loaded.Count == 1 && loaded.All(a => a != null), "count=" + loaded.Count);
            var o = loaded.FirstOrDefault(a => a != null);
            Check("missing text loads as empty text, so nothing downstream hits a null",
                  o != null && o.Cookie == "" && o.Username == "" && o.DisplayName == "" && o.Alias == "" && o.Group == "" && o.TrackerId == "" && o.LastGame == "" && !string.IsNullOrEmpty(o.Id));
            File.WriteAllBytes(Paths.Accounts + ".tmp", new byte[] { 9, 9, 9 });   // a temp file left behind by a crash
            Store.SaveAccounts(new[] { acc });
            Check("a stale temp file doesn't stop the next save and is gone afterwards", !File.Exists(Paths.Accounts + ".tmp") && Store.LoadAccounts().Count == 1);
        }

        Console.WriteLine("\n[Missing or damaged saved session]  (must read as 'signed out', never as 'no connection')");
        {
            var (sEmpty, _) = RobloxApi.GetUserAsync("").GetAwaiter().GetResult();
            Check("an empty cookie is a signed-out account", sEmpty == AuthStatus.Expired, sEmpty.ToString());
            var (sBad, _) = RobloxApi.GetUserAsync("two\r\nlines").GetAwaiter().GetResult();
            Check("a cookie with a line break is a signed-out account", sBad == AuthStatus.Expired, sBad.ToString());
            var (sNull, _) = RobloxApi.GetUserAsync(null).GetAwaiter().GetResult();
            Check("no cookie at all is a signed-out account (and sends nothing)", sNull == AuthStatus.Expired, sNull.ToString());
            var tEmpty = RobloxApi.GetAuthTicketAsync("").GetAwaiter().GetResult();
            Check("launching with an empty cookie asks for a new sign-in", tEmpty.Expired && tEmpty.Error == null, "Expired=" + tEmpty.Expired + " err=" + tEmpty.Error);
        }

        Console.WriteLine("\n[Errors shown to the user]");
        {
            var shown = LaunchErrors.Exception(new InvalidOperationException("could not start for " + ck('A')));
            Check("an exception's text never carries a cookie into the window", !(shown.Title + shown.Why + shown.Fix).Contains("DO-NOT-SHARE"), shown.Title);
        }

        Console.WriteLine("\n[Launch ticket shape]  (the ticket goes onto a command line)");
        Check("a normal token passes", RobloxApi.IsPlausibleTicket("CAEaAhADIhwKBGR1aWQSFDEwMDAwMDAwMDEw+/ab-_=") );
        Check("a space is refused", !RobloxApi.IsPlausibleTicket("abc def"));
        Check("a quote is refused (would end the argument)", !RobloxApi.IsPlausibleTicket("abc\" --evil"));
        Check("a backslash is refused", !RobloxApi.IsPlausibleTicket("abc\\"));
        Check("a line break is refused", !RobloxApi.IsPlausibleTicket("abc\r\ndef"));
        Check("non-ASCII is refused", !RobloxApi.IsPlausibleTicket("abcé"));
        Check("empty and absurdly long are refused", !RobloxApi.IsPlausibleTicket("") && !RobloxApi.IsPlausibleTicket(null) && !RobloxApi.IsPlausibleTicket(new string('A', 5000)));

        Console.WriteLine("\n[Erase protection]  (LANTERN_HOME can point anywhere)");
        {
            var sysRoot = Path.GetPathRoot(Environment.SystemDirectory);
            Check("a drive root is protected", Store.IsProtectedFolder(sysRoot));
            Check("the user profile is protected", Store.IsProtectedFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
            Check("Documents is protected", Store.IsProtectedFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
            Check("a folder that contains a protected one is protected", Store.IsProtectedFolder(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))));
            Check("the default data folder is not protected", !Store.IsProtectedFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lantern")));
            Check("a folder of its own under Temp is not protected", !Store.IsProtectedFolder(home));
        }

        Console.WriteLine("\n[Unpacking a downloaded package]");
        {
            var unpack = typeof(RobloxBuild).GetMethod("Unpack", BindingFlags.NonPublic | BindingFlags.Static);
            var work = Path.Combine(Path.GetTempPath(), "Lantern-unpack-" + Guid.NewGuid().ToString("N"));
            var build = Path.Combine(work, "build");
            Directory.CreateDirectory(build);
            string MakeZip(string file, params string[] entries)
            {
                var zipPath = Path.Combine(work, file);
                using (var z = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
                    foreach (var name in entries)
                        using (var w = new StreamWriter(z.CreateEntry(name).Open())) w.Write("x");
                return zipPath;
            }

            var good = MakeZip("good.zip", "a.txt", "sub/b.txt");
            unpack.Invoke(null, new object[] { good, "good.zip", @"content\fonts", build });
            Check("files land in the package's folder", File.Exists(Path.Combine(build, @"content\fonts\a.txt")) && File.Exists(Path.Combine(build, @"content\fonts\sub\b.txt")));
            Check("the downloaded zip is removed afterwards", !File.Exists(good));

            var evil = MakeZip("evil.zip", "ok.txt", "../../escaped.txt");
            string refused = null;
            try { unpack.Invoke(null, new object[] { evil, "evil.zip", "", build }); }
            catch (TargetInvocationException ex) { refused = ex.InnerException?.GetType().Name; }
            Check("a file that would land outside the build is refused", refused == nameof(InvalidDataException), refused ?? "no exception");
            Check("nothing was written outside the build", !File.Exists(Path.Combine(work, "escaped.txt")) && !File.Exists(Path.Combine(Path.GetDirectoryName(work), "escaped.txt")));
            Check("a refused zip is removed too (it would otherwise stay in Temp)", !File.Exists(evil));
            try { Directory.Delete(work, true); } catch { }
        }

        Console.WriteLine("\n[Native library search path]");
        {
            var attr = (System.Runtime.InteropServices.DefaultDllImportSearchPathsAttribute)Attribute.GetCustomAttribute(typeof(Native).Assembly, typeof(System.Runtime.InteropServices.DefaultDllImportSearchPathsAttribute));
            Check("native libraries are looked up in System32 only (not next to Lantern.exe)", attr != null && attr.Paths == System.Runtime.InteropServices.DllImportSearchPath.System32, attr == null ? "attribute missing" : attr.Paths.ToString());
        }

        Console.WriteLine("\n[Signature check on the client Lantern runs]");
        {
            var official = RobloxProcesses.FindLauncher();
            if (official == null || !File.Exists(official) || !official.EndsWith("RobloxPlayerBeta.exe", StringComparison.OrdinalIgnoreCase)) Skip("signature checks", "no installed Roblox client to test against");
            else
            {
                Check("the official client is accepted as signed by Roblox", CodeSignature.IsSignedBy(official, RobloxBuild.Publisher, out var why1), why1);
                Check("...but not as signed by someone else", !CodeSignature.IsSignedBy(official, "Somebody Else", out var why2) && why2 != null && why2.Contains(RobloxBuild.Publisher), why2);
                Check("an unsigned program is refused", !CodeSignature.IsSignedBy(Assembly.GetExecutingAssembly().Location, RobloxBuild.Publisher, out var why3) && why3 != null, why3);
                Check("a missing file is refused", !CodeSignature.IsSignedBy(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".exe"), RobloxBuild.Publisher, out var why4) && why4 != null);
                var tampered = Path.Combine(Path.GetTempPath(), "Lantern-tamper-" + Guid.NewGuid().ToString("N") + ".exe");
                try
                {
                    File.Copy(official, tampered);
                    using (var fs = new FileStream(tampered, FileMode.Open, FileAccess.ReadWrite))
                    {
                        fs.Position = 1_000_000;
                        int b = fs.ReadByte();
                        fs.Position = 1_000_000;
                        fs.WriteByte((byte)(b ^ 0xFF));
                    }
                    Check("a client changed after signing is refused", !CodeSignature.IsSignedBy(tampered, RobloxBuild.Publisher, out var why5), why5);
                    Console.WriteLine("    (reason given: " + why5 + ")");
                }
                finally { try { File.Delete(tampered); } catch { } }
            }
        }

        Console.WriteLine("\n[Erase everything]  (runs last among the storage checks: it deletes the test data folder)");
        {
            Store.SaveAccounts(new[] { acc });
            Store.SavePlain(Paths.Settings, new SettingsData());
            Logger.Write("a line for the log");
            var wipe = typeof(Store).GetMethod("WipeEverything");
            object notDeleted;
            using (new FileStream(Paths.Accounts, FileMode.Open, FileAccess.Read, FileShare.None))   // e.g. a backup tool or antivirus holding the file
                notDeleted = wipe.Invoke(null, null);
            int stuck = 0;
            if (notDeleted is System.Collections.IEnumerable items) foreach (var _ in items) stuck++;
            Check("a file that couldn't be deleted is reported, not silently left behind", stuck > 0 && File.Exists(Paths.Accounts), "reported=" + stuck + " (null = nothing reported)");
            Store.SaveAccounts(new[] { acc });
            Check("a failed erase leaves Lantern able to keep saving", File.Exists(Paths.Accounts));

            wipe.Invoke(null, null);
            Check("with nothing holding it, the whole data folder is gone", !Directory.Exists(Paths.Root));
            Store.SaveAccounts(new[] { acc });                    // a save that was already on its way when the erase ran
            Store.SavePlain(Paths.Settings, new SettingsData());
            Logger.Write("late line");
            Check("a late save or log line can't bring the accounts back", !File.Exists(Paths.Accounts) && !File.Exists(Paths.Settings) && !File.Exists(Paths.Log));
        }

        if (args.Contains("--offline")) { Skip("live Roblox API checks", "--offline"); goto Done; }
        Console.WriteLine("\n[Roblox API - live, no account needed]");
        var bogus = "_|WARNING:-DO-NOT-SHARE-THIS." + new string('x', 200);
        var (st, _) = RobloxApi.GetUserAsync(bogus).GetAwaiter().GetResult();
        Check("invalid cookie -> Expired (HTTP path + headers work)", st == AuthStatus.Expired, st.ToString());
        var tk = RobloxApi.GetAuthTicketAsync(bogus).GetAwaiter().GetResult();
        Check("invalid cookie ticket -> Expired", tk.Expired, "Expired=" + tk.Expired + " err=" + tk.Error);
        var gi = RobloxApi.GetGameInfoAsync(920587237).GetAwaiter().GetResult();
        Check("game info lookup", gi != null && gi.Name.Length > 0 && gi.UniverseId > 0, gi == null ? "null" : gi.Name);
        if (gi != null) Console.WriteLine("    -> " + gi.Name + " (universe " + gi.UniverseId + ")");
        var heads = RobloxApi.GetHeadshotUrlsAsync(new long[] { 1 }).GetAwaiter().GetResult();
        Check("headshot url", heads.ContainsKey(1));
        if (heads.ContainsKey(1))
        {
            var img = RobloxApi.DownloadImageAsync(heads[1]).GetAwaiter().GetResult();
            Check("headshot download is a PNG", img != null && img.Length > 200 && img[1] == (byte)'P', img == null ? "null" : img.Length + " bytes");
        }
        Check("non-roblox image host refused", RobloxApi.DownloadImageAsync("https://example.com/a.png").GetAwaiter().GetResult() == null);

    Done:
        try { Directory.Delete(home, true); } catch { }
        Console.WriteLine("\n==== " + pass + " passed, " + fail + " failed, " + skip + " skipped ====");
        return fail == 0 ? 0 : 1;
    }
}