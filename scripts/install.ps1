<#
.SYNOPSIS
    Installs or upgrades BedrockSentry as a Windows Service.

.DESCRIPTION
    Unpacks a published BedrockSentry release into a versioned directory, points a
    junction named 'current' at it, and registers the service against that junction.

    The versioned layout exists from the very first install, before anything uses
    it. Milestone 3.5's self-update works by pointing the junction at a new version
    directory — an operation that is close to atomic and trivially reversible,
    unlike copying files over a running install. Laying the files down flat here
    would mean 3.5 opens with a migration of already-deployed machines, which is a
    poor first exercise for code whose entire job is not to break installations.

    Keeping the service's ImagePath fixed at the junction also matters on its own:
    a program path that changes with every update silently invalidates every
    Windows Firewall rule, AppLocker rule and antivirus exclusion keyed to the
    executable.

    No .NET runtime is required on this machine. The release is self-contained.

.PARAMETER ZipPath
    Path to the BedrockSentry release zip.

.PARAMETER SkipDrain
    Stops the service without first asking the running wrapper to warn players,
    drain its servers and take any backup-on-shutdown. Worlds are still saved -
    the wrapper's own shutdown runs either way - but players get a few seconds'
    notice instead of the configured countdown.

.PARAMETER InstallRoot
    Where binaries live. Replaced wholesale by updates; keep nothing here.

.PARAMETER DataRoot
    Where configuration, certificates, keys, backups and player data live. Never
    touched by an update or by uninstall.

.PARAMETER AllowRemoteSetup
    Lets the first-run wizard be reached from the network instead of loopback only.
    Needed on headless Server Core boxes with no local browser; understand that it
    exposes an unauthenticated "claim the administrator account" page until setup
    is completed.

.PARAMETER AllowCustomBackupCommand
    Lets the custom-command backup strategy run on this install. A command typed
    into a server's Backups tab runs as the account this service runs as, and the
    panel binds every interface once setup is done - so anyone who can sign in to
    the panel can use it. It is off on a fresh install and stays off until this
    switch is used.

    -ZipPath is still required. To turn it on against an install that already
    exists, re-run this script with the same or a newer zip and add the switch,
    the way -AllowRemoteSetup works.

    Honest limit: running as LocalSystem the service could rewrite its own flag,
    so this is not a boundary against somebody who already has code execution on
    the machine. The point is that a default install is not a remote shell.

.PARAMETER DenyCustomBackupCommand
    Turns it back off. Takes effect without a restart.

.EXAMPLE
    .\install.ps1 -ZipPath .\bedrocksentry-0.3.0-build.57.zip
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ZipPath,

    [string] $InstallRoot = "$env:ProgramFiles\BedrockSentry",

    [string] $DataRoot = "$env:ProgramData\BedrockSentry",

    [string] $ServiceName = 'BedrockSentry',

    [switch] $AllowRemoteSetup,

    [switch] $OpenFirewall,

    # Skips asking the running wrapper to warn players and drain its servers
    # before the service is stopped. The stop still goes through the wrapper's
    # own shutdown, so worlds are saved either way - this only gives up the
    # player countdown and the backup-on-shutdown.
    [switch] $SkipDrain,

    # D45. Lets the custom-command backup strategy run. A command typed into the
    # Backups tab runs as the account this service runs as, and the panel is
    # reachable from the network once setup is done, so it is off unless an
    # administrator turns it on from an elevated prompt - which is what running
    # this script is. -ZipPath is still required: this is a switch on an ordinary
    # install or upgrade, not a configure-only mode.
    [switch] $AllowCustomBackupCommand,

    # And the way back. A switch that can only turn a thing on is a trap;
    # -AllowRemoteSetup having the same wart is not a precedent worth copying.
    [switch] $DenyCustomBackupCommand
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($AllowCustomBackupCommand -and $DenyCustomBackupCommand) {
    throw 'Pass -AllowCustomBackupCommand or -DenyCustomBackupCommand, not both.'
}

function Assert-LastExitCode {
    param([string] $What)

    if ($LASTEXITCODE -ne 0) {
        throw "$What failed with exit code $LASTEXITCODE."
    }
}

function Assert-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)

    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this from an elevated PowerShell session. Registering a service, creating an event log source and setting ACLs all require administrator rights.'
    }
}

<#
.SYNOPSIS
    Reads a property that may not be there.

.DESCRIPTION
    Set-StrictMode -Version Latest is on, and under it referencing a property a
    PSCustomObject does not have is an error rather than $null. Both callers here
    read JSON from somewhere else - a config file an operator hand-edits, and a
    response from a wrapper that may be an older version - so "not present" is an
    ordinary case, not a fault.
#>
function Get-Property {
    param($Object, [string] $Name)

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]

    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

<#
.SYNOPSIS
    The port the wrapper will actually serve its panel on.

