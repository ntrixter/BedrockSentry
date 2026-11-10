# BedrockSentry

A Windows Server wrapper and manager for Minecraft Bedrock Dedicated Server
(BDS).

BedrockSentry runs as a Windows Service and serves a browser control panel over
HTTPS. It manages one or more Bedrock servers on one machine and adds what the
vanilla server lacks: backups and restore, player history, Bedrock and
self-updates, an add-on manager, and a companion behavior pack.

## Main features

- **A web panel behind a login.** HTTPS with a generated or your own
  certificate, a first-run setup wizard, and an optional authenticator-app code
  at sign-in and before destructive steps.
- **Many servers, supervised.** A live console per server, a crash watchdog with
  a backoff ladder, rolling transcripts, and a graceful drain that warns players
  before any stop.
- **Players.** Who is on, who has been and playtime, with one profile per player
  across every server, faces beside their names, and allowlist and operator
  editing per server in the server's own files.
- **Backups.** ZIP archive or folder copy, optionally read from a Volume Shadow
  Copy, or your own command; on a schedule or on demand, with retention, notes
  and locks. Restore in place or as a new server. A failed backup never leaves a
  live server with saves paused.
- **Bedrock updates.** Watches Mojang's feed, warns players on a countdown,
  applies, and rolls back a build that will not start. Per-server channel,
  pinning, Preview builds, and manual install of any downloaded build.
- **Self-update.** The wrapper updates itself from its release channel, with a
  health gate that rolls back a build that will not start.
- **Networking.** New servers use NetherNet with a pinned play window. Firewall
  rules follow a server's ports and name, and a reachability check per server
  says what blocks a join and offers a fix.
- **Add-ons.** A shared library for `.mcaddon`, `.mcpack` and `.zip` uploads; per
  server, switch packs on and off, order them, edit their settings, and see why
  an enabled pack does nothing. Changes made while a server runs wait for its
  next stop.
- **The companion pack.** Records deaths per player per server and where each
  player was when they left, adds operator commands such as `listcord`, and
  draws area outlines in game.
- **Creative sister servers.** A creative server takes a survival server's newest
  backup and keeps chosen areas of its own, edited from the panel or in game.
- **Least privilege.** The panel's service runs under its own virtual account. A
  small broker service does the administrative work (firewall, Defender
  exclusions, shadow copies, self-update) from a fixed list, and each Bedrock
  server runs at low integrity, out of reach of the panel's secrets.
- **Settings export and import**, with credentials and certificate secrets kept
  out of the file, and a status page with a readiness check of the host and a
  Storage table: each volume's drive type, free space with a low-space warning,
  and Data Deduplication savings where the role is installed.

## Status

**Not production ready.** Every release so far is a prerelease on the Build
channel; nothing has been released on the Stable channel.

**Verified on Windows** against Bedrock 1.26.36.1 through 1.26.61.1 and Preview
1.26.60.28: the service and panel, self-update, server supervision, players,
backups and restore, Bedrock updates, settings and status, and the add-on library.

**Built, partly verified on Windows:** the privilege broker and low-integrity
servers, NetherNet defaults with firewall and reachability checks, the
authenticator-app sign-in, the companion pack, player faces, and creative sister
servers (synced from the panel; the in-game `syncarea` command is unverified).

**Built, not yet verified on Windows:** the companion pack's operator commands
and outlines, and the Storage table's drive type and deduplication columns.

**Designed, not built:** in-game chat commands and the Telegram and Discord bots.
Game chat needs an event that is only in Mojang's pre-release scripting API.

**Known issue: the scripts and executables are unsigned.** Unblock the zip with
`Unblock-File` before extracting it; [`docs/windows-setup.md`](docs/windows-setup.md)
has the steps. Every release publishes its SHA-256 in the release notes and in
`version.json`.

"Verified on Windows" means exercised by hand on a real Windows machine against a
real Bedrock server, in most cases also broken on purpose to see what the panel
says. It does not mean every combination of settings has been tried, and it is
no substitute for a backup you have restored from once.

See [`ROADMAP.md`](ROADMAP.md) for what is next, [`docs/design.md`](docs/design.md)
for the technical design, and [`CHANGELOG.md`](CHANGELOG.md) for each release.

## Installing

See [`docs/windows-setup.md`](docs/windows-setup.md). No .NET runtime is needed
on the server — releases are self-contained.

## About the Bedrock Dedicated Server binary

**The BDS binary is not included in this repository and never will be.**
Minecraft's usage guidelines require every copy of the server to come from
Mojang directly. BedrockSentry downloads the first copy and every update from
Mojang on your machine, or you can download one from Mojang yourself. This
repository contains wrapper code only.

## Branches

- `main` — stable, tagged releases on the Stable channel; what the wrapper's
  self-update tracks by default
- `build` — active development, on the Build channel. Selectable in the wrapper's
  update settings, but meant for test servers you have backups of, not for a world
  you care about

## License

BedrockSentry is released under the MIT License — see [`LICENSE`](LICENSE). In
plain terms: you may use it, change it, redistribute it and sell it, including
inside closed-source work, with no obligation to share changes back. The one
condition is that the copyright and permission notice stay with any copy. It
comes with no warranty of any kind — read that clause before trusting it with a
world you care about.

**What the licence does not cover.** It applies to the wrapper and nothing else.
It grants no rights whatsoever to Minecraft, to the Bedrock Dedicated Server, or
to any Mojang or Microsoft asset. Running BDS means accepting Mojang's EULA and
usage guidelines, and those apply exactly as they would without BedrockSentry.
That is also why the server binary is never in this repository or in a release:
every copy has to come from Mojang, so it is downloaded on your machine rather
than shipped.

Release archives are self-contained and therefore carry a copy of the .NET
runtime and the libraries the wrapper uses. Their licences and notices are in
[`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt), which ships inside every
release. Nearly all of it is MIT, Apache-2.0 or public domain, and nothing is
copyleft. A few runtime parts, `coreclr.dll`,
`Microsoft.DiaSymReader.Native.amd64.dll` and the runtime inside the single-file
updater, are under Microsoft's .NET Library License instead; the notices file
names them and links the terms.

The name is not part of the grant. MIT covers copyright, not trademarks, so a
fork is welcome to the code but should pick its own name.
