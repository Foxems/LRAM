using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml.Linq;

namespace Lantern.Core
{
    /// <summary>What a low-resource mode changes.</summary>
    public sealed class PerfPreset
    {
        public string Name = "";
        /// <summary>Roblox's manual graphics level, 1 (lowest) .. 10.</summary>
        public int Quality;
        public int FrameRate;
        public bool Mute;
        public int WindowWidth, WindowHeight;
        /// <summary>Windows "efficiency mode" for the client process.</summary>
        public bool Eco = true;
        /// <summary>Normal | BelowNormal | Idle</summary>
        public string Priority = "Normal";

        // Engine flags (only Roblox-allowlisted ones, see EngineFlags). Nothing is written unless UseFlags is set.
        public bool UseFlags;
        public bool LowTextures, NoAntiAliasing, NoGrass, LowDetailMeshes, PauseLighting, ForceLowestLevel;
        /// <summary>Default | D3D11 | Vulkan | OpenGL</summary>
        public string GraphicsApi = "Default";

        public int FlagCount => FlagsFor(this).Count;

        /// <summary>The flags this preset asks for (empty when flags are off).</summary>
        public static Dictionary<string, string> FlagsFor(PerfPreset p)
        {
            var f = new Dictionary<string, string>();
            if (!p.UseFlags) return f;
            // frame-rate lock: also asked for through the engine's frame-rate target (see EngineFlags.TargetFps)
            if (p.FrameRate > 0) f[EngineFlags.TargetFps] = p.FrameRate.ToString();
            if (p.LowTextures) { f["DFFlagTextureQualityOverrideEnabled"] = "true"; f["DFIntTextureQualityOverride"] = "0"; }
            if (p.NoAntiAliasing) f["FIntDebugForceMSAASamples"] = "1";
            if (p.NoGrass) { f["FIntFRMMinGrassDistance"] = "0"; f["FIntFRMMaxGrassDistance"] = "0"; }
            if (p.LowDetailMeshes)
            {
                f["DFIntCSGLevelOfDetailSwitchingDistance"] = "0"; f["DFIntCSGLevelOfDetailSwitchingDistanceL12"] = "0";
                f["DFIntCSGLevelOfDetailSwitchingDistanceL23"] = "0"; f["DFIntCSGLevelOfDetailSwitchingDistanceL34"] = "0";
            }
            if (p.PauseLighting) f["DFFlagDebugPauseVoxelizer"] = "true";
            if (p.ForceLowestLevel) f["DFIntDebugFRMQualityLevelOverride"] = Math.Max(1, p.Quality).ToString();
            switch (p.GraphicsApi)
            {
                case "D3D11": f["FFlagDebugGraphicsPreferD3D11"] = "true"; break;
                case "Vulkan": f["FFlagDebugGraphicsPreferVulkan"] = "true"; break;
                case "OpenGL": f["FFlagDebugGraphicsPreferOpenGL"] = "true"; break;
            }
            return f;
        }
    }

    /// <summary>
    /// Low-resource mode. Roblox reads its graphics options from %LOCALAPPDATA%\Roblox\GlobalBasicSettings_13.xml,
    /// the same file its own Settings menu writes. Before a launch Lantern writes leaner values there (graphics level,
    /// frame-rate cap, muted audio, a small start window) and remembers every original value. Optionally it also
    /// writes engine flags from Roblox's published allowlist (see <see cref="EngineFlags"/>). Everything is put back
    /// exactly when the windows are gone. No client memory is read, nothing is injected, and the running client is only
    /// given Windows' own scheduling hints (see <see cref="Native"/>).
    /// </summary>
    public static class Perf
    {
        public static readonly string[] Modes = { "Off", "Balanced", "Minimal", "Custom" };

        /// <summary>Overridable so tests can use a copy.</summary>
        public static string FilePath { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "GlobalBasicSettings_13.xml");

        static string FullBackup => FilePath + ".lantern-backup";
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        static readonly object Gate = new object();

        public static PerfPreset Preset(string mode) => Preset(mode, null);

