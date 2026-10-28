using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Media;

namespace Lantern.Core
{
    /// <summary>Runtime wrapper around a saved account: what the list binds to.</summary>
    public sealed class AccountItem : INotifyPropertyChanged
    {
        public AccountItem(AccountData data) { Data = data; }

        public AccountData Data { get; }

        // runtime (not persisted)
        public int Pid;
        public DateTime? StartedAt;
        public DateTime? RejoinAt;
        public int Failures;
        /// <summary>What a launch or rejoin is waiting on right now ("Roblox is updating…"), shown instead of the plain status; cleared on every state change.</summary>
        public string WaitNote = "";
        public bool WantRunning;
        public bool CancelRequested;
        /// <summary>Lantern closed this window from Roblox's error screen (kick, server shutdown, maintenance, idle...); the exit that follows is rejoined with this reason.</summary>
        public ExitInfo StuckExit;
        /// <summary>The window was closed from the error screen of Roblox's idle kick (278).</summary>
        public bool IdleKicked => StuckExit?.Kind == "Idle";
        /// <summary>The error-screen code first seen in this window's log, and when (it must still be there a few seconds later).</summary>
        public int StuckSeenCode;
        public DateTime? StuckSeenAt;
        /// <summary>This window's log has been read further back once for an error screen (a window Lantern found already open).</summary>
        public bool StuckDeepChecked;
        /// <summary>Rejoins in a row caused by Roblox's or the game's side (server shut down, maintenance, network): they wait longer each time instead of giving up.</summary>
        public int OutageStreak;
        /// <summary>The private server this account was launched for (empty for a public game); a window that landed on another one is on the backup.</summary>
        public string PrimaryKey = "";
        /// <summary>The window is on the backup private server rather than the first one.</summary>
        public bool OnBackup;
        /// <summary>When Lantern last trimmed this window's idle memory (null = not trimmed while minimised).</summary>
        public DateTime? TrimmedAt;
        /// <summary>Live's trim of open windows: when it last ran, the working set just before, whether its effect has been measured, and what stayed off 30 s later.</summary>
        public DateTime? VisibleTrimAt;
        public long VisibleTrimBefore, VisibleTrimKept;
        public bool VisibleTrimMeasured = true;
        /// <summary>The memory ceiling (MB) Lantern has put on this window while it is minimised; 0 = none.</summary>
        public int CapMb;
        /// <summary>The window's own minimum working-set size, kept so the ceiling can be taken off cleanly.</summary>
        public long CapOrigMin;
        // Pulse (runtime only)
        /// <summary>When Roblox's log first showed this window inside a server (null while it is still loading, or never).</summary>
        public DateTime? JoinedAt;
        /// <summary>The window was already open when Lantern started: its log began long before, so its join is looked for differently.</summary>
        public bool PickedUp;
        /// <summary>The start of a picked-up window's log has been searched for its join once (it isn't read again).</summary>
        public bool JoinDeepChecked;
        /// <summary>What the next launch of this account is: Launch | AutoRejoin | IdleRejoin (recorded with its session).</summary>
        public string NextOrigin = "Launch";
        /// <summary>When the current launch began, before the window existed.</summary>
        public DateTime? LaunchBeganAt;
        /// <summary>Since when the window has used (almost) no CPU at all, which a live client never does; null while it works.</summary>
        public DateTime? StillSince;
        /// <summary>Committed private memory at the last reading.</summary>
        public long PrivateBytes;
        /// <summary>What Roblox's presence service last said (0 offline, 1 online, 2 in a game, 3 Studio; -1 unknown) and when.</summary>
        public int PresenceType = -1;
        public DateTime? PresenceAt;
        public LaunchTarget Target;
        /// <summary>Low-resource mode this window was launched with (Off | Balanced | Minimal).</summary>
        public string PerfMode = "Off";
        /// <summary>Why the last launch failed, in plain words (null when it didn't).</summary>
        public ErrorInfo Error;
        /// <summary>Why the last window ended, read from Roblox's log (null until one has).</summary>
        public ExitInfo Exit;
        /// <summary>Recent things that happened to this account, newest last.</summary>
        public readonly List<string> History = new List<string>();

        // live usage (runtime only)
        public double Cpu;
        public double? Gpu;
        public long WorkingSet;
        public bool HasUsage => WorkingSet > 0;
        public string UsageText =>
            WorkingSet <= 0 ? "" :
            "CPU " + Fmt.Percent(Cpu) + (Gpu.HasValue ? "  \u00b7  GPU " + Fmt.Percent(Gpu.Value) : "") + "  \u00b7  " + Fmt.Memory(WorkingSet) + (CapMb > 0 ? " (capped)" : TrimmedAt.HasValue ? " (trimmed)" : "");

        public void SetUsage(UsageSample s)
        {
            Cpu = s.Cpu; Gpu = s.Gpu; WorkingSet = s.WorkingSet; PrivateBytes = s.Private;
            Raise(nameof(UsageText)); Raise(nameof(HasUsage)); Raise(nameof(FullStatusTip));
        }

        public void ClearUsage()
        {
            if (WorkingSet == 0) return;
            Cpu = 0; Gpu = null; WorkingSet = 0; PrivateBytes = 0;
            Raise(nameof(UsageText)); Raise(nameof(HasUsage)); Raise(nameof(FullStatusTip));
        }

