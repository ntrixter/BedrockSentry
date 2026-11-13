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

## 0.5.0 — 2026-11-13

### Added
- The Status page's server table has a **Uses ports** column listing every port each
  server's `server.properties` says it binds.
- Server tabs can be dragged into a new order; the Status page follows it and new servers
  go last.
- A Sync Area ticked **Ticking area** gets a ticking area over its corners after each sync.
- The New instance form says when a folder outside the data root needs
  `install.ps1 -GrantFolder` before the server can start, and hides the note once the
  folder is granted.

### Fixed
- With **Keep add-ons up to date** on, the Add-ons tab kept listing the old version after
  an update until the page was reloaded.
- `/bedrocksentry:restorearea` said a server had no backups when it had only safety
  backups; it now says it found them and why they cannot restore an area.
- A server refused at service start stayed down after the cause was fixed; it is checked
  again every 5 minutes and starts on its own, with one alert instead of one per check.
- Link refusals name the kind of link (junction, symbolic link) and its reparse tag, and a
  server folder reached through a junction is refused when it is chosen, not at its first
  start.
- A removed server's folder stayed on the privilege broker's grant list, so a new folder
  at the same path read as granted. Grants for folders that no longer exist are dropped.
- A running backup's row briefly showed *No record*. Backup history now stores its times
  in UTC; older records are rewritten at the next write.
- A `server.properties` with no settings, or none at all, shows a dash for its ports, is not
  called missing, and no longer gets firewall rules or port claims from Bedrock's defaults.
- The Sync tab crashed once a Sync Area existed; a blanked corner field kept its old value;
  the white outline colour was invisible in the light theme.
- After a service restart, a RakNet server on another port could take UDP 19132/19133 from
  the server configured for them, which then failed to start. The LAN pair is now held for
  its owner, and a start refused over it names the server holding it.
- **Create** made a server whose ports were already taken; it now reads the ports again and
  holds back over a clash.
- A start refused before anything launched read *Faulted*; it reads *Not started*. A server
  that gives up after repeated crashes still reads *Faulted*.
- The NetherNet play window offered on a new server could overlap UDP ports in use, and
  a UDP clash under NetherNet now begins *Play ports:*.
- A new server's console opened with a *properties updated* notice.
- Removing a server left its add-on run files and sync files behind.
- A label-only settings pack showed an empty settings form; it reads *Text only*.
- The LAN port guard's information lines did not reach the Windows event log.
- The Status page opened slowly while it read each volume's drive type; the Storage table
  now fills in after the page appears.

---

## 0.4.1 — 2026-11-10

### Fixed
- On Windows Server volumes with Data Deduplication, `install.ps1 -GrantFolder` refused
  server, package and backup folders as holding links, and those servers could not start
  or be backed up. Deduplicated files are now accepted; any other link is still refused.
- The Storage table read *Could not be read: Generic failure* for every deduplicated
  volume and logged a warning on each page load. The privilege broker now reads the rate.
- `uninstall.ps1 -Confirm:$false` failed through `powershell.exe -File`; `-Force` skips
  the question instead.
- Settings → Wrapper updates: changing the update channel did nothing until **Save** was
  clicked, so **Check for a BedrockSentry update** still asked the old channel. The channel
  now saves as soon as it is picked.
- The text under **Install now and restart** was drawn over the button.

---

## 0.4.0 — 2026-11-10

### Added
- A privilege broker: the panel's service runs under its own virtual account, a LocalSystem
  broker does the administrative work from a fixed list, and each Bedrock server runs at low
  integrity. An earlier install moves across by uninstalling and installing again; worlds,
  backups and settings are kept.
- An optional authenticator-app code at sign-in and before destructive steps, and
  `install.ps1 -ClearPanelMfa` for a lost phone.
- New servers use NetherNet with a pinned play window. Firewall rules follow a server's ports and
  name, and reachability checks are grouped per server with a fix for each.
- Add-on changes made while a server runs wait in a queue and are written at its next stop or
  restart.
- Player faces beside each name, from GeyserMC or set by hand.
- Creative sister servers that take another server's newest backup and keep chosen areas.
- Companion pack commands (`listcord`, `syncarea`) and area outlines drawn in game.
- Backup locks: a locked backup is never pruned.
- A Storage table on the status page: each volume the wrapper uses, its drive type (SSD, HDD
  or a Storage Space), free and used space with their share of the volume, and a warning when a
  volume runs under 15% free. On a volume with Data Deduplication it shows the rate, the saving
  and the last optimisation.

### Fixed
- The deduplication saving read as a share of the volume (*76% of the volume*); it is a share
  of the data stored, and now reads as a rate and a ratio.
- An imported `.mcworld` or ZIP was listed hours off; it is now listed at the time its world
  was last played.
- Importing a folder the service cannot read said *There is no folder*; it now says the
  service cannot read it and suggests uploading a `.zip` instead.
- Restoring a backup's configuration overwrote the server's own ports with the backup's. A
  restore now keeps `server-name`, `server-port`, `server-portv6` and the play window, and a
  copy restored as a new server plays on its own port rather than the backup's play window.
- A world with the companion pack showed an *Achievements disabled* warning. The pack is now
  marked as an add-on and its commands need no cheats, so worlds keep earning achievements.
- The Server properties tab stopped taking input on a NetherNet server with a play window.
- The new server form wrote play ports that had been cleared.
- Setting a restart schedule to Never during its countdown left the countdown running.
- A fresh install into a data root that did not exist yet failed.

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
