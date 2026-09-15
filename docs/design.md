# BedrockSentry — Technical Design

A Windows Server wrapper and manager for Minecraft Bedrock Dedicated Server (BDS). This document
is the source of truth for **what** gets built: the architecture, the contracts between the
pieces, and the reasoning behind the choices that were not obvious.

Code comments cite it by section — `docs/design.md § 7.3` — so **the numbering is a stable
contract**: a section is never renumbered, and new ones are added at the end rather than
inserted. Around 150 citations in the source depend on that.

---

## 1. Overview

BedrockSentry runs as a Windows Service on the same box as one or more
Bedrock Dedicated Server instances. It wraps each `bedrock_server.exe`
process, exposes a browser-based control panel, and adds capability the
vanilla server doesn't have on its own: multi-strategy backups with
orchestrated restore, a permissioned in-game command system, deep event
insight via a companion behavior pack, and self-updating infrastructure for
both the Minecraft binary and the wrapper itself.

## 2. Tech Stack

| Concern | Choice |
|---|---|
| Runtime | .NET 10 (LTS) |
| Web UI | ASP.NET Core + Blazor Server, real-time via SignalR |
| Hosting | Windows Service (`Microsoft.Extensions.Hosting.WindowsServices`) — runs on Server Core, no desktop shell required |
| Process management | `System.Diagnostics.Process`, redirected stdin/stdout |
| Player data | SQLite (source of truth) + export/import to JSON for manual editing |
| Backup archives | `System.IO.Compression.ZipFile`, configurable compression level |
| VSS snapshots | `vssadmin` invoked from the wrapper |
| Configuration | YAML or JSON config file — no hardcoded paths or secrets |
| License | MIT |

TLS: self-signed certificate generated automatically on first run; config
option to instead supply a real certificate (PFX + password, or a Windows
certificate store thumbprint).

**Target framework moniker (resolved as D9):** `net10.0-windows` for every
project except `BedrockSentry.Core`, which stays platform-neutral `net10.0`
since it has no Windows-specific dependency. Set once in
`Directory.Build.props`, with `Core` overriding it back down in its own
`.csproj`. Declaring the Windows dependency honestly settles CA1416
platform-compatibility warnings for the projects that touch Windows-only
APIs (Windows Service hosting, VSS, the certificate store), while keeping
`Core` provably free of that dependency and the door open to a future Linux
port of the non-Windows-specific parts.

## 3. Authentication

Required — the web UI can edit the whitelist, OP players, trigger restores,
and restart the server, so SSL alone (which secures the connection, not who's
allowed to use it) isn't enough.

- Initial approach: username and password, created during first-run setup,
  stored in an editable config file, editable later
- Store a password **hash** in that file, not the plaintext password — a
  simple config-file scheme doesn't mean the credential itself has to be
  recoverable if the file is ever read by the wrong person
- Ties into the first-run setup wizard (Section 13) as the point where
  initial credentials get set

## 4. Multi-Instance Architecture

One wrapper service process can manage several Bedrock server instances
simultaneously. This is a foundational assumption, not a bolted-on feature —
baked in starting at milestone 4, not retrofitted later.

- Each instance has its own process, port, working directory, and
  configuration (backups, add-ons, chat commands all instance-scoped).
- One Windows Service holds a collection of managed instances rather than
  one service per instance.
- **Player identity is global, activity is per-instance.** One profile per
  XUID across the whole install; every connect/disconnect event is tagged with
  the instance it happened on. This gives two views over one dataset — a
  global roster spanning every server, and a per-instance roster scoped to
  one. Section 7 has the field-by-field split and the schema.
- **Retired instances keep their history (resolved as `D14`).** Removing an
  instance at milestone 16 does not delete its sessions; the instance is
  marked retired and its rows stay. Deleting them would silently shrink every
  affected player's all-time playtime and could move their first-seen date
  forward, which makes the global view lie about the past — and the whole
  point of that view is to be the durable, all-time record. A retired
  instance keeps no per-instance page, since there is no server to manage;
  its history simply continues to count toward the global totals.
- **Retiring and removing are two different things.** Retiring is the
  reversible one described above: the config entry stays, marked retired,
  nothing on disk is touched, and the history keeps a name attached to it.
  Reversible in the UI, not only in principle — a retired instance has no
  supervisor and so appears in none of the live views, which is correct and is
  also exactly how an action labelled reversible becomes a one-way door. They
  are listed under **Retired** on the Instances page with a button that brings
  one back live, no service restart, and the `Retired` key is *removed* from the
  config rather than written back as `false`, so a retire-and-restore round trip
  leaves the file byte for byte as it was. A restored instance comes back
  **stopped** even when its auto-start is on: auto-start means "come up with the
  service", and a restore is not a service start.
  Removing takes the entry out altogether, and asks two independent questions
  on the way — delete the files and world, or leave them where they are; erase
  this instance's player history, or keep it. The answers genuinely are
  independent. Somebody rebuilding a server wants the entry gone and the world
  kept; somebody who mis-typed a name during setup wants both gone; somebody
  winding a server down for good may want the world archived and the players
  forgotten. One "delete everything?" checkbox forces two of those three to
  finish the job by hand.
- **Removal is confirmed with the administrator's password**, not by typing the
  instance name. It is the only thing the wrapper does that cannot be undone,
  and the account that owns the wrapper is the right thing to prove you are.
- **The files are deleted only if the directory looks like a server.** This is
  the most dangerous operation in the codebase — everything else it does is
  recoverable — and its input is a path somebody typed into a config file
  months earlier. The working-directory override exists precisely so a world
  can live anywhere, which means it can also point at a drive root, a documents
  folder, or BedrockSentry's own data root. So a delete is refused unless the
  directory is empty or holds something recognisably Bedrock's
  (`bedrock_server.exe`, `server.properties`, `worlds/`, the allowlist or
  permissions files), and is refused outright for a drive root, a junction, one
  of the wrapper's own folders, or any directory that *contains* one. A refusal
  costs one manual delete; being wrong costs a hard drive. The instance is
  still removed either way, and the result says plainly that the files were
  left behind and where they are.
- **A running instance is stopped properly first, never killed.** Disposing a
  supervisor kills the process — it says so, and that is right for shutdown —
  but a server killed mid-write leaves a world that may not open again. That
  matters most in exactly the case where the administrator asked to *keep* the
  files: removing an instance must not be the thing that corrupts the world it
  just handed back.
- **Instance directories are a root plus per-instance overrides.** The
  administrator picks an instance root during first-run setup, defaulting to
  `%ProgramData%\BedrockSentry\instances`. Each instance then gets a folder
  under it named after the instance. Any instance may additionally override its
  own working directory outright, so a large world can sit on a different
  volume without relocating everything else. The root is a convenience, not a
  constraint — nothing may assume an instance lives beneath it.

  The wizard checks the choice rather than taking it. A relative path is refused
  outright, because it would resolve inside the install tree that a self-update
  replaces wholesale. The folder is created and written to rather than merely
  inspected — the service runs as SYSTEM, and a share or a folder owned by
  another account reads perfectly well right up until the first world is created
  in it. Accepting the default leaves the setting *absent* rather than writing
  the resolved path, since an empty value means "under the data root" and pinning
  it would detach instances the moment anybody relocated that root.
- Wrapper self-update applies once, since it's a single process. BDS
  auto-update is per-instance, so one server can intentionally run ahead of
  another.
- The global `behavior_packs/`/`resource_packs/` folders can act as a shared
  pack library that different instances select from independently, rather
  than duplicating pack files per instance.
