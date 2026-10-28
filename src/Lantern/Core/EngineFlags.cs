using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace Lantern.Core
{
    /// <summary>
    /// Roblox lets a player tune a few engine "Fast Flags" through <c>ClientSettings\ClientAppSettings.json</c> next to
    /// the player. Since September 2025 Roblox publishes an allowlist: listed flags are honored, anything else is
    /// ignored without penalty. Lantern only ever writes flags from <see cref="Allowed"/>, merges them into whatever
    /// the file already contains, and puts the file back exactly when the windows are gone.
    /// </summary>
    public static class EngineFlags
    {
        /// <summary>The flags Roblox's allowlist announcement (Sept 29 2025) lists for the Rendering / Geometry groups that Lantern can use.</summary>
        public static readonly HashSet<string> Allowed = new HashSet<string>
        {
            "DFFlagTextureQualityOverrideEnabled", "DFIntTextureQualityOverride", "FIntDebugForceMSAASamples",
            "DFFlagDebugPauseVoxelizer", "DFIntDebugFRMQualityLevelOverride", "FIntFRMMaxGrassDistance", "FIntFRMMinGrassDistance",
            "FFlagDebugGraphicsPreferD3D11", "FFlagDebugGraphicsPreferVulkan", "FFlagDebugGraphicsPreferOpenGL",
            "DFIntCSGLevelOfDetailSwitchingDistance", "DFIntCSGLevelOfDetailSwitchingDistanceL12",
            "DFIntCSGLevelOfDetailSwitchingDistanceL23", "DFIntCSGLevelOfDetailSwitchingDistanceL34",
        };

        /// <summary>
        /// The one flag Lantern writes that is NOT on the published list: the frame-rate target that frame limiters have
        /// long used. Roblox says unlisted flags are ignored with no further action, so this is harmless where it isn't
        /// honored, and it is the thing that makes a low lock like 12 fps work where it still does.
        /// </summary>
        public const string TargetFps = "DFIntTaskSchedulerTargetFps";

        /// <summary>Overridable so tests can use a throw-away folder.</summary>
        public static string VersionsDir { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions");

        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        static readonly object Gate = new object();

        public static bool Pending => File.Exists(Paths.FlagsBackup);

        public sealed class Entry
        {
            public string Path { get; set; } = "";
            public bool Existed { get; set; }
            public bool CreatedDir { get; set; }
            public string Original { get; set; }
            public string Written { get; set; }
            /// <summary>For each flag Lantern set: the JSON text of what was there before (null = nothing).</summary>
            public Dictionary<string, string> Keys { get; set; } = new Dictionary<string, string>();
        }

        public sealed class Backup { public List<Entry> Files { get; set; } = new List<Entry>(); }

        static IEnumerable<string> PlayerFolders()
        {
            var official = Directory.Exists(VersionsDir)
                ? Directory.GetDirectories(VersionsDir).Where(d => File.Exists(System.IO.Path.Combine(d, "RobloxPlayerBeta.exe")))
                : Enumerable.Empty<string>();
            // Lantern's own builds too: that's what its windows run from (see RobloxBuild)
            return official.Concat(RobloxBuild.Folders()).ToList();
        }

        /// <summary>
        /// Writes the flags Lantern has in place again, so a Roblox build that arrived since (a new one of Lantern's own) gets them
        /// too. Nothing happens when Lantern has no flags written.
        /// </summary>
        public static bool Refresh(out string note)
        {
            note = "";
            Dictionary<string, string> flags;
            lock (Gate)
            {
                try
                {
                    if (!File.Exists(Paths.FlagsBackup)) return true;
                    var bk = Json.Deserialize<Backup>(File.ReadAllText(Paths.FlagsBackup));
                    var e = bk.Files.FirstOrDefault(f => f.Keys.Count > 0 && !string.IsNullOrEmpty(f.Written));
                    if (e == null) return true;
                    var written = Json.DeserializeObject(e.Written) as Dictionary<string, object>;
                    if (written == null) return false;
                    flags = e.Keys.Keys.Where(written.ContainsKey).ToDictionary(k => k, k => Convert.ToString(written[k], System.Globalization.CultureInfo.InvariantCulture));
                }
                catch (Exception ex) { Logger.Write("EngineFlags.Refresh: " + ex.Message); return false; }
            }
            return flags.Count == 0 || Apply(flags, out note);
        }

        static string FileIn(string versionDir) => System.IO.Path.Combine(versionDir, "ClientSettings", "ClientAppSettings.json");

        // apply
        /// <summary>Makes <paramref name="flags"/> the set Lantern has written (adding new ones, removing ones it wrote earlier that are no longer wanted).</summary>
        public static bool Apply(IDictionary<string, string> flags, out string note)
        {
            lock (Gate)
            {
                note = "";
                try
                {
                    foreach (var k in flags.Keys)
                        if (!Allowed.Contains(k) && k != TargetFps) throw new InvalidOperationException("Refusing to write a flag that isn't allowed: " + k);

                    var folders = PlayerFolders().ToList();
                    if (folders.Count == 0) { note = "Roblox's player folder wasn't found, so no engine flags were written."; return false; }

                    var bk = File.Exists(Paths.FlagsBackup) ? Json.Deserialize<Backup>(File.ReadAllText(Paths.FlagsBackup)) : new Backup();
                    var plan = new List<(Entry entry, Dictionary<string, object> dict)>();

                    foreach (var dir in folders)
                    {
                        var path = FileIn(dir);
                        var entry = bk.Files.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
                        var existed = File.Exists(path);
                        string text = existed ? File.ReadAllText(path) : null;

                        Dictionary<string, object> dict;
                        if (existed && !string.IsNullOrWhiteSpace(text))
                        {
                            try { dict = Json.DeserializeObject(text) as Dictionary<string, object>; }
                            catch { dict = null; }
                            if (dict == null) { Logger.Write("EngineFlags: unreadable " + path + ", left alone"); continue; }   // never clobber a file we can't read
                        }
                        else dict = new Dictionary<string, object>();

                        if (entry == null)
                        {
                            entry = new Entry { Path = path, Existed = existed, Original = text, CreatedDir = !Directory.Exists(System.IO.Path.GetDirectoryName(path)) };
                            bk.Files.Add(entry);
                        }

                        // flags we wrote earlier that aren't wanted any more go back to what they were
                        foreach (var k in entry.Keys.Keys.Where(k => !flags.ContainsKey(k)).ToList())
                        {
                            Revert(dict, k, entry.Keys[k]);
                            entry.Keys.Remove(k);
                        }
                        foreach (var kv in flags)
                        {
                            if (!entry.Keys.ContainsKey(kv.Key))
                                entry.Keys[kv.Key] = dict.ContainsKey(kv.Key) ? Json.Serialize(dict[kv.Key]) : null;
                            dict[kv.Key] = kv.Value;
                        }
                        plan.Add((entry, dict));
                    }

                    // remember first, then change
                    Paths.Ensure();
                    foreach (var (entry, dict) in plan) entry.Written = Json.Serialize(dict);
                    File.WriteAllText(Paths.FlagsBackup, Json.Serialize(bk), new UTF8Encoding(false));

                    foreach (var (entry, dict) in plan)
                    {
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(entry.Path));
                        File.WriteAllText(entry.Path, entry.Written, new UTF8Encoding(false));
                    }
                    note = "Engine flags written for " + plan.Count + (plan.Count == 1 ? " Roblox version." : " Roblox versions.");
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Write("EngineFlags.Apply: " + ex.GetType().Name + " " + ex.Message);
                    note = "Couldn't write the engine flags: " + ex.Message;
                    return false;
                }
            }
        }

        static void Revert(Dictionary<string, object> dict, string key, string originalJson)
        {
            if (originalJson == null) dict.Remove(key);
            else dict[key] = Json.DeserializeObject(originalJson);
        }

        // restore
        /// <summary>Takes out everything Lantern wrote. The file ends up byte-for-byte as it was, or gone if Lantern created it.</summary>
        public static bool Restore(out string note)
        {
            lock (Gate)
            {
                note = "";
                try
                {
                    if (!File.Exists(Paths.FlagsBackup)) { note = "No engine flags to remove."; return true; }
                    var bk = Json.Deserialize<Backup>(File.ReadAllText(Paths.FlagsBackup));

                    foreach (var e in bk.Files)
                    {
                        try
                        {
                            if (!File.Exists(e.Path)) continue;   // Roblox updated and removed that version folder
                            var current = File.ReadAllText(e.Path);

                            if (current == e.Written)
                            {
                                if (e.Existed) File.WriteAllText(e.Path, e.Original ?? "", new UTF8Encoding(false));
                                else File.Delete(e.Path);
                            }
                            else
                            {
                                // someone else edited it since: undo only our own flags
                                var dict = Json.DeserializeObject(current) as Dictionary<string, object> ?? new Dictionary<string, object>();
                                foreach (var kv in e.Keys) Revert(dict, kv.Key, kv.Value);
                                if (dict.Count == 0 && !e.Existed) File.Delete(e.Path);
                                else File.WriteAllText(e.Path, Json.Serialize(dict), new UTF8Encoding(false));
                            }

                            var dirPath = System.IO.Path.GetDirectoryName(e.Path);
                            if (e.CreatedDir && Directory.Exists(dirPath) && !Directory.EnumerateFileSystemEntries(dirPath).Any()) Directory.Delete(dirPath);
                        }
                        catch (Exception ex) { Logger.Write("EngineFlags.Restore " + e.Path + ": " + ex.Message); }
                    }
                    File.Delete(Paths.FlagsBackup);
                    note = "Engine flags removed.";
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Write("EngineFlags.Restore: " + ex.GetType().Name + " " + ex.Message);
                    note = "Couldn't remove the engine flags: " + ex.Message;
                    return false;
                }
            }
        }
    }
}
