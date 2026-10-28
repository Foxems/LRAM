using System.Collections.Generic;
using System.Linq;

namespace Lantern.Core
{
    /// <summary>What Lantern does about a window that Roblox disconnected with a given code.</summary>
    public enum Recovery
    {
        /// <summary>Close the window (if it sits on the error screen) and join again. Follows “Rejoin when closed”.</summary>
        Rejoin,
        /// <summary>Only with “Rejoin after an idle kick” on.</summary>
        IdleRejoin,
        /// <summary>Never rejoined: it would fight the cause or risk the account.</summary>
        Stop,
        /// <summary>A normal end; nothing to do.</summary>
        None
    }

    /// <summary>One Roblox disconnect code: what Roblox says, what it means, and what Lantern does about it.</summary>
    public sealed class DisconnectCode
    {
        public int Code;
        /// <summary>Coarse kind stored with sessions (Stats and Pulse group by it).</summary>
        public string Kind = "Code";
        public string Short = "";
        /// <summary>Roughly what Roblox's own error screen says.</summary>
        public string RobloxSays = "";
        public string Meaning = "";
        public Recovery Recovery;
        /// <summary>Roblox's or the game's side (a server going away, a network problem): rejoins back off instead of counting toward giving up.</summary>
        public bool Outage;
        /// <summary>That server no longer exists, so a rejoin goes to the game (or private-server link), not to the old server id.</summary>
        public bool ServerGone;
        /// <summary>How Lantern handles it, in plain words (the Errors page).</summary>
        public string Handling = "";
    }

    /// <summary>
    /// The disconnect codes Roblox writes to its client log ("Disconnection Notification. Reason: N"), from Roblox's published
    /// list of error codes and the logs on a real farm PC. Everything that rejoins, explains or reports an ending reads from here.
    /// </summary>
    public static class Disconnects
    {
        const string StuckRejoin = "Closed from its error screen and joined again.";