        /// <summary>The preset for <paramref name="mode"/>; <paramref name="s"/> supplies the user's Custom values and the engine-flag switch (null = no flags).</summary>
        public static PerfPreset Preset(string mode, SettingsData s)
        {
            bool flags = s != null && s.UseEngineFlags;
            switch (mode)
            {
                case "Balanced":
                    return new PerfPreset { Name = "Balanced", Quality = 3, FrameRate = 30, Mute = true, WindowWidth = 960, WindowHeight = 540, Priority = "BelowNormal",
                                            UseFlags = flags, NoAntiAliasing = true };
                case "Minimal":
                    return new PerfPreset { Name = "Minimal", Quality = 1, FrameRate = 15, Mute = true, WindowWidth = 480, WindowHeight = 300, Priority = "Idle",
                                            UseFlags = flags, LowTextures = true, NoAntiAliasing = true, NoGrass = true, LowDetailMeshes = true, PauseLighting = true, ForceLowestLevel = true };
                case "Custom":
                    var c = (s != null ? s.Custom : null) ?? new PerfCustom();
                    return new PerfPreset
                    {
                        Name = "Custom", Quality = Clamp(c.Quality, 1, 10), FrameRate = Clamp(c.FrameRate, 5, 240), Mute = c.Mute,
                        WindowWidth = Clamp(c.WindowWidth, 200, 3840), WindowHeight = Clamp(c.WindowHeight, 150, 2160), Eco = c.Eco,
                        Priority = "Normal",              // CPU priority follows the global "Window priority" setting
                        UseFlags = flags, LowTextures = c.LowTextures, NoAntiAliasing = c.NoAntiAliasing, NoGrass = c.NoGrass,
                        LowDetailMeshes = c.LowDetailMeshes, PauseLighting = c.PauseLighting, ForceLowestLevel = c.ForceLowestLevel,
                        GraphicsApi = c.GraphicsApi ?? "Default"
                    };
                default:
                    return null;
            }
        }

