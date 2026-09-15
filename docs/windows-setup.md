# Getting BedrockSentry running on Windows

How to install the wrapper on a Windows Server box and get to a working control
panel. Nothing here needs the .NET SDK on the server — the release is
self-contained.

> **Read this first.** BedrockSentry is pre-1.0. Every release so far is a
> prerelease on the `build` channel and nothing has been tagged stable on `prod`,
> so run it on a server you can restore from a backup.
>
> This is no longer a walk into the unknown, though: the install path, the
> service, the panel, backups and the Bedrock update path have all been exercised
> on a real Windows Server box against Bedrock builds from 1.26.36.1 through
> 1.26.45.1. The [Troubleshooting](#troubleshooting) section covers failure modes
> that have actually been hit rather than imagined ones, and there is a short
> [first-run checklist](#what-to-check-on-your-first-run) at the end.

---

## What you need

- **Windows Server 2019 or newer**, or Windows 10/11. Server Core is fine — the
  wrapper never needs a desktop shell.
- **Administrator rights.** Registering a service, creating an event log source,
  and setting ACLs all require elevation.
- **A free TCP port** for the web UI. Default is `8443`.
- **Nothing else.** No .NET runtime, no IIS, no prerequisites.

The wrapper also binds `127.0.0.1:47324` for an internal health check. It is
loopback-only and never reachable from the network, but the port must be free.

---

## Step 1 — Get a release zip

### Option A: from GitHub Releases (normal path)

Every push to `build` or `prod` automatically produces a release. Go to the
repository's **Releases** page and download the
`bedrocksentry-<version>_<commit>.zip` asset from the newest one.

Builds from `build` are marked as prereleases; builds from `prod` are marked
latest. There is also a rolling `channel-build` / `channel-stable` release
carrying just the manifest — that is what the wrapper polls, not something you
need to download.

While the repository is private you can download by hand as a signed-in user. It
is only the wrapper's *automated* self-update that needs the repo public: an
installed service carries no credential, and a private repository serves nothing
to an anonymous request.

### Option B: build it yourself

For testing a change without pushing it, or if you want a build from a branch CI
does not watch. Needs the **.NET 10 SDK** on the machine doing the building —
which can be Linux or macOS, since a `win-x64` publish cross-builds fine:

```powershell
git clone https://github.com/ntrixter/BedrockSentry.git
cd BedrockSentry
git checkout build
.\scripts\package-local.ps1
```

That produces `bedrocksentry-0.3.0-local_<sha>.zip` in the repo root — byte-for-byte
the same shape CI produces, so `install.ps1` cannot tell the difference. Expect
roughly 120 MB and a minute or two.

Copy the zip to the server however you like.

---

## Step 2 — Install

On the server, in an **elevated** PowerShell session:

```powershell
# Unblock it if it came from a browser or a network share, or PowerShell
# will refuse to run the scripts inside.
Unblock-File .\bedrocksentry-0.3.0-local_abc1234.zip

# Name the folder after the build. Never reuse one folder across installs —
# see Updating below for why that matters.
Expand-Archive .\bedrocksentry-0.3.0-local_abc1234.zip -DestinationPath .\bs-0.3.0-local_abc1234
cd .\bs-0.3.0-local_abc1234

.\install.ps1 -ZipPath ..\bedrocksentry-0.3.0-local_abc1234.zip
```

Yes, you extract it and then point the installer back at the zip — the scripts
ship *inside* the archive, so that is the only way to get at them the first time.

### What it does

1. Unpacks into `C:\Program Files\BedrockSentry\versions\<version>_<commit>\`
2. Creates `C:\Program Files\BedrockSentry\current` as a junction pointing there
3. Creates and locks down `C:\ProgramData\BedrockSentry` (SYSTEM + Administrators only)
4. Creates the `BedrockSentry` event log source
5. Registers the service as **LocalSystem**, delayed auto-start, with its image
   path pointing at `current\BedrockSentry.Host.exe`
6. Sets recovery actions with deliberately long delays
7. Starts the service and prints your setup token

The service's image path points at `current` and never changes, even across
updates. That is intentional: a program path that moved every update would
silently invalidate every firewall rule, AppLocker rule and AV exclusion keyed
to the executable.

### Useful switches

| Switch | Why |
|---|---|
| `-AllowRemoteSetup` | Lets you reach the first-run wizard from another machine. Needed on a headless box with no local browser — see the warning below. |
| `-OpenFirewall` | Adds an inbound rule for TCP 8443. |
| `-InstallRoot <path>` | Somewhere other than `C:\Program Files\BedrockSentry`. |
| `-DataRoot <path>` | Somewhere other than `C:\ProgramData\BedrockSentry`. Useful to keep data off the system volume. |
| `-AllowCustomBackupCommand` | Lets the custom-command backup strategy run — see the section below before you use it. |
| `-DenyCustomBackupCommand` | Turns it back off. |

### Custom backup commands

The Backups tab of each server has a **Custom command** box: your own script, run
while the world snapshot is held. **It is switched off on a fresh install, and the
box is visible but disabled until you turn it on.**

That is deliberate. The command runs as the account the service runs as —
`LocalSystem` today — and once setup is complete the panel binds every network
interface. So without a gate, anybody who can sign in to the panel can run
anything they like on the machine. Turning it on is a decision, taken at an
elevated prompt:

```powershell
.\install.ps1 -ZipPath ..\bedrocksentry-...zip -AllowCustomBackupCommand
```

`-ZipPath` is still required: this is a switch on an ordinary install or upgrade,
not a configure-only mode. To turn it on against an install you already have,
re-run the installer with the same or a newer zip and add the switch — exactly the
way `-AllowRemoteSetup` works.

It writes `Sentry:Backups:AllowCustomCommand` into
`C:\ProgramData\BedrockSentry\config\bedrocksentry.json`, which is readable and
writable only by `SYSTEM` and `Administrators`. Those file permissions are the
enforcement — you can edit the key by hand instead if you prefer. Either way it
takes effect without a restart, in both directions.

`-DenyCustomBackupCommand` turns it back off. A command you had already configured
stays saved and simply stops running, so turning the setting on again needs
nothing retyped.

**One honest limit.** Running as `LocalSystem` the service could rewrite its own
flag, so this is not a boundary against somebody who already has code execution on
the machine. The point is narrower and still worth having: a default install is
not a remote shell, and turning it into one is a deliberate act by an
administrator.

---

## Step 3 — First-run setup

**Until you complete setup, the wrapper listens on loopback only.** The first-run
wizard creates the administrator account, so anything that can reach it owns the
install — staying on localhost means an attacker needs to already be on the box.

So run a browser **on the server** and go to:

```
https://localhost:8443/
```

You will get a certificate warning. That is expected: the wrapper generated a
self-signed certificate on first start. Click through it.

Any URL redirects to `/setup` until an account exists. You need:

- **The setup token** — printed by `install.ps1`. If you missed it, it is in
  `C:\ProgramData\BedrockSentry\config\setup-token.txt` (readable only as
  administrator) and also in the Application event log under source
  `BedrockSentry`.
- **A username**
- **A password** — minimum 12 characters

### If the server has no browser

Server Core has no browser and you cannot reach localhost from elsewhere. Two
options:

**Re-run the installer with `-AllowRemoteSetup`:**

```powershell
.\install.ps1 -ZipPath ..\bedrocksentry-...zip -AllowRemoteSetup -OpenFirewall
```

This writes `Sentry:AllowRemoteSetup` into the config and binds to all
interfaces immediately. Understand what that means: an unauthenticated "claim
the administrator account" page is exposed to your network until you finish the
wizard. Do it promptly, on a trusted network, and turn the setting off
afterwards. You still need the token, which is not network-readable.

**Or tunnel over SSH/RDP** and browse from your own machine through the tunnel,
which keeps the loopback restriction intact.

### Then restart the service

The wizard tells you this, but it is easy to skim past. The loopback-only
decision is made when Kestrel starts, so the wrapper does not begin listening on
the network until it restarts:

```powershell
Restart-Service BedrockSentry
```

Now browse to `https://<your-server>:8443/` and sign in.

---

## Step 4 — Confirm it works

```powershell
# Service running?
Get-Service BedrockSentry

# Listening where you expect? 8443 on all interfaces, 47324 on loopback.
Get-NetTCPConnection -State Listen -LocalPort 8443, 47324

# Liveness, for the updater — returns status, version and buildChannel as JSON.
# It reports Healthy whenever the process is answering, by design: this is the
# probe the self-update polls, and a failing one would roll back a good build.
Invoke-RestMethod http://127.0.0.1:47324/healthz

# Wrapper and server health — the one that CAN report a problem. Degraded names
# any server that needs attention; Unhealthy means the data root is unwritable.
Invoke-RestMethod http://127.0.0.1:47324/readyz

# Anything unhappy in the log?
Get-EventLog -LogName Application -Source BedrockSentry -Newest 20
```

The status page shows the running build identity, and it is also in the footer
of every page — worth glancing at after an update to confirm the new build
actually took.

---

## Updating

### While the repo is private

Build or download a newer zip, then **extract it to a new folder and run that
copy of `install.ps1`** — exactly as you did for the first install:

```powershell
Unblock-File .\bedrocksentry-0.3.0-build.99_bbbb222.zip

Expand-Archive .\bedrocksentry-0.3.0-build.99_bbbb222.zip -DestinationPath .\bs-0.3.0-build.99_bbbb222
cd .\bs-0.3.0-build.99_bbbb222

.\install.ps1 -ZipPath ..\bedrocksentry-0.3.0-build.99_bbbb222.zip
```

It stops the service, lays down a new version directory, repoints the junction,
and starts it again. Your configuration, certificates and data are untouched —
they live under `%ProgramData%` and the installer never writes there beyond
creating it.

**Do not reuse the folder from the previous install**, which is what "re-run
`install.ps1`" used to mean here and is the reason this section now spells the
whole thing out. `install.ps1` and the zip are independent arguments: `cd` into
last time's folder and point it at a new archive, and you are running an old
installer against a new payload. The payload is still correct, but the steps
that lay it down are the old ones.

That is not theoretical. `T81` case 3 hit it: build.60's installer hardcoded the
service display name regardless of `-ServiceName`, `sc.exe` returned 1078, and
the cause had to be worked out from an error code. Since build.78 the installer
**notices and says so** — it reads the `install.ps1` inside the archive, compares
it against the one you are running, and warns if they differ. It still installs;
the warning tells you which two files disagree and that extracting to a fresh
folder is the fix (`T147`, `D54`).

### Once the repo is public

The wrapper updates itself. Settings → *Wrapper updates* shows the channel, the
mode, and any newer build it has seen.

- **Notify** (default) — you get an "Install now and restart" button
- **Automatic** — applied on its own schedule
- **Disabled** — no scheduled checks

An update stops the service, switches the junction, starts the new build, and
then *proves* it: the new build has to report healthy **and** report the version
that was just installed, within 90 seconds. If it does not, the previous build
is restored automatically and the reason appears on the settings page.

That version check matters more than it looks. If the stop or the switch fails,
the old build is still running and answering perfectly healthily — without
checking which version replied, that would be recorded as a successful update.

Three things stop a bad build looping: it is blocked by identity, there is a
24-hour cooldown after any rollback, and three consecutive failures disable
automatic updates entirely. Change the channel or mode in
`C:\ProgramData\BedrockSentry\config\bedrocksentry.json`:

```json
{
  "Sentry": {
    "Update": {
      "Channel": "Build",
      "Mode": "Automatic"
    }
  }
}
```

`Channel` is `Prod` or `Build`. Restart the service after editing.

---

## Uninstalling

```powershell
.\uninstall.ps1
```

Stops the service, deregisters it, removes the event log source, and deletes the
install directory.

**It deliberately leaves `C:\ProgramData\BedrockSentry` alone.** Worlds, backups,
player data, certificates and configuration all live there. Delete it by hand if
you genuinely want a clean slate — the script prints the path so you know where
to look.

---

## Troubleshooting

### The service will not start

Check the Application event log for source `BedrockSentry` first. The wrapper
logs one line on every start with its content root, web root, working directory
and data root — if configuration is not loading, that line usually says why.

A failure that kills the process — a port already in use, or a certificate that
cannot be read — is written as a single **Error** entry under the same source, and
to `logs\last-failure.log` under the data root. Read either; they carry the same
text, and the file is there for the case where the event source was never created.
Its first line says whether the process was **starting** or **shutting down**, which
are different problems.

That file did not always exist. A service that could not bind its health port
used to leave the event log and the log directory both empty, with only a
generic APPCRASH to go on — while this page told you to look in exactly those
two places (`T138`).

If both are still empty, the process died before it could write anything. Run it
in the foreground to see the exception directly:

```powershell
Stop-Service BedrockSentry
& "C:\Program Files\BedrockSentry\current\BedrockSentry.Host.exe"
```

### "A 'Kestrel:Endpoints' configuration section was found"

Working as intended. The wrapper configures its endpoints in code, and endpoints
from configuration are *additive* rather than overriding — so a `Kestrel` section
produces a second listener and a port conflict. Kestrel's own certificate schema
also silently ignores a thumbprint, which would leave you serving a different
certificate than you configured with nothing in the log.

Move certificate settings to the `Sentry:Tls` section and delete the `Kestrel`
section.

### Every page returns HTTP 500, but the service started fine

If the service starts cleanly — certificate generated, setup token written, no
startup errors — and then *every* URL returns a bare 500, suspect the routing
table rather than the page you happened to request.

ASP.NET Core builds its route table lazily on the first request, not at startup.
So a bad endpoint registration produces a service that looks perfectly healthy in
`Get-Service` and fails every request identically. The event log entry names
`EndpointRoutingMiddleware` and `DfaMatcherBuilder` in the stack.

The 500 has no body because the failure happens before the error page can be
routed to either.

### Port already in use

```powershell
Get-NetTCPConnection -State Listen -LocalPort 8443 |
  Select-Object -ExpandProperty OwningProcess |
  ForEach-Object { Get-Process -Id $_ }
```

Change `Sentry:Tls:Port` (or `Sentry:Update:HealthPort`) in
`bedrocksentry.json` and restart.

### Browser refuses to connect, no warning page

The self-signed certificate has no click-through if the browser has HSTS pinned
for that hostname from something else that ran on the same host and port. Try a
different hostname (`https://127.0.0.1:8443/` instead of `https://localhost:8443/`),
or clear HSTS state for the host.

The wrapper itself never sends HSTS while using a self-signed certificate,
specifically so it cannot cause this.

### Signed out after every service restart

Means the DataProtection key ring is not persisting, so cookies minted before
the restart no longer validate. Keys should be at
`C:\ProgramData\BedrockSentry\keys\`. If that directory is empty, check the
event log for a DataProtection warning — it usually means the service could not
write there.

This is worth testing deliberately before you rely on self-update, since updates
restart the service routinely.

### The setup wizard reappears after it was already completed

Should not happen — the first-run state latches once an account exists and an
unreadable credential file is treated as "configuration broken" rather than
"never set up", precisely so a typo cannot reopen it.

If you see it anyway, check that `C:\ProgramData\BedrockSentry\config\auth.json`
exists and parses. Report it — that is a security-relevant bug, not a
configuration mistake.

### An update rolled back and you want to know why

Settings → *Wrapper updates* shows the reason. The helper also writes its own
log, which survives the update either way:

```powershell
Get-Content C:\ProgramData\BedrockSentry\logs\update.log -Tail 50
```

If it says the health endpoint *redirected* rather than answering, that is a
misconfiguration rather than a bad build — the endpoint has ended up behind
authentication or HTTPS redirection, and every update will fail identically
until that is fixed.

### Recovering by hand from a failed update

If both the update and its rollback failed, the install may be pointing at a
broken build. Fix it by repointing the junction:

```powershell
Stop-Service BedrockSentry -Force
Get-ChildItem "C:\Program Files\BedrockSentry\versions"   # pick a known-good one

Remove-Item "C:\Program Files\BedrockSentry\current" -Force
New-Item -ItemType Junction `
  -Path "C:\Program Files\BedrockSentry\current" `
  -Target "C:\Program Files\BedrockSentry\versions\<known-good>"

Start-Service BedrockSentry
```

The service's image path never changes, so this is all it takes.

---

## What to check on your first run

The install has been exercised elsewhere; it has not been exercised on *your*
box, and these are the things that most often differ between machines. Worth
confirming deliberately rather than assuming:

1. **Content root resolves correctly** — the startup log line should show the
   install directory, not `C:\Windows\System32`.
2. **`appsettings.json` loads** — the same line lists every configuration source.
3. **The certificate generates and Schannel serves it** — a working HTTPS
   handshake, even with a warning.
4. **Private key ACLs** — `icacls C:\ProgramData\BedrockSentry` should show only
   SYSTEM and Administrators, with inheritance disabled.
5. **Auth survives a restart** — sign in, `Restart-Service`, confirm you are
   still signed in. This is the DataProtection test.
6. **The event log source exists** and is receiving entries.
7. **The junction behaves** — `Remove-Item` on `current` should delete only the
   link, leaving the version directory and its contents intact.

Self-update is the least-proven part of the project, and deliberately so: it
downloads from GitHub releases, which a private repository will not serve
anonymously, so the drills that would prove it — ship a deliberately broken build
and confirm rollback, kill the helper mid-swap, make the service unstoppable,
fill the disk during extraction — cannot run until this repository is public.
Until they have, prefer installing a newer zip by hand over letting the wrapper
update itself — the way **Updating** above describes, from a freshly extracted
folder.

---

## The stdin spike — a field diagnostic

Everything the wrapper does to a running server is the same operation: write a
line to `bedrock_server.exe`'s standard input and read the reply. Backups, chat
command replies, restarts, allowlist edits — all of it.

`scripts/stdin-spike.ps1` exercises that one operation on its own, and it ships
inside the release for that reason. It was written before the supervisor existed,
to prove the mechanism was sound before anything was built on it; it earns its
place now as the fastest way to answer "can this box talk to `bedrock_server.exe`
at all" when something is wrong, without the wrapper in the way.

The script ships inside the release zip alongside `install.ps1`, so it is
already in whichever folder you extracted in step 2. Unzip your BDS download
somewhere first — say `C:\bds` — then, from an elevated PowerShell in that same
folder:

```powershell
# 1. Interactive run. Fastest feedback, catches the common problems.
.\stdin-spike.ps1 -ServerPath C:\bds

# 2. The one that actually matters: the same run as SYSTEM, in session 0,
#    non-interactive — the conditions the Windows Service will launch BDS under.
.\stdin-spike.ps1 -ServerPath C:\bds -AsSystem
```

It does not need BedrockSentry to be installed, and it never touches the
service — it launches its own copy of the server and stops it again.

Both print a results table and leave a timestamped transcript under
`%ProgramData%\BedrockSentry\logs\`. The script exits non-zero if any check
fails.

**Reading the result.** The interesting column is the timing, not the pass. A
program whose C runtime notices it has no terminal can switch stdout from
line-buffered to 4 KB block-buffered, and its replies then sit in the pipe until
enough text piles up behind them. That is not a hang the supervisor can fix, and
it is the specific thing being measured here:

| Result | What it means |
| --- | --- |
| All PASS, replies in tens of ms | The whole roadmap's core mechanism works. Proceed. |
| `WARN ... looks buffered` | Replies arrive, but slowly. The supervisor copes, but console latency on this box will be poor. |
| `never saw "Server started"`, then the whole log appears at once at exit | Fully block-buffered. This is the showstopper the spike exists to find. |
| PASS interactively, FAIL under `-AsSystem` | A session-0 problem, not a buffering one — and the one that would otherwise have been found only after install. |

The spike sends `save resume` from a `finally` block, so an aborted or failed
run still leaves the world saving normally. That is the same invariant the
production backup path holds, for the same reason.

---

## Configuring a server instance

The quickest way to add a server is **New instance** on the Instances page: give it a name, pick a version from the package store, take the suggested
port, and it is created and running without touching a config file or restarting
the service.

Everything below is the config-file route, which is still supported and is where
the less common settings live.

Add servers under `Sentry.Instances.Servers`:

```json
{
  "Sentry": {
    "Instances": {
      "Servers": {
        "survival": {
          "Name": "Survival",
          "AutoStart": true
        }
      }
    }
  }
}
```

Then restart the service. On start-up BedrockSentry creates
`C:\ProgramData\BedrockSentry\instances\Survival\` for it.

To fill it, drop your Bedrock server zip — exactly as downloaded from Mojang —
into `C:\ProgramData\BedrockSentry\server-packages\`, open the instance's
page under **Instances**, and press **Install** on its Status tab. Each instance gets its own copy, so
installing for one never touches another.

Unpacking the zip into the instance folder by hand works too; the wrapper only
cares that `bedrock_server.exe` sits directly inside it.

The Status page then lists every instance with its state, uptime and process id.

The key (`survival`) is the instance id: stable, used for folder and log names,
and never shown to players. `Name` is the display name and can be changed later.

### Everything else is optional

```json
"survival": {
  "Name": "Survival",
  "AutoStart": true,
  "WorkingDirectory": "E:\\worlds\\survival",
  "Watchdog": {
    "MaxConsecutiveFailures": 3,
    "InitialBackoff": "00:00:10",
    "StableAfter": "00:02:00"
  },
  "RestartSchedule": {
    "Mode": "DailyAt",
    "TimeOfDay": "05:00",
    "Warnings": [ "00:15:00", "00:05:00", "00:01:00", "00:00:10" ]
  }
}
```

| Setting | What it does |
| --- | --- |
| `WorkingDirectory` | Runs the instance from here instead of under the root — for a world on another volume. |

> **Do not point `WorkingDirectory` inside a user profile** — `C:\Users\<you>\...`,
> and Downloads or Documents especially. The service runs as LocalSystem and does
> not own that folder; profile folders are often synced to OneDrive, where files
> become cloud placeholders that are not really on disk; and Downloads is a folder
> people empty without thinking, which for a game server means deleting the world.
> BedrockSentry logs a warning if you do it anyway. Leave `WorkingDirectory` out
> unless the world genuinely needs to live on another volume.

| `Watchdog.MaxConsecutiveFailures` | Consecutive failed starts before BedrockSentry stops retrying and raises an alert. `0` disables the watchdog. |
| `Watchdog.StableAfter` | How long a start must survive before the failure count resets. |
| `RestartSchedule.Mode` | `None`, `EveryNHours`, or `DailyAt`. `EveryNHours` counts from the instance's last start, so a manual restart re-anchors it. |
| `RestartSchedule.Warnings` | When to warn players in chat. Omit for sensible defaults; use `[ "00:00:00" ]` to restart without warning anyone. |
| `Retired` | Keeps the instance's player history but never starts it again. |

Section-level settings sit beside `Servers`:

| Setting | Default | What it does |
| --- | --- | --- |
| `Root` | `%ProgramData%\BedrockSentry\instances` | Where instance folders are created. |
| `ServiceStopWarning` | `00:00:05` | Chat warning before a *service* stop. Deliberately short — Windows is waiting. |
| `ShutdownDrainBudget` | `00:00:45` | How long each server gets to exit before it is killed. |
| `ConsoleLog.MaxFileBytes` | 8 MB | Transcript rotation size. |

Console transcripts are written to
`C:\ProgramData\BedrockSentry\logs\instances\<id>\console.log`, rotated by size.
Lines the wrapper sent are prefixed `>`, its own notices `#`, and stderr `!`.

### Two things worth knowing

**Adding or removing an instance needs a service restart.** The rest of the file
reloads live, but the instance list is read once at start-up. BedrockSentry logs
a warning if you change it while running, rather than appearing to accept an edit
that does nothing.

**Raising `ShutdownDrainBudget` too far will refuse to start.** The service
shutdown timeout is derived from it, and has to stay below the updater's stop
timeout — otherwise a self-update could kill a server mid-save. The startup error
names both values and which one to change. This is deliberately a failure at
start-up rather than a corrupted world during an update.

---

## What is not built yet

Working today: the service and panel, instance supervision with a live console
and crash watchdog, player history and per-server allowlist and operator editing,
backups by ZIP, Volume Shadow Copy or custom command with restore and retention,
and Bedrock updates with a countdown, health gate and rollback.

Not built: **in-game chat commands**, the **companion behavior pack** that would
give the wrapper visibility the console cannot provide, the **add-on manager**,
and the **Telegram and Discord bots**. [`ROADMAP.md`](../ROADMAP.md) has the detail
and the order they depend on each other in.

The Bedrock server binary is never bundled with BedrockSentry and never will be —
Minecraft's usage guidelines require every copy to come from Mojang directly. The
wrapper downloads it from Mojang on this machine instead.