.DESCRIPTION
    Read rather than assumed. Both the setup link and the firewall rule used to say
    8443 whatever the configuration held (T114), so on any non-default port the
    printed link was dead and -OpenFirewall opened a port nothing was listening on
    while leaving the real one closed. On a machine with a second install the link
    was worse than dead: it pointed at the other wrapper's setup page.

    8443 is still the answer when nothing says otherwise, because it is what the
    wrapper itself falls back to.
#>
function Get-TlsPort {
    param([string] $DataRoot)

    $configPath = Join-Path $DataRoot 'config\bedrocksentry.json'

    if (-not (Test-Path -LiteralPath $configPath)) {
        return 8443
    }

    try {
        $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
        $configured = Get-Property (Get-Property (Get-Property $config 'Sentry') 'Tls') 'Port'

        if ($null -ne $configured) {
            return [int] $configured
        }
    } catch {
        Write-Host "Could not read the panel port from $configPath ($($_.Exception.Message)); assuming 8443."
    }

    return 8443
}

<#
.SYNOPSIS
    Asks a running wrapper to warn players, drain its servers and take any
    backup-on-shutdown before we stop the service.

.DESCRIPTION
    Stop-Service on its own is not ungraceful - the wrapper's own shutdown runs,
    so every bedrock_server.exe gets a real 'stop' and a chance to save. But it
    is the *service stop* path, which by design gives players about five seconds
    and takes no backup, because there the service control manager is waiting on
    it (docs/design.md section 5.2).

    Nothing is waiting on us. So we ask first, over the wrapper's loopback-only
    health listener, and only then stop the service. By that point every server
    is already down and the short budget has nothing left to hurry.

    Best effort throughout. A wrapper that is not running, is older than this
    endpoint, or has moved its health port answers nothing useful - and none of
    those is a reason to refuse to install. The stop that follows still saves
    worlds.
