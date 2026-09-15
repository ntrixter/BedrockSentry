# Changelog

Notable changes per release, newest first.

Two things about how this file is written, because both are deliberate:

- **Entries are written by hand, not generated from commit subjects.** A commit subject is
  addressed to whoever is working on the code; a changelog entry is addressed to whoever is
  deciding whether to install it. They are different documents and the second one is not a
  by-product of the first.
- **Fixes say what was wrong from the operator's side**, not which internal issue closed. If a
  panel said something untrue, the entry says what it said and what it says now.

Versions are `MAJOR.MINOR.PATCH` with a `build.N` suffix on the build channel. A release on the
**stable** channel carries no suffix; one on the **build** channel always does, and is meant for
a test server you have backups of.

---

## Unreleased

Nothing yet.

---

## 0.3.0 — 2026-09-15

**The first public release.** This entry describes the whole of what 0.3.0 does rather than what
changed since the last one, because there is no previous public version for it to have changed
from. Later entries will be deltas.

Read the README's caveat before installing this anywhere that matters. In short: it has been
installed and exercised on a real Windows machine against Bedrock Dedicated Server 1.26.36.1
through 1.26.45.1, by one person, in the evenings. Take backups you have tested restoring.

**It never ships Minecraft.** `bedrock_server.exe` is downloaded from Mojang at install or update
time and is not redistributed here, because their usage guidelines require every copy to come
from them directly.

### Added

- **Runs as a Windows Service with a browser control panel.** HTTPS behind a login, a
  self-contained installer that needs no .NET on the machine, and a first-run wizard. The wrapper
  updates *itself* too, behind a health gate that rolls a build back if it will not start.
- **Manages one or more servers, not just one.** Each has its own console you can type into, its
  own working directory and port, rolling transcripts of everything it printed, and a crash
  watchdog with a backoff ladder rather than a restart loop.
- **Stops servers gracefully.** Every planned stop — a scheduled restart, an update, the wrapper
  updating itself — warns whoever is online first, drains the server, and can take a backup on the
  way down. Saves are never left paused, whatever else fails.
- **Backups, three ways.** A ZIP, a Volume Shadow Copy, or a command of your own, on a schedule or
  on demand, with retention, notes, and restore. The archive also carries that server's
  `server.properties`, allowlist and permissions file, restorable separately from the world.
- **Bedrock updates, watched and applied.** It reads Mojang's feed, tells you when there is
  something newer, counts players down, applies it, and rolls back a build that will not start.
  Per server: automatic, tell-me-first, or a pin that outranks both. Preview builds and manual
  install of anything already downloaded.
- **Players.** Who is on now, who has been, and how long they have played — recorded off the
  console, one profile per player across every server, with per-server activity underneath it.
  Allowlist and operator lists are editable per server, with each server's own files as the source
  of truth: console commands while it runs, direct file edits while it is stopped.
- **A status page that answers the questions you actually ask.** Memory, CPU, last backup, whether
  the panel is reachable from outside, and a readiness panel that checks the Volume Shadow Copy
  service, the account the service runs as, and whether each server's ports are free *before* you
  press start.
- **Settings export and import**, for moving an install or keeping a copy of how it was set up.
- **It refuses to open a world twice.** If a `bedrock_server.exe` it did not start is already
  running for a server, it names the process and declines to launch over it rather than corrupting
  the world. It never kills one without being asked.
- **It refuses to put a server somewhere dangerous** — inside a Windows system folder, at a drive
  root, in a user profile, or on a network share — and every refusal names somewhere that would
  work instead.

### Security

- **The panel is HTTPS only.** A certificate is generated at install time if you do not supply
  one.
- **Credentials are stored hashed, never in plaintext** — including in the configuration file the
  first-run wizard writes.
- **Settings export cannot leak secrets.** It carries an allow-list of configuration sections
  rather than a deny-list, so the administrator credentials and the certificate's password cannot
  leave in a file you email to somebody — and the same list is enforced on import, so a
  hand-edited file cannot write them back either.
- **The detailed health endpoint is bound to loopback.** The panel listens on every interface; the
  endpoint that returns internal detail does not.
- **The custom-command backup strategy is off until an administrator turns it on** from an
  elevated prompt. It runs a command you supply, so it is the one feature that is disabled by
  default and enabled deliberately, and the setting lives in a file only administrators can write.
- **"Keep me signed in" is opt-in and off by default.** When ticked it lasts fourteen days from
  last use rather than from sign-in, and changing the password ends every session.

---

<!--
  ENTRY TEMPLATE — copy, do not reformat.

  ## 0.3.1 — 2026-01-01

  ### Added
  - What somebody can now do that they could not before.

  ### Changed
  - Behaviour that is different. Say what it did before, or the entry is unreadable
    by the person it is aimed at.

  ### Fixed
  - The symptom first, the cause second if it helps. "The removal notice named the
    wrong folder" beats "corrected TranscriptDirectory propagation".

  ### Security
  - Anything affecting who can reach what. Always its own section, never folded
    into Fixed, because people scan for this heading specifically.

  Omit a section rather than writing "none".
-->
