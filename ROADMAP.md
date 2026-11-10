# Roadmap

What is built, what is next, and what is deliberately not being worked on yet.

This is a plan, not a promise. There are no dates: it is a personal project built in the
evenings, and the order below is the order things depend on each other in, which is a more
useful thing to know than a guess at a month.

---

## Built and running

Installed and exercised on a real Windows machine against Bedrock Dedicated Server builds from
1.26.36.1 through 1.26.61.1. See the README for what "verified" does and does not mean.

- **The service and the panel.** A Windows Service serving a browser panel over HTTPS behind a
  login, a self-contained installer, and self-update with a health gate that rolls back a build
  which will not start.
- **Server supervision.** One or more `bedrock_server.exe` instances, each with a live console,
  a crash watchdog, rolling transcripts, and a graceful drain that warns players before any stop.
- **Players.** Sessions, playtime and one profile per player across every server, with allowlist
  and operator editing per server in each server's own files.
- **Backups.** ZIP archive or folder copy, optionally from a Volume Shadow Copy, or a custom
  command, on a schedule or on demand, with restore, retention and notes.
- **Bedrock updates.** Watches Mojang's feed, warns players on a countdown, applies, and rolls
  back a build that will not start. Pinning, Preview, and manual install of a downloaded build.
- **Settings and status.** Export and import without credentials or certificate secrets, a status
  page, and a readiness panel that checks the host.
- **Add-ons.** A shared library and a tab per server that switches packs on and off, orders them,
  edits their settings and says why an enabled pack does nothing.

---

## Built, partly verified on Windows

- **The privilege broker.** The panel's service runs under its own virtual account instead of
  LocalSystem. A second service, the broker, runs as LocalSystem and does the administrative
  work from a fixed, enumerated list: firewall rules, Defender exclusions, the loopback
  exemption, shadow copies and self-update. There is no general "run this command" operation.
  Each Bedrock server runs at low integrity, out of reach of the panel's secrets.
- **NetherNet by default.** New servers use NetherNet with a pinned play window; firewall rules
  follow a server's ports and name, and a reachability check per server offers a fix.
- **An authenticator-app sign-in step**, off by default, also asked before destructive steps.
- **The companion behavior pack.** Ships with BedrockSentry and reports through the server's
  console: deaths per player per server, and where each player was when they left or when a
  planned stop took the server down.
- **Player faces** beside each name, from GeyserMC or set by hand.
- **Creative sister servers.** A creative server takes a survival server's newest backup and
  keeps chosen areas of its own, set from the panel or with the companion pack's `syncarea`
  command and outlined in game. The sync from the panel is verified; the in-game command and
  outlines are not yet.

## Built, not yet verified on Windows

- **The companion pack's operator commands** (`listcord`, `syncarea`) and area outlines in game.
- **The Storage table's drive type and deduplication columns**, and its low-space warning.

---

## Next

### In-game chat commands

Needs game chat to reach the wrapper first. Bedrock's dedicated server prints no player chat to
its console, and the one script event that carries chat is in Mojang's pre-release scripting
API, which a world has to switch on as an experiment. So the companion pack does not relay chat,
and how chat will reach the wrapper is still being decided.

The command system itself will be a permissioned one — parse, check permission, execute,
reply — built as something other front ends can plug into rather than as a one-off, because
two more are planned.

### Telegram and Discord

Admin commands and player notifications from outside the panel. Both share the command
dispatcher from the chat-command work rather than reimplementing parsing and permissions, so
neither starts before that exists.

---

## Before a stable release

### A presentation pass

The panel's wording is uneven, and some of it reads to whoever wrote it rather than to whoever
is using it. Not a redesign — the shape is right.

### Windows acceptance of the newer work

Finishing the Windows checks of everything in the two sections above, and the self-update drills
against the two-service install.

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