#>
function Invoke-GracefulDrain {
    param(
        [string] $DataRoot,
        [string] $ServiceName
    )

    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

    if (-not $service -or $service.Status -ne 'Running') {
        return
    }

    # The port is configurable, so read it rather than assuming. The default
    # matches UpdateOptions.HealthPort.
    $port = 47324
    $warningSeconds = 60
    $configPath = Join-Path $DataRoot 'config\bedrocksentry.json'

    if (Test-Path -LiteralPath $configPath) {
        try {
            $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
            $update = Get-Property (Get-Property $config 'Sentry') 'Update'

            if ($null -ne $update) {
                $configuredPort = Get-Property $update 'HealthPort'
                if ($null -ne $configuredPort) { $port = [int] $configuredPort }

                $configuredWarning = Get-Property $update 'PlayerWarning'
                if ($null -ne $configuredWarning) {
                    $warningSeconds = [int] ([TimeSpan]::Parse($configuredWarning)).TotalSeconds
                }
            }
        } catch {
            Write-Host "Could not read $configPath ($($_.Exception.Message)); assuming defaults."
        }
    }

    if ($warningSeconds -gt 0) {
        Write-Host "Asking BedrockSentry to warn players and drain its servers ($warningSeconds second warning)..."
        Write-Host 'This is the graceful half of the upgrade. Press Ctrl+C to abandon it; nothing has changed yet.'
    } else {
        Write-Host 'Asking BedrockSentry to drain its servers...'
    }

    # The countdown plus the drain budget plus room for a backup-on-shutdown of a
    # large world. Generous rather than tight: timing out here would stop the
    # service underneath a backup that was still running.
    $timeout = $warningSeconds + 600

    try {
        # -MaximumRedirection 0 is load-bearing (T111). A wrapper that does not serve
        # this endpoint never answers 404: authentication catches the unrecognised
        # path and redirects to /account/login before routing can 404 it. Followed -
        # which is the default, and which turns the POST into a GET - that fetches the
        # login page, returns 200 and a block of HTML, and reads exactly like a
        # successful drain that found nothing running. An old wrapper with servers up
        # would then be stopped with no warning and no backup, under a message saying
        # there was nothing to drain.
        $response = Invoke-RestMethod `
            -Method Post `
            -Uri "http://127.0.0.1:$port/prepare-shutdown" `
            -TimeoutSec $timeout `
            -MaximumRedirection 0 `
            -ErrorAction Stop

        # Belt and braces for the same failure: a drain result is a JSON object, and
        # Invoke-RestMethod only hands back a string when what came off the wire was
        # not JSON at all. Treat that as "no drain available" rather than as a drain
        # that did nothing.
        if ($response -is [string]) {
            throw 'the endpoint did not return a drain result. This wrapper is probably older than /prepare-shutdown.'
        }

        # Filtered, not just array-wrapped: @($null) in PowerShell is an array of
        # one null, not an empty one, so a response missing these fields would
        # otherwise report "Drained: ." and claim something had happened.
        $stopped = @((Get-Property $response 'stopped') | Where-Object { $null -ne $_ })
        $failed = @((Get-Property $response 'failed') | Where-Object { $null -ne $_ })

        if ($stopped.Count -gt 0) {
            Write-Host "Drained: $($stopped -join ', ')."
        } else {
            Write-Host 'Nothing was running.'
        }

        if ($failed.Count -gt 0) {
            Write-Warning "These did not stop cleanly and will be stopped with the service: $($failed -join ', ')."
        }
    } catch {
        # Everything lands here: a wrapper older than this endpoint (which redirects
        # to its login page rather than answering 404 - see the guards above), a
        # health port nothing is listening on, and a wrapper that simply did not
        # reply. All fall through to the ordinary service stop, which still saves
        # worlds. What matters is that the operator is told, rather than reading a
        # line that says nothing needed draining.
        Write-Host "No graceful drain available ($($_.Exception.Message)). Falling back to the service stop, which still saves worlds but gives players only a few seconds."
    }
}


Assert-Elevated

if (-not (Test-Path -LiteralPath $ZipPath)) {
    throw "Release archive not found: $ZipPath"
}

# ---------------------------------------------------------------------------
# Is this script the one that shipped in the archive it is installing?
#
# The two are independent arguments: you run some install.ps1, and you point it
# at some zip. Nothing has ever made them agree. The documented update path used
# to be "re-run install.ps1" against a newer zip, which - read the obvious way,
# from the folder you extracted last time - runs an old installer against a new
# payload. `T81` case 3 is what that costs: build.60's installer hardcoded the
# service DisplayName, sc.exe returned 1078, and the failure had to be traced
# from an error code rather than being named.
#
# Compared by content rather than by version, because install.ps1 carries no
# version to compare. Stamping one at release time would mean CI rewriting a
# committed file, which is real machinery for a weaker answer: a hash also
# catches a locally edited installer, which a version stamp cannot.
#
# Read out of the zip rather than extracted, and this is load-bearing rather
# than tidiness. Extraction happens further down into a staging directory that
# is swept (`.staging-*`, below) and then *moved* into the version directory -
# so a check that extracted first would be reading a file the install is about
# to delete or relocate.
#
# It warns and carries on, never refuses (`D54`, and `D50` before it). The admin
# owns the machine, an edited installer is sometimes exactly right - `T81` case 3
# is only runnable against a patched one - and a stale installer still installs a
# correct payload most of the time.
#
# Best effort throughout: this runs before anything is written to disk, and not
# being able to read the archive is not a reason to refuse an install that would
# otherwise work.
# ---------------------------------------------------------------------------

if ([string]::IsNullOrEmpty($PSCommandPath)) {
    # Piped or dot-sourced from memory, so there is no file to compare. Silent:
    # somebody doing that knows what they are doing, and a warning they cannot
    # act on is noise.
}
else {
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem

        $shippedBytes = $null
        $archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ZipPath).Path)

        try {
            # Ordinal by design, with a case-insensitive sweep behind it: the
            # release workflow writes 'install.ps1' at the root, but a
            # hand-assembled archive is exactly the case worth catching.
            $entry = $archive.GetEntry('install.ps1')

            if (-not $entry) {
                $entry = $archive.Entries |
                    Where-Object { $_.FullName -ieq 'install.ps1' } |
                    Select-Object -First 1
            }

            if ($entry) {
                $reader = $entry.Open()

                try {
                    $memory = New-Object System.IO.MemoryStream
                    $reader.CopyTo($memory)
                    $shippedBytes = $memory.ToArray()
                    $memory.Dispose()
                }
                finally {
                    $reader.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }

        if ($shippedBytes) {
            # SHA256.Create() rather than [SHA256]::HashData(): the latter is
            # .NET 5+, and this script has to run under Windows PowerShell 5.1.
            $sha = [System.Security.Cryptography.SHA256]::Create()

            try {
                $runningHash = [BitConverter]::ToString($sha.ComputeHash([System.IO.File]::ReadAllBytes($PSCommandPath)))
                $shippedHash = [BitConverter]::ToString($sha.ComputeHash($shippedBytes))
            }
            finally {
                $sha.Dispose()
            }

            if ($runningHash -ne $shippedHash) {
                Write-Warning @"
This is not the installer that shipped in that archive.

  Running: $PSCommandPath
  Archive: $((Resolve-Path -LiteralPath $ZipPath).Path)

Installing anyway. The payload will be correct either way, but the steps that
lay it down are this script's, and they are older or newer than the build they
are installing - which has produced at least one failure that looked like
something else entirely.

To use the archive's own installer, extract this zip to a NEW folder and run
install.ps1 from there. Do not reuse the folder from a previous install.
"@
            }
        }
    }
    catch {
        Write-Verbose "Could not compare this installer against the archive: $($_.Exception.Message)"
    }
}

# ---------------------------------------------------------------------------
# A second install beside an existing one is not a supported configuration
# (`D49`, `D50`). It is warned about and allowed, never blocked: the admin owns
# the box, and there are legitimate reasons to want one — an isolated rig for
# testing destructive scripts is how `T129` was run safely. But it will not work
# out of the box, and saying so here is the whole point of `T137`, where the
# installer reported success over a service that was already dying.
#
# Warned here rather than at the service-registration step because this is the
# last moment before anything is written to disk.
#
# Detection is by image path, not by name: the service name is the one thing a
# second install is expected to change, so matching on it would find nothing.
# Best effort throughout — a warning that cannot enumerate services is not a
# reason to refuse to install.
# ---------------------------------------------------------------------------

try {
    $others = @(
        Get-CimInstance Win32_Service -ErrorAction Stop |
            Where-Object {
                $_.Name -ne $ServiceName -and
                $_.PathName -and
                $_.PathName -match 'BedrockSentry\.Host\.exe'
            })
} catch {
    $others = @()
}

if ($others.Count -gt 0) {
    $names = ($others | ForEach-Object { $_.Name }) -join ', '

    Write-Warning @"
This box already has BedrockSentry installed as: $names
Installing a second copy as '$ServiceName' is NOT SUPPORTED and NOT TESTED.

It will almost certainly fail to start. Both copies default to the same health
port (47324) and the same TLS port, and the installer has no switch for either,
so the second service binds a port the first already holds and stops within
seconds of being started.

To make it work you would have to set Sentry:Update:HealthPort and
Sentry:Tls:Port in the new install's own bedrocksentry.json before starting it,
which is a hand-edit this installer will not do for you. The firewall rule is
shared between installs too, so -RemoveFirewallRule on either one removes it
for both.

Supporting this properly may be looked at later. Today you are on your own with
it, and the safe alternative is to stop the existing service first.

Continuing anyway.
"@
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

# ---------------------------------------------------------------------------
# Unpack to a staging directory first so a half-extracted archive never becomes
# a version directory that looks complete.
# ---------------------------------------------------------------------------

$versionsRoot = Join-Path $InstallRoot 'versions'
$currentLink = Join-Path $InstallRoot 'current'
$staging = Join-Path $versionsRoot ('.staging-' + [Guid]::NewGuid().ToString('N'))

New-Item -ItemType Directory -Path $versionsRoot -Force | Out-Null

# Sweep debris from earlier runs before adding to it: staging directories from
# installs that failed part-way, and replaced-version copies whose removal could not
# finish at the time. Best effort throughout — this is housekeeping, and something
# still holding a file is a reason to leave it for next time, never a reason to
# refuse to install.
foreach ($pattern in '.staging-*', '*.replacing-*') {
    Get-ChildItem -LiteralPath $versionsRoot -Directory -Filter $pattern -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
}

New-Item -ItemType Directory -Path $staging -Force | Out-Null

# A failed install used to leave its staging directory behind — a full extracted
# copy of the release, tens of megabytes, every time (`T107`). Defined here rather
# than earlier because nothing before this point has created one to clean up.
# `break` rethrows, so a failure still fails.
trap {
    if ($staging -and (Test-Path -LiteralPath $staging)) {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host 'Removed the staging directory left behind by the failed install.'
    }

    break
}

Write-Host "Extracting $ZipPath..."
[System.IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path $ZipPath).Path, $staging)

$versionFile = Join-Path $staging 'version.txt'

if (-not (Test-Path -LiteralPath $versionFile)) {
    throw 'The archive has no version.txt. It was not produced by the BedrockSentry release workflow.'
}

# The composite identity: <version>+<short sha>. Unique per build rather than per
# commit, so re-running CI on the same commit gets its own directory instead of
# colliding with the one already installed.
# Read, then test, then trim. Get-Content -Raw on a zero-byte file returns
# $null, so trimming first threw InvokeMethodOnNull *before* the guard written
# to catch exactly that case could run (`T157` clause 5).
$identity = Get-Content -LiteralPath $versionFile -Raw

if ([string]::IsNullOrWhiteSpace($identity)) {
    throw 'version.txt is empty.'
}

$identity = $identity.Trim()

# '+' is legal in a Windows path but confuses enough tooling to be worth avoiding.
$versionDirName = $identity.Replace('+', '_')
$versionDir = Join-Path $versionsRoot $versionDirName

# Checked here, while the staging directory is still the only thing that exists.
# Every check that can call the install off belongs before the service is stopped —
# the rule docs/design.md section 12.3 states for the wrapper's own update, and `T78`
# tests — because a bad archive should cost neither an outage nor the install it
# was going to replace.
$stagedHostExe = Join-Path $staging 'BedrockSentry.Host.exe'

if (-not (Test-Path -LiteralPath $stagedHostExe)) {
    throw "BedrockSentry.Host.exe is missing from the archive (expected at $stagedHostExe)."
}

# ---------------------------------------------------------------------------
# Data root. The service secures this itself on startup, but doing it here means
# the directory is never briefly world-readable on a fresh install.
# ---------------------------------------------------------------------------

New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null

$acl = Get-Acl -LiteralPath $DataRoot
$acl.SetAccessRuleProtection($true, $false)

foreach ($rule in @($acl.Access)) {
    $acl.RemoveAccessRule($rule) | Out-Null
}

foreach ($sidType in @('LocalSystemSid', 'BuiltinAdministratorsSid')) {
    $sid = New-Object Security.Principal.SecurityIdentifier([Security.Principal.WellKnownSidType]::$sidType, $null)
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        $sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
}

Set-Acl -LiteralPath $DataRoot -AclObject $acl
Write-Host "Data root secured: $DataRoot"

# ---------------------------------------------------------------------------
# Event log source. Must be created while elevated — a service that logs to a
# source that does not exist fails silently, and on Server Core the event log is
# the channel most likely to actually reach an operator.
# ---------------------------------------------------------------------------

if (-not [System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
    [System.Diagnostics.EventLog]::CreateEventSource($ServiceName, 'Application')
    Write-Host "Created event log source '$ServiceName'."
}

# ---------------------------------------------------------------------------
# Service registration.
# ---------------------------------------------------------------------------

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if ($existing -and $existing.Status -ne 'Stopped') {
    # Before the stop, not after: once Stop-Service is issued the service
    # control manager is waiting, and the wrapper's shutdown path
    # deliberately hurries to fit inside that.
    if (-not $SkipDrain) {
        Invoke-GracefulDrain -DataRoot $DataRoot -ServiceName $ServiceName
    }

    # Captured before the stop, because once it is stopped there is nothing left to
    # ask.
    $servicePid = (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue).ProcessId

    Write-Host "Stopping $ServiceName..."
    Stop-Service -Name $ServiceName -Force
    $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(120))

    # "Stopped" is the service control manager's opinion, and it arrives before the
    # process has necessarily exited. Windows keeps a process's mapped images until
    # it really goes, so a swap that starts on the SCM's word finds the executable
    # and its DLLs still locked and deletes only what is not mapped. Waiting for the
    # process itself is the difference between a clean replacement and a half-deleted
    # directory.
    if ($servicePid) {
        $serviceProcess = Get-Process -Id $servicePid -ErrorAction SilentlyContinue

        if ($serviceProcess -and -not $serviceProcess.WaitForExit(30000)) {
            Write-Warning "$ServiceName reports stopped but process $servicePid is still running; files it holds may not be replaceable."
        }
    }
}

# ---------------------------------------------------------------------------
# The swap — and the first thing in this script that changes the installation.
#
# It sits here, after the drain and the stop, rather than before them. Everything
# above this line is reversible: an extracted staging directory nobody is pointed
# at, which the trap removes. That is what makes the drain's "nothing has changed
# yet" true rather than merely reassuring — Ctrl+C during the countdown now leaves
# the machine exactly as it was, where before it left `current` already aimed at a
# build the operator had declined to install (`T108`).
#
# It also has to be here for the delete below to be safe at all. Removing the
# version directory while the service was still running could not finish: Windows
# refuses to delete the DLLs the process has mapped and deletes everything else,
# so a repair install gutted the live installation — no web assets, no updater —
# while the health endpoint went on reporting Healthy (`T109`).
# ---------------------------------------------------------------------------

$replaced = $null

if (Test-Path -LiteralPath $versionDir) {
    # Renamed aside rather than deleted outright. Deleting first and trusting the
    # move to succeed is how a failed install loses the version it was replacing.
    # Same shape as the restore path's `worlds.restoring-*`.
    Write-Host "Version $identity is already present; replacing it."
    $replaced = "$versionDir.replacing-" + [Guid]::NewGuid().ToString('N')
    Move-Item -LiteralPath $versionDir -Destination $replaced
}

try {
    Move-Item -LiteralPath $staging -Destination $versionDir

    if (Test-Path -LiteralPath $currentLink) {
        # Deletes the link, not what it points at.
        (Get-Item -LiteralPath $currentLink).Delete()
    }

    # A junction rather than a symlink: junctions need no
    # SeCreateSymbolicLinkPrivilege, so this works without developer mode or extra
    # policy.
    New-Item -ItemType Junction -Path $currentLink -Target $versionDir | Out-Null
    Write-Host "current -> $versionDirName"
}
catch {
    if ($replaced) {
        if (Test-Path -LiteralPath $versionDir) {
            Remove-Item -LiteralPath $versionDir -Recurse -Force -ErrorAction SilentlyContinue
        }

        Move-Item -LiteralPath $replaced -Destination $versionDir

        if (-not (Test-Path -LiteralPath $currentLink)) {
            New-Item -ItemType Junction -Path $currentLink -Target $versionDir | Out-Null
        }

        Write-Warning "The swap failed. $identity has been put back and 'current' still points at it."
    }

    throw
}

if ($replaced -and (Test-Path -LiteralPath $replaced)) {
    # Best effort, deliberately. This is a discarded copy of a version that has
    # already been replaced — the new one is in place and `current` points at it, so
    # the install has succeeded whatever happens here. Letting a locked file in a
    # directory nothing reads abort the install at this point would stop the service
    # from ever being started again, which is precisely what it did the first time
    # this ran.
    Remove-Item -LiteralPath $replaced -Recurse -Force -ErrorAction SilentlyContinue

    if (Test-Path -LiteralPath $replaced) {
        Write-Host "The previous copy could not be removed yet; it is at $(Split-Path $replaced -Leaf) and the next install will clear it."
    }
}

if (-not $existing) {
    # Quoted binPath, and the space after each '=' is required by sc.exe.
    #
    # LocalSystem rather than a lower-privileged account: the service has to
    # control itself during self-update, create junctions under Program Files,
    # read machine-scoped private keys, and eventually drive Volume Shadow Copy
    # at milestone 9. NetworkService satisfies none of those.
    # DisplayName follows ServiceName rather than being fixed. Windows treats a
    # display name as a second unique key, so a hard-coded one would fail with
    # 1078 (ERROR_DUPLICATE_SERVICE_NAME) before the service ever starts, any
    # time -ServiceName is used alongside an existing install.
    # That side-by-side case is *not supported* (`D49`) — it is warned about
    # above and allowed through (`D50`), not built for. -ServiceName survives
    # because renaming the service is reason enough on its own, and because
    # blocking it would take away the isolated rig destructive tests run on.
    # For the default install this is the same string it always was.
    & sc.exe create $ServiceName `
        binPath= "`"$currentLink\BedrockSentry.Host.exe`"" `
        DisplayName= $ServiceName `
        start= delayed-auto `
        obj= 'LocalSystem' | Out-Null
    Assert-LastExitCode 'sc.exe create'

    & sc.exe description $ServiceName 'Manages Minecraft Bedrock Dedicated Server instances.' | Out-Null
    Assert-LastExitCode 'sc.exe description'

    Write-Host "Registered service '$ServiceName'."
}

# ---------------------------------------------------------------------------
# Tell the service where its data lives (D36).
#
# It cannot come from configuration, because configuration is read from inside the
# data root. Before this, -DataRoot created and secured a directory and then never
# mentioned it to anyone: the service resolved its own root from
# BEDROCKSENTRY_DATAROOT or fell back to %ProgramData%\BedrockSentry, so an install
# told to keep data off the system volume quietly kept it on the system volume (T110).
#
# Written straight to ImagePath rather than through sc.exe. The value has to carry a
# quoted executable path *and* a quoted argument, and sc.exe's quoting rules for that
# are fragile enough across PowerShell versions to be worth avoiding entirely. Set on
# every install, not only when the service is created, so existing installs pick it up
# on their next upgrade.
#
# The executable's own path is unchanged, so firewall, AppLocker and antivirus rules
# keyed to it are unaffected — those match the binary, not the command line. What the
# junction protects is the exe not moving between version directories, which still
# holds.
# ---------------------------------------------------------------------------

$imagePath = '"{0}\BedrockSentry.Host.exe" --dataroot "{1}"' -f $currentLink, $DataRoot

Set-ItemProperty `
    -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" `
    -Name 'ImagePath' `
    -Value $imagePath `
    -Type ExpandString

Write-Host "Data root: $DataRoot"

# Recovery delays are deliberately long, and the first one especially so.
#
# When self-update installs a build that will not start, the updater needs an
# uninterrupted window to stop the service, flip the junction back and start the
# previous build. A five-second SCM restart would fire repeatedly inside that
# window and race the very recovery it looks like it is helping with. Crash-loop
# protection for a genuinely bad build is the updater's ledger, not the SCM's.
#
# Do not add 'failureflag 1' here. It only matters for an architecture where the
# service stops itself, which is not the one this uses.
& sc.exe failure $ServiceName reset= 86400 actions= restart/120000/restart/300000/restart/600000 | Out-Null
Assert-LastExitCode 'sc.exe failure'

# ---------------------------------------------------------------------------
# Optional settings written into the data root, which survives updates.
# ---------------------------------------------------------------------------

$configDir = Join-Path $DataRoot 'config'
New-Item -ItemType Directory -Path $configDir -Force | Out-Null

# ConvertFrom-Json -AsHashtable would do this in one line, and did until
# `T157`: -AsHashtable is PowerShell 6+ and simply does not bind under Windows
# PowerShell 5.1, which is the engine a Server Core box has. Deleting the
# switch is not the fix either - ConvertFrom-Json then returns a PSCustomObject
# and the key test below throws MethodNotFound under BOTH engines.
#
# Recursive because this file is the operator's whole configuration: it is read
# here, one key is set, and the rest is written back out. Anything this
# flattened or dropped would be silently deleted from their config on an
# upgrade.
#
# [ordered] rather than @{} at every site, because a PowerShell hashtable has no
# order and ConvertTo-Json emits keys in whatever order it enumerates. Values
# survived, so nothing failed and nobody checked - but an operator diffing their
# config after an upgrade got a WHOLLY REORDERED FILE, observed on Windows
# PowerShell 5.1, which makes the diff useless exactly when they are
# looking for what the installer changed. OrderedDictionary implements
# IDictionary, so the shape test above is unchanged under both engines - but the
# key test below is NOT: OrderedDictionary carries the non-generic IDictionary's
# .Contains, not Hashtable's .ContainsKey, so switching these to [ordered] without
# switching that call is a MethodNotFound a parse check cannot see. It was, and the
# lifted-function test caught it; see the note at the call itself.
function ConvertTo-ConfigHashtable {
    param($InputObject)

    if ($null -eq $InputObject) { return $null }

    if ($InputObject -is [System.Collections.IDictionary]) {
        $table = [ordered]@{}
        foreach ($key in @($InputObject.Keys)) {
            $table[$key] = ConvertTo-ConfigHashtable $InputObject[$key]
        }
        return $table
    }

    # A JSON object. Matched by shape rather than by type name so it holds under
    # both engines: 5.1 and 7 disagree about what ConvertFrom-Json hands back.
    if ($InputObject -is [System.Management.Automation.PSCustomObject] -or
        ($null -ne $InputObject.PSObject -and
         $InputObject.PSObject.BaseObject -is [System.Management.Automation.PSCustomObject])) {
        $table = [ordered]@{}
        foreach ($property in $InputObject.PSObject.Properties) {
            $table[$property.Name] = ConvertTo-ConfigHashtable $property.Value
        }
        return $table
    }

    # A JSON array. The leading comma stops PowerShell unrolling it on the way
    # out, which would turn a one-element array into a bare value.
    if ($InputObject -is [System.Collections.IEnumerable] -and $InputObject -isnot [string]) {
        $items = @(foreach ($item in $InputObject) { ConvertTo-ConfigHashtable $item })
        return , $items
    }

    return $InputObject
}

<#
.SYNOPSIS
    Sets one key in the operator's config file, creating the path if it is absent.

.DESCRIPTION
    One read-modify-write for every optional setting below, rather than a block
    each. Two blocks that each read, change and write the same file happen to be
    correct in source order and are one reorder away from the second silently
    discarding the first.

    -Depth 10 is load-bearing rather than decoration. The deepest container in the
    instance configuration is the retention block, at
    Sentry.Instances.Servers.<id>.Backup.Zip.Retention (Vss is the same shape) --
    seven levels, so 7 is the minimum working value and 10 leaves three to spare.
    Too small a depth serialises the deeper objects as their type name, which would
    destroy the operator's instance configuration on an upgrade.

    The path here used to read Backup.Retention.Mode "six levels deep", and there is
    no Backup.Retention: it is Backup.Zip.Retention and Backup.Vss.Retention, one
    level deeper than the comment claimed. The value was right and the reasoning
    beside it was stale, which is the way round that survives longest -- nothing
    fails, so nobody checks. Found by reading the path against the schema.
#>
function Set-ConfigValue {
    param(
        [Parameter(Mandatory = $true)] [string] $ConfigFile,
        [Parameter(Mandatory = $true)] [string[]] $Path,
        [Parameter(Mandatory = $true)] $Value
    )

    $config = if (Test-Path -LiteralPath $ConfigFile) {
        ConvertTo-ConfigHashtable (Get-Content -LiteralPath $ConfigFile -Raw | ConvertFrom-Json)
    } else {
        [ordered]@{}
    }

    $node = $config
    for ($i = 0; $i -lt $Path.Count - 1; $i++) {
        $segment = $Path[$i]
        # .Contains, not .ContainsKey. OrderedDictionary implements the NON-generic
        # IDictionary, whose member is Contains; ContainsKey belongs to Hashtable and
        # to IDictionary<,>. So moving these to [ordered] above turned every one of
        # these calls into a MethodNotFound at runtime - invisible to a parse check,
        # exactly like the -AsHashtable of T157, and caught here only because the
        # lifted-function test drives the real lines. Hashtable has Contains too, so
        # this is the spelling that holds whichever shape arrives.
        if (-not $node.Contains($segment) -or $node[$segment] -isnot [System.Collections.IDictionary]) {
            $node[$segment] = [ordered]@{}
        }

        $node = $node[$segment]
    }

    $node[$Path[-1]] = $Value

    # WriteAllText rather than Set-Content -Encoding UTF8: under Windows PowerShell
    # 5.1 that writes a BOM and under 7 it does not, while the wrapper's own writer
    # (JsonConfigWriter) writes none - so the first three bytes of the operator's
    # config flipped on every install and flipped back on the next panel save. This
    # is deterministic on both engines.
    #
    # $ConfigFile must be absolute: WriteAllText resolves a relative path against
    # the .NET working directory, which is not the PowerShell location. Every caller
    # here builds it from $DataRoot via Join-Path, so it is - asserted rather than
    # assumed, because the failure would be a config written somewhere nobody looks.
    #
    # It also does not append the trailing newline Set-Content adds. Harmless for
    # JSON, and named here because it will show up in a byte-comparison.
    if (-not [System.IO.Path]::IsPathRooted($ConfigFile)) {
        throw "Set-ConfigValue needs an absolute path; got '$ConfigFile'."
    }

    $json = $config | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText($ConfigFile, $json, (New-Object System.Text.UTF8Encoding $false))
}

$configFile = Join-Path $configDir 'bedrocksentry.json'

if ($AllowRemoteSetup) {
    Set-ConfigValue -ConfigFile $configFile -Path @('Sentry', 'AllowRemoteSetup') -Value $true

    Write-Warning 'Remote setup is enabled. The first-run wizard is reachable from the network until an administrator account is created. Complete setup promptly.'
}

# D45. Written before Start-Service below, so the first start is already permitted
# and logs its own warning - writing it afterwards would leave the panel showing
# the command blocked until the file watcher fired, which reads as the switch not
# working.
if ($AllowCustomBackupCommand) {
    Set-ConfigValue -ConfigFile $configFile -Path @('Sentry', 'Backups', 'AllowCustomCommand') -Value $true

    Write-Warning "Custom backup commands are enabled. A command typed into a server's Backups tab will run on this machine as the account the service runs as, and anyone who can sign in to the panel can use it. To turn it back off, re-run with -DenyCustomBackupCommand, or set Sentry:Backups:AllowCustomCommand to false in $configFile."
}

if ($DenyCustomBackupCommand) {
    Set-ConfigValue -ConfigFile $configFile -Path @('Sentry', 'Backups', 'AllowCustomCommand') -Value $false

    Write-Host 'Custom backup commands are disabled. Any command already configured stays saved and stops running.'
}

# ---------------------------------------------------------------------------
# Start, and surface what the operator needs to finish setup.
# ---------------------------------------------------------------------------

Write-Host "Starting $ServiceName..."
Start-Service -Name $ServiceName

$tokenFile = Join-Path $configDir 'setup-token.txt'
$authFile = Join-Path $configDir 'auth.json'
$deadline = (Get-Date).AddSeconds(90)
$token = $null

while ((Get-Date) -lt $deadline) {
    if (Test-Path -LiteralPath $authFile) { break }

    if (Test-Path -LiteralPath $tokenFile) {
        $token = (Get-Content -LiteralPath $tokenFile -Raw).Trim()
        if ($token) { break }
    }

    Start-Sleep -Milliseconds 500
}

Write-Host ''
Write-Host "BedrockSentry $identity installed." -ForegroundColor Green
Write-Host ''

if ($token) {
    $tlsPort = Get-TlsPort -DataRoot $DataRoot
    $reach = if ($AllowRemoteSetup) {
        "https://$($env:COMPUTERNAME):$tlsPort/setup"
    } else {
        "https://localhost:$tlsPort/setup"
    }

    Write-Host 'Finish setup in a browser:'
    Write-Host "  $reach"
    Write-Host ''
    Write-Host "  Setup token: $token"
    Write-Host ''

    if (-not $AllowRemoteSetup) {
        Write-Host 'The wizard is reachable only from this machine until an administrator'
        Write-Host 'account exists. Re-run with -AllowRemoteSetup if this box has no browser.'
    }

    Write-Host 'The certificate is self-signed, so expect a browser warning the first time.'
} elseif (Test-Path -LiteralPath $authFile) {
    Write-Host 'An administrator account already exists; no setup needed.'
} else {
    # Both places, because the event log is empty for a failure that happens
    # before logging starts — which is the most likely reason to be reading this
    # line at all (`T138`).
    Write-Warning @"
The service did not produce a setup token within 90 seconds.
Look in two places: the Application event log under source '$ServiceName', and
$(Join-Path $DataRoot 'logs\last-failure.log') - a failure that kills the process
writes there and may write nothing to the event log.
"@
}

if ($OpenFirewall) {
    # Named after the service, like the install root, data root, event source and
    # service display name already are. This was the one literal left in the
    # scripts (`T139`), so uninstall -RemoveFirewallRule on a renamed install
    # removed whichever install had created the rule. For the default install the
    # name is the same string it has always been, so an existing rule is matched
    # rather than duplicated and nothing needs migrating.
    $ruleName = "$ServiceName Web UI"
    $firewallPort = Get-TlsPort -DataRoot $DataRoot
    $existingRule = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue

    if (-not $existingRule) {
        New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow `
            -Protocol TCP -LocalPort $firewallPort -Profile Any | Out-Null
        Write-Host "Opened TCP $firewallPort ($ruleName)."
    } else {
        # The guard used to stop at "a rule of this name exists", ignoring the
        # port it opens. So changing Sentry:Tls:Port and re-running with
        # -OpenFirewall left the old port open and the new one closed - the panel
        # unreachable, and a port nothing is listening on still open (`T139`).
        $openPort = ($existingRule | Get-NetFirewallPortFilter).LocalPort

        if ("$openPort" -ne "$firewallPort") {
            Set-NetFirewallRule -DisplayName $ruleName -LocalPort $firewallPort | Out-Null
            Write-Host "Moved $ruleName from TCP $openPort to TCP $firewallPort."
        } else {
            Write-Host "TCP $firewallPort is already open ($ruleName)."
        }
    }
}