- **Instance binaries come from a package store (resolved as `D19`).** BDS zips
  live in `<DataRoot>\server-packages\`, named by version, and the store is
  filled two ways: an administrator drops a zip in by hand, or milestone 13's
  updater downloads into it. Creating an instance extracts a chosen package into
  that instance's working directory, so adding a server is never a manual unzip
  into the right folder — which is exactly how an instance ends up pointed
  somewhere it should not be.
- **Each instance gets its own copy of the binaries, not a shared one (`D19`).**
  BDS keeps `worlds/`, `allowlist.json`, `permissions.json` and
  `server.properties` beside its own executable, so a shared install directory
  would mean sharing the data too; separating them would mean linking individual
  files and keeping that correct across every BDS update. Roughly 100 MB per
  instance is the cheaper side of that trade, and it is what makes per-instance
  version pinning above straightforward rather than special.
- **A new instance keeps Bedrock's own `enable-lan-visibility` default, which is
  on (`D24`, superseding `D21`).** With `transport=raknet` a server binds 19132
  and 19133 whenever the setting is on, *even when `server-port` and
  `server-portv6` have non-default values* — `D21` had that right, and turned the
  setting off from the second instance onward to stop the collision. It does stop
  it, by making that server unreachable: with the setting off a server answers an
  unconnected ping with a bare RakNet pong and no server info payload, so no
  client can join it, while the wrapper goes on reporting it healthy because the
  process is up and its socket is bound. That is a worse failure than the one it
  prevents and much harder to recognise — it looks exactly like a firewall or NAT
  problem and survives every check aimed at one.
  The contention is real but narrower than `D21` assumed: the instance that loses
  the race soft-fails and runs on its own ports without retrying, so only an
  instance that needs 19132 is hurt, and only when something started before it.
  Section 5's discovery-port guard settles that at start time instead, leaving
  every instance advertising itself. The Server properties page says what turning
  the setting off would cost, rather than explaining a default the wrapper no
  longer writes.

### 4.3 Storage locations

Only the folders that grow without bound are relocatable — that is the actual
appetite ("put the big things on another volume"), and it is also what keeps the
design simple. Configuration, certificates, key material, the player database,
logs and update staging are all small and stay at the data root; the data root
itself stays put, because the config file's location derives from it and a
setting cannot describe where its own file lives. (`BEDROCKSENTRY_DATAROOT`
still exists for installs that want everything elsewhere, set before first run.)

Four movable things, three of them live with no restart at all:

- **Backups** (`Sentry:Storage:BackupsRoot`) and **server packages**
  (`Sentry:Storage:ServerPackagesRoot`), on Settings → Storage. Every consumer
  resolves these paths per operation rather than caching them, so a change
  simply decides where the *next* backup or download lands. Nothing is moved:
  files at the old location stay there and drop out of the pickers until the
  administrator moves them across — safe at any quiet moment, because nothing
  holds these directories open between operations, and the page says exactly
  that. Blank returns a folder to its default under the data root. Validation
  probes the target (create, write, delete a marker file) before anything is
  written, because "the folder is not writable" belongs beside the field, not
  in a 3 a.m. backup failure.
- **Where new servers land** (`Sentry:Instances:Root`), same page, also live.
  The write order is load-bearing: an instance with no folder of its own
  resolves against the root at load time, so every configured instance —
  retired ones included — is pinned to its currently-resolved absolute path in
  the same merge that changes the root. Existing servers never move; only
  future ones follow.
- **Each instance's own folder**, on that instance's Configuration tab. This is
  the one move that needs anything stopped, and it is the *instance*, never the
  service: the folder threads through the process, the transcript and the
  discovery guard, so the supervisor is released and re-registered against the
  new path — the same rebuild retiring and restoring already exercise. A server
  in backoff counts as running (the watchdog is about to start it). The wrapper
  re-homes the server and moves nothing; the administrator moves the contents
  in either order — point at a folder already holding the server and it reports
  ready, point at an empty one and the page says to move the files before
  starting. Config is written before the live objects change, so a failure
  leaves the wrapper still matching the file. Backups, history and player data
  are keyed by instance id and are untouched by a move.

Every explicitly-chosen folder — created, adopted, or re-homed — passes the same
checks: a full path (a relative one would resolve against the service's own
working directory, which is System32), never overlapping another instance's
folder in either direction, and never inside the wrapper's own data directories.

`Sentry:Storage` is included in the settings export (§ 13) on the same terms as
the per-instance `WorkingDirectory` the Instances section already carries:
machine-specific, and a path that does not exist on the importing box fails
loudly at the next operation rather than silently.

### 4.4 Adopting an existing Bedrock server

The wrapper can take over a BDS folder that already exists — on any volume —
rather than only managing servers it installed. Registration, not installation:
the folder is validated (it must actually hold `bedrock_server.exe` — refused
otherwise, since the mistake would otherwise surface at first start as a crash),
registered with its absolute path pinned, and nothing in it is extracted over,
re-ported, or moved. The one write is `server-name`, and the adoption form
pre-fills its name field from the folder's own `server-name`, so writing it back
is normally the identity rather than a rename.

Auto-start defaults **off** for an adopted server, deliberately: whatever ran it
yesterday may still be running it, and the wrapper starting a second copy
uninvited would fight the first for the port. The form says so, shows what the
folder claims about itself (name, port, world), and flags a port collision with
an already-managed instance as information rather than a refusal — only one of
the two can run at a time, and the properties tab is where that gets fixed.

Adoption refuses a folder the wrapper already manages (equal or nested, in
either direction — a server inside another server's folder would be backed up by
both and restored by either) and anything inside the wrapper's own data
directories. Everything else an instance gets — console, backups, players,
updates — works from the moment it is adopted, because adoption lands in the
same registration path creation uses.

## 5. Process Management & Console

- Launches `bedrock_server.exe` per instance with redirected standard
  input/output, from the instance's own working directory.
- **The LAN discovery pair is held for whichever instance configured it
  (`D24`).** Every server takes 19132 and 19133 when LAN visibility is on,
  whatever its own port is, and only the first one to bind them gets them — the
  rest find them taken, soft-fail, and run on their own ports without retrying.
  **And the bind happens *after* `Server started.`** — measured on 1.26.44 from
  a real console, where `used for LAN discovery` prints after the ready line.
  The first implementation released its hold on ready, reasoning the server was
  past its bind by then; it was past its *gameplay* bind only, and BDS took the
  freshly freed pair a moment later — found on build.60 as a server on 19136
  holding 19132 while the server configured for 19132 crash-looped against it.

  So the hold is **standing**: taken when a non-owning instance starts while
  some configured instance owns the pair, and released only when the owning
  instance itself starts — inside the same serialised start slot, so nothing
  can slip between the release and the owner's bind. Running non-owners never
  retry their failed bind, which is what makes that hand-over window safe.
  When no configured instance owns the pair, nothing is held: a wrapper
  squatting on 19132 forever would make every server on the box undiscoverable
  for the benefit of an owner that does not exist. Instance starts are
  serialised, because two at once would race for exactly what this prevents.
  This arbitrates one host, and only instances the wrapper starts; anything
  else that takes 19132 first still wins, and gets reported rather than
  prevented.
- Console page: live log viewer, command input with history, in-game chat
  displayed alongside server log output.
- **Graceful shutdown is shared infrastructure** — one method (warn online
  players → stop the process) used by scheduled restarts, BDS auto-update,
  and wrapper self-update, not three separate implementations. See Section
  8.1 for how backup-on-shutdown plugs into this.

Redirected stdin is the mechanism the rest of the product is built on: backups
(`save hold`), chat command replies, restarts, and allowlist and OP changes are
all "write a line to the server's stdin and read what comes back". It was proven
in isolation before anything was built on top of it — see Section 5.5.

### 5.1 Crash watchdog

An instance that exits unexpectedly is restarted automatically, with an
increasing delay between attempts.

**Give-up rule (resolved as `D15`):** after a configurable number of
consecutive failed starts — default 3 — the wrapper stops retrying and raises
an alert instead. A server that crashes during startup would otherwise restart
forever, and each cycle writes to the world files, so an unattended loop is not
merely noisy. The alert matters as much as the stopping: a server that is
quietly down is worse than one that is loudly down, and the counter resets on
any start that stays up.

This is the same shape as the self-update ledger (Section 12) — bounded
retries, then a deliberate stop with the reason recorded — and for the same
reason: an automatic recovery that cannot give up is a way to turn one failure
into a permanent one.

**An exit the administrator asked for is never a crash.** A watchdog that only
knows "a process I did not stop has gone away" will restart a server somebody
deliberately shut down — both halves working as designed, and the wrapper
arguing with the person operating it.

Enumerating the ways a shutdown can be triggered is the wrong approach, because
the important one is invisible to the wrapper: an operator running `/stop`
in-game. So the primary signal is **the server announcing its own shutdown** —
BDS emits `Server stop requested.` and `Stopping server...` before it goes,
whoever asked it to. That covers the console, in-game, and any route not
invented yet.

`stop` typed at the wrapper's console additionally records the intent *before*
the command is sent. The announcement arrives after the command, and a server
that exits between the two would still read as a crash; this closes that window
for the one path where the wrapper knows in advance. The command itself is
always passed through verbatim — what changes is how the exit that follows is
read.

**What resets the counter (resolved as `D17`):** a start that *stays up*, not a
start that merely happens. An instance must run for a stability window — default
two minutes — before its consecutive-failure count returns to zero. Resetting on
launch instead is the obvious implementation and it quietly disables the entire
give-up rule: a server that crashes ten seconds into every attempt would clear
its own counter each time and retry forever, which is the exact loop `D15`
exists to stop. The bug is invisible in testing, because it only appears when a
server fails *repeatedly and quickly* — the one case that matters.

### 5.2 Stopping is two different events

Graceful shutdown is one method, but *why* it was called changes how long it
may take:

- **Scheduled restart** — the wrapper stays running throughout, so the player
  warning can be as long as the administrator wants.
- **Service stop** — Windows is waiting on the service control manager. A
  ~5 second warning, a save, then stop. A long countdown here risks the SCM
  killing the service mid-warning, which is precisely the outcome the graceful
  path exists to prevent.

Invariant, top to bottom: the updater's stop timeout must exceed the host's
shutdown timeout, which must exceed the total drain budget of every managed
instance. Inverting any pair means force-killing a server that is legitimately
mid-save.

### 5.3 Scheduled restarts

Deliberately not cron expressions. Two shapes cover what a game server
actually needs — every N hours, or daily at a given time — and both are
readable in a config file, which is how they were edited until milestone 16 put
them on the Configuration tab. Cron's extra power would be paid for on every
edit.

The panel offers **one** warning lead rather than the list the file holds: the
countdown points are derived from it through the shared ladder (§ 11.3), so a
fifteen-minute warning cannot be configured inside a five-minute window. Two
consequences of the storage shape are load-bearing there. The list is written
whole rather than merged, because the configuration binder appends to a
collection — a shortened countdown would otherwise keep announcing the points
just removed. And a zero lead is stored as a single zero-length point, never as
an empty array: the configuration system emits no keys for an empty array, so
"warn nobody" would read back as absent and bind to the default.

"Every N hours" counts from the instance's last start rather than from a fixed
epoch, so a server just restarted by hand is not restarted again minutes later.

**Daylight saving is resolved explicitly, not left to the framework (`D18`).**
A daily time is a wall-clock time, and twice a year a wall-clock time is not a
single instant:

- **The time does not exist** (spring forward). The restart runs when the gap
  closes, rather than being skipped for the day. A daily restart that silently
  does not happen is a worse surprise than one that runs an hour late, and it
  would only ever be noticed by its consequences.
- **The time happens twice** (fall back). The earlier instant wins, so the
  restart fires once rather than being eligible twice in one night.

Neither case is hypothetical and both are cheap to get right at the point the
next occurrence is computed. Left to a naive local-time comparison, the first
produces a restart that never fires and the second produces a double restart —
each once a year, on a date nobody will connect to the behaviour.

### 5.4 Console output retention

Live console output is held in a bounded in-memory buffer per instance for the
web view, and also written to `<DataRoot>\logs\instances\<id>\` with
size-based rotation. Persisting it is the point: the output an administrator
most wants is the output from just before a crash, which is exactly what an
in-memory-only buffer loses when the wrapper restarts.

**In-game chat is classified where it is recorded, and hidden only in the
view.** Chat gets its own console origin, decided once as the line enters the
buffer, so every reader agrees about what it is — the console page, the
transcript on disk, and anything later that counts it. The console has a
**Show in-game chat** switch, and it filters at render time, never at read time.
Filtering as lines are read would advance the cursor past them, so turning it
back on would show only chat from that moment onward — which looks like it works
and quietly loses the conversation somebody turned it back on to read. The
transcript always records chat regardless of the switch: a record with the
conversation removed is no use to whoever reads it after an incident.

Two shapes are recognised, and they are not equally certain. The **companion
pack relay** (Section 9) is definitive, because BedrockSentry defines both ends
of it: a fixed marker and pipe delimiters, so a player called `<Steve>` saying
`hello | goodbye` still parses into exactly two fields. The bare `<name> text`
form is the classic Minecraft one and is matched defensively. It has now been
measured on a real server, and the answer is negative: **BDS 1.26.40 does not
print player chat to its console at all.** Connect and disconnect, save output
and its own diagnostics, and nothing else. No `server.properties` setting turns
it on — the content log settings cover add-on errors, not conversation. The
recogniser stays, and stays tight, as insurance against a build that behaves
differently; but it guards a format that has never been observed rather than one
merely undocumented. A false positive is still the worse failure: it would
restyle real server output as conversation and then hide it the moment somebody
turned chat off, losing operational output to a chat filter.

That makes the companion pack relay the **only** source of chat, not the better
of two. Until the pack exists nothing is ever classified as chat, so the **Show
in-game chat** switch has nothing to filter — worth knowing before the toggle is
tested and read as broken.

**The buffer is read by sequence cursor, and gaps are surfaced (`D16`).** Each
line gets a monotonic per-instance sequence number, and a reader asks for
"everything after N" rather than "everything since a timestamp". Timestamps are
not unique at this resolution — the spike transcript has several lines sharing a
millisecond — so a timestamp cursor drops whichever lines tie with the last one
seen, and does so only under load.

When a reader's cursor predates the oldest retained line, the read reports that
output was missed rather than quietly returning what survived. A console page
that stitches a gap shut shows a plausible, wrong history, and eviction happens
precisely when output is fastest, which is precisely when an administrator is
reading. The wrapper's own notices and the commands it sends share the same
buffer as server output for the same reason: "what did we send it, and when" is
half of any incident, and nobody should have to correlate two logs by timestamp
to recover it.

**Each run of the server opens with a boundary line**, not another notice. A
restart drops hundreds of lines of startup on top of hundreds that look exactly
the same, so without a break it is genuinely hard to tell which run you are
reading — and easy to diagnose the previous run's fault as the current one's.
The console draws it as a rule across the page; the transcript marks it `===`,
so a log spanning several restarts can be split back into runs by reading it.

**The console follows new output only while the operator is at the bottom**, and
that is remembered from their own scrolling rather than measured when output
arrives. Measuring after the fact cannot work: by then the new lines are in the
page, so the view is exactly as far from the bottom as the batch is tall, and
any batch over a couple of lines reads as "scrolled away". Appending fires no
scroll event, which is why the remembered answer survives it.

### 5.5 What BDS actually emits — measured, not assumed

`scripts/stdin-spike.ps1` was run against BDS **1.26.36.1** under
`NT AUTHORITY\SYSTEM`, session 0, non-interactive — the exact conditions the
Windows Service launches instances under. Every check passed, and the timings
say the important part: there is no output buffering to work around.

| Step | Measured |
| --- | --- |
| Launch → `Server started.` | 1590 ms (existing world; first run is slower) |
| `list` → reply | 232 ms |
| `save hold` → `Saving...` | 81 ms |
| `save query` → manifest | ready on the **first** poll |
| `save resume` → confirmation | 21 ms |
| `stop` → process exit | 422 ms, exit code 0 |

So the supervisor can use ordinary asynchronous `OutputDataReceived` reads. The
420 ms stop is also the real input to Section 5.2's budget: per-instance drain is
dominated by the player warning, not by BDS.

Four details of the output contract that are easy to get wrong, all of which
this run settled:

- **Not every line carries the `[timestamp LEVEL]` prefix.** The startup banner,
  wrapped continuation lines inside a warning block, the blank line after a
  `list` header, `Quit correctly`, and — critically — the `save query` manifest
  all arrive bare. A parser that requires the prefix discards exactly the lines
  carrying the payload. Unprefixed lines are content, not noise.
- **`save resume` replies `Changes to the world are resumed.`** Not "level".
  Community wrappers and older documentation carry the "level" wording, and a
  literal match on it would wait out its timeout every backup while the reply
  sat in the buffer already read.
- **`save query` paths are relative to `worlds/`, and omit it.** The real line
  begins `Bedrock level/db/000005.ldb:509`, not `worlds/Bedrock level/...`.
  Backup code has to join the instance directory *and* `worlds\` itself.
- **Each manifest entry ends in `:<length>`, which is a byte count, not a
  checksum.** The file on disk may be longer than the stated length while saves
  are held; a copy must be truncated to it. Copying whole files produces a
  snapshot that restores to a corrupt world — and does so silently.

**A running server holds its world files open, and that is a Windows problem.**
BDS keeps the current LevelDB write-ahead log (`worlds/<level>/db/NNNNNN.log`)
open for writing for the life of the process. `File.OpenRead` asks for
`FileShare.Read` — "others may read this but not write it" — which Windows
refuses against an existing writer, so anything copying a live world must open
with `FileShare.ReadWrite | FileShare.Delete`. The delete bit matters as much
as the write bit and for the opposite reason: without it the *reader's* handle
blocks BDS from deleting a log it has finished compacting. Reading a file
another process is appending to is safe here only because of the manifest —
`save hold` fixes the first `:<length>` bytes and everything past them is
truncated away. Linux has no mandatory locking, so this class of bug passes
every test and every CI run that is not on Windows.

`list` also replies across two lines: a `There are 0/10 players online:` header
followed by a names line that is empty when nobody is on. Roster parsing reads
the line after the header rather than the header alone.

**`NO LOG FILE! - ` in front of every line.** The vanilla dedicated server
writes no log file of its own unless it is told to, and says so on each line
rather than once. The spike did not surface this — its instance happened to
have one — and it is not cosmetic: the prefix sits *ahead* of the
`[timestamp LEVEL]` prefix, so a parser that does not know about it reads an
entire session as unstructured text. Readiness is detected by matching
`Server started` against the parsed message, so an affected instance runs,
takes players, and sits on **Starting** forever while the watchdog's stability
window never closes. The parser strips it; the console says so once, because a
line reading "NO LOG FILE!" looks like a fault and is not one.

### 5.5.1 Console colour coding

Three independent axes, in precedence order, because a line is all three at
once and only one of them can win:

1. **Severity** — `WARN` and `ERROR`, as BDS reports them. A warning is a
   warning whoever raised it and whatever it is about.
2. **Origin** — the wrapper's own notices, commands it sent, chat, and the run
   boundary. Notices are set apart by a tinted band with a rule down the left
   edge rather than by colour, which says "not the server" faster than a hue
   and frees green for arrivals.
3. **Subject** — what plain server output is *about*: lifecycle, a player
   arriving, a player leaving, the save handshake, or housekeeping.

The wrapper's own notices carry a rule down their left edge rather than a
tinted band behind the text. Both say "not the server", but a background reads
as a highlight on the words rather than a margin beside them.

Housekeeping (`Running AutoCompaction...`, `Player Spawned:`,
`Player PartyIdUpdate:`) is **dimmed, never filtered**. The moment
AutoCompaction matters is the moment it stops appearing, and a hidden line
cannot show anybody that.

**Filtering is a separate control from colour**, with four switches — server
output, joins, leaves, chat — and a **Players only** preset that turns the
console into a feed of who is on the server and what they are doing. Server
output covers everything that is not one of the other three, warnings and
errors included: an operator who has hidden the machine has hidden its
complaints too, which is what asking for only the players means. Run
boundaries are never hidden whatever is ticked, because a restart in the
middle of a filtered feed would otherwise be invisible and "why did everyone
leave and rejoin" would have no answer.

Death messages will join the list when the companion pack ships (Section 9).
There is deliberately no disabled checkbox for them now — a control that does
nothing yet is a lie in a settings panel, the same reason the two update
toggles in Section 8.1 wait for the flows that honour them.

The filter is applied when lines are rendered, never when they are read from
the buffer: reading past hidden lines would advance the cursor over them, so
turning a category back on would show only what arrived after that moment
(`D16`).

Every line also carries a one-character mark in a fixed-width gutter, blank
where a category has none. Colour is never the only channel: the mark survives
a screenshot, a paste into a bug report, and a reader who cannot separate the
greens from the blues. Chat's mark is a quote rather than an emoji because
emoji are double-width and knock the line a column out of true.

Classification is by measured shape and happens as the line enters the buffer,
because two of the rules have to see the line before it:

- **A warning wraps onto rows carrying no prefix of their own.** Read one line
  at a time, half an ALLOW LIST WARNING block comes out amber and the rest
  looks like ordinary output. A bare row under a warning inherits it, until the
  next row that states its own level ends the carry. A bare row the classifier
  recognises in its own right keeps its own meaning — the save manifest arrives
  bare too, and must not turn amber because a warning happened to precede it.
- **Bedrock prints boxed notices** several lines between two rules of `=`. The
  TELEMETRY MESSAGE block is advice rather than an event and is dimmed whole.
  The banner's subject opens the box, never a bare row of `=`: the rules are
  identical between one notice and the next, so opening on the rule would
  swallow whatever followed the next one.

It changes no stored line and never reaches the transcript, which stays the raw
bytes BDS wrote. An unrecognised line is left alone, so a future BDS that
renames everything degrades to the console as it was before any of this
existed.

### 5.6 The `server.properties` editor

Bedrock ships its own documentation. Every setting in `server.properties` is
followed by an explanation and, for most of them, an `Allowed values:` line —
66 settings in 1.26.36.1. The editor reads *that*, and the wrapper carries no
list of Bedrock settings at all. A default Mojang changes stays right without a
release here, and a setting added in a future version arrives on the page
documented on the day it is installed rather than as an unlabelled text box.

What the file's own wording is turned into:

| The file says | The page shows |
| --- | --- |
| `"true" or "false"`, or `true, false` | a two-option toggle |
| `"peaceful", "easy", "normal", or "hard"` | a dropdown of exactly those |
| `Integers in the range [1, 65535]` | a whole-number field bounded 1–65535 |
| `Any value in the range of [0, 1]` | a number field that accepts `0.85` |
| `Any positive integer` | a whole-number field with a floor of 1 |
| anything else, or nothing | a text box |

Two of those distinctions are load-bearing rather than cosmetic. `[0, 1]` and
`[4, 12]` are the same shape, and only the word *integer* separates a fraction
from a whole number — read wrongly, the editor refuses `0.85`, which is a
shipped default. And a quoted value that does *not* resolve into a clean list
means the setting takes more than one kind of thing:
`server-build-radius-ratio` is `"Disabled"` **or** a fraction, so it stays a
text box rather than becoming a dropdown that hides every number.

Settings Mojang comments out are listed too, marked as not set and showing the
default on the commented line. Turning one on uncomments it *in place*, beside
the documentation that explains it, rather than appending it at the bottom and
leaving the file appearing to say the same thing twice.

Edits are refused or flagged, never both at once:

- **Refused:** a value that is not on the setting's list, text in a number
  field, a fraction where the file says integers, a line break in any value.
  Nothing is written until every field passes, so a partial save cannot leave a
  file in a state nobody chose.
- **Flagged, and saved anyway:** a number outside the documented range. Mojang's
  own documentation contradicts the implementation here — `max-threads` is "any
  positive integer" and then, a sentence later, `0` means "as many as possible"
  — and a wrapper that refuses a value the server accepts is worse than one that
  says it looks wrong.

**The port fields are checked before the save, not after the failure.** A port
collision is the most confusing way for Bedrock to fail: the process starts,
binds nothing, exits, and the watchdog reports a crash loop with nothing
connecting it to the cause. Saving a port checks it against every other
instance's configured ports *and* against the UDP sockets actually open on the
machine — UDP only, since a TCP listener on the same number is not a conflict
and a false alarm teaches people to ignore the check.

The socket check reads Windows' `GetExtendedUdpTable` rather than
`GetActiveUdpListeners`, because it needs the *owning process*, not just the
port. That buys two things. The message names what is holding the port —
"open by bedrock_server.exe (PID 8124)" ends the question that "already open"
only starts — and an instance's own running server is excluded by process id,
so a server is never reported as colliding with itself. Where a managed
instance owns the socket it is named as that instance, once, rather than twice
over from the file and the socket.

Note that the table stores the port in network byte order inside a DWORD.
Read straight through, 19132 comes out as 48202 — itself a plausible-looking
port number, so the mistake produces conflicts on ports nothing is using while
missing the real one.

Nothing on this page is applied to a running world; the page says so, and a save
writes a notice into the instance's console so the transcript records that the
file changed underneath it.

**Search** covers names, descriptions and allowed values, not names alone —
half the point is finding the setting whose name you cannot remember ("idle",
"chunks", "xbox"). Searching also expands the collapsed group, so a search
cannot silently miss two thirds of the file.

**Reload from file** is always available, and the page notices when the file
changes underneath it: the last-write time is captured at load and compared on
each tick, so a hand edit or a rewrite by the server raises a warning while the
page is open rather than being discovered by overwriting it.

**A text view** shows the file as it is, unchecked. Two things need it. Bedrock
accepts settings it does not ship a line for — it asks for
`emit-server-telemetry` in the console on every start, and there is nothing in
the file for the form to attach it to — and no form can express everything a
config file can. There is also an *Add a setting* row in the form view for the
first case, which is the same need without dropping to raw text.

Switching between the two views carries the working state across rather than
discarding it; a mode switch that threw away what you had typed would make the
text view a trap. Saving from the text view runs no validation at all, which is
the point: a validator that only understood the form would refuse exactly the
cases the text view exists for. It does mean parsing has to be lossless —
unrecognised lines, odd spacing and comments all survive a parse-and-save round
trip, or editing one setting by hand would delete the documentation above it.

## 6. In-Game Chat Commands

Players with permission can trigger wrapper actions via chat — not
Minecraft's own commands.

- Prefix is `\` instead of `/`, specifically to avoid colliding with real
  Minecraft commands (and the achievements-disabled-by-cheats issue that
  comes with enabling commands).
- Initial command set:
  - `\backupnow` — triggers a manual backup. Cooldown-limited to prevent
    spam, with the cooldown duration configurable by the admin.
  - `\restart` — restarts after a short mandatory warning (~10 seconds)
    rather than firing instantly. `\restart <number>` defaults to minutes;
    a `#m` or `#s` suffix specifies minutes or seconds explicitly (e.g.
    `\restart 5m`, `\restart 30s`).
  - `\help` — lists available commands if the player has permission, or
    tells them they don't have access if not
