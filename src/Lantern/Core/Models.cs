using System;
using System.Collections.Generic;

namespace Lantern.Core
{
    /// <summary>Persisted account. Serialised as one encrypted blob (see <see cref="Store"/>).</summary>
    public class AccountData
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public long UserId { get; set; }
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Alias { get; set; } = "";
        /// <summary>Optional group name ("Farm", "Alts"...). Empty = ungrouped.</summary>
        public string Group { get; set; } = "";
        public string Cookie { get; set; } = "";
        public string TrackerId { get; set; } = "";
        public string LastGame { get; set; } = "";
        public long LastPlaceId { get; set; }
        public long LastLaunchTicks { get; set; }
        /// <summary>Where the last window was sent (kept so a window still open when Lantern restarts can be rejoined to the same place).</summary>
        public LaunchTarget LastTarget { get; set; }
        public long AddedTicks { get; set; } = DateTime.UtcNow.Ticks;
    }

    public class GameData
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public long PlaceId { get; set; }
        public long UniverseId { get; set; }
        public string Name { get; set; } = "";
        /// <summary>The raw text the user pasted (keeps private-server links intact).</summary>
        public string Raw { get; set; } = "";
        public bool Private { get; set; }
        public string IconFile { get; set; } = "";
    }

    /// <summary>The user's own low-resource settings (the "Custom" mode). Defaults start from Minimal.</summary>
    public class PerfCustom
    {
        /// <summary>Roblox's manual graphics level, 1 (lowest) .. 10.</summary>
        public int Quality { get; set; } = 1;
        public int FrameRate { get; set; } = 15;
        public bool Mute { get; set; } = true;
        public int WindowWidth { get; set; } = 480;
        public int WindowHeight { get; set; } = 300;
        public bool Eco { get; set; } = true;
        // Engine tweaks. Each one is a flag from Roblox's published allowlist (see EngineFlags).
        public bool LowTextures { get; set; } = true;
        public bool NoAntiAliasing { get; set; } = true;
        public bool NoGrass { get; set; } = true;
        public bool LowDetailMeshes { get; set; } = true;
        public bool PauseLighting { get; set; } = true;
        public bool ForceLowestLevel { get; set; } = true;
        /// <summary>Default | D3D11 | Vulkan | OpenGL</summary>
        public string GraphicsApi { get; set; } = "Default";
    }

    public class SettingsData
    {
        public bool MultiInstance { get; set; }
        public int StaggerSeconds { get; set; } = 6;
        public bool AutoRejoin { get; set; }
        public int RejoinDelaySeconds { get; set; } = 15;
        public int MaxRejoins { get; set; } = 4;
        /// <summary>Opt-in: a window Roblox removed for being idle (code 278) stays open on its error screen; close it and rejoin the place (see InstanceManager.WatchIdleKicksAsync).</summary>
        public bool RejoinAfterIdleKick { get; set; }
        /// <summary>Normal | BelowNormal | Idle</summary>
        public string Priority { get; set; } = "Normal";
        /// <summary>System | Light | Dark</summary>
        public string Theme { get; set; } = "System";
        /// <summary>Animated transitions (switching theme, moving between settings sections). Also needs Windows' own animations on.</summary>
        public bool Animations { get; set; } = true;
        /// <summary>The first-run setup has been completed (or skipped) and the disclaimer accepted.</summary>
        public bool SetupDone { get; set; }
        /// <summary>The last Settings section that was open (Launching | Online | Performance | Appearance | Privacy | General | Credits).</summary>
        public string SettingsSection { get; set; } = "Launching";
        /// <summary>Show the animated lantern while Lantern starts and checks the accounts.</summary>
        public bool ShowSplash { get; set; } = true;
        /// <summary>Off | Balanced | Minimal: how lean the Roblox windows Lantern starts are (see Perf).</summary>
        public string PerfMode { get; set; } = "Off";
        public bool MinimizeAfterLoad { get; set; }
        /// <summary>Opt-in: every minute, return the idle memory of minimised windows to Windows (see InstanceManager.TrimMinimizedAsync).</summary>
        public bool TrimMinimized { get; set; }
        /// <summary>Opt-in (Live page): about once a minute, also return the idle memory of windows that are open, not minimised; no ceiling, the game takes back what it uses (see InstanceManager.TrimVisibleAsync).</summary>
        public bool TrimVisible { get; set; }
        /// <summary>Opt-in: a minimised window may keep at most <see cref="CapMb"/> in physical memory until it is restored (see InstanceManager.WatchCapsAsync).</summary>
        public bool CapMinimized { get; set; }
        public int CapMb { get; set; } = 1000;
        /// <summary>Pulse page range: 1h | 24h | 7d | 30d, and all (Uptime view only).</summary>
        public string PulseRange { get; set; } = "24h";
        /// <summary>Pulse page view: overview | uptime | board.</summary>
        public string PulseView { get; set; } = "overview";
        /// <summary>Live page → Stream mode: what it hides. Stream mode itself is never saved, so Lantern always opens with its rail.</summary>
        public bool StreamBlurNames { get; set; } = true;
        public bool StreamHideRail { get; set; } = true;
        public bool StreamHideTitle { get; set; } = true;
        /// <summary>Take the Roblox windows' buttons off the taskbar while Lantern runs: they are reached through the Live tiles.</summary>
        public bool HideFromTaskbar { get; set; }
        /// <summary>Live page: accounts that aren't online sit in the grid as sleeping tiles that start on a click.</summary>
        public bool LiveShowOffline { get; set; } = true;
        /// <summary>Keep Documents\Lantern Pulse\pulse.json up to date (names and numbers only, no cookies) so the numbers can be read outside Lantern.</summary>
        public bool PulseReportFile { get; set; } = true;
        /// <summary>Also write Roblox's allowlisted engine flags for Minimal and Custom (they are removed again afterwards).</summary>
        public bool UseEngineFlags { get; set; } = true;
        public PerfCustom Custom { get; set; } = new PerfCustom();
        public bool TrayIcon { get; set; }
        public bool CloseToTray { get; set; }
        /// <summary>Wipe the clipboard after cookies pasted from it were added.</summary>
        public bool ClearClipboard { get; set; } = true;
        public string LastTarget { get; set; } = "";
        public string LastJobId { get; set; } = "";
        public string LastBackup { get; set; } = "";
        public double WindowWidth { get; set; } = 1040;
        public double WindowHeight { get; set; } = 680;
        public double WindowLeft { get; set; } = double.NaN;
        public double WindowTop { get; set; } = double.NaN;
    }

    /// <summary>A saved "these accounts, this game" launch.</summary>
    public class ProfileData
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        /// <summary>AccountData.Id values (stable across renames and re-sign-ins).</summary>
        public List<string> AccountIds { get; set; } = new List<string>();
        /// <summary>What was in the launch bar: place id, game link, private-server or share link.</summary>
        public string TargetRaw { get; set; } = "";
        public string JobId { get; set; } = "";
        /// <summary>Second private-server link, tried when the first one is full.</summary>
        public string BackupRaw { get; set; } = "";
        public string GameName { get; set; } = "";
        /// <summary>Empty = follow the global setting; otherwise Off | Balanced | Minimal.</summary>
        public string PerfMode { get; set; } = "";
    }

    public class ProfilesFile
    {
        public List<ProfileData> Profiles { get; set; } = new List<ProfileData>();
    }

    public class GamesFile
    {
        public List<GameData> Games { get; set; } = new List<GameData>();
    }

    /// <summary>Where an account should be sent.</summary>
    public class LaunchTarget
    {
        public long PlaceId { get; set; }
        public string JobId { get; set; } = "";
        /// <summary>Classic private-server link code (privateServerLinkCode=...).</summary>
        public string LinkCode { get; set; } = "";
        /// <summary>New-style share link code (roblox.com/share?code=...&amp;type=Server).</summary>
        public string ShareCode { get; set; } = "";
        public string GameName { get; set; } = "";
        /// <summary>
        /// Set when the target is "wherever this player is": the join is then asked of Roblox as "follow this user", the same request
        /// the Join button on a friend's profile makes, so Roblox itself decides whether the account may enter (a friend's private
        /// server with friends allowed included). A server id alone is refused for private servers (error 524).
        /// </summary>
        public long FollowUserId { get; set; }
        /// <summary>A second private server (never itself carrying a backup) for when this one is full or won't let the account in.</summary>
        public LaunchTarget Backup { get; set; }

        public bool IsPrivate => !string.IsNullOrEmpty(LinkCode) || !string.IsNullOrEmpty(ShareCode);
        public bool IsValid => PlaceId > 0 || !string.IsNullOrEmpty(ShareCode);

        public LaunchTarget Clone() => (LaunchTarget)MemberwiseClone();
    }

    public enum AccountState
    {
        Idle,
        Checking,
        Queued,
        Launching,
        Running,
        Rejoining,
        Closing,
        Expired,
        Error
    }
}