        public void Log(string text)
        {
            History.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + text);
            if (History.Count > 40) History.RemoveAt(0);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // identity
        public string Display => !string.IsNullOrWhiteSpace(Data.Alias) ? Data.Alias
                               : !string.IsNullOrWhiteSpace(Data.DisplayName) ? Data.DisplayName
                               : !string.IsNullOrWhiteSpace(Data.Username) ? Data.Username : "New account";
        public string Handle => string.IsNullOrEmpty(Data.Username) ? "" : "@" + Data.Username;
        public string Group => Data.Group ?? "";
        public bool HasGroup => !string.IsNullOrWhiteSpace(Data.Group);
        /// <summary>"@handle" plus the group, as shown under the name.</summary>
        public string Tagline => Handle + (HasGroup ? "  ·  " + Data.Group : "");
        public string Initial => Display.Length > 0 ? Display.Substring(0, 1).ToUpperInvariant() : "?";
        public string LastGame => string.IsNullOrEmpty(Data.LastGame) ? "—" : Data.LastGame;

        ImageSource _avatar;
        public ImageSource Avatar { get => _avatar; set { _avatar = value; Raise(nameof(Avatar)); Raise(nameof(HasAvatar)); } }
        public bool HasAvatar => _avatar != null;

        bool _selected;
        public bool IsSelected { get => _selected; set { if (_selected == value) return; _selected = value; Raise(nameof(IsSelected)); } }

        // state
        AccountState _state = AccountState.Checking;
        string _detail = "";

        public AccountState State => _state;
        public string Detail => _detail;

        public void SetState(AccountState state, string detail = "")
        {
            _state = state;
            _detail = detail ?? "";
            WaitNote = "";
            RefreshStatus();
        }

        public void RefreshStatus()
        {
            Raise(nameof(State));
            Raise(nameof(StatusText));
            Raise(nameof(StatusKind));
            Raise(nameof(IsActive));
            Raise(nameof(ActionLabel));
            Raise(nameof(ActionGlyph));
            Raise(nameof(CanAct));
        }

        public void RefreshIdentity()
        {
            Raise(nameof(Display)); Raise(nameof(Handle)); Raise(nameof(Initial)); Raise(nameof(LastGame));
            Raise(nameof(Group)); Raise(nameof(HasGroup)); Raise(nameof(Tagline));
        }

        /// <summary>Anything that should hold a window or is about to.</summary>
        public bool IsActive => _state == AccountState.Queued || _state == AccountState.Launching || _state == AccountState.Running
                                || _state == AccountState.Rejoining || _state == AccountState.Closing;

        public bool CanAct => _state != AccountState.Checking && _state != AccountState.Closing;

        public string ActionLabel
        {
            get
            {
                switch (_state)
                {
                    case AccountState.Running:
                    case AccountState.Rejoining: return "Close";
                    case AccountState.Queued:
                    case AccountState.Launching: return "Cancel";
                    case AccountState.Expired: return "Sign in";
                    default: return "Launch";
                }
            }
        }

        public string ActionGlyph
        {
            get
            {
                switch (_state)
                {
                    case AccountState.Running:
                    case AccountState.Rejoining:
                    case AccountState.Queued:
                    case AccountState.Launching: return "\uE711"; // Cancel
                    case AccountState.Expired: return "\uE8D7";   // Contact / sign in
                    default: return "\uE768";                     // Play
                }
            }
        }

        /// <summary>idle | busy | ok | bad - drives the status dot colour.</summary>
        public string StatusKind
        {
            get
            {
                switch (_state)
                {
                    case AccountState.Running: return "ok";
                    case AccountState.Expired:
                    case AccountState.Error: return "bad";
                    case AccountState.Idle: return "idle";
                    default: return "busy";
                }
            }
        }

        public string StatusText
        {
            get
            {
                switch (_state)
                {
                    case AccountState.Checking: return "Checking…";
                    case AccountState.Queued: return "Waiting its turn";
                    case AccountState.Launching: return WaitNote.Length > 0 ? WaitNote : "Launching…";
                    case AccountState.Running: return "Running · " + Uptime() + (OnBackup ? " · backup" : "");
                    case AccountState.Rejoining:
                        // the countdown is over: it is waiting for another launch (or Roblox's update) to finish
                        if (!RejoinAt.HasValue) return WaitNote.Length > 0 ? WaitNote : "Rejoining…";
                        return "Rejoining in " + Math.Max(1, (int)Math.Ceiling((RejoinAt.Value - DateTime.UtcNow).TotalSeconds)) + "s";
                    case AccountState.Closing: return "Closing…";
                    case AccountState.Expired: return "Sign-in expired";
                    case AccountState.Error: return string.IsNullOrEmpty(_detail) ? "Couldn't launch" : _detail;
                    default:
                        return Exit != null && Exit.Unexpected && !string.IsNullOrEmpty(Exit.Short) ? "Not running · " + Exit.Short : "Not running";
                }
            }
        }

        string Uptime()
        {
            if (!StartedAt.HasValue) return "now";
            var t = DateTime.UtcNow - StartedAt.Value;
            if (t.TotalMinutes < 1) return "just now";
            if (t.TotalHours < 1) return (int)t.TotalMinutes + "m";
            if (t.TotalDays < 1) return (int)t.TotalHours + "h " + t.Minutes.ToString("00") + "m";
            return (int)t.TotalDays + "d " + t.Hours + "h";
        }

        /// <summary>Hover text for the status: the whole reason, plus a hint that it can be clicked for details.</summary>
        public string FullStatusTip
        {
            get
            {
                if (_state == AccountState.Error) return (Error != null ? Error.Title : _detail) + "\nClick for details.";
                if (Exit != null && Exit.Unexpected && (_state == AccountState.Idle || _state == AccountState.Rejoining)) return Exit.Title + "\nClick for details.";
                return HasUsage && _state == AccountState.Running ? StatusText + "\n" + UsageText : StatusText;
            }
        }

        /// <summary>Is there anything worth opening a details dialog for?</summary>
        public bool HasDetails => Error != null || Exit != null || History.Count > 0;
    }
}