- **Every denied command replies that the player doesn't have access** — not
  just `\help`.
- Permission is checked against the player's profile (Section 7) — a
  wrapper-specific "admin" flag, distinct from Minecraft's own operator
  levels, since this controls the wrapper rather than the game.
- Replies are sent privately via `/tell`, not broadcast.
- Because `\` isn't a recognized Minecraft prefix, the raw command text still
  broadcasts as an ordinary chat message before the wrapper intercepts it —
  worth knowing going in. The companion add-in's `chatSend` hook (Section 9)
  could suppress this later if wanted.
- Built as a generic command-dispatcher (parse → check permission → execute
  → reply) so Telegram and Discord (Section 15) can plug into the same
  pipeline rather than reimplementing it.

## 7. Player Tracking

SQLite-backed, keyed by XUID (stable across username changes). One database,
two views: a **global** players page covering every instance, and a
**per-instance** page scoped to a single server.

### 7.1 What is global and what is per-instance

The split is not merely a filter. Some values are aggregates that only exist
at the global level, and some are properties of a specific server that have no
global meaning at all.

| Global — one value per player | Per-instance — one value per player, per server |
|---|---|
| Gamertag (most recently seen) | First seen *on this server* |
| Nickname and free-text notes (admin-set) | Last seen on this server |
| First-seen date — earliest connection on *any* instance | Playtime on this server |
| All-time playtime, summed across every instance | Current session and online status |
| Wrapper-admin flag (Section 6) | Allowlist membership |
| | OP level |
| | Companion add-in stats — deaths and so on (Section 9) |

Two consequences worth stating outright, because both were wrong in an
earlier draft of this document:

- **Allowlist and OP are per-instance, and cannot be otherwise.** Bedrock
  stores `allowlist.json` and `permissions.json` inside each server's own
  directory, so a player opped on one instance is not opped on another. A
  single global toggle would have had no correct behaviour: it would either
  write to every server at once or silently pick one.
- **The wrapper-admin flag is global**, because it grants control over the
  wrapper and there is one wrapper. Per-instance wrapper-admin ("may run
  `\backupnow` on the survival server but not the creative one") is
  expressible and might eventually be wanted, but it introduces a permission
  matrix for a distinction nobody has asked for yet.

Online status is per-instance and the global view reports *which* server —
"online on Survival", not a yes/no column.

Both views show everyone who has ever connected, not only who is online now.

### 7.2 Storage

```
players               xuid PK, gamertag, nickname, notes,
                      first_seen_utc, is_wrapper_admin
