# Security

Lantern holds the one thing that matters most for a Roblox account: its `.ROBLOSECURITY` cookie. Whoever has that cookie is signed in as that account, with no password or 2-step prompt. This page says exactly how Lantern handles it, what it can't defend against, and how to check the claims yourself.

## Reporting a problem

Please don't open a public issue for a security bug. Use **Security → Report a vulnerability** on the GitHub repository, or message **foxems** on Discord. Don't send real cookies; a description or a proof of concept against a throw-away account is enough. I'll answer as soon as I can, but this is a hobby project, so there is no fixed response time.

## What is stored, and where

Everything lives in `%APPDATA%\Lantern` unless you set `LANTERN_HOME`.

| File | Contents | Encrypted |
|---|---|---|
| `accounts.dat` | Accounts, **including their cookies**, last game and group | Yes, Windows DPAPI (current user) plus app entropy |
| `accounts.dat.bak` | The previous good copy of the above | Yes, same |
| `settings.json` | Your settings, window position | No, holds no secrets |
| `games.json` | Saved games (names, place ids, links you pasted) | No |
| `profiles.json` | Launch profiles: account ids, game, **private-server links** | No |
| `stats.json` | Session history: account names, game names, start and end times | No, no cookies or tickets |
| `perf-backup.json`, `flags-backup.json` | What Lantern changed in Roblox's settings, so it can undo it | No |
| `lantern.log` (+ `.old`) | Short log, capped at 256 KB | No, cookies and tickets are blanked |
| `cache\` | Avatar and game icon images | No |

Private-server links are not cookies, but anyone who has one can join that server, so treat `profiles.json` as semi-private.

Outside that folder:

- `Documents\Lantern Pulse\pulse.json` (optional, on by default): the Pulse numbers, account names and counts only. It sits outside the data folder on purpose, so it can be shared without going near the account file.
- `%LOCALAPPDATA%\Lantern\Roblox`: Lantern's copy of the Roblox client.
- `%TEMP%\Lantern-signin-*`: the sign-in window's browser profile, only while that window is open. It is cleared and deleted when it closes, and any left behind by a crash are removed at the next start.

**Settings → Privacy & data → Erase everything** deletes the data folder and the Pulse report folder. If another program keeps one of the files open (a backup tool, antivirus), Lantern tells you which and stays open instead of quitting, so you never believe the accounts are gone when they aren't. It also refuses to erase a data folder that is a drive root or holds your profile or Windows (possible if `LANTERN_HOME` points there); delete Lantern's files from such a folder yourself.

## How the cookie is protected

- **At rest.** `accounts.dat` is encrypted with `ProtectedData.Protect` in `CurrentUser` scope, with application entropy. Another Windows user, or the same file on another PC, can't decrypt it. Writes go to a temp file, are flushed to disk, and are then swapped in atomically, so a crash or power cut can't leave a half-written file. A damaged or hand-edited file is read defensively (empty entries are skipped, missing text becomes empty). See `Core/Store.cs`.
- **In transit.** `RobloxApi.SendAsync` throws if a cookie would be attached to anything but an `https://*.roblox.com` address, refuses a cookie that isn't a plain single token (no line breaks, no `;`, printable ASCII only), turns automatic redirects off and stops at a redirect that leaves Roblox. See `Core/RobloxApi.cs`.
- **Sign-in window.** WebView2 in InPrivate mode on a throw-away profile. Navigation outside roblox.com is cancelled, DevTools, autofill and password saving are off, and only the `.ROBLOSECURITY` cookie is read out. Its cookies are deleted when the window closes. See `UI/LoginWindow.cs`.
- **Logs and reports.** No code path writes a cookie or a launch ticket to the log, the health report or the Pulse report. As a second line of defence the logger blanks anything that looks like a cookie before it writes.
- **Clipboard.** After accounts are added from pasted text, a cookie left on the clipboard is cleared (Settings → Privacy & data, on by default).
- **Memory.** The decrypted bytes of the account file are zeroed after use. Cookie strings that are in use stay in memory like any .NET string; they can't be wiped.
- **No other network.** Lantern talks to `*.roblox.com` and, for avatars, game icons and Roblox's client files, `*.rbxcdn.com`. There is no server of mine, no analytics, no crash reporter and no updater. The client download is checked against the size and MD5 in Roblox's own package list, unpacking refuses any path that would land outside the build folder, and Lantern only keeps its copy of the client if Windows confirms that `RobloxPlayerBeta.exe` carries a valid signature from "Roblox Corporation" and is unchanged since it was signed (`Core/CodeSignature.cs`). The launch ticket must be a single plain token before it is put on the client's command line. Lantern loads its native libraries from System32 only.
- **Privileges.** Lantern never asks for administrator rights.

## What Lantern can't defend against

- **Malware that runs as you.** DPAPI protects the file from other users and other machines. A program running under your own Windows login can call the same Windows function and get the cookies out, exactly as it can with your browser's saved sessions. The app entropy in the source is public, so it is no obstacle to that. If your PC is compromised, so are your accounts.
- **Someone at your unlocked PC.** Same reason.
- **The launch ticket.** When a window starts, Roblox's own launch ticket is on the process command line for about a minute. It is single use and is the same ticket the website hands the client when you press Play.
- **Roblox itself.** Lantern can't stop Roblox from acting on an account, and it can't promise how Roblox treats running several windows.

## If you think a cookie leaked

1. On Roblox, go to Settings → Security and choose **Sign out of all other sessions**. That invalidates every cookie issued before now.
2. Change the password and turn on 2-step verification if it isn't on.
3. Add the account to Lantern again. The *Check my accounts* button under Settings → Privacy & data reports sessions that are old, shared between accounts, or sitting on the clipboard or in a plain-text file.

## Check it yourself

- Open `accounts.dat` in any editor: it is binary and contains no account names or cookies.
- Search the source for hosts: `rg "https?://" src` shows every URL the app can contact.
- Run the self-tests (`.\build.ps1 -Test`). They cover the encrypted file, the credential guard, the cookie shape check and the log scrubbing.
- The first-run setup and the build it comes from are the same code you can read here. Build it yourself if you'd rather not trust a release zip.
