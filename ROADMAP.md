# Roadmap

What is built, what is next, and what is deliberately not being worked on yet.

This is a plan, not a promise. There are no dates: it is a personal project built in the
evenings, and the order below is the order things depend on each other in, which is a more
useful thing to know than a guess at a month.

---

## Built and running

Everything here has been installed and exercised on a real Windows machine against Bedrock
Dedicated Server builds from 1.26.36.1 through 1.26.45.1. See the README for the standing
caveat about what "verified" does and does not mean.

- **The service and the panel.** Runs as a Windows Service, serves a browser control panel
  over HTTPS behind a login, ships as a self-contained installer, and updates itself with a
  health gate that rolls back a build which will not start.
- **Server supervision.** One or more `bedrock_server.exe` instances, each with a live console,
  a crash watchdog with a measured backoff ladder, rolling transcripts, and a graceful drain
  that warns players before any stop.
- **Players.** Sessions recorded off the console — who is on, who has been, playtime — with one
  profile per player across every server, and allowlist and operator editing per server with
  each server's own files as the source of truth.
- **Backups.** ZIP, Volume Shadow Copy, or a custom command, on a schedule or on demand, with
  restore, retention, notes, and each server's configuration carried inside the archive.
- **Bedrock updates.** Watches Mojang's feed, downloads, warns players on a countdown, applies,
  and rolls back a build that will not start. Pinning, Preview, and manual install of any build
  already downloaded.
- **Settings and status.** Export and import with credentials and certificate secrets excluded,
  a status page with memory, CPU and last backup, and a readiness panel that checks the Volume
  Shadow Copy service, the account the service runs as, and whether each server's ports are free.

---

## Next

### The companion behavior pack

**The one that unblocks the most.** Bedrock's dedicated server prints no player chat to its
console, so there is no vanilla path by which a message typed in game reaches the wrapper. A
small behavior pack relaying events through the content log is the only way, and three separate
features are waiting behind it.

First use is death tracking: a full death message rather than a bare location, and a death
counter per player **per server**, which is the number that actually means something.

### In-game chat commands

Depends entirely on the pack above. A permissioned command system — parse, check permission,
execute, reply — built as something other front ends can plug into rather than as a one-off,
because two more are planned.

### Add-on manager

A file picker or folder scan for behavior and resource packs, manifest validation, and
enable/disable toggles per server.

### Telegram and Discord

Admin commands and player notifications from outside the panel. Both share the command
dispatcher from the chat-command work rather than reimplementing parsing and permissions, so
neither starts before that exists.

---

## Before a stable release

### A presentation pass

The panel's wording is uneven, and some of it reads to whoever wrote it rather than to whoever
is using it. Not a redesign — the shape is right.

### Splitting privilege

Today the whole service runs as LocalSystem, because a handful of things genuinely need
administrator rights: taking a Volume Shadow Copy, reading firewall state, checking Defender
exclusions. That is more privilege than a service exposing a web panel should hold.

The plan is to split it in two: a small privileged service exposing a **fixed, enumerated** set
of operations, and the main service — the one serving the panel — running as an ordinary account
and asking for them over a local channel. There will never be a general "run this command"
operation; that absence is the whole point, and it is why the code that needs privilege is
already written behind interfaces rather than inline.

### Hardening and the first stable tag

Broader test coverage, redistribution and EULA notes in the documentation, and the first release
tagged stable rather than build.

---

## Not planned

- **Code signing.** The executables are unsigned and Windows SmartScreen will warn on first run.
  Every release publishes its SHA-256. Signing is a cost and a process rather than a feature, and
  it is not attached to any release above.
- **Bundling the Bedrock server.** Never. Minecraft's usage guidelines require every copy to come
  from Mojang directly, so the wrapper downloads it on your machine and keeps it updated there.
- **Linux or container support.** The wrapper is built on Windows service hosting, Volume Shadow
  Copy and NTFS semantics. A port is not a porting job, it is a different program.
- **A second storage backend for player data.** SQLite is the source of truth. Manual editing is
  covered by export and import; two backends would mean every schema change happening twice, and
  the second one going stale.