player_sessions       id PK, xuid FK, instance_id,
                      connected_utc, disconnected_utc (null while online),
                      exit_x, exit_y, exit_z, exit_dimension
                      — null unless the companion pack reported them (Section 9)
player_instance_state (xuid, instance_id) PK, is_allowlisted, op_level
                      — retired by D31; shipped in schema v1, never written
```

**Playtime is derived, never stored.** Per-instance playtime is a sum over
`player_sessions` filtered by instance; global playtime is the same sum
unfiltered. A materialised counter alongside the sessions it summarises is a
second copy of the same fact, and the two drift the first time a write is
interrupted. With an index on `(xuid, instance_id)` the aggregate is
inexpensive at any scale this product will see.

**Last known location is derived for the same reason.** The exit position sits
on the session row rather than in a table of its own, so "where was this player
last" is the position on their most recent session for that instance — the same
shape as playtime being a sum over the rows rather than a counter beside them. A
`(xuid, instance_id)` table holding only the latest would be a second copy of a
fact the sessions already carry, which is exactly what `player_instance_state`
turned out to be. It also yields the history for free: where somebody logged off
*each* time, not only the last. The columns are nullable and that nullability is
load-bearing — sessions recorded before the pack existed, on an instance where
it is disabled, or on one where a crash took the server before the leave event
fired, carry no position at all, and `NULL` says so where a `0, 0, 0` would be a
plausible-looking lie about the world spawn.

**`first_seen_utc` is the deliberate exception (`D30`).** It has exactly the
shape the paragraph above rejects — the earliest `connected_utc` across every
session — and would otherwise be derived for the same reasons. It is stored
because it is the one date that must not move when a cleanup rule fires. `D26`
deletes sessions it cannot trust and `D29` erases a player's rows outright;
either would silently walk a derived first-seen forward, quietly rewriting how
long someone has been around. Recorded here so it reads as the choice it is
rather than as an oversight sitting next to the rule it breaks.

**`player_instance_state` is retired (`D31`), not dropped.** It shipped in
schema version 1 as the intended home for allowlist and OP intent, and Section
7.5's measurements broke its premise before anything ever wrote to it: the
allowlist is keyed on a name that may have no XUID yet, while the table's
foreign key demands a `players` row only a connection can create — so the one
entry an allowlist most exists for, a friend who has not joined yet, is the one
it cannot hold. `D27` is additive-only and the migration has shipped, so the
table stays in the schema, empty, wired into export/import and the `D29`/`D14`
deletes, and nothing writes it. The files themselves are the store — see
Section 7.6.

**`D28` is superseded by `D31`.** It resolved how the wrapper's copy of
allowlist and OP state should reconcile against the server's files — the
wrapper wins on its own writes, divergence is shown rather than overwritten.
With `D31` there is no wrapper copy: one fact, one home, and the divergence it
managed cannot arise. What survives of its spirit is the rule that the wrapper
never stomps a change made in-game or by hand, which the files-as-truth design
gets for free.

**The schema changes additively, and rollback restores the file (`D27`).** The
self-updater (Section 12) can put an older wrapper back after a failed health
check, and that wrapper would then be facing a database a newer one migrated —
the exact failure rollback exists to prevent, arriving through the door rollback
opened. Two rules together: migrations only add, never drop or retype, so an
older binary keeps working against a newer file; and the updater copies the
database before migrating and restores that copy as part of a rollback. Either
alone is thin — additive-only still loses data written by the newer version, and
a copy alone leaves the window between migration and rollback unprotected.

Session rows for a retired instance are kept (`D14`, Section 4), so global
aggregates remain accurate after an instance is removed.

**Sessions come off the console, measured (`D25`).** BDS prints both ends, and
both carry the XUID the schema is keyed on:

```
Player connected: SteveExample, xuid: 2535000000000001
Player disconnected: SteveExample, xuid: 2535000000000001, pfid: 0000000000000000
```

Measured on 1.26.40, unlike the chat shape in Section 5.4, which was assumed for
a long time and turned out not to exist at all. Two traps in the surrounding
output: `Player Spawned:` follows a connect about two seconds later in a
*different* shape — no comma before `xuid:` — and `Player PartyIdUpdate:` carries
no XUID, so a recogniser keyed on `Player ` rather than on the two verbs matches
three things and means none of them. `pfid` appears on disconnect but not
connect, so nothing may require it. The session starts at `connected`, not at
`Spawned`: the two-second gap is loading, and the player is on the server for it.

**A session the server never closed is closed by the wrapper, or dropped
(`D26`).** `disconnected_utc` being null is what makes a player online, so a row
left open is a player online forever and a playtime total that grows without
bound — a permanent corruption of the one number these pages exist to show, and
one that derived-not-stored playtime cannot heal on its own. Two cases, and they
differ in what the wrapper actually knows:

- **The wrapper saw the process go.** Close every open row for that instance at
  the exit timestamp. That is the real end of the session, and the downtime after
  it is never inside a row.
- **The wrapper itself died.** On startup, any row still open for an instance
  that is not running is untrustworthy — there is no record of when the server
  stopped — and is deleted rather than closed at a guessed time. Losing a session
  is better than inventing one, and far better than a row that silently counts
  the outage as playtime.

**A player can be erased globally (`D29`).** `D14` covers forgetting one
instance's history; this is the whole person across every instance — profile,
sessions, and per-instance state — because "remove my data" is a request about a
person, not a server. It is a deliberate, confirmed action in the same shape as
instance removal, and it reports how many rows went.

### 7.3 The two pages

- **Global players page** (`/players`) — every player who has ever connected
  to anything this install manages. The all-time record: lifetime playtime,
  first-seen, nickname and notes, and which instance a player is on right now
  if they are online. Nickname, notes, and the wrapper-admin flag are edited
  here, since they belong to the person rather than to any one server.
- **Per-instance players page** (`/instances/{id}/players`) — only players who
  have connected to that server, showing only that server's numbers. The
  allowlist and OP controls live here, because that is the scope at which they
  actually exist.

Both arrive at milestone 6 rather than waiting for the instance management UI
at milestone 16. The data supports both from the moment sessions are recorded,
and an instance switcher is far cheaper than retrofitting a second view later.

### 7.4 Manual editing

SQLite is the only backend. An export-to-JSON / import-from-JSON tool provides
manual editing without a second storage implementation to keep in sync. Import
is upsert-only — a row missing from the JSON does not delete the corresponding
record. Treat export/import as a stop-the-service operation; the export is a
snapshot, and re-importing a stale one while the service is running would
overwrite session/playtime updates that happened in the meantime.

### 7.5 The allowlist and permissions files — measured, not assumed

Both files were taken off a live install rather than a wiki. They disagree with
each other about almost everything, and the differences are load-bearing.

`permissions.json` — three-space indent, a space either side of the colon,
CRLF, trailing newline:

```json
[
   {
      "permission" : "operator",
      "xuid" : "2535000000000001"
   }
]
```

`allowlist.json` — minified onto a single line, CRLF, trailing newline:

```json
[{"ignoresPlayerLimit":false,"name":"SteveExample"}]
```

Five details that a reader written from the documentation would get wrong:

- **They are keyed on different things.** `permissions.json` is keyed on XUID
  and carries no name. `allowlist.json` is keyed on **name** and carries no
  XUID — Bedrock fills one in only after that player first connects. So the
  allowlist can name somebody the wrapper has never seen and holds no profile
  for, which is the entire point of an allowlist and is also why it cannot be
  stored against a player row (see `D31`).
- **`permissions.json` is an override list, not a roster.** A player absent
  from it is not "no permission" — they get `default-player-permission-level`
  from `server.properties`, which an administrator may well have set to
  `operator`. Any UI that renders absence as "not an operator" is lying
  whenever that default is not `member`. The measured file has one entry on a
  server with more players than that.
- **The permission values are the three Bedrock tiers** — `visitor`, `member`,
  `operator` — not a numeric level. Bedrock's own `server.properties`
  documentation says so for `default-player-permission-level`, and the
  properties parser already reads that line.
- **`ignoresPlayerLimit` is per allowlist entry** and has no analogue anywhere
  else in the wrapper. Round-tripping the file without carrying it drops a
  setting the administrator chose, silently — the failure `D28` exists to
  prevent.
- **Two different files are called `permissions.json`.** The one above sits in
  the instance root and governs players. `config/default/permissions.json`
  governs Scripting API module access and is the one Section 9 refers to for
  `@minecraft/server-net`. They share a name and nothing else.

The formatting asymmetry is Bedrock's own: one writer pretty-prints, the other
does not. The wrapper should not try to match either byte for byte — BDS
rewrites both files itself whenever it changes them — but it must not reformat
a file it was only reading, and it must preserve fields it does not understand.

### 7.6 Editing them (`D31`, resolved)

The files are the source of truth and the wrapper is an editor over them.
There is no intent table and no reconciliation loop — the second copy is what
created the divergence problem `D28` existed to manage, and deleting the copy
deleted the problem. One editor per server state:

- **Running** — the server owns its files: it rewrites them on every in-game
  change, so a direct edit underneath it would be overwritten or ignored.
  Changes go through the console (`allowlist add` / `allowlist remove`,
  `op` / `deop`) and BDS writes its own file. Confirmation is watching that
  file until the rewrite appears — never parsing the command's reply, whose
  wording has not been measured. A change that never lands (an `op` on a
  player who is offline, say) is reported as *sent but not taken*, with a
  pointer at the console.
- **Stopped** — the files are static and the wrapper edits them directly,
  node-based so fields it does not understand survive, write-then-move like
  every other file it owns mid-write.

Consequences worth stating:

- **Deop writes an explicit `member`, not a removal.** Removing the override
  hands the player whatever `default-player-permission-level` says — on a
  server where that is `operator`, a deop-by-removal would give the tier
  straight back.
- **`ignoresPlayerLimit` is a stopped-server edit.** The console command has
  no flag for it, and editing the file under a running server is the thing
  this design forbids. The UI says so instead of silently dropping the tick.
- **Operators are picked from players the wrapper has seen**, because
  `permissions.json` is keyed on XUID and only a connection reveals one. The
  path for a stranger is: allowlist them (name-only works), let them join
  once, then OP them.
- The per-instance page shows when `allow-list=false` in `server.properties`,
  because a faithfully-rendered allowlist that is not being enforced reads as
  security that does not exist.

## 8. Backup System

Three independent, stackable strategies — any combination can run together,
via an `IBackupStrategy` interface so adding a fourth later doesn't touch the
orchestrator.

### 8.1 Orchestration
`save hold` → poll `save query` until the snapshot is ready → run every
enabled strategy in parallel → `save resume`, unconditionally (try/finally),
even if a strategy fails.

- Skipped only if no player was online at all since the last backup. Manual
  triggers always run regardless.
- Manual backups (UI or `\backupnow`) can carry an optional note, stored with
  the history record.
- **Backup-on-shutdown:** when the wrapper does a graceful shutdown,
  optionally back up first if enabled. Since the server is already stopped by
  that point, this particular backup skips `save hold`/`query`/`resume`
  entirely — the world files are already static. Applies independently to
  wrapper self-update, BDS auto-update, and scheduled restarts, each with its
  own toggle.

**Interval schedules are in minutes or hours**, as separate modes rather than
one number with a unit beside it, so a config file always says which value is
in force and every schedule written before minutes existed keeps working with
nothing to migrate.

**Backups can also be triggered by players arriving and leaving**, each with
its own toggle. This is the appetite a clock serves badly: a schedule either
runs on an empty server or misses the session that mattered, where "somebody
just logged off" is exactly when their work is on disk and worth keeping. Two
rules make it safe to leave on:

- A **minimum gap** between activity-triggered backups, default fifteen
  minutes. A player on a poor connection can join and drop a dozen times in a
  minute, and each of those would otherwise be a hold-query-copy-resume against
  a live server — the gap is what separates a trigger from a way to leave saves
  paused. Manual and scheduled backups ignore it.
- A trigger arriving while a backup is already running is **dropped, not
  queued**, on the same reasoning the orchestrator refuses concurrent backups:
  whatever the running one archives is newer than the event that raced it.

They are not subject to the played-since-last skip, because the trigger already
*is* the evidence that somebody played.

### 8.2 Custom Command
Runs a user-provided script/command once the snapshot is ready.

**A script cannot copy a running server's world with ordinary tools.** BDS
holds the current LevelDB write-ahead log open for writing, and `copy`,
`xcopy`, `robocopy` and `Copy-Item` all ask for access Windows will not grant
alongside it — they fail on `worlds/<level>/db/*.log` and nothing the wrapper
does can change that for somebody else's process. A custom command either opens
files the way § 5.5 describes, or runs only against a stopped server. The
strategy's own description in the UI says so.

### 8.3 ZIP to File
- Compression slider spanning none (fastest, dedup-friendly) → balanced
  (still dedup-friendly) → maximum (best ratio, slowest)
- Retention by count (keep last N) or by timeframe (keep X days/weeks)

**The archive carries the server's own configuration as well as its world.** A
world without its `server.properties` restores onto a server that may be on the
wrong port, the wrong difficulty and the wrong name; without `allowlist.json`
and `permissions.json` it restores onto one that lets in the wrong people.
Archiving the world alone was archiving most of the answer.

Four files travel: `server.properties`, `allowlist.json`, `whitelist.json` (the
pre-1.16 name Bedrock still honours) and `permissions.json` — the same set an
update preserves, and for the same reason.

Three details of how, each of which is the answer to a way it could go wrong:

- **They live under a reserved `_serverconfig/` prefix, never at the archive
  root.** The root is the `worlds/`-relative namespace: an entry there is a path
  inside a world folder, and a file called `server.properties` sitting beside
  `Bedrock level/` would be indistinguishable from a world *called*
  `server.properties`. The prefix keeps them apart, and an archive written
  before any of this existed simply has no such entries and restores exactly as
  it always did.
- **They are read straight from the instance directory, not from the manifest.**
  They are not part of the save, so `save hold` says nothing about them and
  there is no byte count to honour — but they are opened the same permissive way
  world files are (§ 5.5), because BDS rewrites the allowlist and permissions
  whenever somebody changes them in game and may well be holding one open.
- **A failure here is logged, not fatal.** They are added after the world and
  never instead of it: a backup with the world and no `server.properties` is
  worth far more than no backup at all.

### 8.4 Volume Shadow Copy (VSS)

Windows-native point-in-time snapshots. A folder per backup rather than an
archive, with the same retention options as ZIP, and both strategies can be on at
once.

**What it buys is not speed or size.** It is that nothing on the volume can move
while the copy runs, and that a file another process holds exclusively can still
be read — LevelDB's `LOCK` being the one that bites. The ZIP strategy opens world
files permissively and hopes; a shadow copy makes the question not arise.

**The manifest lengths still apply**, and that is the part it would be easy to get
wrong. A shadow copy is a faithful image of the volume, and the volume at that
instant holds world files *longer* than the save BDS just flushed — those trailing
bytes belong to the next save (§ 5.5). Copying them whole produces a folder that
looks perfect and restores to a corrupt world.

The output is a folder rather than an archive deliberately: the point of paying
for a snapshot is to get bytes off the volume quickly and let saves resume, and
compressing them while the server is held would spend the time this strategy
exists to save. Configuration files are carried under the same reserved
`_serverconfig/` name the ZIP strategy uses, so a restore reads one layout
whichever strategy wrote the backup — and they are read from the live instance
directory rather than from the shadow, because a config file edited a second ago
is more useful than whatever the volume happened to be holding.

Two rules carry the safety of the operation:

- **The shadow copy is released unconditionally**, in the same spirit as `save
  resume`. One left behind holds volume space until somebody notices, and on a
  schedule it is left behind every single time. A release that fails is logged
  and never fails a backup that otherwise succeeded.
- **A half-copied folder is deleted**, because it would list in the restore
  picker looking exactly like a complete one.

Taking a shadow copy is an administrative operation. Off by default, because a
strategy enabled where the service account cannot use it fails every scheduled
backup at three in the morning — which is precisely what the Readiness panel
(§ 14) exists to predict.

The snapshot is taken through `Invoke-CimMethod` against `Win32_ShadowCopy` with
the `ClientAccessible` context, not `vssadmin` (Server SKUs only) and not
`diskshadow` (a scripting language of its own with prose to parse). The CIM
cmdlets work under both `powershell.exe` and PowerShell 7; the older `[wmiclass]`
accelerator does not. `ClientAccessible` is what general-purpose backup tools use
— a persistent read-only snapshot that survives until deleted, rather than one
expecting a requester to hold the VSS session open for the life of the copy,
which this process is not.

### 8.5 Restore
Picker listing available ZIP archives and VSS snapshots side by side, labelled
by which they are and with notes shown. Custom-command backups appear in history
for reference but aren't restorable through the wrapper, since it doesn't know
their format.

The two shapes go through one flow. A snapshot's world is copied back where an
archive's is extracted, and both obey the same two rules: the reserved
`_serverconfig/` name is skipped, so configuration never arrives as a world
folder called `_serverconfig`, and every target is checked to be inside the
worlds directory. The wrapper's own backups cannot contain a traversal, which is
exactly why the check exists — the dangerous one is the folder somebody placed
there by hand.

Restore flow: stop the server if running → take a safety backup of current
state first → apply the selected backup → restart.

**Restoring the configuration is a separate, opt-in choice.** The picker offers
it as a checkbox, shown only when the archive actually carries configuration and
unticked every time the panel opens rather than remembered. Putting an old
`server.properties` back can move the port, which is the one change that makes a
server unreachable rather than merely wrong — so the world and the
configuration are restored independently and the configuration half is off
unless asked for.

The two halves are also handled differently on failure, deliberately. The world
goes through the move-aside dance because a half-extracted world is corrupt;
the four configuration files are written afterwards, outside it, because their
previous contents are already in the safety backup and a failure there is worth
reporting rather than worth unwinding a completed world restore for. The safety
backup carries configuration too, so undoing a config restore is restoring that
archive with the box ticked again.

## 9. Companion Add-In (Behavior Pack)

Installed by default with every instance, toggleable per instance. Gives the
wrapper visibility the console alone doesn't provide, using the Bedrock
Scripting API.

- **Relay mechanism:** `console.log()` calls inside the pack land in the
  server's content log (not player-visible chat), reaching the wrapper
  through the same parser already watching console output. An HTTP push via
  `@minecraft/server-net` is a possible later upgrade, but that module is
  still pre-release and requires explicitly enabling it in `permissions.json`
  — console relay is the safer default.
- **Death tracking (initial scope):** `world.afterEvents.entityDie` itself
  carries only `deadEntity` and `damageSource` — no location or dimension of
  its own — so both are read off `deadEntity.location` and
  `deadEntity.dimension` while the entity is still valid to query, immediately
  in the after-event handler.
  - Per-player death counter, shown on the player profile
  - **A death message printed to the console, not just a location.** "Steve
    was slain by a Zombie", "Alex fell from a high place", "Steve drowned" —
    built from `damageSource.cause` and, where present,
    `damageSource.damagingEntity` / `damagingProjectile`, followed by the same
    location-and-dimension text the login/logout lines use, so the two never
    format a place two different ways.
    - **Bedrock hands the pack an enum, not a sentence.** Unlike Java, nothing
      in the scripting API returns a ready-made death message string — the
      pack builds one from `EntityDamageCause`, which is why the phrasing is
      the pack's to choose rather than a translation of something BDS already
      says.
    - **A curated set of causes gets its own phrase; everything else falls
      back to a generic one that still names the cause.** `fall`,
      `entityAttack` (worded as "slain by" when `damagingEntity` is another
      player, "killed by" for a mob), `projectile`, `drowning`, `lava`,
      `fire`/`fireTick`, `void`, `starve`, and `suffocation` cover what the
      cause-breakdown list below already names as worth tracking on their own;
      the enum carries roughly thirty values today and Mojang adds more with
      each update, so a switch that only handles the common ones and prints
      "died (`entityDamageCause`)" for the rest stays correct as the enum
      grows, where a table that silently omits new values would go stale
      without ever failing loudly.
    - **A mob's name is formatted, not looked up.** `damagingEntity.typeId`
      comes back namespaced and `snake_case` (`minecraft:cave_spider`); the
      pack strips the namespace and title-cases the rest ("Cave Spider")
      rather than maintaining a lookup table for every mob in the game, which
      would need updating on every new mob release and silently show the raw
      id for anything the table left out. A killed-by-player message uses the
      player's name directly instead, since a person is not a `typeId` to
      translate
  - Death location sent to the player privately, to help them return — same
    coordinates, same dimension, same formatting, so what a player is told
    privately never disagrees with what the console showed
  - Death-cause breakdown (fall, mob, drowning, lava, PvP, etc.) via
    `damageSource`
  - Fallback position tracking (sample position on an interval) for edge
    cases where the death event's location isn't reliable — void falls,
    instant deaths — carries the same dimension, off the same sampler, for
    the same reason
- **Last known location (`D35`):** where each player was when they last logged
  off, per instance, shown beside their last-seen time. The hook is
  `world.beforeEvents.playerLeave`, not the after-event — this is measured from
  the API reference, not assumed. `PlayerLeaveAfterEvent` exposes only
  `playerId` and `playerName`; the `Player` is already gone by the time it
  fires, so there is no position left to read. `PlayerLeaveBeforeEvent` carries
  the leaving `Player` itself and is the only hook that can
  (`@minecraft/server` 1.7.0, Bedrock 1.20.50 — so it also sets the pack's
  floor).
  - Its callback runs with **restricted-execution privilege**. The position is
    read there and stashed; the `console.log` relay happens on the next tick
    via `system.run`, outside the restriction, rather than from inside the
    handler
  - **Dimension travels with the coordinates.** A bare x/y/z is ambiguous
    across the Nether's eighth-scale grid — the same three numbers name
    different places — so a coordinate without its dimension is not merely
    incomplete, it is confidently wrong
  - Rounded to whole blocks; sub-block precision is noise on a page whose
    question is "roughly where"
  - The interval sampler scoped above for death edge cases doubles as the
    fallback here, so a leave whose own position reads unusable still has one
- **Position echoed to console on login and logout, independently of the
  storage above.** A visible line — coordinates and a dimension name — on
  both ends of a session, not only the logout half `D35` persists. The two
  are separate toggles: an operator who wants the console readable without a
  database row for every logout, or the row without the console line, can
  have either alone.
  - **Login uses a different hook than logout, and the difference is not
    cosmetic.** The obvious pairing — `playerJoin` for login, mirroring
    `playerLeave` for logout — is wrong the same way `PlayerLeaveAfterEvent`
    was: `PlayerJoinAfterEvent` is also `playerId`/`playerName` only, no
    `Player`, no position. `world.afterEvents.playerSpawn` carries the
    `Player`, but it fires on every respawn, not only the first spawn after
    joining, so it needs its own `initialSpawn` flag checked — read on any
    other spawn and every death report doubles as a spurious login line.
  - **Login's callback runs unrestricted, logout's does not, and the pack
    reflects that rather than treating both alike.** `playerSpawn` is an
    `after`-event; `after`-events run under **default execution privilege**
    — the full API, per the engine's own execution-privilege model — so its
    `console.log` fires straight from the handler. `playerLeave` is a
    `before`-event under **restricted execution**, which is what forces the
    stash-then-`system.run` shape above; writing the login path the same way
    would be copying a workaround into a handler that never needed it.
  - **The dimension is translated, not printed raw.** `player.dimension.id`
    reads back a namespaced id (`minecraft:overworld`, `minecraft:nether`,
    `minecraft:the_end`); the console line and the stored column both carry
    that raw id, and it becomes "Overworld" / "Nether" / "The End" only at
    the point something renders it for a person — the console formatter and,
    later, the Players page. Storing the friendly text would make the wrapper
    match on English strings a future dimension or a locale could break;
    storing the id and translating at render time cannot drift from what
    `dimension.id` actually returns.
- **The pack relays a gamertag, not an XUID.** Nothing in the scripting API
  exposes one — `playerId` is an opaque entity identifier — so every relayed
  event joins the player database by name and meets `D31`'s mismatch again.
  The join here is exact where the allowlist's is not: at the moment a leave is
  relayed the wrapper holds exactly one open session for that gamertag on that
  instance, and that session names the XUID. It resolves against the most
  recently open session rather than only currently-open ones, because the
  pack's relay line and BDS's own disconnect line race, and the disconnect may
  have closed the session first.
- **Planned additions:** blocks placed/broken by type (`playerPlaceBlock` /
  `playerBreakBlock`), mobs killed, PvP kills specifically, distance
  traveled, AFK detection, achievements/advancements earned.
- **Every stat is independently toggleable** rather than one on/off switch
  for the whole pack.
- Ships as part of the wrapper's own release — updates alongside the wrapper
  at milestone 13.5, not just the wrapper's binaries.

## 10. Add-On Manager

Distinct from the companion add-in (Section 9) — this manages *user-supplied*
behavior/resource packs.

- File picker to manually add packs, or folder scan of a configured directory
- Manifest validation (`manifest.json` presence)
- Enable/disable toggles, which modify the world's `world_behavior_packs.json`
  and `world_resource_packs.json`

## 11. BDS Auto-Update

Where builds come from was measured, not assumed. Mojang publishes a JSON feed
listing what it is currently offering:

```json
{"result":{"links":[
  {"downloadType":"serverBedrockWindows",
   "downloadUrl":".../bin-win/bedrock-server-1.26.44.3.zip"},
  {"downloadType":"serverBedrockPreviewWindows",
   "downloadUrl":".../bin-win-preview/bedrock-server-1.26.50.25.zip"},
  {"downloadType":"serverJar", "downloadUrl":"https://piston-data.mojang.com/..."}
]}}
```

Four details that shape the design:

- **There is no version field.** The only statement of which build a link points
  at is the file name, so the version is parsed from it with the same parser the
  package store uses — a downloaded build and a hand-placed one are read
  identically.
- **The feed carries Java Edition.** `serverJar` is a different product on a
  different host. Anything not recognised as a Bedrock *Windows* build is
  ignored rather than offered.
- **It is an envelope, not an array** — `result.links[]`.
- **There is no checksum published**, so there is none to verify. The guarantee
  is HTTPS to Mojang's own host and nothing more, which is worth stating rather
  than leaving as an apparent oversight.

The feed's address is configurable for the reason `D13` made the wrapper's own
feed configurable: it is somebody else's endpoint and has moved before.

**When the wrapper asks is install-wide and reuses `RestartSchedule`** — the same
type scheduled restarts and scheduled backups already use, rather than a third
way of writing "every so often". That gives "every N hours" and "daily at a set
time" with the daylight-saving handling of `D18` already solved and already
tested, which a bare interval would have had to grow the first time somebody
asked to check at 4am. Mode `None` means no timer: the check on service startup
still runs and **Check now** still works, which is why the page never calls it
"never". To stop contacting Mojang at all there is a separate switch.

Two details the loop gets right because neither is obvious:

- **The schedule is re-read every time round**, so a change from the Settings
  page takes effect without restarting the Windows Service. A setting that needs
  a service restart is a setting that gets reported as broken.
- **A due time the wrapper slept through fires late rather than being skipped.**
  The next check is measured from the last check, not from now — asking for the
  next occurrence after *now* means a paused VM or a machine that was off rolls
  silently forward to the following slot, and for a poll that is not what
  "every six hours" means.

**Update behaviour is per-instance; where builds come from is install-wide.**
The appetite for a new build differs per server — a creative test server can
follow every release the day it lands, and the survival world people have played
for a year probably should not — while the feed address describes Mojang, not
any one server.

- **Notify is the default.** Mojang ships the occasional build that breaks
  something, and these are live servers. Automatic is opt-in per instance.
- **A pin outranks the mode**, including automatic. Otherwise "stay on this
  version" and "update yourself" would be two settings that contradict each
  other with no stated winner. Pinning is also the manual rollback: set it to
  the build that worked.
- **Preview is a channel, not a flag.** A preview server is unjoinable from the
  retail client, so it is a deliberate choice per instance.
- **Not knowing is never treated as being behind.** The installed version comes
  off the console at startup (`Version: 1.26.36.1`), because a server somebody
  unzipped over by hand stops matching the package it was installed from. A
  server that has not run under the wrapper has told it nothing, and the page
  says so rather than offering an update that may already be applied.

Checking and downloading are separate from applying. A downloaded build lands in
the package store under Mojang's own file name — the same folder an
administrator can drop a zip into (`D19`) — so there is one install path and a
downloaded build is not a special case.

### 11.1 Applying a build

The sequence is the one every other planned outage in this codebase uses — warn
the players, drain, back up if that instance asked for it, do the work, start
again — because it is one method with several callers rather than three
implementations that drift. What is specific to an update is only the middle.

**The update is in place, not staged.** Staging would mean copying a world that
can run to many gigabytes in order to replace an executable that is tens of
megabytes, and the world is the one thing an update never touches. Only what the
package actually contains is written; everything else in the directory is left
exactly where it is.

**What is never overwritten**, whatever the package contains: `worlds/`,
`config/` (Scripting API module permissions — a deliberate opt-in nobody wants
silently reverted), `server.properties`, `allowlist.json`, `whitelist.json`,
`permissions.json`. A BDS zip ships defaults for all of them, and letting those
land would reset a server's settings, its allowlist and its operators in one
step.

**Behaviour and resource packs are deliberately absent from that list and
survive anyway.** An update writes only the entries the package contains and
deletes nothing, so Mojang's own vanilla packs are refreshed while a pack an
administrator added is simply never touched. Naming them as preserved would
freeze the vanilla packs at whatever version the server was first installed
from, which is the opposite of what an update is for.

**The health gate is what makes this safe to offer.** Every file about to be
replaced is copied aside first and every file the package adds is recorded;
between them those two lists are a complete undo. If the server does not report
itself started within three minutes the previous binaries go back and it is
started again on the version that was working. Mojang ships the occasional build
that will not run on somebody's box, and finding that out at 3am with no way
back is precisely what this exists to prevent. The kept binaries are removed
only on success — a rolled-back update leaves them on disk deliberately, because
somebody diagnosing why a build would not run wants them.

**A stopped server is updated where it stands.** There is nothing to warn, drain
or health-check, and whether the new build runs is found out when somebody
starts it. The page says so rather than implying a check happened.

**The countdown differs by who asked.** An update somebody clicked gets a
minute's warning: the operator is at the keyboard and has asked for it. An
automatic update is the unattended case and gets the instance's full
restart-schedule countdown, which people plan their evening around. Same method,
different lead time, chosen by the caller rather than assumed inside it.

### 11.2 The package store as a picker

Downloading and applying are separate steps (§ 11), which leaves a state the
first cut of the UI had no exit from: a build sits in the store and the only
install button is the one that appears when Mojang is offering something newer.
So **every package in the store is listed with an Install button**, on the
instance's Configuration tab because installing acts on one server.

**Any build installs, not only a newer one.** Reinstalling the current build is
how somebody repairs a damaged install, and going back a version is the manual
rollback this section already names — so both go through the one path that warns
players, keeps the world and puts the previous binaries back if the new ones will
not start.

**A downgrade is warned about rather than refused**, and it is the one case the
safety net does not cover. The health gate catches a build that will not start;
it cannot catch a build that starts and then refuses the world, which is what
Bedrock does when the world was last saved by a newer version. Refusing outright
would remove the rollback; saying nothing would spring it on somebody. So the
confirmation says it plainly and the choice stays theirs.

Store housekeeping — what is there, what it occupies, and deleting a zip — is on
the **Settings** page instead, because the store is shared by every instance.
Deleting a package removes the zip only: installing copies files into the
instance's own directory, so nothing running depends on the package still being
there.

### 11.3 Applying automatically — the per-instance schedule

**The schedule is a delay, not a time of day.** The event being scheduled around
is Mojang publishing, which nobody controls and which happens every few weeks at
no particular hour. Given that, the only thing worth setting per server is how
much warning its players get before it takes the build — and that is exactly what
a delay expresses. Thirty minutes by default, settable to the minute, per
instance: a creative test server can take it in five, and the survival world
people have played for a year can sit on it for a day.

**The delay is the countdown.** Players are told the moment the build is seen and
again as the window closes, so half an hour's delay is half an hour of notice
rather than half an hour of silence followed by a surprise. The notice points are
derived from the delay rather than configured beside it — a separate list would
let somebody put a fifteen-minute warning inside a five-minute delay, which
cannot be honoured and would have to be silently dropped. The ladder runs on three
scales, each matching what a warning is *for* at that range: sparse above an hour
(12h, 6h, 2h), where a notice is something to plan a session around; **every five
minutes through the last hour**; and **every minute inside the last five**, where
there is nothing left to plan and only somewhere safe to stand. All of it is
filtered to the window, and a rung within a minute of the window itself is
dropped, since the opening notice already said that number.

Those cadences replaced a sparse tail (30m, 15m, 5m) that read well on paper and
badly in practice. Sparse assumes the audience was there when the
window opened; anybody who joins part-way through hears nothing until five
minutes out, which on the default half-hour delay is most of the window, and the
first they know of it is a server about to go down. The property that matters is
therefore stated as a bound rather than as a list: **inside the last hour, nobody
is ever more than five minutes from being told**, and inside the last five it is
never more than one. Above an hour it goes back to being sparse, because at that
range a warning is something to plan around rather than a reason to stop, and
five-minute chatter for two hours is how a line gets ignored.

The opening notice always states the whole window rather than the nearest rung
below it. Zero means "as soon as it is seen", and then the only warning is the
stop's own.

**The delay does not run inside the instance's gate**, and that is the load-
bearing decision. The obvious implementation hands the supervisor a half-hour
warning lead and lets the existing stop countdown do the work — but that
countdown runs while holding the instance's lock, so a thirty-minute window would
be thirty minutes in which nobody could stop, start or restart that server, and
nobody could call the update off either. The one thing an operator most wants
during a countdown is the ability to interrupt it. So the applier holds the
pending update, speaks the notices through the ordinary console, and only enters
the supervisor at the end — with a short final countdown, because by then players
have had the whole window.

Consequences worth stating:

- **Cancelling is real.** It removes the pending update and declines that version
  on that instance, so the next sweep does not immediately re-schedule it. The
  decline is deliberately not persisted: a wrapper restart is a fair place to
  reconsider, and a declined version that outlived the process would be a pin
  nobody could see in the config file. **Apply now** brings the window forward
  instead.
- **A pin arriving mid-countdown calls it off**, because a pin outranks the mode.
- **A build that rolls back is declined**, so the wrapper does not spend every
  sweep reinstalling a build that will not start.
- **The download runs during the window, not before it.** Players hear about the
  update immediately rather than after the bytes are down, and a download that
  fails is recorded on the pending update rather than leaving an announced update
  that silently never happens.
- **A package deleted from the store mid-window fails loudly** rather than at the
  swap: the file name is carried across a window that can run for hours, and the
  Settings page can delete it in the meantime.
- **A stopped server is still updated on time.** There is nobody to tell, which is
  not an error.

## 12. Wrapper Self-Update

Updates the wrapper's own code from its repository — separate from BDS
auto-update (Section 11). Deliberately split across two milestones so the
core mechanism is available early:

- **Core mechanism (milestone 3.5):** branch selection (`build` or `prod`),
  version check, download of the artifact published by the release pipeline
  (Section 12.1), and applying the update via a small, rarely-
  changing helper process — a running service can't overwrite its own loaded
  files, so the wrapper stops itself, the helper swaps the files, then starts
  the wrapper back up.
- **Graceful integration (milestone 13.5):** once process management and
  backups exist, the update runs through the same sequence as any other
  shutdown — warn online players → stop the server → optional backup-on-
  shutdown → apply the update → restart everything.
- Update mode: automatic / notify-only with a manual button and a
  description of the pending update / don't check for updates (manual "check
  now" stays available even in "don't check" mode)
- Service Update page shows a changelog

### 12.1 Release Pipeline & Installer

Milestone 3.25. Two consumers, one artifact: an administrator installing
BedrockSentry for the first time, and the self-update mechanism above
fetching a newer build. Both pull the same published output, so it's built
once rather than improvised separately for each.

- CI (GitHub Actions) builds, tests, and publishes on every push to `build`
  and `prod`. No local `dotnet build` is ever required to install or update
  the wrapper — the SDK is a contributor prerequisite, not an operator one.
- Each branch has its own published artifact stream, matching the branch
  selection in the update settings above.
- The installer registers the Windows Service and hands off to the first-run
  setup wizard (Section 13).
- **Uninstall is non-destructive to data.** It removes the service
  registration and the wrapper's own binaries; instance working directories,
  `worlds/`, backups, and player data survive it. Removing those is a
  separate, explicit action.

**Distribution feed (resolved as D13):** the repository is made public, and both
the manifest and the release archive are fetched by anonymous HTTPS GET from
GitHub Releases. An installed service cannot carry a credential, and a private
repository serves nothing anonymously; Section 16 already commits to a public
repo and milestone 19 ends in a public release, so this only brings that forward.
Each build gets an immutable release, plus a rolling `channel-build` /
`channel-stable` tag carrying the newest manifest so the wrapper has one stable URL
to poll rather than having to enumerate releases.

### 12.2 Certificate Key Protection

**Resolved as D12.** The generated PFX's export password is protected with
`ProtectedData` at `LocalMachine` scope rather than written to a plaintext
sidecar file. The directory ACL — SYSTEM and Administrators only, with
inheritance broken — is the primary control; DPAPI is defence in depth for the
case where the file is copied somewhere less protected, since LocalMachine-scoped
ciphertext is useless on another machine. The ASP.NET Core DataProtection key
ring was rejected for this: it lives under the same data root, so using it to
protect the certificate would create a startup ordering cycle.

**Artifact and installer format (resolved as D10):** a self-contained
`win-x64` publish, distributed as a zip, installed by a PowerShell
`install.ps1`. Self-contained means the target machine needs no .NET runtime
at all — the wrapper is a Windows Server product where "install the right
runtime first" is a support burden disproportionate to the download size it
saves. PowerShell rather than MSI because it needs no extra build tooling,
works on Server Core where there is no desktop shell, and produces exactly
the flat file layout the self-update helper already has to swap. A signed MSI
is worth revisiting at milestone 19, when there's a public release to polish;
nothing here forecloses it.

### 12.3 Taking the servers down first

A wrapper self-update stops the Windows Service, which takes every Bedrock server
on the machine with it. Left to that stop, the servers get the short countdown of
§ 5.2 — deliberately short, because the service control manager is waiting and a
long one risks the whole wrapper being killed mid-save. That budget is right for a
stop somebody else initiated and wrong for this one: an update the wrapper chose
to apply is a planned outage, and it is planned *before* any clock starts.

So the drain happens in `ApplyAsync`, ahead of the helper being launched. Players
get a real warning (`Sentry:Update:PlayerWarning`, one minute by default), each
instance that asked for a backup-on-shutdown gets one, and by the time the SCM
issues its stop every server is already down — so the short budget it enforces has
nothing left to hurry. That is what makes the § 5.2 invariant comfortable rather
than tight.

Three details, each of which is the answer to a way this could be worse than not
having it at all:

- **The drain is the last thing before the hand-off, not the first thing in the
  method.** Everything above it can decide *not* to update — a download that will
  not verify, a `current` link that does not resolve, a helper that was never
  staged — and every one of those returns false. Draining first would take every
  server on the box down for an update that then did not happen.
- **It is never cancelled part-way.** The manual path is an HTTP POST, so the
  caller's token belongs to a browser; aborting there would leave some servers
  stopped, some running and no update applied. The drain runs on
  `CancellationToken.None`, the same call `InstanceHostedService` makes at the
  same seam and for the same reason. Cancelling *before* it starts is fine.
- **The button starts it and returns.** Applying is no longer a quick hand-off, so
  awaiting it on the request would hold the response open for the whole countdown.
  `RequestApply` starts one apply, refuses a second while one is in flight, and
  the page reports that one is under way rather than looking inert.

**The countdown runs outside the instance gate**, the same separation § 11.3
makes for a Bedrock build's window. The first cut of this handed the supervisor a
long warning lead and let its existing countdown do the work, which put the whole
warning inside the gate — and a service stop landing in that window blocked the
host's own shutdown on the same gate, past the budget § 5.2's invariant depends
on. The notices are sent from outside, and the supervisor is entered at the end
with no warning left to give, so it holds its gate for the seconds a stop
actually takes. `D32` covers the part of that hazard which is not specific to
this caller.

Cancellation therefore means "stop waiting", not "stop draining": the host going
down cuts the countdown short and the servers still get their graceful stop. If
the host is stopping once the drain is done, **the helper is not launched at
all** — a detached updater swapping versions and issuing a service stop in the
middle of an operating-system shutdown is strictly worse than not updating, and
the staged build is still there for the next start.

### 12.4 A hand-run installer upgrade

`install.ps1` run over an existing install is a second update path, and it was
never as graceful as the self-update. It is not *un*graceful — `Stop-Service`
goes through the wrapper's own shutdown, so every `bedrock_server.exe` gets a
real `stop` and a chance to save — but it is the **service-stop** path of § 5.2:
about five seconds of warning, and no backup-on-shutdown, because there the
service control manager is waiting.

Nothing is waiting on the installer. So it asks first: a POST to
`/prepare-shutdown` on the same loopback listener the updater polls for health,
which runs the same warn-drain-back-up sequence § 12.3 does, and only then stops
the service. By that point every server is already down and the short budget has
nothing left to hurry.

- **The path is part of the frozen contract**, like `/healthz` and the port, and
  for the same reason: `install.ps1` from a new release talks to the wrapper from
  an old one. A build that does not know the path answers 404, the installer
  reads that as "no drain available" and carries on — the contract degrades
  rather than breaking. Same for a wrapper that is not running, or one whose
  health port has been moved and whose config the installer then reads to find
  out.
- **Unauthenticated, and loopback-only three times over**: Kestrel binds the port
  with `ListenLocalhost`, middleware 404s the path on any other port, and the
  remote address is checked again in the handler. Anyone who can reach it can
  already run `Stop-Service`, so it grants nothing local access did not.
- **`-SkipDrain`** gives back the old behaviour for somebody who wants the
  upgrade over with. Worlds are still saved; only the countdown and the backup
  are given up.

## 13. Settings & Status

- Settings: `server.properties` editor, backup scheduling, BDS update
  channel selection
- Status: uptime, current BDS version, resource usage, last backup timestamp
- Self-diagnostics/health-check panel: confirm VSS availability, admin
  rights, and free ports before something fails at runtime
- First-run setup wizard: EULA acceptance, initial BDS download,
  `server.properties` walkthrough, initial admin credentials (Section 3)
- Full config export/import: instance definitions, backup configuration,
  update settings, chat command settings, add-on paths — same pattern as the
  player-data export/import (Section 7), pointed at wrapper settings instead.
  Deliberately excludes auth credentials (Section 3), even hashed, so a
  restore requires re-setting the login rather than carrying it over.

### 13.2 What the settings export carries, and what it does not

**An allow-list of sections, not a deny-list.** A deny-list is one section away
from leaking: somebody adds a section with a token in it, nobody remembers this
file, and it ends up in an export people email around. With an allow-list a new
section is simply absent until somebody decides it belongs — the failure that
costs an afternoon rather than an incident. Two sections are excluded on
purpose:

- `Sentry:Admin`, the credentials. A hash is not a password, but it is
  offline-attackable and this file is meant to be copied between machines.
- `Sentry:Tls`, which holds a certificate password in plain text. The rest of it
  describes the machine rather than the installation, and carrying the section
  *without* the password would be worse than not carrying it — importing would
  replace a working certificate configuration with one missing the password to
  open it.

**The allow-list holds on the way in as well as out.** An import is a file from
somewhere else; without the same filter on the read side, a hand-edited export
could write `Sentry:Admin` and hand somebody a login.

**It works on the config file's own JSON, not on the options objects.** Binding
to POCOs and serialising back would write out every property at its default —
turning settings nobody chose into explicit ones that stop tracking future
defaults — and would silently drop every key the POCOs do not model. Same
reasoning as `JsonConfigWriter`, and it applies at least as strongly to a file
whose whole purpose is to be complete.

**Import merges; it never deletes.** Each server the file carries is written
whole, so no stale setting survives inside it, while a server configured here and
absent from the file is left alone — a file older or narrower than the machine is
far likelier than somebody deleting a server by leaving it out, which is the same
call § 7.4 makes. The page previews what a file would do before anything is
written, because unlike the player import this one rewrites server definitions
and can leave the wrapper pointed at directories that do not exist here. Servers
new to the machine need a service restart to be managed, and the preview says so.

### 13.3 Readiness

A second check panel, separate from § 13.1's reachability one because it answers
a different question: not "why can nobody connect" but "will the thing I am
relying on actually work when it runs".

- **Volume Shadow Copy.** Only `Disabled` is actionable. VSS is demand-started
  and spends almost all of its life stopped on a perfectly healthy machine, so a
  check that reported "stopped" as a fault would train people to ignore the
  panel — which is the failure mode a diagnostics page can least afford.
- **The service account.** Run under a plain user the wrapper still starts, still
  serves the panel and still runs servers, and then every firewall fix, Defender
  exclusion and shadow copy fails with its own unrelated-looking error. One row
  saying why beats three reporting `Unknown`. It is a report and never a fix: a
  process cannot grant itself privileges, and a button implying otherwise would
  be a lie.
- **Port availability**, from the ports already saved rather than one being
  typed. The properties tab checks a port before it is committed; this catches a
  collision that appears afterwards — another program installed, a second
  instance created — which otherwise presents as a crash loop with no mention of
  ports anywhere.

Same three-state contract, same post-paint probing, and the row markup is one
component shared with § 13.1's panel so the rules about when a fix is offered
cannot drift between them.

### 13.1 Host details

The status page reports the machine as well as the wrapper, because most
questions an administrator arrives with are about the host: what address do I
give people, is there room for another backup, why did every server restart last
night.

- **Addresses.** Local IP, hostname, and public IP.
- **Reachability.** Whether a firewall rule exists for each instance's port, and
  whether Microsoft Defender excludes the instance's folder — a real-time scanner
  walking a live LevelDB costs throughput and can hold a file open at the moment
  a save wants to replace it, which shows up as a stalled save and never as
  anything mentioning antivirus. Both read the system and *offer* a fix behind an
  **Apply fix** button; neither changes anything on its own, and each says what
  it will do before it is pressed, because "fix" describes nothing.

  Three states, not two. `Unknown` — the check could not run — is deliberately
  not folded into `Missing`. "Defender is not excluding your world" and "we could
  not ask Defender anything" call for different actions, and reporting the second
  as the first would have people changing a machine nothing had inspected.
  A check that could not run offers no fix at all.

  The Defender exclusion is the one thing in the wrapper that makes a machine
  *less* protected, so it is scoped as narrowly as the problem allows — the
  instance's own folder, never a drive, never a file type — and the offer states
  the cost beside the benefit. A parent exclusion counts as covering its
  children, so excluding the instance root does not leave every instance under it
  still asking.

  **Local client isolation (resolved as `D23`).** When Minecraft is installed on
  the same machine as a server, the status page also reports whether it is
  allowed to connect to one. Windows isolates packaged apps from their own host,
  and the Minecraft client is a packaged app — so a client on the server's own
  machine cannot reach it, no matter what the firewall says. That is the trap:
  the standard diagnosis is to turn the firewall off, that changes nothing, and
  the evidence then points away from the real cause. The fix grants a loopback
  exemption for that package alone.

  Only when a client is installed here. A dedicated server has none, and a
  permanent `Good` row about a component that is not present is something to read
  and dismiss forever. Nothing is reported in that case rather than a reassurance
  nobody needs. Retail and Preview are separate rows, because they are separate
  packages and are exempted separately.

  This is the one check that reaches outside the wrapper's own concerns — it is
  about a *client* rather than about a server BedrockSentry manages. Included
  anyway, because it is the third member of the same family as the other two, it
  costs an afternoon to diagnose without help, and it is the one an administrator
  is least equipped to guess at. The exemption is additive, so one already
  granted by hand is never revoked, and the offer says that Windows may drop it
  again when the app updates.

  These are read through PowerShell asking for JSON, since the firewall and
  Defender configuration have no supported managed surface and the cmdlets'
  display output is localised. The process launch sits behind one interface so
  the half that can be wrong — reading the answer and deciding what it means —
  is testable on a machine that is not Windows.

  Each answer describes the *machine*, not a server, so both are read once per
  run and every instance's row is decided against them. Reading per instance
  meant paying for an identical answer once per server. The firewall read is two
  bulk queries joined on `InstanceID` rather than the obvious pipeline: the
  rule-to-filter relationship is one-to-one, but asking each rule for its own
  filter is a CIM round trip per rule, measured at 56 seconds against 1.3 for the
  join. It reads `ActiveStore`, the effective policy, so a port opened by group
  policy is not reported as closed.

  **Deadline (resolved as `D22`).** A script gets 30 seconds and is then killed
  as a process tree, since the cmdlets start children of their own. The value is
  a constructor argument rather than a setting — the only correct value is "long
  enough", and one set too short would quietly turn working checks into `Unknown`
  — and it is an argument rather than a constant only so the timeout can be
  proved in milliseconds instead of half a minute. A check that runs out of time
  is `Unknown`, and so offers no fix.

  None of this runs before the page is sent. The probes are started after the
  first paint, and each row appears named but unanswered and resolves in place.
  Awaiting them during prerender withheld the whole HTTP response until the
  slowest one returned, which is indistinguishable from the page being broken.
- **Capacity.** Free space, and what instances, backups and packages each
  consume. A backup schedule quietly filling a disk is the most common way an
  installation like this dies.
- **Clock.** Local time and time zone, because scheduled restarts are written in
  local time (Section 5.3) and a machine set to UTC runs an 05:00 restart at
  midnight — discoverable otherwise only from player complaints.
- **Pending reboot.** Windows will take every server down without asking.

**Public IP discovery (resolved as `D20`).** A machine cannot know its own public
address, so this is an outbound request to a third-party echo service — enabled
by default, endpoint configurable, cached, and named in the settings page so it
is disclosed rather than discovered. Defaulting it off would hide the answer to
"what do I give my friends" behind a toggle most people never find, which is the
wrong trade for a self-hosted game server panel.

Alongside it, an optional **domain check**: enter the name players actually use,
and the page resolves it and compares against the public IP, warning when they
disagree. That is the whole of "why can't anyone connect" for a dynamic address
whose DNS record has gone stale.

## 14. Instance Management UI

Add, remove, and switch between managed instances from the web UI —
including port assignment and per-instance working directory. The
architecture supports multiple instances from milestone 4 onward; this
milestone is where that gets a proper UI instead of being config-file-only.

## 15. Telegram & Discord Bots

Scope: admin commands/remote control, and external player notifications.
Both should plug into the same command-dispatcher built for in-game chat
(Section 6) rather than reimplementing parsing and permission logic. Detailed
command and notification list still to be defined.

## 16. Design Principles

- No redistribution of the BDS binary — wrapper code only in the public repo
- Config-file based; no secrets or personal paths committed
- MIT license
- Activity-aware backup skipping to avoid unnecessary writes
- Backup strategies are stackable, never mutually exclusive
- Graceful shutdown, and eventually command dispatch, are shared
  infrastructure used by multiple features rather than duplicated per feature
- Sensitive credentials (Section 3) are hashed, and excluded from the
  config export/import tooling (Section 13)
