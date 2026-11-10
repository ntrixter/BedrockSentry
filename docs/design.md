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
| Hosting | Two Windows Services (`Microsoft.Extensions.Hosting.WindowsServices`) — runs on Server Core, no desktop shell required. The main service serves the panel and runs the servers under its own account; the privilege broker runs as LocalSystem and does the administrative work it asks for (§ 17) |
| Process management | Redirected stdin/stdout. On Windows each server starts through `CreateProcessAsUserW` at low integrity (§ 17.6); elsewhere `System.Diagnostics.Process` |
| Player data | SQLite (source of truth) + export/import to JSON for manual editing |
| Backup archives | `System.IO.Compression.ZipFile`, configurable compression level |
| VSS snapshots | `Invoke-CimMethod` against `Win32_ShadowCopy` (§ 8.7) — not `vssadmin`, which is Server SKUs only |
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
- An optional second step (`D125`): a six-digit code from an authenticator app
  (RFC 6238, HMAC-SHA1, 30-second steps, one step of drift either way, a used code
  refused). Off by default; switched on from Settings → **Authenticator app**, which
  shows a QR code and stores nothing until a code from it is confirmed. The secret
  is encrypted with the data-protection key ring and kept in `auth.json` under
  `Sentry:Admin:Mfa`; ten single-use recovery codes are stored as SHA-256 hashes and
  still work if the key ring is lost. A right password issues a five-minute cookie
  under its own scheme, which opens only `/account/second-step`. Switching the step
  on or off, replacing the recovery codes or using one writes a new security stamp,
  which signs every session out. With the phone and the codes both lost, an
  administrator runs `install.ps1 -ClearPanelMfa`
- With the step on, the confirm dialogs (Remove, Retire, Restore, the sync and its
  put back, erasing a player) ask for a current code in place of the password
  (`DestructiveConfirmPolicy`); recovery codes are refused there, and wrong codes
  count against the second step's limit. Changing the password still asks for it
- Wrong attempts are limited (`SignInThrottle`): five inside fifteen minutes per
  address and step, then refused until the oldest ages out. The second step also
  counts every address together, because reaching it means the password is known

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
  inspected — the service runs under its own account (§ 17), and a share or a
  folder owned by another account reads perfectly well right up until the first
  world is created in it. A folder outside the data root that already holds files
  is granted to the service by an administrator with `install.ps1 -GrantFolder`
  (§ 17.7) before a server can start there. Accepting the default leaves the setting *absent* rather than writing
  the resolved path, since an empty value means "under the data root" and pinning
  it would detach instances the moment anybody relocated that root.
- Wrapper self-update applies once per install, to both services together, since
  both executables ship in one version folder (§ 12). BDS
  auto-update is per-instance, so one server can intentionally run ahead of
  another.
- The global `behavior_packs/`/`resource_packs/` folders can act as a shared
  pack library that different instances select from independently, rather
  than duplicating pack files per instance.
