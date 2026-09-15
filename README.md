# BedrockSentry

A Windows Server wrapper and manager for Minecraft Bedrock Dedicated Server
(BDS).

BedrockSentry runs as a Windows Service and exposes a browser-based control
panel over HTTPS, managing one or more BDS instances as child processes. It
adds what the vanilla server does not have on its own: multi-strategy backups
with orchestrated restore, player history across every server it manages, and
self-updating for both the Minecraft binary and the wrapper itself. A
permissioned in-game command system and a companion behavior pack for deeper
event insight are designed and not yet built — see the status below.

## Status

**Not production ready.** Every release so far is a prerelease on the `build`
channel; nothing has been tagged stable on `prod`. Until a build lands there,
treat this as pre-release software whatever the feature list below says.

That said, it is not a paper project either. Everything below has been installed
and exercised on a real Windows box against Bedrock Dedicated Server builds from
1.26.36.1 through 1.26.45.1, with newer builds pulled through the update path
along the way. Use it on a server you can restore from a backup; that is what
the backups are for.

**Working, and verified on Windows:**

- Runs as a Windows Service, serves the panel over HTTPS behind a login, ships
  as a self-contained installer, and updates itself with health-gated rollback.
- Launches and supervises one or more `bedrock_server.exe` instances: a live
  console per server, a crash watchdog with a measured backoff ladder, rolling
  transcripts, and a graceful drain that warns players before any stop.
- Records player sessions off the console — who is on, who has been, playtime —
  with one profile per player across every server, and allowlist and operator
  editing per server with the server's own files as the source of truth.
- Backups by ZIP, by Volume Shadow Copy, or by a custom command, on a schedule
  or on demand, with restore, retention, notes, and the server's configuration
  carried inside each archive. `save resume` is unconditional: a failed backup
  never leaves a live server with saves paused.
- Bedrock updates: watches Mojang's feed, downloads, warns players on a
  countdown, applies, and rolls back a build that will not start. Pinning,
  Preview, and manual install of any build in the store.
- Settings export and import, with credentials and certificate secrets kept out
  of the file. A status page with memory, CPU and last backup, and a readiness
  panel that checks the Volume Shadow Copy service, the account the service runs
  as, and whether each server's ports are free.

**Known issues:**

- **The executables are unsigned**, so Windows SmartScreen will warn on first run. Choose
  *More info* → *Run anyway*, or unblock the downloaded zip before extracting. Every release
  publishes its SHA-256 in the release notes and in `version.json`; check that if you want to
  verify what you have. Code signing is a separate decision and is not planned for a
  particular release.

**Designed, not built:** in-game chat commands, the companion behavior pack,
the add-on manager, and the Telegram and Discord bots.

**What "verified on Windows" means here.** Every feature listed above has been
exercised by hand on a real Windows machine against a real Bedrock server —
installed, run, and in most cases deliberately broken to see what the panel says
when it fails. Nothing is listed as working on the strength of a passing build.
It does not mean every combination of settings has been tried, and it is not a
substitute for having a backup you have actually restored from once.

See [`ROADMAP.md`](ROADMAP.md) for what is next and what is deliberately not
planned, [`docs/design.md`](docs/design.md) for the technical design, and
[`CHANGELOG.md`](CHANGELOG.md) for what changed in each release.

## Installing

See [`docs/windows-setup.md`](docs/windows-setup.md). No .NET runtime is needed
on the server — releases are self-contained.

## About the Bedrock Dedicated Server binary

**The BDS binary is not included in this repository and never will be.**
Minecraft's usage guidelines require every copy of the server to come from
Mojang directly, so BedrockSentry downloads it from Mojang at install time and
keeps it updated from there. This repository contains wrapper code only.

## Branches

- `prod` — stable, tagged releases; what the wrapper's self-update tracks by
  default
- `build` — active development. Selectable in the wrapper's update settings, but
  meant for test servers you have backups of, not for a world you care about

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
every copy has to come from Mojang, so BedrockSentry fetches it on your machine
rather than shipping it.

Release archives are self-contained and therefore carry a copy of the .NET
runtime and the libraries the wrapper uses. Their licences and notices are in
[`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt), which ships inside every
release. Everything there is MIT, Apache-2.0 or public domain; nothing copyleft.

The name is not part of the grant. MIT covers copyright, not trademarks, so a
fork is welcome to the code but should pick its own name.
