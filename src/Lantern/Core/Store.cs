using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Lantern.Core
{
    public static class Paths
    {
        /// <summary>%APPDATA%\Lantern, or the folder named by LANTERN_HOME (portable use).</summary>
        public static readonly string Root;

        // Explicit static constructor on purpose: without it the CLR may run the initialiser early
        // (beforefieldinit), before the environment override has been applied.
        static Paths()
        {
            var home = Environment.GetEnvironmentVariable("LANTERN_HOME");
            Root = !string.IsNullOrEmpty(home)
                ? home
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lantern");
        }

        public static string Accounts => Path.Combine(Root, "accounts.dat");
        public static string Settings => Path.Combine(Root, "settings.json");
        public static string Games => Path.Combine(Root, "games.json");
        public static string Profiles => Path.Combine(Root, "profiles.json");
        /// <summary>Every window Lantern has watched: start, end, why, memory. No cookies (see SessionLog).</summary>
        public static string Stats => Path.Combine(Root, "stats.json");
        /// <summary>Original Roblox graphics values while low-resource mode has them changed (see Perf).</summary>
        public static string PerfBackup => Path.Combine(Root, "perf-backup.json");
        /// <summary>Remembers what Lantern put in Roblox's ClientSettings files, so it can take it out again.</summary>
        public static string FlagsBackup => Path.Combine(Root, "flags-backup.json");
        public static string Cache => Path.Combine(Root, "cache");
        public static string Log => Path.Combine(Root, "lantern.log");
        /// <summary>
        /// Where the Pulse report goes: deliberately outside <see cref="Root"/>, so it can be shared or read without going near
        /// the folder that holds the sessions. With LANTERN_HOME set it stays inside that folder (portable use, tests).
        /// </summary>
        public static string PulseDir => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LANTERN_HOME"))
            ? Path.Combine(Root, "pulse")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Lantern Pulse");

        public static void Ensure()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Cache);
        }
    }

    /// <summary>
    /// Disk persistence. Everything that contains a cookie goes through Windows DPAPI
    /// (CurrentUser scope): only this Windows user on this PC can decrypt it.
    /// Writes are atomic (temp file + replace) and keep a .bak of the previous good copy.
    /// </summary>
    public static class Store
    {
        // Application-specific entropy for DPAPI. It keeps accounts.dat from being opened by any other program that merely calls
        // ProtectedData.Unprotect with the defaults. It is not a secret (the source is public), so it is no defence against malware
        // that targets Lantern on purpose; the real boundary is the Windows user account. See SECURITY.md.
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Lantern.accounts.v1");
        static readonly object Gate = new object();
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        /// <summary>Set when an existing file could not be read (so the UI can say so instead of silently resetting).</summary>
        public static string LoadWarning { get; private set; }

        /// <summary>
        /// True once everything has been erased. From then on nothing is written into the data folder, so a save or log line that was
        /// still on its way when the erase ran can't bring the accounts (or a log) back.
        /// </summary>
        public static bool Erased { get; private set; }

        /// <summary>A hand-edited or half-written file can hold empty entries and missing text; neither may reach the rest of the app.</summary>
        static List<AccountData> Clean(List<AccountData> list)
        {
            var clean = new List<AccountData>();
            if (list == null) return clean;
            foreach (var a in list)
            {
                if (a == null) continue;
                if (string.IsNullOrEmpty(a.Id)) a.Id = Guid.NewGuid().ToString("N");
                a.Username = a.Username ?? "";
                a.DisplayName = a.DisplayName ?? "";
                a.Alias = a.Alias ?? "";
                a.Group = a.Group ?? "";
                a.Cookie = a.Cookie ?? "";
                a.TrackerId = a.TrackerId ?? "";
                a.LastGame = a.LastGame ?? "";
                clean.Add(a);
            }
            return clean;
        }

        public static List<AccountData> LoadAccounts()
        {
            lock (Gate)
            {
                var path = Paths.Accounts;
                foreach (var candidate in new[] { path, path + ".bak" })
                {
                    if (!File.Exists(candidate)) continue;
                    try
                    {
                        var plain = ProtectedData.Unprotect(File.ReadAllBytes(candidate), Entropy, DataProtectionScope.CurrentUser);
                        var list = Json.Deserialize<List<AccountData>>(Encoding.UTF8.GetString(plain));
                        Array.Clear(plain, 0, plain.Length);
                        if (candidate != path) LoadWarning = "Your account file was damaged, so the last backup was restored.";
                        return Clean(list);
                    }
                    catch (Exception ex)
                    {
                        Logger.Write("LoadAccounts failed for " + Path.GetFileName(candidate) + ": " + ex.Message);
                    }
                }
                if (File.Exists(path))
                {
                    try { File.Move(path, path + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); } catch { }
                    LoadWarning = "Your saved accounts couldn't be decrypted (were they created under a different Windows user?). The old file was set aside.";
                }
                return new List<AccountData>();
            }
        }

        public static void SaveAccounts(IEnumerable<AccountData> accounts)
        {
            lock (Gate)
            {
                if (Erased) return;
                Paths.Ensure();
                var plain = Encoding.UTF8.GetBytes(Json.Serialize(accounts));
                try
                {
                    WriteAtomic(Paths.Accounts, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
                }
                finally { Array.Clear(plain, 0, plain.Length); }
            }
        }

        /// <summary>
        /// What <paramref name="sample"/> looks like once Lantern has protected it, as hex, cut to <paramref name="bytes"/> bytes. Uses
        /// the same call, scope and entropy as the account file, so the first-run setup can show real ciphertext. Nothing is saved.
        /// </summary>
        public static string ProtectedPreview(string sample, int bytes)
        {
            var plain = Encoding.UTF8.GetBytes(sample);
            try
            {
                var sealedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
                var hex = new StringBuilder();
                for (int i = 0; i < Math.Min(bytes, sealedBytes.Length); i++) hex.Append(sealedBytes[i].ToString("X2"));
                return hex.ToString();
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }

        public static T LoadPlain<T>(string path) where T : new()
        {
            lock (Gate)
            {
                try
                {
                    if (File.Exists(path))
                        return Json.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8)) ?? new T();
                }
                catch (Exception ex) { Logger.Write("LoadPlain " + Path.GetFileName(path) + ": " + ex.Message); }
                return new T();
            }
        }

        public static void SavePlain<T>(string path, T value)
        {
            lock (Gate)
            {
                if (Erased) return;
                Paths.Ensure();
                WriteAtomic(path, Encoding.UTF8.GetBytes(Json.Serialize(value)));
            }
        }

        static void WriteAtomic(string path, byte[] bytes)
        {
            var tmp = path + ".tmp";
            using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                f.Write(bytes, 0, bytes.Length);
                f.Flush(true);   // on the disk before the swap: otherwise a power cut can leave an empty file under the real name
            }
            if (File.Exists(path))
                File.Replace(tmp, path, path + ".bak", true);
            else
                File.Move(tmp, path);
        }

        /// <summary>
        /// Is <paramref name="dir"/> a drive root, or a folder that is or holds the user's profile, documents or Windows? Erasing
        /// "the data folder" must never take such a folder with it (LANTERN_HOME can point anywhere, a USB stick's root included).
        /// </summary>
        public static bool IsProtectedFolder(string dir)
        {
            try
            {
                var full = Path.GetFullPath(dir).TrimEnd('\\', '/');
                var root = (Path.GetPathRoot(Path.GetFullPath(dir)) ?? "").TrimEnd('\\', '/');
                if (full.Length == 0 || string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return true;
                foreach (var f in new[]
                {
                    Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments,
                    Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.CommonApplicationData,
                    Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows, Environment.SpecialFolder.System
                })
                {
                    var special = Environment.GetFolderPath(f);
                    if (string.IsNullOrEmpty(special)) continue;
                    special = Path.GetFullPath(special).TrimEnd('\\', '/');
                    if (string.Equals(special, full, StringComparison.OrdinalIgnoreCase) || special.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            }
            catch { return true; }   // can't tell where it points: don't delete
        }

        /// <summary>
        /// Deletes the data folder and the Pulse report folder. Returns what could not be deleted (empty = everything is gone), so the
        /// caller can say so instead of letting the user believe their accounts are erased.
        /// </summary>
        public static List<string> WipeEverything()
        {
            lock (Gate)
            {
                var left = new List<string>();
                if (IsProtectedFolder(Paths.Root))
                {
                    left.Add(Paths.Root);
                    return left;
                }
                Erased = true;
                WipeFolder(Paths.Root, left);
                WipeFolder(Paths.PulseDir, left);
                if (left.Count > 0) Erased = false;   // Lantern stays open, so it must keep saving
                foreach (var path in left) Logger.Write("Erase: could not delete " + path);
                return left;
            }
        }

        static void WipeFolder(string dir, List<string> left)
        {
            if (!Directory.Exists(dir)) return;
            try { Directory.Delete(dir, true); return; }
            catch { /* something is in use: remove what can be removed, report the rest */ }
            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); }
                catch { left.Add(file); }
            }
            foreach (var sub in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(s => s.Length))
            {
                try { Directory.Delete(sub); } catch { }
            }
            try { Directory.Delete(dir); } catch { if (left.Count == 0) left.Add(dir); }
        }
    }

    /// <summary>
    /// Tiny file logger. Callers never pass cookies or tickets; as a second line of defence anything shaped like one is blanked
    /// before it reaches the disk.
    /// </summary>
    public static class Logger
    {
        static readonly object Gate = new object();
        static readonly System.Text.RegularExpressions.Regex Secret = new System.Text.RegularExpressions.Regex(
            @"_\|WARNING:-DO-NOT-SHARE[^\s;""',]*|\.ROBLOSECURITY=[^\s;]+|rbx-authentication-ticket[:=]\s*\S+",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>The message with every cookie- or ticket-shaped piece replaced.</summary>
        public static string Scrub(string message) => message == null ? "" : Secret.Replace(message, "[removed]");

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    if (Store.Erased) return;
                    Paths.Ensure();
                    var info = new FileInfo(Paths.Log);
                    if (info.Exists && info.Length > 256 * 1024)
                        File.Move(Paths.Log, Paths.Log + ".old");
                    File.AppendAllText(Paths.Log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + Scrub(message) + Environment.NewLine);
                }
            }
            catch { /* logging must never throw */ }
        }
    }
}
