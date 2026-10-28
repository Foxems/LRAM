using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Lantern.Core
{
    /// <summary>
    /// Lantern's own copy of the Roblox client, which every account is launched from.
    ///
    /// Roblox puts accounts on different test channels, each with its own client build. A client started through Roblox's link
    /// handler checks the build its account's channel wants and, when it differs, hands over to RobloxPlayerInstaller, which swaps
    /// builds and closes every open Roblox window (Roblox's tray process does the same when it finds an update). Lantern's copy has
    /// no installer next to the client, so nothing can swap builds under running windows. Lantern keeps the copy on the build Roblox
    /// currently releases: a new one is put in a new folder beside the old one, from Roblox's own download servers (or copied from
    /// the official install when that already has it), so new launches and rejoins use it while open windows keep running.
    /// </summary>
    public static class RobloxBuild
    {
        /// <summary>Where Lantern's builds live (local, not roaming: about 350 MB each). Overridable for tests.</summary>
        public static string Dir { get; set; } = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LANTERN_HOME"))
            ? Path.Combine(Paths.Root, "roblox")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lantern", "Roblox");

        /// <summary>The official install's builds, copied from when one already has the build wanted.</summary>
        public static string OfficialVersionsDir { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions");

        const string VersionUrl = "https://clientsettingscdn.roblox.com/v2/client-version/WindowsPlayer";
        const string Cdn = "https://setup.rbxcdn.com/";
        const string Marker = "lantern-build.txt";
        const string Installer = "RobloxPlayerInstaller.exe";
        /// <summary>Who must have signed the client before Lantern will run its copy.</summary>
        public const string Publisher = "Roblox Corporation";
        static readonly Regex GuidRx = new Regex("^version-[0-9a-f]{16}$", RegexOptions.CultureInvariant);

        /// <summary>Where each of Roblox's packages goes inside a build folder (the layout Roblox's installer makes).</summary>
        static readonly Dictionary<string, string> Layout = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RobloxApp.zip"] = "",
            ["redist.zip"] = "",
            ["WebView2.zip"] = "",
            ["shaders.zip"] = "shaders",
            ["ssl.zip"] = "ssl",
            ["WebView2RuntimeInstaller.zip"] = "WebView2RuntimeInstaller",
            ["content-avatar.zip"] = @"content\avatar",
            ["content-configs.zip"] = @"content\configs",
            ["content-fonts.zip"] = @"content\fonts",
            ["content-sky.zip"] = @"content\sky",
            ["content-sounds.zip"] = @"content\sounds",
            ["content-textures2.zip"] = @"content\textures",
            ["content-models.zip"] = @"content\models",
            ["content-platform-fonts.zip"] = @"PlatformContent\pc\fonts",
            ["content-platform-dictionaries.zip"] = @"PlatformContent\pc\shared_compression_dictionaries",
            ["content-terrain.zip"] = @"PlatformContent\pc\terrain",
            ["content-textures3.zip"] = @"PlatformContent\pc\textures",
            ["extracontent-luapackages.zip"] = @"ExtraContent\LuaPackages",
            ["extracontent-translations.zip"] = @"ExtraContent\translations",
            ["extracontent-models.zip"] = @"ExtraContent\models",
            ["extracontent-textures.zip"] = @"ExtraContent\textures",
            ["extracontent-places.zip"] = @"ExtraContent\places",
        };

        const string AppSettings = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<Settings>\r\n\t<ContentFolder>content</ContentFolder>\r\n\t<BaseUrl>http://www.roblox.com</BaseUrl>\r\n</Settings>\r\n";

        static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        static DateTime _nextCheck = DateTime.MinValue;

        /// <summary>What Lantern's copy is doing, for the health page ("" when nothing to say).</summary>
        public static string Status { get; private set; } = "";

        static string CurrentFile => Path.Combine(Dir, "current.txt");

        /// <summary>The build launches use (e.g. "version-5a21b87bbc2641cf"), or null while there is none ready.</summary>
        public static string Current
        {
            get
            {
                try { var g = File.Exists(CurrentFile) ? File.ReadAllText(CurrentFile).Trim() : ""; return Ready(g) ? g : null; }
                catch { return null; }
            }
        }

        /// <summary>The client to launch, or null (then launches go through Roblox's own link handler, as before).</summary>
        public static string ExePath { get { var c = Current; return c == null ? null : Path.Combine(Dir, c, "RobloxPlayerBeta.exe"); } }

        /// <summary>Lantern's finished build folders (engine flags are written to these too).</summary>
        public static IEnumerable<string> Folders()
        {
            try { return Directory.Exists(Dir) ? Directory.GetDirectories(Dir).Where(d => File.Exists(Path.Combine(d, Marker))).ToList() : new List<string>(); }
            catch { return new List<string>(); }
        }

        static bool Ready(string guid) =>
            !string.IsNullOrEmpty(guid) && GuidRx.IsMatch(guid) && File.Exists(Path.Combine(Dir, guid, Marker)) && File.Exists(Path.Combine(Dir, guid, "RobloxPlayerBeta.exe"));

        /// <summary>
        /// Makes sure Lantern's copy is the build Roblox currently releases. Cheap when nothing changed (Roblox is asked at most every
        /// 10 minutes). Returns the client to launch, or null when there is no copy and none could be made (offline on the very first run).
        /// </summary>
        public static async Task<string> EnsureAsync()
        {
            if (App.Demo) return null;
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (DateTime.UtcNow < _nextCheck && Current != null) return ExePath;
                string latest = null;
                try { latest = await LatestAsync().ConfigureAwait(false); }
                catch (Exception ex) { Logger.Write("Roblox build: version check failed: " + ex.Message); }
                // a failed check is tried again in a minute, a good one in 10
                _nextCheck = DateTime.UtcNow + (latest == null ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(10));
                if (latest != null && latest != Current)
                {
                    if (await Task.Run(() => Install(latest)).ConfigureAwait(false))
                    {
                        File.WriteAllText(CurrentFile, latest);
                        Logger.Write("Roblox build: launches now use " + latest);
                        if (EngineFlags.Pending) EngineFlags.Refresh(out _);
                    }
                }
                await Task.Run(() => Cleanup()).ConfigureAwait(false);
                return ExePath;
            }
            finally { Gate.Release(); }
        }

        static async Task<string> LatestAsync()
        {
            var json = await Http.GetStringAsync(VersionUrl).ConfigureAwait(false);
            var d = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            var guid = d != null && d.TryGetValue("clientVersionUpload", out var v) ? v as string : null;
            return guid != null && GuidRx.IsMatch(guid) ? guid : null;
        }

        /// <summary>Puts build <paramref name="guid"/> in its own folder: copied from the official install if it has it, else downloaded.</summary>
        static bool Install(string guid)
        {
            if (Ready(guid)) return true;
            // filled in place (Roblox's own paths are already near Windows' 260-character limit, so no longer staging name);
            // the marker is written last, and a folder without it is never launched from
            var part = Path.Combine(Dir, guid);
            try
            {
                Directory.CreateDirectory(Dir);
                if (Directory.Exists(part)) Directory.Delete(part, true);   // unfinished from an earlier run
                Directory.CreateDirectory(part);

                var manifest = ParseManifest(Http.GetStringAsync(Cdn + guid + "-rbxPkgManifest.txt").GetAwaiter().GetResult());
                if (manifest.Count == 0) throw new InvalidDataException("Roblox's package list for " + guid + " is empty");
                var unknown = manifest.Where(p => p.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !Layout.ContainsKey(p.Name)).Select(p => p.Name).ToList();
                if (unknown.Count > 0) throw new InvalidDataException("Roblox added packages Lantern doesn't know where to put: " + string.Join(", ", unknown));

                var official = Path.Combine(OfficialVersionsDir, guid);
                if (OfficialComplete(official, manifest))
                {
                    Status = "Copying Roblox " + guid + " from the installed one…";
                    Copy(official, part);
                }
                else
                {
                    Status = "Downloading Roblox " + guid + "…";
                    foreach (var p in manifest.Where(p => p.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                        Fetch(guid, p, part);
                }
                File.WriteAllText(Path.Combine(part, "AppSettings.xml"), AppSettings);
                File.Delete(Path.Combine(part, Installer));   // the whole point: nothing here can swap builds
                if (!File.Exists(Path.Combine(part, "RobloxPlayerBeta.exe"))) throw new InvalidDataException("the client is missing after installing");
                // The checksums above come from the same server as the files. Windows' own verdict on the client decides whether it runs.
                if (!CodeSignature.IsSignedBy(Path.Combine(part, "RobloxPlayerBeta.exe"), Publisher, out var why))
                    throw new InvalidDataException("the client isn't properly signed by " + Publisher + " (" + why + ")");
                File.WriteAllText(Path.Combine(part, Marker), guid + "\r\n" + DateTime.UtcNow.ToString("o"));
                Status = "";
                Logger.Write("Roblox build: " + guid + " ready");
                return true;
            }
            catch (Exception ex)
            {
                Status = "Couldn't prepare Roblox " + guid + ": " + ex.Message;
                Logger.Write("Roblox build: " + Status);
                try { if (Directory.Exists(part)) Directory.Delete(part, true); } catch { }
                return false;
            }
        }

        public sealed class Package { public string Name, Md5; public long Size; }

        /// <summary>Roblox's rbxPkgManifest.txt: "v0", then four lines per package (name, MD5, packed size, unpacked size).</summary>
        public static List<Package> ParseManifest(string text)
        {
            var lines = (text ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).Select(l => l.Trim()).ToList();
            var list = new List<Package>();
            if (lines.Count == 0 || lines[0] != "v0") return list;
            for (int i = 1; i + 3 < lines.Count; i += 4)
            {
                if (lines[i].Length == 0) break;
                if (!long.TryParse(lines[i + 2], out var size)) break;
                list.Add(new Package { Name = lines[i], Md5 = lines[i + 1].ToLowerInvariant(), Size = size });
            }
            return list;
        }

        /// <summary>The folder (inside a build) a package's files go to, or null for one Lantern doesn't know.</summary>
        public static string FolderFor(string package) => Layout.TryGetValue(package, out var f) ? f : null;

        /// <summary>
        /// The official install has this build complete: its client reports the same version as every other build file, and every
        /// folder the packages fill is there. (Roblox leaves an empty .crdownload even in finished folders, so that can't be used.)
        /// </summary>
        static bool OfficialComplete(string dir, List<Package> manifest)
        {
            try
            {
                var exe = Path.Combine(dir, "RobloxPlayerBeta.exe");
                if (!File.Exists(exe) || !File.Exists(Path.Combine(dir, "RobloxPlayerBeta.dll"))) return false;
                foreach (var p in manifest)
                {
                    var f = FolderFor(p.Name);
                    if (!string.IsNullOrEmpty(f) && !Directory.Exists(Path.Combine(dir, f))) return false;
                }
                // a folder Roblox's installer is still filling has files open for writing
                using (File.Open(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { }
                return true;
            }
            catch { return false; }
        }

        static void Copy(string from, string to)
        {
            foreach (var src in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var rel = src.Substring(from.Length).TrimStart('\\', '/');
                var name = Path.GetFileName(rel);
                // the installer stays out; Roblox's own engine-flag file and download leftovers aren't part of the build
                if (name.Equals(Installer, StringComparison.OrdinalIgnoreCase) || name.Equals(".crdownload", StringComparison.OrdinalIgnoreCase)) continue;
                if (rel.StartsWith("ClientSettings" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                var dst = Path.Combine(to, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(src, dst, true);
            }
        }

        /// <summary>Downloads one package, checks its size and MD5 against Roblox's list, and unpacks it where it belongs.</summary>
        static void Fetch(string guid, Package p, string root)
        {
            var zip = Path.Combine(Path.GetTempPath(), "lantern-" + guid + "-" + p.Name);
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    using (var resp = Http.GetAsync(Cdn + guid + "-" + p.Name, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                    {
                        resp.EnsureSuccessStatusCode();
                        using (var src = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                        using (var dst = File.Create(zip)) src.CopyTo(dst);
                    }
                    string md5;
                    using (var s = File.OpenRead(zip))
                    using (var h = MD5.Create())
                    {
                        if (s.Length != p.Size) throw new InvalidDataException(p.Name + " came down " + s.Length + " bytes, expected " + p.Size);
                        md5 = BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
                    }
                    if (md5 != p.Md5) throw new InvalidDataException(p.Name + " doesn't match its checksum");
                    break;
                }
                catch (Exception)
                {
                    try { File.Delete(zip); } catch { }   // a bad download is hundreds of MB that would otherwise stay in %TEMP%
                    if (attempt >= 3) throw;
                    Thread.Sleep(2000 * attempt);
                }
            }

            Unpack(zip, p.Name, FolderFor(p.Name), root);
        }

        /// <summary>
        /// Unpacks a downloaded package into <paramref name="folder"/> of the build at <paramref name="root"/>, refusing any file that
        /// would land outside the build. The downloaded zip is deleted whether or not that worked.
        /// </summary>
        static void Unpack(string zip, string name, string folder, string root)
        {
            try
            {
                var target = Path.GetFullPath(Path.Combine(root, folder));
                var rootFull = Path.GetFullPath(root).TrimEnd('\\') + "\\";
                using (var archive = ZipFile.OpenRead(zip))
                {
                    foreach (var e in archive.Entries)
                    {
                        var rel = e.FullName.Replace('/', '\\');
                        if (rel.Length == 0 || rel.EndsWith("\\")) continue;
                        var dst = Path.GetFullPath(Path.Combine(target, rel));
                        if (!dst.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(name + " has a file outside the build: " + e.FullName);
                        Directory.CreateDirectory(Path.GetDirectoryName(dst));
                        e.ExtractToFile(dst, true);
                    }
                }
            }
            finally { try { File.Delete(zip); } catch { } }
        }

        /// <summary>
        /// Removes Lantern's older builds once no window runs from them (open windows keep their build until they close), and any
        /// half-finished download. The build in use is never touched.
        /// </summary>
        static void Cleanup()
        {
            try
            {
                if (!Directory.Exists(Dir)) return;
                var current = Current;
                var old = Directory.GetDirectories(Dir).Where(d => !string.Equals(Path.GetFileName(d), current, StringComparison.OrdinalIgnoreCase)).ToList();
                if (old.Count == 0) return;
                var running = new HashSet<string>(RobloxProcesses.Scan().Select(p => Path.GetDirectoryName(p.ExePath ?? "") ?? ""), StringComparer.OrdinalIgnoreCase);
                foreach (var d in old)
                {
                    if (running.Contains(d)) continue;
                    try { Directory.Delete(d, true); Logger.Write("Roblox build: removed " + Path.GetFileName(d)); }
                    catch (Exception ex) { Logger.Write("Roblox build: couldn't remove " + Path.GetFileName(d) + ": " + ex.Message); }
                }
            }
            catch (Exception ex) { Logger.Write("Roblox build cleanup: " + ex.Message); }
        }

        /// <summary>True for a client started from one of Lantern's builds.</summary>
        public static bool IsOurs(string exePath) =>
            !string.IsNullOrEmpty(exePath) && exePath.StartsWith(Dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