        static int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));

        public static string Describe(string mode, SettingsData s = null)
        {
            var p = Preset(mode, s);
            if (p == null) return "Roblox runs exactly as it normally does.";
            string pri = p.Priority == "Idle" ? ", lowest CPU priority" : p.Priority == "BelowNormal" ? ", lower CPU priority" : "";
            string text = (p.Name == "Custom" ? "Your own settings: " : "Lowest-cost settings for every window: ") +
                          "graphics level " + p.Quality + ", " + p.FrameRate + " fps lock" + (p.Mute ? ", muted" : "") + ", " +
                          p.WindowWidth + "×" + p.WindowHeight + " start size" + (p.Eco ? ", efficiency mode" : "") + pri;
            int n = PerfPreset.FlagsFor(p).Count - (p.UseFlags && p.FrameRate > 0 ? 1 : 0);   // the frame-rate lock is already described above
            if (p.UseFlags && n > 0) text += ", plus " + n + " engine " + (n == 1 ? "tweak" : "tweaks") + " Roblox allows";
            return text + ".";
        }

        /// <summary>One sentence about the mode, with <c>**key words**</c> marked for the UI; the numbers come as <see cref="Facts"/>.</summary>
        public static string Lead(string mode)
        {
            switch (mode)
            {
                case "Balanced": return "A **middle ground**: clearly lighter than normal, still comfortable to look at.";
                case "Minimal": return "The **lowest-cost** settings for every window, made for running many accounts at once.";
                case "Custom": return "**Your own values**, set below.";
                default: return "Roblox runs **exactly as it normally does**.";
            }
        }

        /// <summary>What a mode changes, as label/value pairs (empty for Off). Settings that stay as Roblox has them are left out.</summary>
        public static List<KeyValuePair<string, string>> Facts(string mode, SettingsData s = null)
        {
            var list = new List<KeyValuePair<string, string>>();
            var p = Preset(mode, s);
            if (p == null) return list;
            void Add(string k, string v) => list.Add(new KeyValuePair<string, string>(k, v));
            Add("Graphics level", p.Quality.ToString());
            Add("Frame rate", p.FrameRate + " fps");
            Add("Window", p.WindowWidth + "×" + p.WindowHeight);
            if (p.Mute) Add("Audio", "Muted");
            if (p.Priority == "Idle") Add("CPU priority", "Lowest");
            else if (p.Priority == "BelowNormal") Add("CPU priority", "Lower");
            if (p.Eco) Add("Efficiency mode", "On");
            int n = PerfPreset.FlagsFor(p).Count - (p.UseFlags && p.FrameRate > 0 ? 1 : 0);
            if (p.UseFlags && n > 0) Add("Engine tweaks", "+" + n);
            return list;
        }

        public sealed class Backup
        {
            public Dictionary<string, string> Values { get; set; } = new Dictionary<string, string>();
            public long Ticks { get; set; }
        }

        /// <summary>True while Roblox's settings or flag files still carry anything Lantern changed.</summary>
        public static bool Pending => File.Exists(Paths.PerfBackup) || EngineFlags.Pending;

        public static bool SettingsFileExists => File.Exists(FilePath);

        // settings values
        // Scalar properties Lantern may touch; vectors are handled separately.
        static List<KeyValuePair<string, string>> ScalarsFor(PerfPreset p)
        {
            var l = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("GraphicsQualityLevel", p.Quality.ToString()),
                new KeyValuePair<string, string>("SavedQualityLevel", p.Quality.ToString()),
                new KeyValuePair<string, string>("MaxQualityEnabled", "false"),
                new KeyValuePair<string, string>("FramerateCap", p.FrameRate.ToString()),
                new KeyValuePair<string, string>("Fullscreen", "false"),
                new KeyValuePair<string, string>("StartMaximized", "false"),
            };
            if (p.Mute) l.Add(new KeyValuePair<string, string>("MasterVolume", "0"));
            return l;
        }

        static XElement Prop(XDocument d, string name) =>
            d.Descendants("Properties").Elements().FirstOrDefault(e => (string)e.Attribute("name") == name);

        static string Read(XDocument d, string key)
        {
            var el = Prop(d, key);
            if (el == null) return null;
            if (key == "StartScreenSize" || key == "StartScreenPosition")
                return (string)el.Element("X") + "," + (string)el.Element("Y");
            return el.Value;
        }

        static void Write(XDocument d, string key, string value)
        {
            var el = Prop(d, key);
            if (key == "StartScreenSize" || key == "StartScreenPosition")
            {
                if (value == null) { el?.Remove(); return; }
                var xy = value.Split(',');
                if (el == null) return;                      // never invent a vector the file doesn't have
                el.Element("X")?.SetValue(xy[0]);
                el.Element("Y")?.SetValue(xy[1]);
                return;
            }
            if (value == null) { el?.Remove(); return; }
            if (el == null)
            {
                var props = d.Descendants("Properties").FirstOrDefault();
                if (props == null) return;
                var type = value == "true" || value == "false" ? "bool" : (value.Contains(".") || key == "MasterVolume" ? "float" : "int");
                if (key == "SavedQualityLevel") type = "token";
                props.Add(new XElement(type, new XAttribute("name", key), value));
                return;
            }
            el.SetValue(value);
        }

        static XDocument Load(out bool bom)
        {
            var raw = File.ReadAllBytes(FilePath);
            bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            return XDocument.Parse(new UTF8Encoding(false).GetString(raw, bom ? 3 : 0, raw.Length - (bom ? 3 : 0)), LoadOptions.PreserveWhitespace);
        }

        static void SaveAtomic(XDocument d, bool bom)
        {
            var bytes = new UTF8Encoding(bom).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(d.ToString(SaveOptions.DisableFormatting))).ToArray();
            var tmp = FilePath + ".lantern-tmp";
            File.WriteAllBytes(tmp, bytes);
            try { File.Replace(tmp, FilePath, null, true); }
            catch { File.Copy(tmp, FilePath, true); try { File.Delete(tmp); } catch { } }
        }

        // apply / restore
        public static bool Apply(string mode, out string note) => Apply(mode, null, out note);

        /// <summary>Writes the lean values for <paramref name="mode"/>. Safe to call repeatedly; originals are remembered once.</summary>
        public static bool Apply(string mode, SettingsData s, out string note)
        {
            note = "";
            var p = Preset(mode, s);
            if (p == null) return true;

            bool okSettings = ApplySettingsFile(p, out var n1);
            string n2 = "";
            if (p.UseFlags) EngineFlags.Apply(PerfPreset.FlagsFor(p), out n2);
            else if (EngineFlags.Pending) EngineFlags.Restore(out n2);   // switched to a mode without flags: take the old ones out

            note = n1 + (n2.Length > 0 && p.UseFlags && !n2.StartsWith("Engine flags written") ? " " + n2 : "");
            return okSettings;
        }

        static bool ApplySettingsFile(PerfPreset p, out string note)
        {
            lock (Gate)
            {
                note = "";
                try
                {
                    if (!File.Exists(FilePath)) { note = "Roblox's settings file wasn't found yet. Start Roblox once, then Low-resource mode can take effect."; return false; }

                    Backup bk = File.Exists(Paths.PerfBackup) ? Json.Deserialize<Backup>(File.ReadAllText(Paths.PerfBackup)) : null;
                    bool fresh = bk == null;
                    if (fresh)
                    {
                        bk = new Backup { Ticks = DateTime.UtcNow.Ticks };
                        if (!File.Exists(FullBackup)) File.Copy(FilePath, FullBackup);
                    }

                    var doc = Load(out bool bom);
                    var scalars = ScalarsFor(p);
                    foreach (var kv in scalars)
                        if (!bk.Values.ContainsKey(kv.Key)) bk.Values[kv.Key] = Read(doc, kv.Key);
                    if (!bk.Values.ContainsKey("StartScreenSize")) bk.Values["StartScreenSize"] = Read(doc, "StartScreenSize");

                    // Remember first, then change: if anything below fails the originals are already safe.
                    Paths.Ensure();
                    File.WriteAllText(Paths.PerfBackup, Json.Serialize(bk), Encoding.UTF8);

                    foreach (var kv in scalars) Write(doc, kv.Key, kv.Value);
                    Write(doc, "StartScreenSize", p.WindowWidth + "," + p.WindowHeight);
                    SaveAtomic(doc, bom);
                    note = "Applied " + p.Name + ".";
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Write("Perf.Apply: " + ex.GetType().Name + " " + ex.Message);
                    note = "Couldn't change Roblox's settings: " + ex.Message;
                    return false;
                }
            }
        }

        /// <summary>Puts every value Lantern changed back to what it was, in both files.</summary>
        public static bool Restore(out string note)
        {
            bool okFlags = EngineFlags.Restore(out var nf);
            bool okSettings = RestoreSettingsFile(out var ns);
            note = ns.StartsWith("Nothing") && !nf.StartsWith("No engine") ? nf : ns;
            return okSettings && okFlags;
        }

        static bool RestoreSettingsFile(out string note)
        {
            lock (Gate)
            {
                note = "";
                try
                {
                    if (!File.Exists(Paths.PerfBackup)) { note = "Nothing to restore."; return true; }
                    var bk = Json.Deserialize<Backup>(File.ReadAllText(Paths.PerfBackup));

                    if (File.Exists(FilePath))
                    {
                        try
                        {
                            var doc = Load(out bool bom);
                            foreach (var kv in bk.Values) Write(doc, kv.Key, kv.Value);
                            SaveAtomic(doc, bom);
                        }
                        catch (System.Xml.XmlException)
                        {
                            // The file got damaged; fall back to the full copy taken before the first change.
                            if (File.Exists(FullBackup)) File.Copy(FullBackup, FilePath, true);
                        }
                    }
                    File.Delete(Paths.PerfBackup);
                    try { File.Delete(FullBackup); } catch { }
                    note = "Roblox's original settings are back.";
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Write("Perf.Restore: " + ex.GetType().Name + " " + ex.Message);
                    note = "Couldn't restore Roblox's settings: " + ex.Message;
                    return false;
                }
            }
        }

        /// <summary>The more aggressive of two priority names (Normal &lt; BelowNormal &lt; Idle).</summary>
        public static string Lower(string a, string b)
        {
            int Rank(string s) => s == "Idle" ? 2 : s == "BelowNormal" ? 1 : 0;
            return Rank(a) >= Rank(b) ? (a ?? "Normal") : b;
        }
    }
}
