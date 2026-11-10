# Getting BedrockSentry running on Windows

How to install the wrapper on a Windows Server machine and get to a working control
panel. Nothing here needs the .NET SDK on the server — the release is
self-contained.

> **Read this first.** BedrockSentry is pre-1.0. Every release so far is a
> prerelease on the Build channel and nothing has been released on the Stable
> channel, so run it on a server you can restore from a backup.
>
> This is no longer a walk into the unknown, though: the install path, the
> service, the panel, backups and the Bedrock update path have all been exercised
> on real Windows machines, Windows Server among them, against Bedrock builds from
> 1.26.36.1 through 1.26.61.1, and Preview 1.26.60.28. The [Troubleshooting](#troubleshooting)
> section covers failure modes that have actually been hit rather than imagined
> ones, and there is a short [first-run checklist](#what-to-check-on-your-first-run)
> at the end.

---

## What you need

- **Windows Server 2019 or newer**, or Windows 10/11. Server Core is fine — the
  wrapper never needs a desktop shell.
- **Administrator rights.** Registering a service, creating an event log source,
  and setting ACLs all require elevation.
- **A free TCP port** for the web UI. Default is `8443`.
- **A Bedrock Dedicated Server zip for Windows**, downloaded from Mojang, for
  your first server. The wrapper downloads later builds itself; see
  [Configuring a server instance](#configuring-a-server-instance).
- **Nothing else.** No .NET runtime, no IIS, no prerequisites.

The wrapper reaches out over HTTPS to Mojang for Bedrock updates, to GitHub for
its own, and to `api.ipify.org` for the public address on the status page.
Setting `Sentry:HostDetails:DiscoverPublicAddress` to `false` stops the last.

The wrapper also binds `127.0.0.1:47324` for an internal health check. It is
loopback-only and never reachable from the network, but the port must be free.

---

## Step 1 — Get a release zip

### Option A: from GitHub Releases (normal path)

Every push to `build` or `main` automatically produces a release. Go to the
repository's **Releases** page and download the
`bedrocksentry-<version>_<commit>.zip` asset from the newest one.

Builds from `build` are marked as prereleases; builds from `main` are marked
latest. There is also a rolling `channel-build` / `channel-stable` release
carrying just the manifest — that is what the wrapper polls, not something you
need to download.

While the repository is private you can download by hand as a signed-in user. It
is only the wrapper's *automated* self-update that needs the repo public: an
installed service carries no credential, and a private repository serves nothing
to an anonymous request.

### Option B: build it yourself

For testing a change without pushing it, or if you want a build from a branch CI
does not watch. Needs the **.NET 10 SDK** on the Windows machine doing the
building:

```powershell
git clone https://github.com/ntrixter/BedrockSentry.git
cd BedrockSentry
git checkout build
.\scripts\package-local.ps1
```

That produces `bedrocksentry-0.4.0-local_<sha>.zip` in the repo root, with the
same files CI puts in a release and checked against the same list, so
`install.ps1` treats it like any other. Expect roughly 80 MB and a minute or two.

Copy the zip to the server however you like.

---

## Step 2 — Install

On the server, in an **elevated** PowerShell session. On Windows 10 or 11,
PowerShell runs no scripts at all until it is told to, so allow them for this
window first with
`Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned`; Windows
Server already allows unblocked scripts.

```powershell
# Unblock it if it came from a browser or a network share, or PowerShell
# will refuse to run the scripts inside.
Unblock-File .\bedrocksentry-0.4.0-local_abc1234.zip

# Name the folder after the build. Never reuse one folder across installs —
# see Updating below for why that matters.
Expand-Archive .\bedrocksentry-0.4.0-local_abc1234.zip -DestinationPath .\bs-0.4.0-local_abc1234
cd .\bs-0.4.0-local_abc1234

.\install.ps1 -ZipPath ..\bedrocksentry-0.4.0-local_abc1234.zip
```

Yes, you extract it and then point the installer back at the zip — the scripts
ship *inside* the archive, so that is the only way to get at them the first time.
The installer reads the two helper scripts it uses, `ServiceStop.ps1` and
`ServiceRoots.ps1`, from the zip, so an `install.ps1` copied out on its own works
too.

### What it does

BedrockSentry installs as two services. `BedrockSentry` serves the panel and runs
your servers under its own account, `NT SERVICE\BedrockSentry`. `BedrockSentryBroker`
runs as LocalSystem and does the few things that need an administrator:
firewall rules, Defender exclusions, the loopback exemption, shadow copies, ending
a server left running by an earlier process, and self-update. The broker accepts
those requests from the main service's process only, and only from a fixed list;
[`docs/design.md`](design.md) § 17 has the detail.

1. Checks the account the service will run as. An install whose service runs as
   LocalSystem, as every build before the broker did, is refused here, before
   anything is stopped (see [Updating across the account change](#updating-across-the-account-change));
   so is a `-ServiceAccount` that does not exist or whose password is wrong
2. Secures the install folder (SYSTEM and Administrators may change it, everyone
   may read it, Administrators own it), then unpacks the zip to a staging folder
   inside it and checks it: it must carry
   `version.txt`, `BedrockSentry.Host.exe` and `broker\BedrockSentry.Broker.exe`.
   It also warns if it is not the `install.ps1` from inside that zip
3. Grants `C:\ProgramData\BedrockSentry` to SYSTEM, Administrators and the
   service's account, with inheritance off, by running the new build's broker
   once. The service's account gets Modify on what is inside, never Full Control.
   A data root that does not exist is created with these permissions. An existing
   one that already has them is left as it is; one that does not is changed only
   when it and every folder in it are owned by SYSTEM or Administrators, only they
   could write there, and no junction or link is among them, and is refused
   otherwise. The secret files (the credential, the setup token, the generated
   certificate and its password) keep permissions of their own and are granted
   again on every run. This happens before anything is stopped, so a refusal
   costs no outage
4. Creates the event log sources `BedrockSentry` and `BedrockSentryBroker`
5. On an upgrade, asks the running wrapper to warn players and stop its servers,
   then stops the service, then the broker, and waits for each process to exit
   (see [Updating](#updating))
6. Moves the new build to `C:\Program Files\BedrockSentry\versions\<version>_<commit>\`
   and points the junction `C:\Program Files\BedrockSentry\current` at it
7. On a first install, registers `BedrockSentry` with delayed auto-start. On every
   run, sets its command line to
   `"C:\Program Files\BedrockSentry\current\BedrockSentry.Host.exe" --dataroot "C:\ProgramData\BedrockSentry" --service "BedrockSentry" --broker pipe`
   and its recovery actions: restart after 2, 5 and 10 minutes, with the failure
   count reset after a day. Then sets the account it runs as, cuts its privileges
   to `SeChangeNotifyPrivilege` and `SeCreateGlobalPrivilege`, and gives its token
   its own service SID
8. Registers `BedrockSentryBroker` as LocalSystem with automatic start, running
   `current\broker\BedrockSentry.Broker.exe` with the same data root and service
   name, and the same recovery actions. With `-NoBroker` it registers none, removes
   one an earlier run registered, and leaves `--broker pipe` off the main service's
   command line
9. Writes the settings the switches below ask for into
   `C:\Program Files\BedrockSentry\policy.json`. It writes no settings into the
   data root, since the service's account can change anything there
10. Starts the broker, then the service, and on a first install prints the setup
    link and token
11. With `-OpenFirewall`, adds or updates the firewall rule

The executable path in the service's command line goes through `current`, so it
stays the same across updates. That is intentional: a program path that moved
every update would silently invalidate every firewall rule, AppLocker rule and
AV exclusion keyed to the executable.

### Useful switches

| Switch | Why |
|---|---|
| `-AllowRemoteSetup` | Lets you reach the first-run wizard from another machine. Needed on a headless machine with no local browser — see the warning below. |
| `-OpenFirewall` | Adds an inbound rule named `BedrockSentry Web UI` for the panel's port: 8443 unless `Sentry:Tls:Port` says otherwise. The rule admits only the service's own processes. Re-run with it after changing the port and it moves the rule. |
| `-InstallRoot <path>` | Somewhere other than `C:\Program Files\BedrockSentry`. |
| `-DataRoot <path>` | Somewhere other than `C:\ProgramData\BedrockSentry`. Useful to keep data off the system volume. |
| `-SkipDrain` | On an upgrade, stops the service without first warning players and draining its servers. Worlds are still saved, but players get a few seconds' notice and no backup-on-shutdown runs. |
| `-ServiceName <name>` | Registers the service under a name other than `BedrockSentry`. A second install beside an existing one is not supported: both use the same ports, so the second stops within seconds of starting. |
| `-ServiceAccount <DOMAIN\user>` | Runs the service as a named account instead of its own virtual account, for example to reach a network share. The password is asked for, or passed as `-ServiceAccountPassword`, and checked before anything stops; the account is given the right to log on as a service. LocalSystem, LocalService and NetworkService are refused. To change the account of an existing install, see [Changing the account](#changing-the-account). |
| `-NoBroker` | Installs without the broker. Servers and backups still run; the firewall, Defender and loopback fixes, shadow-copy backups and self-update are unavailable, and the Readiness panel and Settings say why. Removes a broker an earlier run registered. |
| `-GrantFolder <path>` | Gives the service, and a server running there, a folder outside the data root that already holds files — see [Folders outside the data root](#folders-outside-the-data-root). Installs nothing, and takes no `-ZipPath`. |
| `-AllowCustomBackupCommand` | Lets the custom-command backup strategy run — see the section below before you use it. |
| `-DenyCustomBackupCommand` | Turns it back off. |
| `-AllowPanelFolderGrants` | Lets the panel hand the service a new or empty folder outside the data root itself — see [Folders outside the data root](#folders-outside-the-data-root). |
| `-DenyPanelFolderGrants` | Turns it back off, the default. |

**On an upgrade, the roots come from the installed service.** A root you do not
pass is read from the command line of the service named by `-ServiceName`, and
the installer prints each root and where it came from. Passing one the service
does not use moves the install there, with a warning; the old folder is left as
it is. `-ServiceName` is the one value you have to repeat if you changed it.

### Custom backup commands

The Backups tab of each server has a **Run a custom command** checkbox: your own
script, run while the world snapshot is held. **It is switched off on a fresh
install, and the checkbox is visible but disabled until you turn it on.**

That is deliberate. The command runs as the account the service runs as,
`NT SERVICE\BedrockSentry` unless you chose another, and once setup is complete
the panel binds every network interface. That account holds no administrative
privileges, but it can read and change everything in the data root: worlds,
backups, the player database, the certificate and the stored credential. So
without a gate, anybody who can sign in to the panel could run whatever they like
with that reach. Turning it on is a decision, taken at an elevated prompt:

```powershell
.\install.ps1 -ZipPath ..\bedrocksentry-...zip -AllowCustomBackupCommand
```

`-ZipPath` is still required: this is a switch on an ordinary install or upgrade,
not a configure-only mode. To turn it on against an install you already have,
re-run the installer with the same or a newer zip and add the switch — exactly the
way `-AllowRemoteSetup` works.

It writes `"allowCustomBackupCommand": true` into
`C:\Program Files\BedrockSentry\policy.json`, in the install folder rather than the
data folder, because only an administrator can write the install folder. Those file
permissions are the enforcement — you can edit the key by hand instead if you prefer.
Either way it takes effect without a restart, in both directions. Only a literal
`true` turns it on; a missing file, a typo or anything else leaves it off. Older
installers wrote `Sentry:Backups:AllowCustomCommand` into the data folder's config;
that key is no longer read, so re-run with the switch after upgrading if you use it.
The installer secures the install folder wherever it is (SYSTEM and Administrators
may change it, everyone else may only read it), so this holds for an `-InstallRoot`
outside `C:\Program Files` too. Uninstalling removes the install folder and
`policy.json` with it, so a reinstall starts with the command switched off.

`-DenyCustomBackupCommand` turns it back off. A command you had already configured
stays saved and simply stops running, so turning the setting on again needs
nothing retyped.

**The service cannot turn it on itself.** It runs as its own account, which may
read the install folder and not change it, so code running as the service, a
custom command included, cannot rewrite `policy.json`. A default install is not a
remote shell, and turning it into one is a deliberate act by an administrator.

### Folders outside the data root

The installer grants the service the data root, which covers every server created
in its `instances` folder. A server folder, a backups folder or a server-packages
folder anywhere else needs a grant of its own before the service can use it. By
default an administrator hands each one over from an elevated PowerShell. When you
point a server, backups or downloads at such a folder, the panel says why it cannot
use it and prints the exact command to run, with the folder in it:

```powershell
& "C:\Program Files\BedrockSentry\current\install.ps1" -GrantFolder "D:\Minecraft\survival"
```

It installs and restarts nothing. It gives the service's account Modify on the
folder and everything in it, and gives the same to a server running from that
folder, and labels the folder so a low-integrity server can write there (below). A folder that
does not exist yet is created first. It refuses a system folder, a drive root, the
install folder, the data root (a server's folder in its `instances` folder is the
exception, on a Windows build with RedirectionGuard), and a folder with a junction, symbolic link or second hard link anywhere
inside it, since the grant would follow it out of the folder. It needs the broker,
so an install made with `-NoBroker` cannot use it.

**Letting the panel grant folders.** If you would rather pick such folders from the
panel without running a command each time, turn that on once:

```powershell
.\install.ps1 -ZipPath ..\bedrocksentry-...zip -AllowPanelFolderGrants
```

or set `"allowPanelFolderGrants": true` in `C:\Program Files\BedrockSentry\policy.json` by
hand. The panel then asks the broker, which grants a folder it creates or one that is
empty; a folder that already holds files still needs `-GrantFolder`, and the panel
prints that command. Anyone who can sign in to the panel can then give the service a
new or empty folder anywhere on the machine, which is why it is off by default.
`-DenyPanelFolderGrants` turns it back off without a restart; folders already granted
stay granted.

**Name a folder by its real path.** On Windows builds that have RedirectionGuard,
both services refuse to follow a junction that was made without elevation, so a
server folder or data root reached through one made by an ordinary `mklink /J` is
unreachable to them. Name the folder by its real path, or make the junction from an
elevated prompt.

### Changing the account

The installer refuses to change the account of an existing install, and an
uninstall leaves the data root's permissions as they are, so the old account still
holds them and the next install refuses the root rather than walk a tree another
account could have written. To move an install to another account, uninstall it,
then reset the data root to LocalSystem and Administrators from an elevated
PowerShell before installing with the new account (the default data root shown). The
root's own list is replaced first, closed to inheritance, so nothing in it is ever
readable to other users while the rest follows:

```powershell
$root = "C:\ProgramData\BedrockSentry"
takeown /f $root /r /a /d Y
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
foreach ($sid in 'S-1-5-18', 'S-1-5-32-544') {
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        (New-Object Security.Principal.SecurityIdentifier $sid),
        'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
}
$acl.SetOwner((New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'))
Set-Acl -LiteralPath $root -AclObject $acl
icacls "$root\*" /reset /t /c
```

The install that follows grants the root, its server folders and its secret files to
the new account. Folders outside the data root are granted again with
`-GrantFolder`.

**Each server runs at low integrity.** `bedrock_server.exe` starts under a copy of
the service's account with every privilege but folder traversal removed, lowered
to low integrity. It can write only to folders with a low label: the instances
folder, which every server folder in it inherits, and a folder granted with
`-GrantFolder`. It cannot read the panel's certificate, its
password, the sign-in keys, the stored credential, the database or the backups.
Servers can read one another's folders. A server whose folder was never granted
is refused rather than started, and the panel gives the reason.

---

## Step 3 — First-run setup

**Until you complete setup, the wrapper listens on loopback only.** The first-run
wizard creates the administrator account, so anything that can reach it owns the
install — staying on localhost means an attacker needs to already be on the machine.

So run a browser **on the server** and go to:

```
https://localhost:8443/
```

You will get a certificate warning. That is expected: the wrapper generated a
self-signed certificate on first start. Click through it.

Any URL redirects to `/setup` until an account exists. You need:

- **The setup token** — printed by `install.ps1`. If you missed it, it is in
  `C:\ProgramData\BedrockSentry\config\setup-token.txt` (readable only as
  administrator). A warning in the Application event log under source
  `BedrockSentry` names that file; the token itself is not in the log.
- **A username** — 3 to 64 characters
- **A password** — at least 12 characters, typed twice
- **An instance folder** — where each server's files and worlds are kept, one
  folder per server underneath it. It starts as `instances` under the data root;
  put it on the volume with the room. Settings → **Storage** changes it later,
  for new servers only: servers that already exist stay where they are.

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
afterwards by setting `Sentry:AllowRemoteSetup` to `false` in
`C:\ProgramData\BedrockSentry\config\bedrocksentry.json`; the installer has no
switch for that. You still need the token, which is not network-readable.

**Or tunnel over SSH/RDP** and browse from your own machine through the tunnel,
which keeps the loopback restriction intact.

### Then restart the service

The wizard tells you this, but it is easy to skim past. The loopback-only
decision is made when Kestrel starts, so the wrapper does not begin listening on
the network until it restarts (with `-AllowRemoteSetup` it already is):

```powershell
Restart-Service BedrockSentry
```

Now browse to `https://<your-server>:8443/` and sign in.

---

### Optional: an authenticator app

Settings → **Authenticator app** → **Set up** adds a second step to signing in: a
six-digit code from Google Authenticator, Microsoft Authenticator, 1Password or any
app that reads the same QR codes. Scan the code (or type the key), enter the code
the app shows, and save the ten recovery codes it then shows once. Each recovery
code signs in once in place of an app code. Switching it on signs every browser out.

The host's clock and the phone's must agree to within about 30 seconds; Windows'
automatic time keeps them there.

If the phone and the recovery codes are both gone, an administrator on the machine
switches the step off from an elevated PowerShell in the folder holding
`install.ps1`:

```powershell
.\install.ps1 -ClearPanelMfa
```

It stops the service, removes the step from
`C:\ProgramData\BedrockSentry\config\auth.json`, signs every session out and starts
the service again. The password is unchanged. Players get the short notice of a
plain service stop, not the configured countdown.

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

## Step 5 — Add a server

Press **New instance** on the Instances page. With no Bedrock build in
`C:\ProgramData\BedrockSentry\server-packages\` yet, the form first downloads the
current Release build from Mojang and fills in when it arrives.
[Configuring a server instance](#configuring-a-server-instance) has the details.

---

## Using your own certificate

The certificate the wrapper generates names the machine, `localhost`,
`127.0.0.1` and `::1`. A browser on another machine that opens the panel by its
IP address therefore warns twice: the certificate is not trusted, and it does not
name that address. A certificate of your own avoids both. It can come from your
own certificate authority, or be one you make that names every address and name
people use; an IP address has to be an IP address entry in the certificate's
subject alternative names, because browsers do not match an address written as a
DNS name.

Both ways of supplying one are settings in the `Sentry:Tls` section of
`C:\ProgramData\BedrockSentry\config\bedrocksentry.json`, and both take effect
when the service restarts. There is no page for this in the panel.

**From the Windows certificate store.** Import the certificate with its private
key into **Local Computer → Personal** (`certlm.msc`), copy its thumbprint, and
set:

```json
{
  "Sentry": {
    "Tls": {
      "Source": "WindowsStore",
      "Thumbprint": "0123456789ABCDEF0123456789ABCDEF01234567"
    }
  }
}
```

Spaces in the thumbprint, and the invisible mark `certlm.msc` puts in front of a
copied one, are ignored. The service reads the Local Computer store; a
certificate in your own user store is not visible to it. It runs as its own
account, `NT SERVICE\BedrockSentry`, so that account needs read on the
certificate's private key: in `certlm.msc`, right-click the certificate, choose
**All Tasks → Manage Private Keys**, and add `NT SERVICE\BedrockSentry` with
**Read**. No password is written anywhere, which is why this is the way to
prefer.

**From a PFX file.**

```json
{
  "Sentry": {
    "Tls": {
      "Source": "PfxFile",
      "PfxPath": "C:\\ProgramData\\BedrockSentry\\config\\panel.pfx",
      "PfxPassword": "the file's password"
    }
  }
}
```

The password sits in this file in plain text, protected only by the file's
permissions (SYSTEM, Administrators and the service's account). A settings export
never carries it. Keep the PFX under the data root, where the service's account
can read it; a PFX elsewhere has to grant that account read.

Then restart:

```powershell
Restart-Service BedrockSentry
```

Open the panel and check the certificate your browser shows. If the password is
wrong, or no certificate with that thumbprint is found, the service does not
start, and the Application event log says what is wrong. It never falls back to
the generated certificate.

**Or keep the generated certificate and add names to it.** List the extra
addresses and names under `AdditionalSubjectAltNames`; an address is written as
an IP address entry:

```json
{
  "Sentry": {
    "Tls": {
      "AdditionalSubjectAltNames": [ "192.0.2.10", "minecraft.example.com" ]
    }
  }
}
```

At the next start the wrapper sees the certificate does not name them, makes a
new one that does, and writes a warning to the Application event log saying so.
The new certificate replaces the old one, so a browser that was told to trust the
old one has to be told again.

---

## Updating

### While the repo is private

Build or download a newer zip, then **extract it to a new folder and run that
copy of `install.ps1`** — exactly as you did for the first install:

```powershell
Unblock-File .\bedrocksentry-0.4.0-build.99_bbbb222.zip

Expand-Archive .\bedrocksentry-0.4.0-build.99_bbbb222.zip -DestinationPath .\bs-0.4.0-build.99_bbbb222
cd .\bs-0.4.0-build.99_bbbb222

.\install.ps1 -ZipPath ..\bedrocksentry-0.4.0-build.99_bbbb222.zip
```

First the installer asks the running wrapper to warn players and stop its
servers, taking any backup-on-shutdown they are set for. The warning lasts
`Sentry:Update:PlayerWarning`, one minute by default. Then it stops the service,
lays down a new version directory, repoints the junction, and starts it again.
Pressing Ctrl+C during the countdown calls it off: the servers keep running,
players are told, and nothing is installed. Once the countdown has ended and the
servers are stopping it is too late, and the installer says so; start them again
from the panel.
`-SkipDrain` leaves the warning out: servers are still stopped and saved, with a
few seconds' notice and no backup.

Your configuration, certificates and data stay in the data root and are not
replaced. The installer checks the data root's permissions on every run, leaves
them alone when they already grant SYSTEM, Administrators and the service's
account, and grants the secret files again. It writes no settings into the data
root; the switches write `policy.json` in the install folder.

**Do not reuse the folder from the previous install.** `install.ps1` and the zip
are independent arguments: `cd` into last time's folder and point it at a new
archive, and you are running an old installer against a new payload. The payload
is still correct, but the steps that lay it down are the old ones. The installer
compares itself with the `install.ps1` inside the archive and warns if they
differ; it still installs, and extracting to a fresh folder is the fix.

### Once the repo is public

The wrapper updates itself. Settings → **Wrapper updates** shows the running
build, the channel, when it last checked, and any newer build it has seen. Its
controls choose the channel, what happens when a build appears, the hours
between checks (6 by default) and how many minutes players are warned, and take
effect when saved, without a restart:

- **Tell me — I will apply it** (`Notify`, the default): a newer build gets an
  **Install now and restart** button
- **Apply it automatically** (`Automatic`): applied after a scheduled check
- **Do not check at all** (`Disabled`): no scheduled checks; **Check for a
  BedrockSentry update** still asks

An update downloads the build and checks its SHA-256, warns players and stops
every server, then hands over to a helper. The broker does the download and
launches the helper, so the service's own account never writes the install
folder. The helper stops the service and then the broker, switches the junction,
starts both on the new build, and then *proves* it: the new build
has to report healthy **and** report the version that was just installed, within
90 seconds. If it does not, the previous build is restored automatically and the
reason appears on the settings page.

That version check matters more than it looks. If the stop or the switch fails,
the old build is still running and answering perfectly healthily — without
checking which version replied, that would be recorded as a successful update.

After a failed update, three things stop a bad build looping: that build is not
tried again automatically, no automatic update runs for 24 hours, and three
failures in a row pause automatic updates. They hold back automatic updates
only; **Install now and restart** is not held back. While automatic updates are
paused, Settings says so and **Applying** reads *automatically, but paused after
repeated failed updates*. **Resume automatic updates** on Settings turns them back
on and allows three more attempts; an update that succeeds also turns them back
on. A build that already failed stays blocked either way.

The same settings live in `C:\ProgramData\BedrockSentry\config\bedrocksentry.json`,
where an edit also takes effect without a restart:

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

`Channel` is `Stable` or `Build`; `Mode` is `Notify`, `Automatic` or `Disabled`.

Without the broker (`-NoBroker`) the service's account cannot write the install
folder, so the wrapper cannot update itself, and Settings says so. Update by
running a newer `install.ps1` instead.

### Updating across the account change

Builds before the privilege broker ran the service as LocalSystem. Such an install
is not converted in place: `install.ps1` from a broker build refuses it before
anything is stopped and names the way across, which is to uninstall and install
again. Your worlds, backups, player data, settings, sign-in and certificate are in
the data root, which uninstalling leaves alone.

```powershell
# From a fresh folder the new release was extracted into:
.\uninstall.ps1
.\install.ps1 -ZipPath ..\bedrocksentry-<new build>.zip
```

Pass the same `-ServiceName` to both if you changed it, and the same
`-InstallRoot` and `-DataRoot` to `install.ps1`: with the service gone there is
nothing to read them from. The new install grants the data root to the service's account on its way
in. A server folder, backups folder or packages folder outside the data root
needs `-GrantFolder` afterwards (see
[Folders outside the data root](#folders-outside-the-data-root)).

If the install refuses the data root, something in it is owned by an account
other than SYSTEM or Administrators: usually a folder copied in or made by hand
while the old service ran as LocalSystem. Reset the root's owner and permissions
with the block under [Changing the account](#changing-the-account), then run
`install.ps1` again. The refusal changes nothing in the data root.

Self-update does not cross this either. A build that needs a reinstall carries a
higher install epoch in its manifest, and a wrapper that reads it shows, in place
of the install button, that the build cannot be installed from there and to run
`uninstall.ps1` and then `install.ps1` from its release.

---

## Uninstalling

In an **elevated** PowerShell session, from the folder you extracted the
installed release into (`uninstall.ps1` ships beside `install.ps1`). Not the copy
under `C:\Program Files\BedrockSentry\current`: the script deletes that folder,
so a second run finds nothing there. An `uninstall.ps1` copied out on its own
reads its two helper scripts from the release the service runs from.

```powershell
.\uninstall.ps1
```

It first lists what it will remove and what it keeps, and asks; the default
answer is No. Then it stops the service and then the broker, waiting for each
process to exit, deregisters both, removes both event log sources, and deletes
the install directory with every build under `versions\`. A named account keeps
its right to log on as a service, which another service may share. The install and data roots are
read from the service, so only `-ServiceName` needs repeating if you changed it.

| Switch | Why |
|---|---|
| `-RemoveFirewallRule` | Also removes the `BedrockSentry Web UI` rule that `-OpenFirewall` created. |
| `-ServiceName <name>` | The name you installed with, if you changed it. It decides which install is removed. |
| `-InstallRoot <path>`, `-DataRoot <path>` | Only needed once the service is gone, to finish a partial uninstall of an install kept elsewhere. One that disagrees with the service's removes nothing. |
| `-Confirm:$false` | Skips the question, for a script that has already checked what it is removing. |
| `-WhatIf` | Prints the list and removes nothing. |

If a file under the install directory cannot be deleted yet, the script says
BedrockSentry has been **PARTLY** uninstalled, names the error, and exits with
code 1. Run it again and it finishes the job. It exits with 0 when everything
went, with 1 when it refused and removed nothing, and with 2 when the answer
was No.

**It deliberately leaves `C:\ProgramData\BedrockSentry` alone.** Worlds, backups,
player data, certificates and configuration all live there. Delete it by hand if
you genuinely want a clean slate — the script prints the path so you know where
to look. It deletes nothing outside the install directory, so backups,
packages and servers you placed on another volume, and servers you adopted, stay
where they are too.

---

## Troubleshooting

### The service will not start

Check the Application event log for source `BedrockSentry` first. It receives
the wrapper's warnings and errors only. Informational lines are not written
there, including the one the wrapper logs on every start with its content root,
web root, working directory and data root, followed by one listing every
configuration source; run the wrapper in the foreground (below) to read those.

A failure that kills the process — a port already in use, or a certificate that
cannot be read — is written as a single **Error** entry under the same source, and
to `logs\last-failure.log` under the data root. Read either; they carry the same
text, and the file is there for the case where the event source was never created.
Its first line says whether the process was **starting** or **shutting down**, which
are different problems.

If both are empty, the process died before it could write anything. Run it in
the foreground to see every log line and the exception directly. Give it the
data root you installed with:

```powershell
Stop-Service BedrockSentry
& "C:\Program Files\BedrockSentry\current\BedrockSentry.Host.exe" --dataroot "C:\ProgramData\BedrockSentry"
```

It starts any server set to start automatically, as the service would. Press
Ctrl+C to stop it, then `Start-Service BedrockSentry`.

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
`bedrocksentry.json` and restart. If you opened the firewall with
`-OpenFirewall`, re-run the installer with it to move the rule to the new port.

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

Settings → **Wrapper updates** shows the reason, under *The last update did not
succeed.* The helper also writes its own log, which survives the update either
way:

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

# Deletes the link only, the way install.ps1 does, never what it points at.
(Get-Item "C:\Program Files\BedrockSentry\current").Delete()
New-Item -ItemType Junction `
  -Path "C:\Program Files\BedrockSentry\current" `
  -Target "C:\Program Files\BedrockSentry\versions\<known-good>"

Start-Service BedrockSentry
```

The service's command line goes through `current`, so this is all it takes.

---

## What to check on your first run

The install has been exercised elsewhere; it has not been exercised on *your*
machine, and these are the things that most often differ between machines. Worth
confirming deliberately rather than assuming:

1. **Content root resolves correctly** — the startup log line should show the
   install directory, not `C:\Windows\System32`. It is an informational line,
   so read it from a foreground run (see
   [The service will not start](#the-service-will-not-start)).
2. **`appsettings.json` loads** — the line after it lists every configuration
   source.
3. **The certificate generates and Schannel serves it** — a working HTTPS
   handshake, even with a warning.
4. **Data root permissions** — `icacls C:\ProgramData\BedrockSentry` should show
   SYSTEM and Administrators with full control and `NT SERVICE\BedrockSentry`
   (or the account you chose) with read and create on the folder itself and
   Modify on everything inside it, with inheritance disabled.
5. **Auth survives a restart** — sign in, `Restart-Service`, confirm you are
   still signed in. This is the DataProtection test.
6. **The event log source exists** and is receiving entries.
7. **The junction resolves** —
   `(Get-Item "C:\Program Files\BedrockSentry\current").Target` names the
   version directory the installer printed.

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
line to `bedrock_server.exe`'s standard input and read the reply. Backups,
restarts, allowlist edits — all of it.

`scripts/stdin-spike.ps1` exercises that one operation on its own, and it ships
inside the release for that reason. It was written before the supervisor existed,
to prove the mechanism was sound before anything was built on it; it earns its
place now as the fastest way to answer "can this machine talk to `bedrock_server.exe`
at all" when something is wrong, without the wrapper in the way.

The script ships inside the release zip alongside `install.ps1`, so it is
already in whichever folder you extracted in step 2. Unzip a Bedrock server
download somewhere first — say `C:\bds` — then run it from an elevated
PowerShell in the release folder:

```powershell
# 1. Interactive run. Fastest feedback, catches the common problems.
.\stdin-spike.ps1 -ServerPath C:\bds

# 2. The same run as SYSTEM, in session 0, non-interactive — the session the
#    Windows Service launches BDS in.
.\stdin-spike.ps1 -ServerPath C:\bds -AsSystem
```

The second run matches the service's session, not its account: the service
starts each server under a restricted copy of its own account, which the spike
does not reproduce.

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
| `WARN ... looks buffered` | Replies arrive, but slowly. The supervisor copes, but console latency on this machine will be poor. |
| `never saw "Server started"`, then the whole log appears at once at exit | Fully block-buffered. This is the showstopper the spike exists to find. |
| PASS interactively, FAIL under `-AsSystem` | A session-0 problem, not a buffering one — and the one that would otherwise have been found only after install. |

The spike sends `save resume` from a `finally` block, so an aborted or failed
run still leaves the world saving normally. That is the same invariant the
production backup path holds, for the same reason.

---

## Configuring a server instance

The quickest way to add a server is **New instance** on the Instances page: give
it a name, pick a build from the package store, take the suggested port, and
press **Create instance**. It is created without touching a config file or
restarting the service. **+ Add existing** on the same page takes over a Bedrock
server folder you already have instead.

The package store starts empty. The first time, **New instance** and the empty
**Bedrock builds on this machine** list on Settings both show a link to the
[Minecraft EULA](https://www.minecraft.net/en-us/eula) and an **I accept and
download** button, since running a Bedrock server means accepting it. The button
records your acceptance and downloads the current Release build from Mojang; after
that the button is gone, and an empty store fills itself when you open **New
instance** or press **Check now** under **Bedrock update checks**. Either says which
build arrived, or why none did. Both need **Check for Bedrock builds** set to Yes.

Mojang names a Preview zip the way it names a Release one, so the wrapper records
which channel each download came from and tags a Preview build **Preview** in the
builds lists and the build pickers. A zip copied in by hand, or downloaded before
the wrapper kept that record, is tagged only while Mojang still offers it. The
record is `data\server-packages.json` under the data root.

To supply the build yourself instead, download the Bedrock Dedicated Server zip for
Windows from <https://www.minecraft.net/en-us/download/server/bedrock> and copy it,
unchanged, into `C:\ProgramData\BedrockSentry\server-packages\`, or the **Server
packages** folder on Settings → **Storage** if you moved it. The form names the
folder when the download could not be made.

To start from a world you already have, choose **World file** under **World** on
the form and pick a `.mcworld` exported from Minecraft or a ZIP backup this panel
made, up to 8 GB. The file uploads when you pick it, and the form says what it
holds before anything is created; a file with no world in it is refused there.

- A `.mcworld` becomes the new server's world.
- A backup's worlds keep their folder names, and the server's `level-name` is set
  to the world the backed-up server loaded.
- A backup also carries that server's `server.properties`, allowlist and
  permissions. Tick **Also use its configuration** to take them; the new server
  still gets its own name and ports, and the backup's play window is left out.
  Restoring a backup's configuration onto an existing server keeps that server's
  name, ports and play window the same way.

The server stays stopped with auto-start off until its world is in place, then
follows **Start automatically with the service**. Its console says what went in.
An upload you do not use is deleted when you leave the form, or a day later.

Every per-server setting below can also be changed on the server's
**Configuration** tab, without a restart. The config file is the other route,
and the only one for the section-level settings further down apart from `Root`,
which is **New servers** on Settings → **Storage**.

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

To fill it, put a Bedrock server zip in the package store as above, open the
instance's page under **Instances**, and press **Install** on its Status tab. Each
instance gets its own copy, so installing for one never touches another.

Unpacking the zip into the instance folder by hand works too; the wrapper only
cares that `bedrock_server.exe` sits directly inside it.

The Status page then lists every instance with its state, uptime, version,
memory, CPU and last backup.

The key (`survival`) is the instance id: stable, used for the console-log folder,
and never shown to players. `Name` is the display name, and the instance's folder
is named after it. Rename a server on its Configuration tab, which keeps its
folder where it is. Changing `Name` in the file instead points the server at a
new, empty folder named after the new name at the next start.

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
| `Watchdog.MaxConsecutiveFailures` | Consecutive failed starts before BedrockSentry stops retrying and raises an alert. Default 3. `0` removes the limit: a server that cannot start is retried indefinitely. |
| `Watchdog.InitialBackoff` | Wait before the first retry. Default 10 seconds; each later wait is 3 times the last, up to 5 minutes (`BackoffMultiplier`, `MaxBackoff`). |
| `Watchdog.StableAfter` | How long a start must survive before the failure count resets. Default 2 minutes. |
| `RestartSchedule.Mode` | `None`, `EveryNHours`, `EveryNMinutes` or `DailyAt`. The two intervals count from the instance's last start, so a manual restart re-anchors them. |
| `RestartSchedule.IntervalHours`, `IntervalMinutes` | The interval for `EveryNHours` (default 12) and `EveryNMinutes` (default 30). |
| `RestartSchedule.TimeOfDay` | The local time for `DailyAt`. Default `05:00`. |
| `RestartSchedule.Warnings` | When to warn players in chat. Omit for the defaults, 15 minutes, 5 minutes, 1 minute and 10 seconds before; use `[ "00:00:00" ]` to restart without warning anyone. |
| `Retired` | Keeps the instance's player history but never starts it again. **Restore** on the Instances page undoes it. |

> **Do not point `WorkingDirectory` inside a user profile** — `C:\Users\<you>\...`,
> and Downloads or Documents especially. The service runs as its own account and
> does not own that folder, so it needs `install.ps1 -GrantFolder` before a server
> can start there; profile folders are often synced to OneDrive, where files
> become cloud placeholders that are not really on disk; and Downloads is a folder
> people empty without thinking, which for a game server means deleting the world.
> The panel refuses `C:\Users` itself and each account's own folder, and
> BedrockSentry logs a warning for a folder inside one. Leave `WorkingDirectory`
> out unless the world genuinely needs to live on another volume.

Section-level settings sit beside `Servers`:

| Setting | Default | What it does |
| --- | --- | --- |
| `Root` | `%ProgramData%\BedrockSentry\instances` | Where instance folders are created. |
| `ServiceStopWarning` | `00:00:05` | Chat warning before a *service* stop. Deliberately short — Windows is waiting. |
| `ShutdownDrainBudget` | `00:00:45` | How long each server gets to exit before it is killed. |
| `ConsoleLog.MaxFileBytes` | 8 MB | Transcript rotation size. |

Console transcripts are written to
`C:\ProgramData\BedrockSentry\logs\instances\<id>\console.log`, rotated by size
into `console.1.log` to `console.5.log`. Lines the wrapper sent are prefixed `>`,
its own notices `#`, and stderr `!`.

### Two things worth knowing

**A hand edit to `Sentry:Instances` needs a service restart.** The instance list
and each server's settings are read once at start-up. BedrockSentry logs a warning
if you add or remove a server in the file while it runs, rather than appearing to
accept an edit that does nothing. Changes made in the panel, including creating,
adopting, retiring and removing servers, apply at once.

**Raising `ShutdownDrainBudget` or `ServiceStopWarning` too far will refuse to
start.** The service shutdown timeout is their sum plus 15 seconds, and has to
stay below the updater's stop timeout, `Sentry:Update:StopTimeout` (120 seconds)
— otherwise a self-update could kill a server mid-save. The startup error names
the values and says to lower the drain budget or raise the stop timeout. This is
deliberately a failure at start-up rather than a corrupted world during an update.

---

## Add-ons and the companion pack

**The add-on library.** The **Add-ons** page in the sidebar holds every pack you
upload to it — `.mcaddon`, `.mcpack` or `.zip` — under `addon-library\` in the
data root, shared by every server. Each server's own **Add-ons** tab has four
views: **On this server** switches a pack on or off for that server's world,
**Settings** edits the settings of a pack that has them (the gear on its row
opens them), **Load order** orders the packs that are on, and **Issues** says why
a pack that is on does nothing. Bedrock rewrites a world's pack lists as it
stops, so a switch, a move in Load order or a pack's settings changed while the
server runs waits in a queue, listed above the views with a Discard on each, and
is written when the server stops or restarts. Removing a pack's folder still needs
the server stopped.

**The companion pack.** BedrockSentry ships a small behavior pack, *BedrockSentry
companion*, and adds it to the library at every start; the library offers no
Remove for it. When a server is created from a package, the new-server form's
**Switch on the companion pack** checkbox starts ticked, and a server created
with it ticked has the pack on before its first start. For a server you already
have, press **Switch on** on the pack's row in its Add-ons tab.

The pack reports through the server's console, which Bedrock ships switched off.
Switching the pack on also sets `content-log-console-output-enabled=true` in
`server.properties`, and `content-log-level=info` where it reads `warning` or
`error`, since either key the other way hides everything the pack prints. They
apply at the server's next start. Switching the pack off leaves both as they are.

The companion pack is the first entry in the tab's **Settings** view. A badge
there says whether it has reported since the server started, and three options
under **Recorded by BedrockSentry** are saved as you change them:

- **Count deaths**: each player's deaths on this server, on the Players pages.
- **Keep each player's last position**: where a player was when they left, and,
  just before a planned stop, where everyone still online was.
- **Show join and leave positions**: the pack's join and leave lines, with a
  position, shown among arrivals and departures on the console.

Telling a player where they died is the pack's own setting, *Tell a player where
they died*, under **In game** in the same place, and is on unless you change it.
Like any pack setting, a change made while the server runs waits for its next
stop. The pack also adds operator commands, such as `listcord` for every online
player's position, and draws Sync Area outlines in game. It does not relay game chat: the script event that carries chat is only in
Mojang's pre-release API.

---

## What is not built yet

Working today: the service and panel, instance supervision with a live console
and crash watchdog, player history and per-server allowlist and operator editing,
backups as a ZIP archive or folder copy, optionally from a Volume Shadow Copy, or
by custom command, with restore and retention, Bedrock updates with a countdown,
health gate and rollback, and the add-on library. The companion pack is built and
partly verified on Windows, as are the privilege broker with low-integrity servers,
NetherNet defaults with firewall and reachability checks, and the authenticator-app
sign-in. Player faces and creative sister servers are built and not yet verified on
Windows.

Not built: **in-game chat commands** and the **Telegram and Discord bots**.
[`ROADMAP.md`](../ROADMAP.md) has the detail and the order they depend on each
other in.

The Bedrock server binary is never bundled with BedrockSentry and never will be —
Minecraft's usage guidelines require every copy to come from Mojang directly. The
wrapper downloads the first copy and every update from Mojang on this machine, and
a zip you downloaded from Mojang yourself works as well.