        public static readonly IReadOnlyList<DisconnectCode> All = new[]
        {
            new DisconnectCode { Code = 256, Kind = "ServerShutdown", Short = "Server closed", Recovery = Recovery.Rejoin, Outage = true, ServerGone = true,
                RobloxSays = "Developer has shut down all game servers or game server has shut down for other reasons, please reconnect.",
                Meaning = "The server was closed, usually for a game update.", Handling = StuckRejoin },
            new DisconnectCode { Code = 260, Kind = "LostConnection", Short = "Connection problem", Recovery = Recovery.Rejoin, Outage = true,
                RobloxSays = "There was a problem receiving data, please reconnect.",
                Meaning = "Data from the server stopped arriving correctly.", Handling = StuckRejoin },
            new DisconnectCode { Code = 261, Kind = "LostConnection", Short = "Connection problem", Recovery = Recovery.Rejoin, Outage = true,
                RobloxSays = "Error while receiving data, please reconnect.",
                Meaning = "Data from the server arrived damaged.", Handling = StuckRejoin },
            new DisconnectCode { Code = 262, Kind = "LostConnection", Short = "Connection problem", Recovery = Recovery.Rejoin, Outage = true,
                RobloxSays = "There was a problem sending data, please reconnect.",
                Meaning = "The client couldn't send to the server.", Handling = StuckRejoin },
            new DisconnectCode { Code = 264, Kind = "Replaced", Short = "Signed in elsewhere", Recovery = Recovery.Stop,
                RobloxSays = "Same account launched experience from different device. Reconnect if you prefer to use this device.",
                Meaning = "The same account joined from another window, device or browser.", Handling = "Not rejoined: the two sessions would keep pushing each other out. Auto-rejoin pauses for this account." },
            new DisconnectCode { Code = 266, Kind = "LostConnection", Short = "Timed out", Recovery = Recovery.Rejoin, Outage = true,
                RobloxSays = "Your connection timed out. Check your internet connection and try again.",
                Meaning = "The server stopped answering for too long.", Handling = StuckRejoin },
            new DisconnectCode { Code = 267, Kind = "Kicked", Short = "Kicked", Recovery = Recovery.Rejoin,
                RobloxSays = "You were kicked from this experience.",
                Meaning = "A script in the game removed the account (AFK rules, anti-cheat, moderation, a full server…).", Handling = StuckRejoin + " If the game keeps kicking it within a minute or two, Lantern stops after “Give up after” tries." },
            new DisconnectCode { Code = 268, Kind = "BadBehavior", Short = "Removed by Roblox", Recovery = Recovery.Stop,
                RobloxSays = "You have been kicked due to unexpected client behavior.",
                Meaning = "Roblox's own protection ended the session.", Handling = "Not rejoined: coming straight back could look like the same behaviour again. Check for other tools or overlays, then launch it yourself." },
            new DisconnectCode { Code = 272, Kind = "LostConnection", Short = "Connection error", Recovery = Recovery.Rejoin, Outage = true,
                RobloxSays = "Lost connection due to an error.",
                Meaning = "The connection broke because of an internal error.", Handling = StuckRejoin },
            new DisconnectCode { Code = 273, Kind = "Replaced", Short = "Signed in elsewhere", Recovery = Recovery.Stop,
                RobloxSays = "Disconnected from game, possibly due to game joined from another device.",
                Meaning = "The same account joined from somewhere else.", Handling = "Not rejoined, and auto-rejoin pauses for this account, so the two sessions don't fight." },
            new DisconnectCode { Code = 274, Kind = "ServerShutdown", Short = "Server shut down", Recovery = Recovery.Rejoin, Outage = true, ServerGone = true,
                RobloxSays = "The experience's developer has temporarily shut down the experience server. Please try again.",
                Meaning = "The game closed this server (update or restart).", Handling = StuckRejoin },
            new DisconnectCode { Code = 275, Kind = "Maintenance", Short = "Roblox maintenance", Recovery = Recovery.Rejoin, Outage = true, ServerGone = true,
                RobloxSays = "Roblox has shut down the server for maintenance. Please try again.",
                Meaning = "Roblox itself restarted the server. Usually every window in that server gets it at the same moment.", Handling = StuckRejoin + " When many windows get it at once they rejoin one by one." },
            new DisconnectCode { Code = 277, Kind = "LostConnection", Short = "Lost connection", Recovery = Recovery.Rejoin, Outage = true,
                RobloxSays = "Please check your internet connection and try again.",
                Meaning = "The link to the server dropped: your connection, an overloaded PC, or the server crashing.", Handling = StuckRejoin + " Back into the same server when it still exists." },
            new DisconnectCode { Code = 278, Kind = "Idle", Short = "Idle 20 min", Recovery = Recovery.IdleRejoin,
                RobloxSays = "You were disconnected for being idle 20 minutes.",
                Meaning = "No input for 20 minutes. Lantern never sends input, so a game without its own anti-AFK will do this.", Handling = StuckRejoin + " Into the game, not the old server id." },
            new DisconnectCode { Code = 279, Kind = "FailedToConnect", Short = "Couldn't connect", Recovery = Recovery.Rejoin, Outage = true, ServerGone = true,
                RobloxSays = "Failed to connect to the Game. (ID = 17: Connection attempt failed.)",
                Meaning = "The server didn't answer the join: gone, restarting, or blocked by the network.", Handling = StuckRejoin },
            new DisconnectCode { Code = 285, Kind = "Closed", Short = "Closed", Recovery = Recovery.None,
                RobloxSays = "Client initiated disconnect.",
                Meaning = "The window left on purpose (closed, or a teleport between places).", Handling = "Nothing to fix. A teleport that goes on into the next place is not treated as an ending." },
            new DisconnectCode { Code = 286, Kind = "OutOfMemory", Short = "Out of memory", Recovery = Recovery.Rejoin,
                RobloxSays = "Your device does not have enough memory to run this experience.",
                Meaning = "The PC ran out of free RAM.", Handling = StuckRejoin + " Closing it frees its memory first. If it keeps happening, use the memory cap or fewer windows." },
            new DisconnectCode { Code = 517, Kind = "FailedToConnect", Short = "Game unavailable", Recovery = Recovery.Rejoin, Outage = true, ServerGone = true,
                RobloxSays = "This experience is currently unavailable. Please try again later.",
                Meaning = "The game or server couldn't be reached at that moment.", Handling = StuckRejoin },
            new DisconnectCode { Code = 522, Kind = "FailedToConnect", Short = "Player left", Recovery = Recovery.Rejoin, ServerGone = true,
                RobloxSays = "The user you attempted to join has left the experience.",
                Meaning = "The player this account followed was already gone.", Handling = StuckRejoin + " Into the game itself instead of following that player." },
            new DisconnectCode { Code = 523, Kind = "ServerShutdown", Short = "Server closed", Recovery = Recovery.Rejoin, Outage = true, ServerGone = true,
                RobloxSays = "The status of the experience has changed and you no longer have access. Please try again later.",
                Meaning = "The (private) server was closed or its access changed.", Handling = StuckRejoin + " The private-server link opens a fresh one; if the account isn't allowed in, the backup link takes over." },
            new DisconnectCode { Code = 524, Kind = "NoAccess", Short = "Not allowed in", Recovery = Recovery.Stop,
                RobloxSays = "You do not have permission to join this experience.",
                Meaning = "This account may not enter that server or game.", Handling = "Not rejoined. When joining a player's private server by id, Lantern switches to joining through the player instead." },
            new DisconnectCode { Code = 529, Kind = "FailedToConnect", Short = "Roblox error", Recovery = Recovery.Rejoin, Outage = true,
                RobloxSays = "We are experiencing technical difficulties. Please try again later.",
                Meaning = "A web request inside the client failed, usually a Roblox outage.", Handling = StuckRejoin },
            new DisconnectCode { Code = 533, Kind = "NoAccess", Short = "Privacy settings", Recovery = Recovery.Stop,
                RobloxSays = "Your privacy settings prevent you from joining this server.",
                Meaning = "The account's own privacy settings block this join.", Handling = "Not rejoined: change the account's privacy settings on roblox.com first." },
            new DisconnectCode { Code = 600, Kind = "Banned", Short = "Banned", Recovery = Recovery.Stop,
                RobloxSays = "You are banned from this experience.",
                Meaning = "The game banned this account.", Handling = "Never rejoined: a ban doesn't go away by trying again." },
            new DisconnectCode { Code = 610, Kind = "FailedToConnect", Short = "Server gone", Recovery = Recovery.Rejoin, ServerGone = true,
                RobloxSays = "Unable to join game instance.",
                Meaning = "The exact server this account was sent to doesn't exist any more.", Handling = StuckRejoin + " Into the game itself instead of that server id." },
        };

        static readonly Dictionary<int, DisconnectCode> ByCode = All.ToDictionary(c => c.Code);

        /// <summary>The entry for a code, or null for one Lantern doesn't know.</summary>
        public static DisconnectCode Find(int code) => ByCode.TryGetValue(code, out var c) ? c : null;

        /// <summary>Back-off for rejoins after an outage: the n-th one in a row waits this long (the first just the normal delay).</summary>
        public static int OutageWaitSeconds(int streak) =>
            streak <= 1 ? 0 : streak == 2 ? 30 : streak == 3 ? 60 : streak == 4 ? 120 : streak == 5 ? 300 : 600;
    }
}
