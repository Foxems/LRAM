using System;

namespace Lantern.Core
{
    /// <summary>
    /// Why an account couldn't launch, in words a person can act on. <see cref="Title"/> is the one-liner
    /// shown in the row; <see cref="Why"/> and <see cref="Fix"/> appear in the "details" dialog.
    /// </summary>
    public sealed class ErrorInfo
    {
        public string Title = "";
        public string Why = "";
        public string Fix = "";
        /// <summary>HTTP status Roblox answered with, or 0 when not applicable.</summary>
        public int Http;
        /// <summary>Roblox or the network, not the account (no connection, rate limit, Roblox's servers failing): a rejoin waits it out instead of giving up.</summary>
        public bool Transient;
        public DateTime Time = DateTime.Now;
    }

    public static class LaunchErrors
    {
        public static ErrorInfo NoPlace() => new ErrorInfo
        {
            Title = "No place to join.",
            Why = "The launch bar doesn't contain a usable place ID or game link.",
            Fix = "Paste a place ID, a roblox.com game link or a private-server link into the launch bar."
        };

        public static ErrorInfo ShareUnresolved() => new ErrorInfo
        {
            Title = "That private server link didn't resolve.",
            Why = "Roblox couldn't turn the share link into a server for this account. The link may have expired, the owner may have reset it, or this account isn't allowed in.",
            Fix = "Open the link in a browser signed in as this account and join once. If it still fails, ask the owner for a fresh link."
        };

        public static ErrorInfo PrivateDenied() => new ErrorInfo
        {
            Title = "This account can't open that private server.",
            Why = "Private servers only admit their owner and players who joined through the owner's link. This account hasn't been let in (or the link code was reset).",
            Fix = "Open the private-server link in a browser signed in as this account. Once Roblox lets it in there, launch again from Lantern."
        };

        public static ErrorInfo PrivateFull() => new ErrorInfo
        {
            Title = "Both private servers are full.",
            Why = "Roblox didn't let this account into either private server link; a private server stops admitting players at the game's player limit.",
            Fix = "Free a slot (close an account in one of the servers) or add another server, then launch again."
        };

        public static ErrorInfo Ticket(int http, string roblox)
        {
            var e = new ErrorInfo { Http = http, Title = string.IsNullOrEmpty(roblox) ? "Couldn't get a launch ticket (HTTP " + http + ")." : roblox, Transient = http == 429 || http >= 500 };
            switch (http)
            {
                case 403:
                    e.Why = "Roblox refused the request. That is usually a security prompt on the account (captcha or verification) or a temporary block.";
                    e.Fix = "Open roblox.com in a browser with this account, finish any prompt it shows, then try again.";
                    break;
                case 429:
                    e.Why = "Roblox rate-limited the requests: too many launches in a short time.";
                    e.Fix = "Raise “Delay between launches” in Settings and wait a minute before trying again.";
                    break;
                case 415:
                    e.Why = "Roblox rejected the format of the request. It probably changed what it expects from this call.";
                    e.Fix = "Make sure you're on the latest Lantern. If you are, this needs a fix in Lantern itself.";
                    break;
                case 500: case 502: case 503: case 504:
                    e.Why = "Roblox's servers are having problems right now.";
                    e.Fix = "Wait a few minutes and try again. status.roblox.com shows outages.";
                    break;
                default:
                    e.Why = "Roblox answered the sign-in request with an unexpected status.";
                    e.Fix = "Try again. If it keeps happening, use “Check session” on the account and run the health check in Settings.";
                    break;
            }
            return e;
        }

        public static ErrorInfo Network() => new ErrorInfo
        {
            Title = "Couldn't reach Roblox.", Transient = true,
            Why = "No internet connection, a VPN or firewall blocking roblox.com, or a Roblox outage.",
            Fix = "Check your connection, then run the health check in Settings to see which part fails."
        };

        public static ErrorInfo Timeout(bool multiInstance) => multiInstance
            ? new ErrorInfo
            {
                Title = "Roblox didn't open a second window.",
                Why = "Roblox may have blocked running more than one window, or another Roblox process (including its system-tray one) still owns the single-instance lock.",
                Fix = "Close every Roblox window and the Roblox tray icon, switch “Allow multiple Roblox windows” off and on, then retry. If it keeps happening, a Roblox update may have blocked the method."
            }
            : new ErrorInfo
            {
                Title = "Roblox didn't open in time.",
                Why = "Roblox may be updating itself, the PC may be busy, or the roblox-player link handler isn't registered.",
                Fix = "Wait for any Roblox update to finish, open Roblox once from the website, then retry. The health check in Settings shows whether Roblox is installed correctly."
            };

        public static ErrorInfo Exception(Exception ex) => new ErrorInfo
        {
            Title = "Launch failed: " + Logger.Scrub(ex.Message),
            Why = "An unexpected error (" + ex.GetType().Name + ") happened while starting Roblox.",
            Fix = "Try again. If it repeats, send the line from Lantern's log file (Settings → Open folder → lantern.log); it never contains cookies."
        };

        public static ErrorInfo Generic(string title) => new ErrorInfo { Title = title };
    }
}
