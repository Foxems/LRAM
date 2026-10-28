# Contributing

Bug reports, ideas and pull requests are welcome. Lantern is a personal project with no maintenance guarantee, so a reply or a merge can take a long time, or never come. Forks are fine.

**Bugs.** Open an issue with what you did, what you expected and what happened. Attach the relevant lines from `lantern.log` (Settings → Privacy & data → open the data folder). The log never contains cookies, but look it over before you post it anyway. Never paste a cookie, a launch ticket or the contents of `accounts.dat` into an issue.

**Security problems.** Please don't open a public issue. See [SECURITY.md](SECURITY.md).

**Pull requests.**

- Keep them small and about one thing.
- Build with `.\build.ps1 -Test` and make sure the self-tests pass. A change to cookie handling, storage or the network code should come with a test.
- Match the code around yours: naming, comment density, formatting. Comments say why, not what.
- No new dependencies without a good reason. Lantern ships WebView2 and nothing else.
- Anything that touches the Roblox client has to stay on the safe side of the line described in the README: no memory access, no injection, no editing Roblox's program files.

By contributing you agree that your work is released under the MIT license.