- **Instance binaries come from a package store (resolved as `D19`).** BDS zips
  live in `<DataRoot>\server-packages\`, named by version, and the store is
  filled two ways: an administrator drops a zip in by hand, or the wrapper
  downloads into it, milestone 13's updater and the first build for an empty store
  (§ 11) alike. Creating an instance extracts a chosen package into
  that instance's working directory, so adding a server is never a manual unzip
  into the right folder — which is exactly how an instance ends up pointed
  somewhere it should not be. The world can come from a file instead of being
  generated: a `.mcworld` or one of this panel's ZIP backups, uploaded on the
  create form (§ 8.5).
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
- **What "a port" means comes from the instance's own `transport` key (`D70`).** Under
  `transport=raknet` a server binds `server-port` and `server-portv6`, both UDP — the model every
  port claim in this document was written for. Under `transport=nethernet` it binds `server-port`
  as a **TCP** listener carrying an HTTP signalling handshake, does not read `server-portv6` at
  all, and moves play to a second set of UDP sockets, one per player online. Measured on BDS
  1.26.51.1: a server on 19150 held TCP 19150 listening, with nothing on 19151; a server on the same
  build whose file has no `transport` line held UDP 19136 and UDP 19137 and no TCP at all.
  **Where those play sockets land is `server-udp-ports`' answer, and it is the half the first
  measurement could not see (`D71`).** That capture was taken with nobody connected. With one client
  and then two: with the key **unset**, play lands on `server-port`'s own **UDP** side — the same
  number, the other protocol; with a window **set**, nothing touches that number's UDP side at all.
  The property moves the allocation window rather than widening it, so a NetherNet server has two
  port cases and the file says which.
  So **one install holds both models at once** and the answer is per instance, never install-wide.
  `ServerPorts.For` is the one function that turns a transport into a port set, and the start
  pre-flight, the Readiness panel, the firewall check, the discovery-pair guard and the Status card
  all ask it rather than deciding for themselves. `ListeningSockets` chooses the socket table to
  match, because a NetherNet server holds its port as a TCP listener from the moment it starts — so
  asking the UDP table alone returned *free* whoever held it, a false clear on the one check that
  exists to stop a second process opening a live world. With the key unset that number's UDP side
  is not reliably empty either, since play lands there once somebody connects; such a server is
  therefore asked on **both** tables, and a firewall row for it appears twice, TCP and UDP, each
  openable on its own.
  **A transport this wrapper does not recognise, a directory with no `server.properties` yet, and a
  `server-udp-ports` this code cannot read are all checked on both protocols.** Over-gating there
  names a holder an administrator can act on; under-gating opens a world twice.
  **A pinned play window is deliberately not in that model.** It is a run of ports rather than a
  socket, and what the model feeds is a *refusal*: missing one of those ports costs a player who
  cannot join, and over-gating costs a server that will not start because something unrelated holds
  one port out of ten. That is the opposite balance to the one above, so the same rule weighed again
  on this case comes out the other way. `PlayPorts` describes the window instead, and the panel
  reports on it.
- **The create form's port is the first number where none of the new server's
  sockets is taken (`ON1`, `T371`).** A RakNet server needs the number and the one
  after it, a NetherNet server the number on TCP and its play window on UDP; taken
  means another server's file names the port or its window, or something on this
  machine is listening on it. `NewServerPorts` holds the rule and
  `PortInspector.Claims` gathers what is taken. Choosing NetherNet fills a **Play
  ports** field with the `server-udp-ports` line the Status tab would suggest, ten
  ports wide for the package's `max-players`, which is written at creation as the
  operator leaves it; `D71`'s rule that the wrapper never writes the key afterwards
  stands. A number typed by hand is checked when the field loses focus, and each
  clash is named with its holder. A server switched to NetherNet on its
  Configuration tab is given a window the same way, from its own `server-port` up
  past every other server's file and every socket open on the machine, unless the
  window it has is free and wide enough; a window that runs into a holder refuses
  the save, and the Status tab calls it a fault and offers a free line (`T415`).
- **A new instance is written `transport=nethernet` with a pinned play window, and the
  create form offers RakNet for an older build (`D75`, `T413`).** BDS 1.26.60 prints a
  `TRANSPORT TYPE ERROR` for `transport=raknet` and runs NetherNet anyway, so a RakNet
  server's rows describe ports it does not use; its Status tab says so. The default was
  RakNet until then, for the reason that follows. BDS 1.26.50 onward extracts a `server.properties` that says
  `transport=nethernet`, which moves `server-port` to TCP, ignores
  `server-portv6` entirely and puts play on negotiated UDP ports — one per player
  online. Every port claim above, the start pre-flight, the Readiness panel and
  the firewall check describe the RakNet model, so creating a NetherNet server
  silently produces one the wrapper reports wrongly. Writing the transport at
  creation is what keeps those claims true of the servers this wrapper makes,
  and it is the only door that needs it: a file with no `transport` line at all
  is a RakNet server whatever the build, so nothing that already exists moves.
  The picker is where the choice stays available, and it states what NetherNet
  costs — TCP as well as UDP on the server port, a forwarded UDP range as wide as
  `max-players`, the public address in `server.properties`, and a saved server
  identity — at the moment of choosing rather than in a document. This is a
  default with an end rather than a position on the transport: it turns over when
  the port model can describe a NetherNet server honestly.
  **`D71` and `D73` did both halves and the default has not moved** — the port
  model carries both cases, the firewall covers both protocols, the panel works out
  the `server-udp-ports` line, and a **Server identity** row says when a server has
  no saved key and offers to send `serveridentity save` on its own console. What is
  left before the default could flip is a run against a real client.
- **A server with no saved identity is reported, and the command that saves one is
  offered rather than run automatically (`D73`).** BDS keeps it at
  `keys/server_identity_key.pem`; with no file it generates one in memory at every
  start, so the identity changes and every player is asked to trust the server
  again. That is harmless on a server somebody restarts by hand and is not harmless
  here — this wrapper restarts servers on a schedule, on a Bedrock update, on its
  own update and after a crash, so it is the reason it keeps happening.
  The row reports in three states rather than two: a stopped server can be reported
  and cannot be fixed, because the fix is a console write; an unreadable folder is
  *unknown* rather than missing, since those two produce opposite advice. The
  sentence names what it costs rather than the path, and the fix reports what it
  **sent** — what the command did is on the server's own console, and the row reads
  the file again on the next pass.
  **The wrapper never writes the key.** `serveridentity` is the server's own
  command, so BDS authors its own identity and the button is only the keystroke —
  the reasoning § 7.6 already applies to the access files.
  **The key is out of every backup by construction**: the archive carries four named
  flat configuration files and its reader refuses anything nested, so nothing under
  `keys/` can reach one. A private key in a file that gets copied and emailed is a
  different class of thing from a settings file.

### 4.3 Storage locations

Only the folders that grow without bound are relocatable — that is the actual
appetite ("put the big things on another volume"), and it is also what keeps the
design simple. Configuration, certificates, key material, the player database,
logs and update staging are all small and stay at the data root; the data root
itself stays put, because the config file's location derives from it and a
setting cannot describe where its own file lives. (`BEDROCKSENTRY_DATAROOT`
still exists for installs that want everything elsewhere, set before first run.)

**What a removed server left behind is listed on the same page** (`D63`): its backups folder when
the box to delete it was left unticked, its console-log folder, and its version record. Each row
has a tickbox, and **Delete selected** removes the ticked ones after a second press; nothing is
deleted unasked and nothing is pruned by age, which is what a removed server's backups are kept
for. A retired server is configured and keeps its folders, so it is never listed. The deletion
asks the list again when it runs, so a server created since under a removed one's id keeps its
folders, and a folder delete that stops partway is reported as partial with what remains.

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

A server adopted from a folder outside the data root cannot start until an
administrator grants that folder with `install.ps1 -GrantFolder` (§ 17.7); until
then each start is refused with the reason and never counted as a crash.

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

**The drain runs before anything else in the host stops (`T112`).** Hosted
services stop in reverse registration order, and the web server, registered
last, stops first and waits for its open connections until the host's shutdown
timeout. So the drain runs in `StoppingAsync`, which the host calls before any
hosted service's `StopAsync`. And a request that arrives once the stop has begun
gets a 503: a panel tab reconnects at once, and a connection it opens then is
one nothing closes.

**Windows is told the service stopped when the host has (`T167`, `D32`).** The
framework's service lifetime waits the host's shutdown timeout and then reports
stopped whether the host has finished or not, so a drain that overran it left
servers saving behind a service Windows called stopped, and a start in that gap
found them on its worlds. The service's lifetime now holds its stop pending until
the host has stopped, asks Windows for more time every ten seconds, and writes the
overrun to the Application log. The hold has no timeout: a server's stop kills it
when its budget runs out, so only something holding an instance's gate can make it
long, and then "stopping" is the true answer. A Windows shutdown is unchanged,
because Windows bounds that wait itself.

**Asking where everyone is comes out of the drain budget (`D35`).** Before
`stop`, a server whose companion pack has answered this run's probe is asked
where every online player is (Section 9). The wait is at most two seconds, or a
quarter of the budget if that is less, and it is spent from the budget rather
than added to it, so the invariant above holds unchanged.

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

Setting the schedule to none while a restart's countdown runs calls that restart
off (`T425`): the stop has not begun, so players are told the restart is off, the
server stays up and the claim ends at once. Once the stop has begun it finishes.

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
transcript on disk, and anything later that counts it. Once chat is relayed the
console gets a **Chat** box, and it filters at render time, never at read time.
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

That makes a pack's relay the **only** source of chat, not the better of two. The
companion pack relays none, because the event it would need is pre-release
(Section 9, `D95`), so nothing is ever classified as chat. Until a relay exists
the console has no **Chat** box at all (`ON4`, `T370`); it had an unticked,
disabled one (`T316`), which the operator asked to be removed until chat is
supported. A chat line that arrives anyway, from some pack other than the
companion, is shown rather than hidden behind a box nobody can see.

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

`scripts/stdin-spike.ps1` was run against BDS **1.26.36.1** as LocalSystem
(`NT AUTHORITY\SYSTEM`), session 0, non-interactive — the conditions the Windows
Service launched instances under while it ran as LocalSystem. Every check passed,
and the timings say the important part: there is no output buffering to work
around. The service now starts each server at low integrity under its own
account (§ 17.6). The same measurements there are `T385`'s, and this
table stays the LocalSystem reading until that entry has run.

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

The `save query` file list folds under the `Data saved. Files are now ready to
be copied.` line before it, behind a *Show N files* toggle on that line
(`ConsoleSaveFold`). It is one long line of paths a backup prints every time,
and reading it is rare. A list with no `Data saved` line above it on screen,
because the buffer evicted that line or a filter hides it, stays shown: a fold
with no toggle would hide it for good.

**Filtering is a separate control from colour**, with five switches — server
output, joins, leaves, deaths, chat, the last disabled until chat is relayed —
and a **Players only** preset that turns
the console into a feed of who is on the server and what they are doing. Server
output covers everything that is not one of the other four, warnings and
errors included: an operator who has hidden the machine has hidden its
complaints too, which is what asking for only the players means. Run
boundaries are never hidden whatever is ticked, because a restart in the
middle of a filtered feed would otherwise be invisible and "why did everyone
leave and rejoin" would have no answer.

**The companion pack's lines are read, not just shown** (Section 9). A death
is its own subject, marked with a dagger, under the deaths switch. The pack's
join and leave lines, which carry the position, count as arrivals and
departures while the server shows join and leave positions, and are dimmed
otherwise. The lines it prints for the wrapper rather than for a person —
hello, the probe, the positions asked for before a stop — are dimmed. The
console shows the pack's sentence where a line has one; the message underneath
stays what the server printed, so every matcher reads the same text.

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
  TELEMETRY MESSAGE block is advice rather than an event and is dimmed whole,
  and folds under its opening line, which carries a *Show notice* toggle
  (`ConsoleNoticeFold`, `ON2`); the buffer and the transcript keep every line.
  The ALLOW LIST WARNING and TRANSPORT TYPE ERROR boxes keep their severity and
  carry the same toggle as *Hide notice*, open at first (`T406`). The choice is
  per banner, kept in the browser per instance (`ConsoleNoticeChoices`), so it
  reaches the next start's box.
  The banner's subject opens the box, never a bare row of `=`: the rules are
  identical between one notice and the next, so opening on the rule would
  swallow whatever followed the next one.
- **A blank line is never shown** (`T311`), whatever prefix or padding it
  carries, and the transcript keeps it. The wrapper reads one after every script
  line from Linux BDS (`D93`'s spike) and two on Windows (`T295`), and two on
  Windows after *Content logging to console is enabled.* at each start. A row
  with a time and nothing after it says nothing.

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

### 5.7 One job at a time

A job that stops a server, changes its files and starts it again claims the server for its length
(`InstanceClaim`): a scheduled restart, a restart from the panel, a Bedrock update's install, a
restore, a sync (`D103`), and removing, retiring or moving a server. While one holds the claim, a
start, stop, restart or second claim from anyone else is refused with the job's name, so nothing
lands between its own stop and its start. A claim waits for a start or stop already under way and
is set while the gate they take is held. The wrapper's own stops are the exception: a service stop
and the drain before the wrapper's update stop every server, claimed or not, because Windows waits
for nobody (§ 5.2). The wrapper's update therefore waits a check while any server is claimed,
rather than drain one part-way through a job.

What comes due during a claim waits for it. A scheduled restart says so once on the console; when
the claim ends it runs if the server is running and has not started since it came due. A start
after that moment, such as a sync's own, is the fresh process the restart was for, and a server
left stopped has nothing to restart. A daily restart is remembered past its time, since the
schedule would answer with tomorrow's. An automatic Bedrock update says so once, keeps the build
rather than declining it, and installs on the first tick after with its short final countdown. A
scheduled backup runs on the first tick after, and a join or leave takes none. **Back up now** and
add-on changes are refused. The backups a job takes inside its own stop, a shutdown backup and a
safety backup, run.

### 5.8 Command completion

The Console tab completes commands as they are typed (`D105`). The list opens with the first
character, Tab takes the highlighted match, Up and Down move in the list while it is open and walk
the history while it is closed, and Esc closes it. A grey preview shows the rest of the first match.
Under the input, the usage line shows the command's description and the forms still possible, with
the word the caret is in marked. Enter sends exactly what is typed; a suggestion is taken with Tab,
never with Enter. Completion runs in the browser (`console-completion.js`), and the send goes back
through the component, so what may be sent stays in C#.

**The commands are the server's own.** A version's commands, forms, descriptions and aliases are
read with `help` from a throwaway copy of its package: its own folder under the data root, cheats
on, LAN visibility off, two free ports, deleted afterwards. A server with cheats off lists 17
commands and none of an add-on's, so the instance's own start cannot stand in, and turning cheats on
in the operator's world is not an option. Option-list values ship with BedrockSentry, and the same
run sends each one and keeps those the server parses; `gamerule` lists the rules. Until a version
has been read, completion uses the newest version read that is not newer, then the list that ships
with BedrockSentry, and says which.

**Names come from Mojang.** Blocks, items, entity types, biomes, effects, enchantments and camera
presets come from Mojang's `@minecraft/vanilla-data` package for the server's version, fetched at
each BDS update check and at an install, only while the check is on. The tarball is checked against
the registry's `sha512`. Mojang can publish the package after the server, so until it does the
newest earlier release is used and the Commands view says so. Names the server refused where a form
takes them ship as measured exclusions. Biomes keep their `minecraft:` namespace, which
`locate biome` requires.

**An add-on's commands** come from a second throwaway run with the instance's switched-on script
packs, their dependencies and the world's `level.dat`, started once it is switched on. They complete
at once, and until the server restarts the usage line says it has not loaded them. Blocks, items,
entities and functions an add-on declares are read from its files. Values of an add-on's own option
lists are not completed.

**Players.** Online players complete wherever the server takes a player or a selector.
`allowlist add` offers the players BedrockSentry has seen that are not on the server's allowlist,
and `allowlist remove` offers the names on it.

One throwaway runs at a time. A failed run is retried at the next update check, install, add-on
change or start, never in a loop. The Commands view, the second half of a Console | Commands
switch, lists where each kind of word came from and how fresh it is, then every command with its
forms, filtered by name, alias, description or form.

**The world's ticking areas are the console's third view (`D107`).** The Script API cannot see an
area a command made, so the view reads `tickingarea list all-dimensions` from the console,
collecting its answer from its header to the `n/10 ticking areas in use.` line, and changes areas
with the commands an operator would type, in the area's dimension through `execute in`. Bedrock
snaps an area to whole chunks and cannot change one in place, so an edit removes the area and adds
it again, and puts the old one back if the new one is refused. A world has ten areas in all its
dimensions together. Each area can be shown in game for operators in a colour of its own, kept in
the instance's `TickingAreas` by name and dimension, since one name may be used in two dimensions;
its outline is a full-height box over its chunks, a circle drawn as a circle. Nothing is sent while
a sync runs, since a sync adds and removes its own area named `bedrocksentry` through the same
console; the view shows that area and does not edit it.

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
  worth knowing going in. A `chatSend` before-event could suppress this later
  if wanted; it is pre-release (Section 9, `D95`).
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
                      gamertag — the name the connect line printed (schema v2)
                      exit_x, exit_y, exit_z, exit_dimension
                      — null unless the companion pack reported them (Section 9)
player_deaths         id PK, xuid FK, instance_id, died_utc, cause,
                      killer_type, killer_name, x, y, z, dimension, message
                      — one row per relayed death; counts are derived (schema v2)
companion_reports     (instance_id, reported_utc) PK, pack_version
                      — each hello; the earliest is when counting began there
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

A timestamp in the file is read as an instant, not as text (`T301`). An editor
that writes the file back often respells them, `2026-09-22T18:00:00.0000000+00:00`
as `2026-09-22T20:00:00+02:00`, so import converts each one to the store's own
spelling before matching or writing it. Only ISO 8601 is read; a row whose
timestamp is not one is skipped and named in the result.

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

**Format and acquisition are separate questions.** A strategy decides what the
backup *is* — an archive (§ 8.3), a folder (§ 8.4), somebody's own command
(§ 8.2). How the world's bytes are *obtained* is one setting covering all of
them (§ 8.7), held by the orchestrator, because a snapshot is taken once per run
rather than once per strategy.

### 8.1 Orchestration
`save hold` → poll `save query` until the snapshot is ready → watch the save's folders for
the write the hold sets off, and when one lands `save resume` and hold again → acquire the world
(§ 8.7) → run every enabled strategy in parallel → `save resume`,
unconditionally (try/finally), even if a strategy fails.

**The first list predates the save it announces** (`T356`). Measured on Linux BDS 1.26.52.3 with
an idle world: `save query` answers at once with the database's lengths as they stood at the hold,
then BDS writes the changes from before the hold into the `db` log past those lengths, 0.9 to 4.2
seconds later, and the list does not move while the hold lasts. A copy at the first list's lengths
read the blocks from before a `fill` made up to 10 seconds ahead of the hold; one at a second
hold's list, after that write, read the new blocks four times out of four. So the backup watches
the folders the list names for up to 10 seconds, and once a write has landed and stayed still for
a second, or is still going at the end, it resumes, holds again and copies at the second list.
Nothing written means the first list is the whole save, and the exchange is the one hold. A resume
between the two holds that is not confirmed fails the backup, with that resume on its record.

**A backup carries the whole `worlds` folder** (`D97`). While the server runs, the files
`save query` names — the loaded world's `db` folder, `level.dat` and `levelname.txt` — are copied at
the lengths it gives, because only that list is consistent while saves are held. Everything else
under `worlds` is copied whole: the world's pack folders and pack lists, other worlds, and any file
an administrator keeps there. Inside a `db` folder the save names, the save's list is the whole
answer: LevelDB's lock, its logs and tables it has finished with stay out. A file beside the save
that cannot be opened is left out and named in the backup's result, and the backup still stands;
so does one whose folder cannot be listed, which then holds the save's files alone and says so.
A stopped server's backup walks every file under `worlds`, so it always carried all of this;
before `D97` a running server's backup held the save's files and nothing else, so it had no
add-ons in it.

**Where the resume falls depends on acquisition.** With a shadow copy it comes
straight after the snapshot, before the copy begins, which is the whole of what
that setting buys; without one it comes after every strategy has finished. Either
way it is in a `finally` and fires exactly once per hold, and the instance console says
which of the two it was — a resume that follows a snapshot has not finished a
backup, and saying so would tell somebody watching the console the wrong thing.

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

**A backup shows where a sync does (`D113`).** Whoever asks for it, the orchestrator keeps
where it is (`BackupRuns`), and the strip under the controls lists its steps: Pause saves, Copy
the world, Resume saves on a running server, with Take a snapshot after the pause and the resume
before the copy when a shadow copy is taken; the copy alone on a stopped one. While the ZIP or
folder copy runs, a bar counts the world's files, each once however many formats copy it; a
custom command copies with another program and has no bar. One somebody asked for, and any that
did not work, stays with its record's label and sentence until dismissed; an automatic one that
worked goes when it ends. A backup taken inside a job that claims the server is a step of that
job's strip and draws none of its own. *Back up now* waits while any backup runs.

### 8.2 Custom Command
Runs a user-provided script/command once the snapshot is ready.

**A script cannot copy a running server's world with ordinary tools.** BDS
holds the current LevelDB write-ahead log open for writing, and `copy`,
`xcopy`, `robocopy` and `Copy-Item` all ask for access Windows will not grant
alongside it — they fail on `worlds/<level>/db/*.log` and nothing the wrapper
does can change that for somebody else's process. A custom command either opens
files the way § 5.5 describes, or runs only against a stopped server. The
strategy's own description in the UI says so.

The manifest file it is handed names the save's files only. The rest of the `worlds` folder,
which the ZIP and folder formats carry beside the save (`D97`), is the script's own to copy.

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

- **The archive is laid out as the server's own folder (`D110`).** The world is
  under `worlds/`, and the four files are at the top beside `bds_version.txt`,
  which names the Bedrock build the server last reported, and
  `bedrocksentry_origin.json`, which names the instance it was taken from by id and
  name (`D89`). A restore into another instance says so and goes ahead; a restore as a
  new server picks the build the backup names before the source's version log. Unzipped, with
  `bedrock_server.exe` dropped in, it is a server. Backups written before `D110`
  hold the `worlds` folder's contents at the top and the configuration under a
  reserved `_serverconfig/` prefix; they restore exactly as they always did.
  `NewServerWorld` tells the layouts apart: a `_serverconfig/` entry means the
  older one, and otherwise a file only a server's folder has at its top means the
  new one, with or without a `worlds/` folder left beside it, so a folder backup
  gutted to those files holds no world (`T236`). The identity key under `keys/` is
  never written (`D73`).
- **They are read straight from the instance directory, not from the manifest.**
  They are not part of the save, so `save hold` says nothing about them and
  there is no byte count to honour — but they are opened the same permissive way
  world files are (§ 5.5), because BDS rewrites the allowlist and permissions
  whenever somebody changes them in game and may well be holding one open.
- **A failure here is logged, not fatal.** They are added after the world and
  never instead of it: a backup with the world and no `server.properties` is
  worth far more than no backup at all.

### 8.4 Folder copy

The world copied out uncompressed, one file per file, with the same retention
options as ZIP. Both formats can be on at once, and they produce two copies of
the same backup, of the same instant.

**What it buys over an archive is not speed.** A folder restores by copying and
can be read without any tooling, and on a volume running Data Deduplication it
stores best: unchanged world files are byte-identical from one backup to the
next and collapse completely, which compression prevents (§ 14 has the rest of
that trade).

**The manifest lengths still apply**, and that is the part it would be easy to
get wrong. The world files on disk are *longer* than the save BDS just flushed —
those trailing bytes belong to the next save (§ 5.5). Copying them whole produces
a folder that looks perfect and restores to a corrupt world.

Configuration files are laid out as the ZIP strategy lays them out, the world
under `worlds/` and the configuration at the top (`D110`), so a restore reads one
layout whichever format wrote the backup — and they are read from the live instance directory rather than from
wherever the world came from, because a config file edited a second ago is more
useful than whatever the volume happened to be holding.

**A half-copied folder is deleted**, because it would list in the restore picker
looking exactly like a complete one.

**This format was called "VSS" and it produced no VSS snapshot of its own.** It
took a shadow copy, read the world out of the frozen view, wrote a plain folder
and released the snapshot — so the tab offered *ZIP* against *VSS*, which reads
as file-versus-snapshot and named a compressed file against an uncompressed
folder. It also broke a reasonable space expectation: shadow copies are
copy-on-write and cost deltas, and materialising one into a folder throws that
sharing away at full world size every time. The snapshot was a means, and it is
now § 8.7, applying to both formats.

### 8.5 Restore
Picker listing available ZIP archives and folder copies side by side, labelled
by which they are and with notes shown. Each row names its file or folder as it
sits in the backups folder, so a row can be matched to what is on disk; a row whose
file retention has removed keeps the name it had. Neither is a snapshot by the time it is
listed — a shadow copy is released when the backup that used it finishes (§ 8.7),
so every row is a file or a folder on an ordinary disk. Custom-command backups appear in history
for reference but aren't restorable through the wrapper, since it doesn't know
their format.

The two shapes go through one flow. A folder copy's world is copied back where an
archive's is extracted, and both obey the same two rules: only the backup's
`worlds` folder goes into `worlds`, read by its layout (`D110`), so configuration
never arrives as a world folder and a server folder's binary, packs and `keys/`
go nowhere; and every target is checked to be inside the worlds directory. An
entry written with backslashes, as Windows PowerShell 5.1's `Compress-Archive`
writes them, is read as one with slashes. The wrapper's own backups cannot contain a traversal, which is
exactly why the check exists — the dangerous one is the folder somebody placed
there by hand.

Restore flow: stop the server if running → take a safety backup of current
state first → apply the selected backup → restart.

**The restore panels open near the top of the Backups tab, and a restore runs where it is seen
(`D111`).** Restore, Restore as new and An area… open their panel under the tab's four cards and
above *Back up now*, and scroll the page to it; a confirmed restore scrolls to the controls. A confirmed restore of the world runs in the background: the strip
a sync draws under the controls lists its steps (Stop, Safety backup, Put back the world, Start for
a running server; the middle two for a stopped one) and stays after it ends with the restore's own
summary until dismissed. Its outcome is the sync's: finished, called off with the world as it was,
or stopped after the backup's world was put in place. An area restore draws there too.

**Restoring the add-ons is a choice too, on by default** (`D97`). A world's add-ons are its two
pack folders and the files that switch packs on, record their history and hold their settings.
The dialog offers a box, ticked each time it opens, when the backup carries any: ticked, the world
comes back exactly as the backup has it; unticked, it keeps the add-ons the server has now, copied
across from the world being replaced. A backup with no add-ons in it — every one a running server
took before `D97` — offers no box and keeps the server's, and the dialog says so: restoring it used
to replace the whole folder and take every add-on the world used with it. Each world is settled on
its own, inside the move-aside, so a failure puts the whole previous world back. Restoring into a
new server offers the same box; unticked, the new world has no add-ons, since a new server has none
of its own to keep. The experiments a script pack needs live in `level.dat`, which is the world,
and come back with it either way.

**Restoring the configuration is a separate, opt-in choice.** The picker offers
it as a checkbox, shown only when the archive actually carries configuration and
unticked every time the panel opens rather than remembered. Putting an old
`server.properties` back can move the port, which is the one change that makes a
server unreachable rather than merely wrong — so the world and the
configuration are restored independently and the configuration half is off
unless asked for. When it is asked for, the server keeps its own `server-name`,
`server-port`, `server-portv6` and `server-udp-ports` over the restored file (`D127`): a backup of
another server carries that server's ports, and two servers on one port is a
pair of which one will not start. The summary says so, and names the server the
backup was taken from when it is another one. A new server made from a backup
has the backup's `server-udp-ports` lines commented out, since the window was the
source server's; unset, play goes to the copy's own `server-port`.

The two halves are also handled differently on failure, deliberately. The world
goes through the move-aside dance because a half-extracted world is corrupt;
the four configuration files are written afterwards, outside it, because their
previous contents are already in the safety backup and a failure there is worth
reporting rather than worth unwinding a completed world restore for. The safety
backup carries configuration too, so undoing a config restore is restoring that
archive with the box ticked again.

**A new server loads the world its backup's server loaded (`T335`).** BDS opens the folder under
`worlds` that `level-name` names and generates a new world when there is none. A new server's
`server.properties` is the package's, which names `Bedrock level`, so a backup of a server whose
world has another name went into a folder nothing opened. After the world and any configuration
are in, `level-name` is pointed at a world the server holds: the one the backup's own
`server.properties` names, then the one already set, then the first by name. The summary says
which when there are several, and when `level-name` could not be written it says so and the
server is not started.

**A world file makes a new server the same way (`T334`).** The create form takes a `.mcworld`
exported from Minecraft, which holds one world with `level.dat` at its top, or a ZIP in either of this
panel's backup layouts (`D110`). The upload is written
under the data root before it is read, since a ZIP's directory is at its end and a world can be
gigabytes; one nobody uses is deleted with the form or a day later. Every entry is planned
before the server is created, by the add-on uploads' rules (`D82`) with bounds for worlds, and
two entries Windows would write to one file refuse the file. A `.mcworld` goes into the folder
`level-name` names. A backup's worlds keep their names and `level-name` is settled as above. Its
configuration is opt-in as in a restore, limited to the four files a backup carries, with the new
server's name and ports written over it (`D42`). The server is created stopped with auto-start
off, and takes the form's answers only once its world is in and `level-name` names it; the
companion pack goes into that world (`D93`); a world that already has it keeps its version until
its next start, which moves it to the shipped one (`D106`).

**A backup made elsewhere is imported one at a time (`D110`).** The Backups tab's
Import… takes an uploaded `.zip` or `.mcworld`, or a folder on the machine by its
full path, which is copied and never moved; a folder holding a link is refused.
Every entry is planned first by the uploads' rules (`D82`), and the panel shows the
layout, the worlds, the configuration, the build from `bds_version.txt` and the server
from `bedrocksentry_origin.json` when there are, and when it was taken: the stamp in a BedrockSentry backup's name,
else for a ZIP or `.mcworld` the `LastPlayed` its `level.dat` carries (seconds in UTC, since a ZIP's entry
times have no zone), else the newest `level.dat`'s file time. A folder the service's account may not read,
such as one under a user's profile, is refused with that reason and the advice to upload a `.zip` of it,
since the panel grants the service no folder (`D120`). Entry times the wrapper writes into a ZIP are local
time, the convention Windows reads by. It is kept as it came under `backup-{stamp}.zip`,
`backup-{stamp}.mcworld` or `folder-{stamp}`, except that a copy of a server's whole
folder keeps only its worlds, configuration and version file, and names what it
left out. It is recorded as `Imported` where its time puts it in the history, with
the note, and marked Keep. It is never the server's last backup: not on the
standing card, not for the skip rule and not as the schedule's anchor. Restoring an
imported `.mcworld` in place puts its world in the folder `level-name` names.

**A backup downloads as a `.zip` or a `.mcworld` (`D110`).** A ZIP is sent as it is.
A folder copy or an imported `.mcworld` is zipped as the server's own folder as it is
sent. A `.mcworld` holds one world, the one the backup's `server.properties` names or
its only one, with `level.dat` at its top and no configuration. Packing is Deflate at
its smallest size, the strongest Minecraft reads.

**A sync copies another server's newest backup in and keeps chosen areas (`D103`).** A server's
Sync tab names the server it takes its world from and the areas of its own world it keeps, the
Sync Areas, each a name, a dimension and two corners. A sync saves each Sync Area through the
running server's console as structures of at most 64 blocks across and the dimension's height
(384 in the Overworld, 128 in the Nether, 256 in the End), every command for a Nether or End area
run through `execute in` (`D106`), then stops the server, which writes them into the
world's database. It reads them out of the stopped world, takes a safety backup at this server's
own ZIP compression and retention, as a ZIP run would (`D104`), and puts the other server's newest
usable backup where `level-name` points, keeping this server's
`server.properties`, name and ports. It then writes the pieces into a pack in the new world, starts
the server, and loads each piece back after removing the entities in its box. Nothing in the world
changes until every piece is a file outside it: a failure before then calls the sync off and starts
a server that was running again. A failure after the world is replaced leaves the pieces waiting:
the sync records its areas as it saved them beside the pieces, under the data root, and the Sync
tab offers **Put the Sync Areas back**, which stops the server, starts it with the pieces in its pack
and loads each again, or **Discard**. A new sync is refused while pieces wait, since it would save
the replaced world's areas over them. The pack comes off at the next launch, since a world's pack list
is only edited with its server stopped (`D85`). The other server is only read through its backups,
and its safety backups are passed over, since each holds a world that was replaced. The sync claims
this server for its whole length (§ 5.7). Two Sync Areas may share blocks only in different
dimensions. Each also carries an outline colour, new areas taking the next unused one of eight, and
whether its outline is drawn in game; neither changes what a sync keeps. The wrapper hands the
companion pack the areas with Show in game ticked after its every `hello` and after every save on
the Sync tab, and the pack outlines them for operators (§ 9).

**One area can be put back from a backup (`D108`).** Restore on a backup's row offers *An area…*:
a panel under the tab's four cards taking a Sync Area of the server's own, or two corners and a
dimension, with no password, since the restore takes a safety backup of the whole world first. The wrapper starts a throwaway copy
of the server's own Bedrock package on the backup's world, with the server's packs from outside its
world beside it, clears the copy's ticking areas, saves the area through its console as a sync saves,
and reads the pieces out of the stopped copy, all while the server runs. Then it stops the server
with 30 seconds' warning, takes a safety backup at its ZIP compression and retention, writes the
pieces into the world's structures pack, starts the server and loads each piece back. A read that
fails calls the restore off before the countdown, with the world untouched. The restore claims the
server for its whole length (§ 5.7), so a sync on it is refused meanwhile. `restorearea` in game
does the same with no confirmation (§ 9). A backup chosen as `latest` is the newest that is not a
safety backup, opens, holds a world and was not recorded as damaged, the rule a sync's source
backup follows. A backup waits for the write `save hold` sets off before it copies (§ 8.1, `T356`).

**An area can come from another server's backup (`D111`).** The area panel's *Put it back on*
names any server that is not retired. The Sync tab's *Sync one area* takes one area from the source
server's newest usable backup and leaves the rest of the world as it is. Either way the server whose
world changes is the one that is claimed, warned, stopped and backed up, and its own Bedrock build
reads the backup; the other server is only read through its backup. A Sync Area is carried with its
corners, so the server it goes onto needs none of that name. A server on an older build than the
backup's server is refused before anything stops, as `D103` requires of a sync; a build the wrapper
does not know refuses nothing.

### 8.6 Notes and locks

Two controls on every backup row. A **note** is free text an administrator types against one
artifact — why this one matters, as opposed to why it was taken. A **lock** means *never removed by
retention*, and it is **not counted toward the retention number**: keep 5 with 2 locked is 5
unlocked plus 2 locked on disk. Counting a lock against the number would make a lock silently
shrink the retention somebody asked for.

**Both are per artifact rather than per run.** One run with both formats enabled writes one history
record and two artifacts, so a per-run note would be ambiguous in a way a lock is not — and the two
controls sit on the same row of the same table, so they have to mean the same "this backup".

**They live in their own file**, `artifacts.json`, beside the history in the instance's own backup
folder — same directory, same atomic write, same fate if the instance is removed. Not in the
history: that records *what happened*, and a history that is rewritten when somebody ticks a box has
stopped being one. Entries are keyed by artifact name, and an entry whose artifact is gone is kept
rather than swept, because deciding which artifacts exist is the filesystem's job.

**There are two notes and both are shown.** The run's note travels with the history record and does
not change; the artifact's note is typed afterwards. The row shows the second first, then the first.

**History rows have a retention of their own** (`D98`), per server and 30 days by default. Each
recorded run trims the history to it, except for three kinds of row something still reads: a row
whose backup is still in the backups folder, a row recording that a prune damaged a backup still
there, and the newest successful run. An administrator can remove a row whose run left nothing on
disk; a row still describing a backup stays with it. The chips are **Restorable**, **Skipped**,
**Problems** and **All**, and the tab opens on Restorable, which hides every row without a backup;
Skipped shows the runs that were skipped (`D110`).

**A lock stops retention and nothing else.** The Remove-instance dialog's delete-backups checkbox
names how many locked backups it will take and then takes them: an explicit, confirmed,
password-gated instruction is not something a lock may veto, because a lock that could would make
removal uncompletable from the panel. The safety backup a restore takes is lockable and is not
locked by default — locking it by default converts a safety net into storage retention can never
reclaim.

### 8.7 Acquisition: the shadow copy

How the world's bytes are obtained, which is a separate question from what format
they are written in. One toggle per instance, applying to every strategy, and one
snapshot per run rather than one per strategy — so two formats enabled together
read the same frozen view.

**What it buys is time, not space.** World saves are held for every backup on a
running server; with a shadow copy they are held only long enough to take the
snapshot, and resume before the copy begins. Nothing on the volume can move while
the copy runs either, and a file another process holds exclusively can still be
read — LevelDB's `LOCK` being the one that bites. Without it, the world is read
live and saves stay held until every strategy has finished.

**Backups are not kept in shadow storage.** The snapshot is released as soon as
the copy is done, so every backup costs its full size on disk whichever format
wrote it. Retained snapshots were considered and rejected: Windows deletes
oldest-first when the shadow area fills, which would make retention Windows's
decision rather than the administrator's, and copy-on-write cost tracks volume
churn, which an active LevelDB generates hard.

Three rules carry the safety of the operation:

- **The shadow copy is released unconditionally**, in the same spirit as `save
  resume`. One left behind holds volume space until somebody notices, and on a
  schedule it is left behind every single time. A release that fails is logged
  and never fails a backup that otherwise succeeded.
- **A snapshot that cannot be taken falls back to the live world**, warns, and
  still produces a backup. A failed acquisition is a slower backup — saves held
  for the whole copy — rather than a lost one, and reporting it as a failure
  would blank the last-successful time and make the skip rule back up on every
  tick. The warning reaches the backup's own history row rather than only the
  log, and it names the consequence before the cause.
- **A stopped server takes no snapshot at all.** There is nothing holding saves
  to shorten, so it would be pure cost. The record says so rather than reporting
  the toggle as off.

Taking a shadow copy is an administrative operation. Off by default, because an
account that cannot take one would otherwise fall back on every scheduled backup
— which is precisely what the Readiness panel (§ 14) exists to predict.

**The broker takes the snapshot and the main service reads it** (milestone 18.5,
§ 17). The main service sends `CreateShadowCopy` with the instance id and nothing
else. The broker resolves the server's folder from the configuration, refuses it
by `BrokerPathRules`, opens it without following a link, snapshots its volume,
records the shadow id Windows answered with in its own record, and answers with
that id and the snapshot's device path. The main service copies from the device
path with its own account's rights, opening each file through `ServerFileReader`
as it does for a live read, then sends `ReleaseShadowCopy`, which the broker carries
out only for an id it recorded. An install without the broker (`-NoBroker`)
takes the snapshot in the main service's own process, which its account has no
right to do, so every backup on it reads the live world and says so.

The snapshot is taken through `Invoke-CimMethod` against `Win32_ShadowCopy` with
the `ClientAccessible` context, not `vssadmin` (Server SKUs only) and not
`diskshadow` (a scripting language of its own with prose to parse). The CIM
cmdlets work under both `powershell.exe` and PowerShell 7; the older `[wmiclass]`
accelerator does not. `ClientAccessible` is what general-purpose backup tools use
— a persistent read-only snapshot that survives until deleted, rather than one
expecting a requester to hold the VSS session open for the life of the copy,
which this process is not.

## 9. Companion Add-In (Behavior Pack)

Ships with the wrapper and reaches each server through the add-on library
(`D77`, `D93`): it is put on the library at every start, a new server gets it
switched on by default, and an existing server switches it on from its Add-ons
tab. A world that has it follows the version BedrockSentry ships: just before a
launch, an older companion is moved to the newest in the library and the console
says so (`D106`). It is never switched on that way, never moved down, and every
other pack stays at its version. Gives the wrapper visibility the console alone doesn't provide, using the
Bedrock Scripting API on stable modules only.

- **Achievements stay earnable (`T428`).** A world that runs with cheats on can
  never earn achievements again, even with cheats turned back off, so nothing the
  wrapper ships needs cheats. Every custom command the companion registers sets
  `cheatsRequired: false` (the API's default is true), and every pack the wrapper
  ships or writes (the companion, its resources and the structure pack) carries
  `"metadata": { "product_type": "addon" }`, without which a world holding it shows
  an *Achievements disabled* warning. Turning `allow-cheats` on is the
  administrator's choice alone, and the panel says what it costs wherever it is set.
- **Relay mechanism:** `console.log()` calls inside the pack reach the server's
  console as script output, not player-visible chat, and the wrapper reads
  them there. BDS ships script output to the console switched off
  (`content-log-console-output-enabled=false`, `D91`), and `content-log-level`
  at `warning` or `error` hides it too (`D94`), so switching the pack on sets
  both (`D93`). An HTTP push via `@minecraft/server-net` is a possible later
  upgrade, but that module is still pre-release and requires explicitly
  enabling it in `permissions.json` — console relay is the safer default.
- **The wire (protocol 1).** Each line is `[BedrockSentry] kind|field|…`, after
  whatever tags BDS puts in front (`[Scripting]` on Linux 1.26.51.1), with the
  sentence for a person last on the kinds that have one, where it may hold any
  pipe. The kinds are `hello` (as the world loads, naming the protocol and the
  pack's version), `death`, `join` and `leave`, and the wrapper's side of the
  channel: `probe` and `where`/`where-end` answer a console `scriptevent` the
  wrapper sends. A later protocol adds fields before the sentence, so nothing
  is recorded from a pack whose hello names another protocol, and a kind the
  wrapper does not know is ignored.
- **Commands (`D106`).** From 1.1.0 the pack registers operator-level commands
  through `customCommandRegistry`, which run in game and from the server console:
  `listcord [player]` answers with every online player's whole-block x y z and
  dimension, `Steve: 1 64 2 Nether`, or that one player's. The console counts as an operator and prints
  the answer; an unknown name answers as a failure. The console's completion reads
  the companion's commands with the rest of a server's add-ons (§ 5.8). From 1.3.0
  `syncarea <new|update|show|list> [name] [pos1|pos2|true|false] [x y z]` edits the
  server's Sync Areas: `new` adds one with both corners on one block, shown in game,
  and on an area that exists `new <name> pos1|pos2` sets that corner, as `update` does;
  `update` moves a corner; `show` ticks or unticks Show in game. A corner is the
  word `pos1` or `pos2`, because BDS 1.26.52.3 refuses a bare number in a `String` or
  `Enum` parameter, and without x y z it is the block under the player. The pack
  checks every word, since the console does not hold an enum to its values, and
  relays a whole request as `area|…`; the wrapper saves it as the Sync tab would and
  answers a player with `scriptevent bedrocksentry:reply`, which the pack passes on,
  and the console on its own page. An open Sync tab shows the change, and an edit is
  refused while a sync runs on that server.
- **Outlines (`D106`).** From 1.2.0 the pack draws areas the wrapper hands it with
  `scriptevent bedrocksentry:outline`, a JSON message of a group, its part and how
  many parts, and each area's dimension, corners and colour. BDS refuses a message
  over 2,048 characters, so a long set is cut between areas and the pack replaces
  the group once its last part arrives, answering `outline|group|count`. From 1.7.0
  (`D109`) it draws each edge as `bedrocksentry:outline_line`, a thin full-bright line
  in the area's colour defined by the companion's resource pack (`companion-resources/`),
  in pieces of at most 16 blocks within 128 blocks of each operator, every second, each
  living 1.25 seconds so the line does not go out between rounds. It draws through
  `Player.spawnParticle`, so nobody else sees them, and a player who declined the
  server's resource packs sees nothing. The resources are not a manifest dependency, so
  a server without them still reports; the wrapper seeds both folders at every start,
  switches the resources on and off with the companion, and adds them before a launch
  to a world that has the companion on (`CompanionResources`). The outlines live in the pack's
  memory, so the wrapper sends them again after each `hello`, and not at all to a
  pack older than 1.2.0. From 1.4.0 an area may be marked `"circle"`, drawn as a circle inside
  its box with rings of chords about 4 blocks long at the bottom and top and posts at eight
  compass points, and the wrapper
  sends the world's shown ticking areas as the group `tick`. `showtickingarea <name>
  <true|false>` is relayed as `tickshow|…`; the wrapper reads the areas, shows or hides the one
  named (a player's own dimension first, every dimension from the console) and answers.
- **Operator commands (`D106`).** From 1.6.0 a player's run of `listcord`, `syncarea`,
  `showtickingarea` or `restorearea` prints `cmd|player|command line`, the command rebuilt from
  its arguments; the console shows *Steve ran /syncarea new Spawn pos1* under an *Operator
  commands* box. The console's own commands are not relayed, since it shows them as typed. A
  vanilla command a player types prints nothing on BDS 1.26.52.3 and has no event in
  `@minecraft/server` 2.8.0, so it cannot be shown.
- **Area restore (`D108`).** From 1.5.0 `restorearea <name> [backup]` restores one of the
  server's Sync Areas and `restorearea pos <backup> <x1> <y1> <z1> <x2> <y2> <z2>` the blocks
  between two corners in the player's dimension (the Overworld from the console). The backup is
  `latest` or a backup's name on disk and comes before the corners, because BDS 1.26.52.3 refuses
  a bare number in a `String` parameter. The pack floors the two `Location` corners and relays
  `restore|…`; the wrapper answers at once whether the restore started, and the countdown that
  follows is the only warning (§ 8.5).
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
      `fire`/`fireTick`, `starve`, and `suffocation` cover what the
      cause-breakdown list below already names as worth tracking on their own
      (a death in the void reports the cause `void`, which has no phrase of its
      own and reads *died (void)*, measured on Windows under `T294`);
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
    `damageSource`: each death's cause and killer are stored, and no page
    groups them yet
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
  - A planned stop asks the pack where everyone is first (`D35`), so players
    still online when the server stops get a position too; a crash, a kill or
    a power cut leaves none, and the page says so
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
  The join here is exact where the allowlist's is not. A relayed line matches
  the sessions on its instance, started on this run, whose connect line carried
  exactly that gamertag, and that are open or, for a death or a leave, closed
  within the last ten seconds, because the pack's line and BDS's own disconnect
  line race. A death needs exactly one player among them and a position
  exactly one session; anything else writes nothing, because a death or a
  position put on the wrong player is believed (`CompanionAttribution`).
- **Planned additions:** blocks placed/broken by type (`playerPlaceBlock` /
  `playerBreakBlock`), mobs killed, PvP kills specifically, distance
  traveled, AFK detection, achievements/advancements earned.
- **Each record is its own toggle per server**, on the Add-ons tab's Settings view: counting
  deaths, keeping each player's last position (which also asks the pack where
  everyone is before a planned stop, `D35`), and showing the join and leave
  positions as arrivals and departures on the console. Each applies from the
  next line the pack prints. Telling a player where they died is the pack's
  own setting, under its gear, because only the pack can act on it.
- **Game chat is not relayed.** The event the pack would need,
  `world.afterEvents.chatSend`, is pre-release, and a pack depending on a beta
  module needs the world's Beta APIs experiment (`D95`).
- Ships as part of the wrapper's own release — updates alongside the wrapper
  at milestone 13.5, not just the wrapper's binaries.

## 10. Add-On Manager

Distinct from the companion add-in (Section 9) — this manages *user-supplied*
behavior/resource packs.

- File picker to manually add packs, or folder scan of a configured directory
- Manifest validation (`manifest.json` presence)
- Enable/disable toggles, which modify the world's `world_behavior_packs.json`
  and `world_resource_packs.json`

**A tab on each instance, over one shared library.** The library is a folder
under the data root, relocatable like backups and server packages, filled either
from Explorer or through an upload in the panel. Each instance enables or
disables each add-on for itself, and `.mcaddon`, `.mcpack` and `.zip` are all
accepted for both behaviour and resource packs.

**The tab is four views of one server's add-ons** (`T307`). *On this server* lists
every add-on with its icon, type, version, status and open issues: the companion
first and the rest by name, switched-on packs above the others, and a line under a
name only when something about it is unusual. A row's details say where it is, its
uuid, its scripts and whether they need Beta APIs, what it needs, its author and
its description. *Settings* is each pack's own settings screen, with the companion's
recording options first. *Load order* is each of the world's two lists with moves
up, down and to the top. *Issues* holds the findings, each naming its add-on, and
any of them can be hidden until what it says changes (`D96`).

**A pack the library has never seen can be copied into it from the server**
(`D78`). Its row offers *Copy to library* while the library lacks a version the
server has; it copies one folder per missing version, the world's copy first, and
leaves the server's own where it was, switched on or not. It reads the server and
writes only the library, so it works on a running server. The library page shows
each pack's icon, the newest version's `pack_icon.png`, as the tab does.

**A change made while the server has a process waits for it to exit** (`D85`).
BDS rewrites a world's pack lists when it stops, so a list written while it runs is
undone by its own exit. Switching a pack on or off, changing its version, a move in
*Load order* and a pack's settings are kept in a queue per instance under the data
root, listed above the views with a Discard on each, and written once the server
reaches Stopped or Faulted and again before every launch, so a crash or a stop from
outside the panel cannot skip them. Each is planned again against the world as it
is then, and writing one twice changes nothing the first did not. One change waits
per pack, per pack's settings and per list; a later one replaces it. A restart's
claim lets the queue write between its stop and its start; any other claim may be
replacing the world and holds it off (`T417`). Removing a
pack's folder is never queued and needs the server stopped. A pack the world lists
with no folder on the server is named by its list and version on its Issues card,
which carries **Remove from this world**: the same switch-off, by uuid (`T416`).

**Identity is the manifest's `uuid`; the version is separate.** That is what
makes two files that look like different add-ons recognisable as one add-on at
two versions, and it is why an instance enables a *version* rather than a file.
The version comes in two shapes — a semver string at `format_version: 3`, an
array in older packs — and so does `min_engine_version`, so a reader accepts
both. The author is in `metadata.authors` rather than in `header`, and
`texts/en_US.lang` may override the displayed name and description through
`pack.name` and `pack.description`, so the header strings are a fallback rather
than the answer. Two versions of one uuid enabled at once is refused rather than
warned about: BDS resolves a uuid to one pack, so the second is an ambiguity
rather than an addition.

**Activation follows the world, so it follows `level-name`.** The lists above
live in `worlds/<level-name>/`, which means the tab reads the list of the world
the instance would actually load rather than keeping its own record of what was
enabled — the rule `D31` applies to allowlists, for the same reason.

**A pack reaches an instance by being copied into its own `behavior_packs/` or
`resource_packs/`.** BDS resolves uuids out of the server's own directory, and a
copy needs no privilege, keeps the instance self-contained across re-homing and
adoption, and is small beside the disk an instance already costs. Links were
rejected because a symbolic link on Windows needs a privilege an ordinary
install does not have.

**A pack's settings screen is a `settings` array in its own manifest**, which
declares the controls and their defaults. Entries are `label`, `toggle`,
`slider` and `dropdown`; `label` carries text and nothing else and is most of a
real screen; the rest carry a namespaced `name`, a `default`, and the bounds or
options the control needs. Text carries Minecraft's `§` formatting codes, so
rendering one faithfully means decoding them.

**The values a world actually uses live somewhere else**, in
`world_behavior_pack_settings.json` beside the world's pack list, keyed by the
pack's **header** uuid. It holds only what differs from the declared defaults,
so a faithful view is the manifest's controls overlaid with that file rather
than either one read alone; it is created lazily, the first time something
changes, so on a server it very often does not exist yet; and it is exactly what
the in-game gear writes, so editing it is equivalent to using that screen.
**Writing there rather than into the pack's manifest is the difference between a
setting that survives an update and one that does not** — the manifest belongs
to the pack and is replaced when the pack is, while this file belongs to the
world.

The neat part is why an add-on needs no config file of its own: Bedrock scripts
have no filesystem access, but the game reads this file and hands the values to
the script through `world.getPackSettings()`.

**Three preconditions decide whether an enabled add-on does anything**, and all
three are cases where the panel says *enabled* and the game shows nothing: a
`dependencies` entry naming a pack by uuid that is not present — distinct from
one naming a script API module by name, which is a compatibility gate against
the server's own runtime rather than against the library — the order of
`world_behavior_packs.json` — position decides which of two colliding packs is in
effect, so such a pair can only be reconciled by reordering — and a world
experiment a script module needs, which lives in `level.dat` rather than in
`server.properties`. Which end of that list wins is deliberately not stated here
until it has been measured rather than recalled.

**Removing a pack from the library can change the servers that have it** (`D83`). A
server has a pack when a world list names it or a copy sits in the world's or the
server root's pack folders; the development folders are the administrator's and are
neither counted nor touched. The companion pack is never removed, since it is put
back at every start. A pack a server that is not stopped has is refused, naming the
server: its world's files are rewritten when its process exits, so nothing written
now would last. A pack only stopped servers have is removed once the administrator
has confirmed a warning naming them, and it comes off each of them first: its
registrations in both lists, its entries in the history and settings files, and its
copies. Every file on a server is planned before any is written, so one this cannot
read refuses the removal there with nothing changed, and the library copy stays
until every server is done. The plan is asked again when it is carried out, and one
that has changed since the page showed it is shown again rather than carried out.

**Uploading is the one place this wrapper extracts a file it did not produce.**
Archive entries are resolved against the destination root and an archive is
refused whole on the first one that escapes it, rather than entry by entry — a
partly extracted hostile archive is worse than a rejected one. An archive
carrying no `manifest.json` is not an add-on and is refused saying so.

**An add-on's commands complete in the console** (§ 5.8). Switching on a script pack starts a
throwaway run with it, its dependencies and the world's `level.dat`, which reads the commands it
registers; a pack without a script module registers none and needs no run. Its declared blocks,
items, entities and functions are read from its files at once.

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

**Every check also asks npm for Mojang's `@minecraft/vanilla-data`**, the names console
completion offers (§ 5.8), and an install asks again for the new build. With the check switched
off, nothing is fetched, and the Commands view says that blocks, items and entities do not
complete.

The Configuration tab and the Settings page say when the feed was last read and,
when a check since could not read it, when and why. That mark is held in memory,
like the last read, and cleared by the next read that works (`T52`).

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
- **When the announcement does not parse, the package the wrapper installed
  decides** (`D53`). A Preview build installed from `bedrock-server-1.26.60.21.zip`
  announces `1.26.60-beta21`. The wrapper records which package it laid down, on
  an update and on a server created from a package, and what the server
  announced on its first start on those files; the package is the build while
  the announcement still matches. A changed announcement that does not parse is
  an unknown build, and the page names what the server calls itself rather than
  telling a running server to start.

Checking and downloading are separate from applying. A downloaded build lands in
the package store under Mojang's own file name — the same folder an
administrator can drop a zip into (`D19`) — so there is one install path and a
downloaded build is not a special case.

**An empty store gets a first build (`T330`).** The updater only replaces a build a
server already has, so without this no first server could be made until a zip was
put in the store by hand. Opening the new-server form with the store empty, or
**Check now** on Settings, downloads the Release build the feed offers; callers
asking at once share one download, which runs to the end if the page goes away.
With checking turned off, or the feed unreadable, nothing arrives and the form says
why and names the folder a zip goes in. Mojang's download page asks for its EULA
before its link works and the wrapper's download passes no such page, so the first
download waits for somebody to press **I accept and download** beside a link to it;
the acceptance is recorded and the button does not appear again (`D102`).

**The package log records each zip's channel (`T333`).** Mojang names a Preview zip
the way it names a Release one, so nothing in the store says which is which; the
feed does, link by link. `data\server-packages.json` under the data root takes a line
when a zip is downloaded from an offer, and when a check finds an offer naming a zip
already in the store, so a build that arrived by hand is tagged while Mojang still
offers it. A zip no line names is shown untagged rather than guessed. The file is
appended to and read by its latest line per name; it lives under the data root so a
moved package store keeps its tags, and it holds the EULA acceptance too.

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

- **Core mechanism (milestone 3.5):** channel selection (Stable or Build),
  version check, download of the artifact published by the release pipeline
  (Section 12.1), and applying the update via a small, rarely-
  changing helper process — a running service can't overwrite its own loaded
  files, so the wrapper stages the build and launches the helper, which stops
  the service through the service control manager, switches the files, then
  starts the service back up. With the privilege broker (milestone 18.5) the
  service sends only the build identity; the broker fetches the manifest itself,
  unpacks a fresh version folder, writes the plan, and the helper stops and
  starts both services, since both services' executables sit behind one `current` link.
- **Graceful integration (milestone 13.5):** once process management and
  backups exist, the update runs through the same sequence as any other
  shutdown — warn online players → stop the server → optional backup-on-
  shutdown → apply the update → restart everything.
- Update mode: automatic / notify-only with a manual button and a
  description of the pending update / don't check for updates (manual "check
  now" stays available even in "don't check" mode)
- The Settings page says when the channel was last read and, when a check
  since could not read it, when and why. That mark is held in memory and
  cleared by the next read that works (`T196`).
- Service Update page shows a changelog

**Two executables in one version folder (milestone 18.5).** A version folder holds
`BedrockSentry.Host.exe` at its top, the broker at `broker\BedrockSentry.Broker.exe`
and the updater at `updater\BedrockSentry.Updater.exe`. Each is a self-contained
publish in a folder of its own, since two in one folder would overwrite each other's
libraries. Both services' ImagePaths go through `current`, so one switch of the
junction moves both, and the contract handshake (§ 17.3) refuses only a
half-applied update or a hand-copied file.

**The broker owns staging and applying** (`BrokerUpdates`). The main service sends
`StageUpdate` and then `ApplyUpdate` with a build identity and nothing else. The
broker reads the manifest from the install policy's `updateFeedUrlTemplate`, or the
built-in feed, and takes only the channel, the health port and the timeouts from
the main service's configuration. It refuses a manifest naming another build, a build
whose install epoch is above its own, and a download address that is not https; it
checks the archive's SHA-256, unpacks it into a fresh `versions\<id>`, copies the
running build's updater to `update\helper` and writes every field of the plan
itself. The download, the helper, the plan, the log and the result all sit under
`update` in the install root, which the main service's account cannot write, so a
link that account planted cannot redirect a LocalSystem write. `ApplyUpdate` launches
only the build `BrokerRecord` names as staged last. The updater stops the main
service and then the broker, switches `current`, starts them in reverse order, and
refuses to run from inside `versions` or `current`. The panel checks the broker's
feed (`IWrapperUpdateInstaller.FeedUrlTemplate`), so the check and the stage read
one feed. On an install without the broker, under an account that cannot write
the install tree, Settings and Readiness say self-update is unavailable.

**An install epoch keeps a build that needs a reinstall out of self-update**
(`D114`). `BedrockSentryInstallEpoch` in `Directory.Build.props` is stamped into
each build as `BuildInfo.InstallEpoch` and into its manifest as `installEpoch`; it
is raised by one on a build that cannot be reached by self-update and never
lowered. A missing field reads as `0`. A manifest whose epoch is above the running
build's is refused before the download, in the main service (`UpdateService.ApplyAsync`)
and again in the broker, and Settings shows `UpdateManifest.ReinstallNotice` in
place of the button: run `uninstall.ps1`, then `install.ps1` from that release.
The refusal is not counted as a failed update, since three of those pause automatic
updates (`D101`). A wrapper older than the field ignores it.

### 12.1 Release Pipeline & Installer

Milestone 3.25. Two consumers, one artifact: an administrator installing
BedrockSentry for the first time, and the self-update mechanism above
fetching a newer build. Both pull the same published output, so it's built
once rather than improvised separately for each.

- CI (GitHub Actions) builds, tests, and publishes on every push to `build`
  and `main`. No local `dotnet build` is ever required to install or update
  the wrapper — the SDK is a contributor prerequisite, not an operator one.
- Each branch has its own published artifact stream, one per channel: `main`
  publishes Stable and `build` publishes Build.
- The installer registers the Windows Service and hands off to the first-run
  setup wizard (Section 13).
- **Two services (milestone 18.5).** The main service runs as its own virtual
  account, `NT SERVICE\<ServiceName>`, or as a named account passed with
  `-ServiceAccount`; LocalSystem, LocalService and NetworkService are refused by
  SID. A named account's password is checked with `LogonUser` before anything
  stops, it is given `SeServiceLogonRight` through the LSA, and the password is set
  through CIM, never on a command line. `sc.exe privs` leaves the main service
  `SeChangeNotifyPrivilege` and `SeCreateGlobalPrivilege` only, so the servers and
  backup commands it starts cannot reach SYSTEM through impersonation, and
  `sc.exe sidtype unrestricted` puts the service SID in its token on a named
  account too, so the pipe lists and the panel's firewall rule can name it. The
  administrative work is the broker's (§ 17): `<ServiceName>Broker`, LocalSystem,
  automatic start, while the main service starts delayed. The installer writes
  `--broker pipe` into the main service's ImagePath in the same run that registers
  the broker, so only an administrator changes which one does the work. `-NoBroker`
  leaves both out and removes a broker service an earlier run registered; the
  firewall, Defender and loopback fixes, shadow copies and self-update are then
  unavailable, and Readiness says so (§ 13.3).
- **The data root is granted to the service's account** (`AccessPlan`). The
  installer runs the staged `BedrockSentry.Broker.exe --prepare-data-root`
  elevated, before the drain, so a refusal costs no outage. It gives the account
  `Modify`, never `FullControl`, beneath the root, and on the root itself read and
  create without `DELETE`, so the service cannot rename the root away and leave a
  junction in its place. Each secret file (the credential, the setup token, the
  certificate and its password, `SecretFiles`) breaks inheritance, so each one
  that exists is granted again on every run. A new root is created carrying its list. An existing root is
  written over only when it and every folder beneath it are owned by LocalSystem
  or Administrators, only they could write there, and no link is among them; it
  is left alone when it already carries that list, and anything else is refused.
  The virtual account's SID is worked out from the service name (`ServiceSid`), so
  the grant can be written before the service is registered. The installer writes
  nothing else into the data root: `-AllowRemoteSetup` and
  `-AllowCustomBackupCommand` go to `policy.json` in the install root, which only an
  administrator can write.
- **An install that runs as LocalSystem is not converted in place.** Everything it
  wrote, in the data root and outside it, was written by SYSTEM, and a switch made
  halfway would leave a service that cannot read its own files. The installer
  refuses it before anything stops and names the way across: `uninstall.ps1`,
  which keeps the data, then `install.ps1` with the same roots (`T129`). Changing
  the account of an existing install is the same uninstall, then install, since
  the grant refuses a tree the old account could have left links in.
- **Each server runs restricted (milestone 18.5).** `bedrock_server.exe`, the
  throwaway runs included, starts under a restricted copy of the service's
  token: every privilege but `SeChangeNotifyPrivilege` removed, and a
  restricting list of a SID for its own folder (worked out from the folder's
  path) and the groups Windows grants its own files to. An access passes only
  where both the account and that list allow it, so the server can change its
  own folder and read none of what is granted to the service alone: the
  certificate and its password, the credential, the setup token, the
  DataProtection keys, the database, the backups and the update folder. The
  service grants a folder it created as its owner; the installer grants the
  data root's server folders when it first takes the root over, and the broker
  a server folder it makes; an administrator grants an existing folder outside
  the data root with `install.ps1 -GrantFolder` (§ 17.7). A folder none of them
  granted refuses the start and says so. The shape is measured on Windows by
  `T385`. The server is less trusted than the service from here on, so what it
  writes is read as data (§ 17.6).
- **Uninstall is non-destructive to data.** It removes both services'
  registrations, both event log sources and the wrapper's own binaries; it leaves
  the named account's right to log on as a service, which another service may
  share; instance working directories,
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
sidecar file. The file's own access list — SYSTEM, Administrators and the
service's account only, with inheritance broken — is the primary control; DPAPI is defence in depth for the
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

**Every server the drain stopped comes back after the update** (milestone 13.5).
The drain records the ids it stopped in `<data>\update\start-again.json`, and the
wrapper that starts next starts them whether or not they auto-start, saying so on
each console. The build under test starts once before its health check can fail, so
its take moves the list to `start-again.taken.json` under its own identity; the old
build, started again by a rollback, takes it from there, and the taking build's next
start or the next drain drops it (`T402`). A
put-back the drain's own caller makes, when the helper cannot be launched, takes its
ids off first. The installer's drain (§ 12.4) goes through the same method. Each of
those launches is where the companion pack moves: the new build's companion is on
the add-on library before any server starts, because `CompanionPackSeeder` is
registered ahead of `InstanceHostedService`, and `CompanionPackUpgrade` moves each
world to it at its launch (`D106`).

### 12.4 A hand-run installer upgrade

`install.ps1` run over an existing install is a second update path, and it was
never as graceful as the self-update. It is not *un*graceful — `Stop-Service`
goes through the wrapper's own shutdown, so every `bedrock_server.exe` gets a
real `stop` and a chance to save — but it is the **service-stop** path of § 5.2:
about five seconds of warning, and no backup-on-shutdown, because there the
service control manager is waiting.

Nothing is waiting on the installer. So it asks first, on the wrapper's **admin
pipe**, which runs the same warn-drain-back-up sequence § 12.3 does, and only then
stops the service. By that point every server is already down and the short budget
has nothing left to hurry.

- **The admin pipe is part of the frozen contract**, like `/healthz` and the port,
  and for the same reason: `install.ps1` from a new release talks to the wrapper
  from an old one. Its name is `<ServiceName>.admin` (`BedrockSentry.admin` by
  default). A message is a four-byte little-endian length and that many bytes of
  UTF-8 JSON, one request and one answer per connection, with no handshake:
  `{"operation":"drain"}` answers `stopped`, `failed`, `calledOff`,
  `warningSeconds` and `version`; `{"operation":"callOff"}` answers `calledOff`
  and `tooLate`; anything else answers `error` and does nothing.
- **Only an elevated administrator or LocalSystem may open it** (`D62`). Its access
  list names those two and denies NETWORK; an administrator's unelevated token and
  an ordinary local account, which cannot stop the service either, can no longer
  drain its servers (`T177`). The installer connects at Identification level and
  checks that the process serving the pipe is the service's before it sends.
  The service's own account holds only the right to create the pipe's second
  instance, and OWNER RIGHTS is named with that one right, so no process carrying
  the account, a server included, can add a third instance or rewrite the list.
  A local account can still create the name while the service is stopped; that
  costs the next upgrade its drain, never its saves, and the Readiness panel
  names it (§ 13.3).
- **Older wrappers.** Every wrapper older than the pipe runs as LocalSystem, and the
  installer refuses those before it drains, naming uninstall then install (§ 12.1,
  `T387`). So when nothing serves the pipe, it is a current wrapper whose pipe was
  not created: the installer says "No graceful drain available" and stops the
  service the ordinary way, which still saves every world. It asks nothing over
  HTTP (`T386` clause 4). An installer older than the pipe posts to
  `/prepare-shutdown`, which this build maps to nothing; its authentication
  redirects the request to the login page, and that installer reads the redirect as
  no drain (`T111`).
- **`-SkipDrain`** gives back the old behaviour for somebody who wants the
  upgrade over with. Worlds are still saved; only the countdown and the backup
  are given up.
- **Ctrl+C calls the countdown off** (`D100`). The drain runs on
  `CancellationToken.None`, because its token means "stop waiting" and a cancelled
  countdown goes straight to the stop, so the installer's `finally` sends
  `callOff` on a second connection to the pipe instead (a `DELETE` to the HTTP
  path for an older wrapper). Until the stops begin that ends the countdown with
  every server running and tells the players; after, it is too late and the answer
  says so. The installer reads the pipe's answer by polling, so Ctrl+C reaches it
  mid-drain.
- **`/readyz` writes at most once in 30 seconds.** Its data-root check tests by
  creating and deleting a file, and the result stands for 30 seconds, so an
  anonymous request is not a way to make the service write.

**Every name derives from the installed service name.** `install.ps1` takes
`-ServiceName` (letters, digits, `-` and `_`, up to 64 characters) and writes it
into the ImagePath as `--service`. From it come the broker service
(`<ServiceName>Broker`), the broker's pipe (`<ServiceName>.broker`), the admin pipe,
the virtual account `NT SERVICE\<ServiceName>` and its SID, and the event log
source. A `--service` with no usable name fails the start rather than answering as
the default install. The application's own names do not follow it: the default
data root folder, the data protection application name and the companion pack's
author stay `BedrockSentry`, since changing them would orphan keys and data.

## 13. Settings & Status

- Settings: `server.properties` editor, backup scheduling, BDS update
  channel selection
- Status: uptime, current BDS version, resource usage, last backup timestamp
- Self-diagnostics/health-check panel: confirm VSS availability, admin
  rights, and free ports before something fails at runtime
- First-run setup wizard: initial admin credentials (Section 3). The EULA
  acceptance and the initial BDS download are the new-server form's and Settings'
  (§ 11), and the `server.properties` walkthrough is the new-server form's
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
- **The service account.** The service runs under its own account and hands every
  firewall, Defender and loopback fix, shadow copy, orphan's end and self-update to
  the privilege broker (§ 17). The row asks the broker its `Status` and names one of
  three cases:
  it answers; it does not, with its reason and "start it or run `install.ps1`
  again"; or the install left it out (`-NoBroker`). Without the broker, under an
  account that is not an administrator, each of those fails with its own
  unrelated-looking error, so the row says why once and the fix buttons elsewhere
  give way to a sentence pointing at it. It is a report and never a fix: a process
  cannot grant itself privileges, and a button implying otherwise would be a lie.
  What the account cannot read is said rather than guessed: Defender's
  administrators-only answer reads Unknown, never "not excluded", and the Status
  page names a restart key it cannot read rather than taking it as clear.
- **The installer drain.** The admin pipe (§ 12.4) is created at start. When another
  process already holds the name the service logs it and this row names it, since
  the next upgrade then stops servers without draining them first.
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

  Rows are grouped under each server's name, with the local-client rows under
  "This machine" last, and each server's own group also sits at the foot of its
  Status tab, under its play-port advice. A fix re-reads only the source its row
  is decided against (the firewall, Defender, the client exemptions, or the
  identity key file) and re-decides that source's rows; the rest keep their
  answers rather than going back to "Checking…".

  A rule the wrapper adds is named for the server and its port and carries the
  description `BedrockSentry server <id>`, so it stays the server's through a
  rename: renaming a server relabels its rules. When a rule named for a server
  opens a port it no longer uses, a strip under the server's controls on every
  tab says so and offers **Update firewall rules**, which moves that rule to the
  current port rather than adding a second beside it, and **Dismiss**, which
  hides that notice until the rules or ports it names change; the firewall row's
  **Apply fix** does the same as Update (`T411`, `T414`). Readiness groups the
  firewall, Defender and loopback rows under each server, the same rows close that
  server's Status tab, and Apply fix rechecks only the row it fixed (`T418`).

  Three states, not two. `Unknown` — the check could not run — is deliberately
  not folded into `Missing`. "Defender is not excluding your world" and "we could
  not ask Defender anything" call for different actions, and reporting the second
  as the first would have people changing a machine nothing had inspected.
  A check that could not run offers no fix at all. Defender answers an account
  that is not an administrator with a one-item list, "N/A: Must be an
  administrator to view exclusions"; that reads as `Unknown`, never as a list
  that excludes nothing. A pending-reboot key the account cannot read is named
  beside the reboot notice rather than taken as clear.

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
  installation like this dies. The Storage table lists the system volume and
  every volume the wrapper writes to (the data root, backups, server packages,
  new servers and each server's folder; a volume mounted in a folder is a row of
  its own). Each row gives the drive type (*SSD*, *HDD*, *Array* for a Storage
  Space, or a dash), and Free and Used with their share of the volume. Free turns
  amber under 15% free and red under 10%, where File Explorer marks a drive low,
  and a line under the table names the volume (`T430`). On a machine with Data
  Deduplication, three columns give the rate and ratio of the saving against the
  data stored, never the volume, the bytes saved, and the last optimisation
  (`T429`); without the role one sentence under the table says so.
- **Clock.** Local time and time zone, because scheduled restarts are written in
  local time (Section 5.3) and a machine set to UTC runs an 05:00 restart at
  midnight — discoverable otherwise only from player complaints.
- **Pending reboot.** Windows will take every server down without asking. Read
  from the two flags that can restart the machine by themselves, component
  servicing's and Windows Update's. The file-rename queue is not read: it cannot
  cause a restart and is non-empty on most healthy machines (`T188`, `D64`).

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

A server's page draws one row above its tabs, whichever tab is showing: the
server's state and process id, the players online against `max-players`, and
Start, Stop and Restart. The Status tab's Players card shows the same count
from the same reading and adds how many of those online are operators, by the
rule the Players tab's Role column uses: the player's `permissions.json` entry,
else `default-player-permission-level`.

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
- The panel's service never runs as LocalSystem. Administrative work is a closed
  list of broker operations, each naming what it acts on by an id the broker
  resolves itself; there is no operation that runs a command or takes a path
  (§ 17)
- The panel writes every time of day on the twelve-hour clock, `2:05 PM`,
  through one helper (`ClockText`) in the invariant culture, so a moment reads
  the same on every page and every machine (`T304`). Two exceptions: an
  `<input type="time">`, whose value HTML defines as 24-hour and which the
  browser shows in its own locale, and the lines BDS prints, which the console
  shows as BDS wrote them

## 17. The privilege broker

Milestone 18.5, `D46`. The wrapper is two Windows services. The main service serves
the panel, runs the servers and takes the backups under an account of its own
(§ 12.1). The broker, `<ServiceName>Broker`, runs as LocalSystem and does the
administrative work the main service cannot, from a closed list of operations. No
operation runs a command, and none takes a path, a port or a script from its
caller: each names what it acts on by an id the broker resolves from the
configuration itself. That absence is what makes the broker a boundary rather than
a second shell for whoever controls the main service.

The broker trusts nothing the main service's account can write. It reads the main
service's configuration, `config\bedrocksentry.json` under the data root named by
its own `--dataroot`, and never writes it; every folder that configuration names
passes the path rules (§ 17.4), and every port must be one the server's own files
name. The feed it updates from is the install policy's, in the install root.

### 17.1 The operations

`BrokerOperation` in `Core/Broker/BrokerContract.cs`, numbered explicitly because
the values cross a pipe between two executables. `BrokerArguments` states the
fields each takes; every one of them is required, and a request that carries a
field its operation does not take is refused as well as one that lacks a field.

| Operation | Fields | What the broker does |
|---|---|---|
| `AllowServerPorts` | instance id, port span, protocol | Adds an inbound firewall rule for a socket the instance's own `server.properties` names and no other server's does; refuses the well-known ports and the administration and database ports in `BrokerPortRules` |
| `ExcludeServerFolder` | instance id | Adds a Defender exclusion for the instance's folder, only one under the data root's instances folder or one the broker granted (`D115`) |
| `ReadDefenderExclusions` | none | Reads Defender's exclusion list, which a non-administrator is shown as a refusal |
| `ExemptLoopback` | package family | A loopback exemption for Minecraft or Minecraft Preview, the two families in `BrokerArguments.LoopbackFamilies` |
| `EnableVss` | none | Sets the Volume Shadow Copy service back to demand start |
| `CreateShadowCopy` | instance id | Snapshots the volume holding the instance's folder and records the shadow id (§ 8.7) |
| `ReleaseShadowCopy` | shadow id | Releases a snapshot, only one the record says the broker took |
| `FindOrphans` | instance id | Lists Bedrock servers running from the instance's folder (`D56`); the main service leaves out the ones it started |
| `TerminateOrphan` | instance id, process id | Ends one, after checking the live process is a Bedrock server running from that folder |
| `GrantFolder` | grant target, and an instance id for a server's folder | `Modify` for the main service's account on the instance's folder, the backups root or the server-packages root, only when the folder is missing or empty (§ 17.7) |
| `StageUpdate` | build identity | Fetches, verifies and unpacks that build (§ 12) |
| `ApplyUpdate` | build identity | Launches the updater for the build staged last |
| `Status` | none | Answers with the broker's build, for the Readiness panel (§ 13.3) |
| `ReadFirewallRules` | none | Reads the inbound firewall rules, which the firewall's cmdlets refuse a service's virtual account |
| `ReadLoopbackExemptions` | none | Reads the Minecraft packages installed for every user and the loopback exemptions; listing every user's packages needs an administrator |
| `RemoveServerRules` | instance id | Removes the inbound rules `AllowServerPorts` named for the instance, matched on the name the broker reads from the configuration; a removal sends it before the configuration forgets the server (`T400`) |
| `RenameServerRules` | instance id | Relabels the instance's rules, found by the description `AllowServerPorts` gives them (`BedrockSentry server <id>`) or by the name, with the name the configuration gives it now; a rename sends it under the old name and again under the new one (`T411`) |
| `UpdateServerRules` | instance id | Moves the instance's rules to the sockets its own `server.properties` names, adding one only where none is left to move and removing a leftover; refused when a socket is another server's or one `BrokerPortRules` refuses (`T411`) |

The main service reaches the broker through the same seams the in-process code
sits behind (`IFirewallService`, `IDefenderService`, `ILoopbackExemptionService`,
`IVssServiceControl`, `IShadowCopyService`, `IOrphanInspector`,
`IWrapperUpdateInstaller`). `--broker pipe` in the main service's ImagePath swaps in
the broker clients; without it the same seams run in the main service's process
with its own account's rights.

### 17.2 The pipe and who may ask

The broker serves the pipe `<ServiceName>.broker`. Its access list gives LocalSystem
full control, Administrators and the main service's service SID read and write only,
so neither of those two can create another instance of the name, and denies
NETWORK outright. The broker creates its eight instances at start, the
first with `FirstPipeInstance`, and reuses them until it stops, so another process
can never hold the name while the broker runs. A broker that cannot create the
first instance stops rather than serve a pipe somebody else made. An instance whose
caller left before its answer is disconnected all the same, or it never accepts
again; and the main service sends one read at a time per question (`SharedRead`),
since a firewall or Defender read holds an instance for a whole PowerShell run and a
few open pages asking at once held every instance (`T412`).

The access list alone is not enough, because every process the main service
starts carries its service SID, a server included. So the broker asks the service
control manager for the main service's process id on every connection and compares
it with the client's (`ServiceProcessCallerCheck`). Anything else is turned away
before a byte is read. The main service checks the other end the same way: the
pipe's server process must be the broker service's (`ServiceProcessServerCheck`),
and it connects at Identification level, so whatever answers on the name can learn
who called and never act as them.

### 17.3 The handshake and the wire

A message is a four-byte little-endian length and that many bytes of UTF-8 JSON, at
most 1 MiB. One connection carries one exchange: each side sends a `BrokerHello`
with its `BrokerContract.Version`, then the main service sends one `BrokerRequest`
and the broker answers with one `BrokerResponse`. The broker answers the hello
before it judges it, so a caller of another version learns why from the broker's
hello rather than from a closed pipe. Versions must be equal, not "at least":
neither side can know what a newer peer's operations mean, and both executables
ship in one version folder, so a mismatch means a half-applied update or a
hand-copied file. The contract version is raised whenever an operation, a request
field or an argument rule changes.

The JSON is read strictly: an unknown member or a duplicate property refuses the
request whole, so a field `BrokerArguments` does not know never reaches the
dispatcher. A caller has five seconds to send its hello and its request; the work
itself has no such deadline. A refusal is an answer with `refused` set and a
sentence saying which rule refused it; nothing ran.

### 17.4 Arguments and folders

Every request meets the same order in `BrokerDispatcher`: the shape of its fields
(`BrokerArguments`), then the instance from the configuration, then the rules on
what that resolves to, then the work. An instance id must be what the wrapper makes,
lower-case letters, digits and single hyphens; a shadow id a GUID; a build identity a
version with an optional pre-release and commit, since it becomes a folder name
under `versions`. An instance whose executable is not a plain `.exe` file name is
refused.

`BrokerPathRules` refuses, for a grant, an exclusion or a snapshot: the operating
system's folders (`D43`), a drive root, Program Files, the install tree and anything
above it, the data root, anything above it and anything in it, and a path through a
junction or symbolic link. The one exception inside the data root is a server folder
under its instances folder, which may be excluded or snapshotted and is refused for
a grant as unneeded. Spellings that
would slip past the comparisons are refused outright: a path not starting with a
drive letter, a stream (a colon after the drive's), a segment ending in a dot or a
space, and an 8.3 short name. The rules are asked of the path as given and again
once it is resolved.

A folder that passes is then held open without following a link (`FolderPin`) and
refused unless the handle's own name is the path asked for. The grant is written
through that handle, so what was checked is what is changed.

### 17.5 The record

`BrokerRecord` is what the broker itself made: the shadow copies it took and has
not released, the folders it granted, and the build it staged last. It is
`record.json` in `broker-state` under the install root, a folder locked to
LocalSystem and Administrators, written beside and moved over so a crash leaves
the previous record. A shadow id enters it only from Windows' answer to
`CreateShadowCopy`, never from a request, so `ReleaseShadowCopy` can never delete a
snapshot somebody else's backup tool took. `ApplyUpdate` launches only the staged
build, and `ExcludeServerFolder` accepts a folder outside the instances folder only
when the record says the broker granted it. An unreadable record reads as empty,
which costs disk space and never a backup.

### 17.6 Each server at low integrity

`bedrock_server.exe`, the throwaway runs included, starts through
`IServerProcessStarter`. On Windows `RestrictedServerStarter` takes the main
service's own token, removes every privilege but `SeChangeNotifyPrivilege`, lowers
it to low integrity (`S-1-16-4096`) and creates the process with
`CreateProcessAsUserW`, with only the console's three pipe ends inherited, which a
restricted copy of its own token lets the service do without
`SeAssignPrimaryTokenPrivilege` (`D126`). No other account is involved.

A low-integrity token writes nothing that does not carry a low label, and reads
nothing labelled no-read-up. The installer creates the instances folder with a low
label (`S:(ML;OICI;NW;;;LW)`) of its own, written before the root's no-read-up
label, that every server folder made in it inherits, and the broker labels a folder
it grants for a server. Each label is written on one object at a time through
`SetKernelObjectSecurity`, the folder and then each folder and file already in it:
one `SetSecurityInfo` write carried down the tree was refused over an empty new
folder and over a server's files (`T419`, `T422`). Setting a label takes
`WRITE_OWNER`, which the service's account does not hold. A server
folder without the label refuses the start with `ServerFolderNotGrantedException`'s
sentence, recorded as a refusal and never counted as a crash. The rest of the data
root is labelled `S:(ML;OICI;NRNW;;;ME)` on every install: the root itself, inherited
by what is made there later, and each folder and file already in it, one at a time
through a handle that refuses a link, so nothing is labelled by name over a tree the
service's account can change. So the certificate and its password, the DataProtection
keys, the credential, the setup token, the database, the backups and the update folder
stay out of a server's reach. Servers can read one another's folders, which the
operator accepted (`D126`).

An AppContainer per server folder (`D124`) came first and took no connection from
another process on its own machine (`T409`), so a player on the server's PC could not
join; only `CheckNetIsolation LoopbackExempt -is` lifted it, and only while it ran. A
token restricted by a list of SIDs (`D121`) got Bedrock as far as "Signed in to
signaling service successfully" and stopped at "Could not connect to Minecraft
services" (`T385` clause 1). A low-integrity token outside an AppContainer signed in
and took a localhost join over NetherNet (2026-10-28). The entry `D124` gave
each server folder for its container SID (`ServerSandbox.SidFor`) refused a
low-integrity token outside an AppContainer everything in the folder, its listing
included, though no SID in the token matched it (`T409`); the installer, the broker's
grant and the service at a server's start now remove it. The server's executable
inherits the folder's low label, so the process runs at low integrity whatever its
token asks. A server starts on the service's own window station and
desktop: a desktop of its own, which `D124` needed, killed a low-integrity server with
0xC0000142 before its first line (`T409`).

The server is less trusted than the service, so what it writes is read as data:

- `SaveManifest` refuses a `save query` path outside `worlds`.
- The backups open each file through `ServerFileReader`, without following a link,
  and refuse a file whose handle names another path or that has a second hard link.
- A restore and the add-on copy refuse a server folder with a link in it
  (`ServerTree`), and the size count skips links.
- Both services turn on Windows' RedirectionGuard at start where Windows has it, so
  no open in either process follows a junction a non-administrator made; where it
  is missing, the reason is logged and the checks above still hold.

A server's profile folders are its own (`D123`). Bedrock creates
`Minecraft Bedrock Server` under the roaming application-data folder and `minecraftpe`
under the temporary folder before it signs in to Minecraft services; inherited from
the service, both were the service account's profile, which the server's token is
kept out of. The
server now starts with `USERPROFILE`, `APPDATA`, `LOCALAPPDATA`, `TEMP` and `TMP`
pointing into `sentry-profile` in its own folder, which the service makes before the
start, refusing a link on the way, and which the folder grant and label cover.

The container's shape is measured on Windows by `T385`.

### 17.7 A folder outside the data root

The data root is the installer's to grant (§ 12.1), and covers every server
created in its instances folder. A server folder, backups folder or server-packages
folder elsewhere needs a grant of its own, and who may give it depends on whether
the folder already holds anything (`D115` (b)). The broker grants only a folder that
is missing, which it creates, or one that is empty: the folder it is asked for comes
from a configuration the main service's account writes, and a folder that already
holds files belongs to something.

Who asks is a switch only an administrator sets (`D120`): `allowPanelFolderGrants` in
`policy.json` in the install root, written by `install.ps1 -AllowPanelFolderGrants`
and cleared by `-DenyPanelFolderGrants`, or edited there by hand. Anything but a
literal `true` leaves it off, which is the default: every such folder is then granted
by the command an administrator runs, and the broker refuses `GrantFolder` from the
service. Wherever the panel takes a folder outside the data root (a new server's
folder, a server added from a folder, a moved server, the backups or server-packages
root), `OutsideFolders` says why the service cannot use it and prints the exact
command, with the installed script's full path and the folder's:

```powershell
& "C:\Program Files\BedrockSentry\current\install.ps1" -GrantFolder "D:\Minecraft\survival"
```

A refused start names `install.ps1 -GrantFolder` on the folder too. With the switch on, the panel writes the
folder into the configuration first, since the broker reads what it grants from
there, asks the broker, and writes the previous value back when the broker refuses;
a folder that already holds files is refused and its command printed. The switch is
not in the data root's configuration because the service's account can write that,
and the switch decides what the service may have granted.

Run elevated against an installed service, it installs nothing. The installed
broker executable applies the same path rules, walks the tree and refuses it
whole when a link or a file with a second name is anywhere in it, since either
would carry the inherited grant outside the folder, then grants the main service's
account and the folder's server SID and adds the folder to the record, after which
it may be excluded from Defender. It also takes a server's folder in the data root's
instances folder, which the broker's grant refuses as already covered, so a folder
made before its server SID existed can be given it, but only where RedirectionGuard
is on: the main service's account can write there while the grant spreads, and
could otherwise plant a junction for it to follow. An install without the broker
has nothing to run it with.
