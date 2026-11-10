<#
.SYNOPSIS
    Removes the BedrockSentry services and their binaries.

.DESCRIPTION
    Stops the service, then its privilege broker, waiting for each process to exit,
    deregisters both, removes their event log sources, then deletes the install
    directory. A delete that cannot
    finish is reported as a partial uninstall with exit code 1, and running the
    script again finishes it.

    It deliberately does NOT touch the data root. Worlds, backups, player data,
    certificates and configuration all live there, and an uninstaller that deletes
    a Minecraft world is a data-loss bug rather than a thorough cleanup. The path
    is printed at the end so anyone who genuinely wants it gone knows where to look.

    Before removing anything it lists what it will remove and what it keeps, and
    asks; No is the default answer. -Force or -Confirm:$false skips the question and
    -WhatIf stops after the list.

    Exit codes: 0 removed, or nothing to remove; 1 partly removed (run it again),
    or refused with nothing removed; 2 declined at the question.

.PARAMETER InstallRoot
    The install directory. Read from the registered service when not passed
    (D99); passing one the service does not use removes nothing.

.PARAMETER DataRoot
    The data root. Only printed, never deleted. Read from the registered service
    when not passed, like -InstallRoot.

.PARAMETER ServiceName
    The service name, if install.ps1 was given one. It decides which install the
    roots are read from, and the broker is <ServiceName>Broker.

.PARAMETER RemoveFirewallRule
    Also removes the inbound rule for the web UI, if install.ps1 created one.

.PARAMETER Force
    Skips the question, as -Confirm:$false does. Through powershell.exe -File the
    $false in -Confirm:$false arrives as text and the switch refuses it, so a
    script that runs this through -File passes -Force.

.EXAMPLE
    .\uninstall.ps1
#>

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $InstallRoot = "$env:ProgramFiles\BedrockSentry",

    [string] $DataRoot = "$env:ProgramData\BedrockSentry",

    [string] $ServiceName = 'BedrockSentry',

    [switch] $RemoveFirewallRule,

    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)

if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this from an elevated PowerShell session.'
}

# ---------------------------------------------------------------------------
# The two helpers: ServiceStop.ps1 (`T243`) and ServiceRoots.ps1 (`D99`).
#
# From beside this script first, which is the release it came in. Beside is
# resolved in the body and from two sources, because $PSScriptRoot is not always
# populated -- `T199` is this project's own instance of that.
#
# Then from the release the service runs from (`T336`): install.ps1 extracts the
# whole archive into the version directory the service's executable is in, so an
# uninstall.ps1 copied out on its own finds them there. Only the executable's
# folder is read from the service's command line here; where the roots are is
# ServiceRoots.ps1's answer, once it is loaded.
#
# A helper found in neither place is a THIRD STATE rather than a failure: the
# stop has a floor below, and a registered service no switch names is refused.
# ---------------------------------------------------------------------------
$serviceStopHelper = $null
$serviceRootsHelper = $null
$helperFolders = @($PSScriptRoot, (Split-Path -Parent $PSCommandPath))

$registered = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue

# Only a BedrockSentry executable's folder: a -ServiceName naming some other
# service must never run scripts out of that service's folder. Quoted, or unquoted
# with spaces in it as an install last upgraded to build.69 still carries it, read up
# to the first prefix naming the Host, as Windows runs it (`T341`).
if ($registered -and $registered.PathName -and
    ($registered.PathName -match '^\s*"(?<exe>[^"]+)"' -or
     $registered.PathName -match '^\s*(?<exe>(?:(?!\.exe\s)[^"])*?\\BedrockSentry\.Host\.exe)(?=\s|$)' -or
     $registered.PathName -match '^\s*(?<exe>[^\s"]+)') -and
    (Split-Path -Leaf $Matches['exe']) -ieq 'BedrockSentry.Host.exe') {
    $helperFolders += Split-Path -Parent $Matches['exe']
}

foreach ($base in $helperFolders) {
    if ([string]::IsNullOrEmpty($base)) { continue }

    $candidate = Join-Path $base 'ServiceStop.ps1'

    if (-not $serviceStopHelper -and (Test-Path -LiteralPath $candidate)) {
        . $candidate
        $serviceStopHelper = $candidate
    }

    # What a run does without it is below, where the roots are decided.
    $candidate = Join-Path $base 'ServiceRoots.ps1'

    if (-not $serviceRootsHelper -and (Test-Path -LiteralPath $candidate)) {
        . $candidate
        $serviceRootsHelper = $candidate
    }
}

# `D88`, answered by the operator: the degraded path waits too.
#
# Until this existed, a script that could not find the helper stopped the service,
# waited on the SERVICE CONTROL MANAGER, and carried on -- which is precisely the
# pre-fix behaviour `T243` was filed for, with a warning in front of it. The trade
# is not symmetric: duplication costs a drift somebody has to notice, and not
# waiting costs a half-deleted install root.
#
# `ServiceStop.ps1` IS THE SPECIFICATION AND THIS IS A FLOOR, NOT A MIRROR. Both
# answer one question -- may the caller assume this service's files are free? --
# and the floor's licence is to be CONSERVATIVE: it may answer $false where the
# helper answers $true, and it must never do the reverse. That is what makes a
# second copy of these lines tolerable where this codebase normally refuses one
# (`T126`, `T132`, `T136`, `T145`): the two are allowed to disagree, in one
# direction, and the direction they may disagree in is the safe one.
#
# It deliberately does not carry the helper's timeout parameter or its
# explanation of the Windows behaviour. A caller that needs either has the
# helper to read.
if (-not $serviceStopHelper) {
    function Stop-SentryServiceAndWait {
        param([Parameter(Mandatory)] [string] $ServiceName)

        # Captured BEFORE the stop, because once it is stopped there is nothing
        # left to ask. This ordering is the whole of it.
        $servicePid = (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue).ProcessId

        Stop-Service -Name $ServiceName -Force
        (Get-Service -Name $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(120))

        if (-not $servicePid) {
            return $false
        }

        $serviceProcess = Get-Process -Id $servicePid -ErrorAction SilentlyContinue

        if (-not $serviceProcess) {
            return $true
        }

        return [bool] $serviceProcess.WaitForExit(30000)
    }

    Write-Warning 'ServiceStop.ps1 is in neither the folder this script is in nor the release the service runs from, so this run is using a built-in copy of the stop-and-wait rule. That is a fallback: the archive should carry that file, and one that does not is incomplete.'
}

# ---------------------------------------------------------------------------
# `D99`. A root no switch names is read from the registered service. This script
# used to delete the default install root whichever service it had just removed.
# A switch naming a different root than the service uses removes nothing: the
# two describe different installs, and the person running this has to say which
# one they meant. A run meant for another install that also leaves out
# -ServiceName still names the default service; the list and the question below
# are what stop that one.
#
# Without ServiceRoots.ps1, beside this script or in the release the service runs
# from, nothing can be read, so a registered service needs -InstallRoot rather
# than the default.
# ---------------------------------------------------------------------------

$installRootSource = $null
$dataRootSource = $null

if ($serviceRootsHelper) {
    $roots = Resolve-SentryRoots -ServiceName $ServiceName `
        -CommandLine (Get-SentryServiceCommandLine -ServiceName $ServiceName) `
        -InstallRoot $InstallRoot -DataRoot $DataRoot `
        -InstallRootGiven $PSBoundParameters.ContainsKey('InstallRoot') `
        -DataRootGiven $PSBoundParameters.ContainsKey('DataRoot')

    if ($roots.Refusal) {
        throw "$($roots.Refusal) Nothing was removed."
    }

    if ($roots.Disagreements.Count -gt 0) {
        throw "$($roots.Disagreements -join ' ') Nothing was removed. Leave the switch out to remove what the service '$ServiceName' uses, or pass the -ServiceName of the install the switch describes."
    }

    $InstallRoot = $roots.InstallRoot
    $DataRoot = $roots.DataRoot
    $installRootSource = Format-SentryRootSource -From $roots.InstallRootFrom -ParameterName '-InstallRoot' -ServiceName $ServiceName
    $dataRootSource = Format-SentryRootSource -From $roots.DataRootFrom -ParameterName '-DataRoot' -ServiceName $ServiceName
}
elseif ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) -and -not $PSBoundParameters.ContainsKey('InstallRoot')) {
    throw "ServiceRoots.ps1 is in neither the folder this script is in nor the release the service runs from, so the install root the service '$ServiceName' uses cannot be read. Nothing was removed. Pass -InstallRoot."
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
# The privilege broker (milestone 18.5), named after the main service as install.ps1
# names it, and run from the same install root.
$brokerName = "${ServiceName}Broker"
$broker = Get-Service -Name $brokerName -ErrorAction SilentlyContinue

# ---------------------------------------------------------------------------
# `D99`. What goes is named before it goes, and the question defaults to No: an
# empty answer removes nothing, and a caller that cannot answer at all fails at
# the prompt with nothing removed. A plain ShouldProcess prompt defaults to Yes,
# which is why this is not one.
# ---------------------------------------------------------------------------

$removals = @()

if ($service) {
    $removals += "the service '$ServiceName' ($($service.Status)) and its event log source"
}

if ($broker) {
    $removals += "the service '$brokerName' ($($broker.Status)) and its event log source"
}

if ($RemoveFirewallRule) {
    $removals += "the firewall rule '$ServiceName Web UI', if there is one"
}

if (Test-Path -LiteralPath $InstallRoot) {
    $removal = "$InstallRoot, the install root"

    if ($installRootSource) {
        $removal += ", $installRootSource"
    }

    $removals += $removal
}

if ($removals.Count -gt 0 -or $WhatIfPreference) {
    Write-Host 'This removes:'

    foreach ($removal in $removals) {
        Write-Host "  $removal"
    }

    if ($removals.Count -eq 0) {
        Write-Host '  nothing'
    }

    $kept = "$DataRoot, the data root"

    if ($dataRootSource) {
        $kept += ", $dataRootSource"
    }

    Write-Host 'and keeps:'
    Write-Host "  $kept"
    Write-Host ''
}

if ($WhatIfPreference) {
    Write-Host 'Nothing was removed: -WhatIf was passed.'
    exit 0
}

if ($removals.Count -gt 0) {
    $confirmedByCaller = $Force -or ($PSBoundParameters.ContainsKey('Confirm') -and -not $PSBoundParameters['Confirm'])

    if (-not $confirmedByCaller) {
        $choices = [System.Management.Automation.Host.ChoiceDescription[]] @(
            (New-Object System.Management.Automation.Host.ChoiceDescription '&Yes', 'Remove everything listed.'),
            (New-Object System.Management.Automation.Host.ChoiceDescription '&No', 'Remove nothing.'))

        $answer = $Host.UI.PromptForChoice('Uninstall BedrockSentry', 'Remove these?', $choices, 1)

        if ($answer -ne 0) {
            Write-Host 'Nothing was removed.'
            exit 2
        }
    }

    # Asked once for the whole run: an explicit -Confirm would otherwise ask
    # again at every Stop-Service and Remove-Item below.
    $ConfirmPreference = 'None'
}

# Whether this run can assume the install root's files are unlocked. `T243`: it
# could not, and deleted anyway. Stays $true when there was no service to stop,
# which is the re-run-after-a-failure case and the already-stopped case.
$filesAreFree = $true

if ($service) {
    if ($service.Status -ne 'Stopped') {
        Write-Host "Stopping $ServiceName..."
        $filesAreFree = Stop-SentryServiceAndWait -ServiceName $ServiceName

        # ONE SENTENCE AGAIN, AND THAT IS `D88` LANDING RATHER THAN A SIMPLIFICATION.
        # There were two, because $filesAreFree false meant two different things: the
        # helper WAITED and the process was still there, or nothing waited and nobody
        # looked. Both paths wait now, so false means the same thing on each -- and a
        # second sentence would be the rule stated twice and enforced once, which is
        # what the pair was written to avoid in the first place (`T188`).
        #
        # The missing file is said where it is found, above, because it is a fact
        # about the archive rather than about this stop.
        if (-not $filesAreFree) {
            Write-Warning "$ServiceName reports stopped but its process has not exited; files it holds may not be removable yet."
        }
    }

    & sc.exe delete $ServiceName | Out-Null

    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe delete failed with exit code $LASTEXITCODE."
    }

    Write-Host "Removed service '$ServiceName'."
} else {
    Write-Host "Service '$ServiceName' is not registered."
}

# After the main service, which asks the broker for its work until it stops.
if ($broker) {
    if ($broker.Status -ne 'Stopped') {
        Write-Host "Stopping $brokerName..."

        if (-not (Stop-SentryServiceAndWait -ServiceName $brokerName)) {
            $filesAreFree = $false
            Write-Warning "$brokerName reports stopped but its process has not exited; files it holds may not be removable yet."
        }
    }

    & sc.exe delete $brokerName | Out-Null

    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe delete failed for $brokerName with exit code $LASTEXITCODE."
    }

    Write-Host "Removed service '$brokerName'."
}

if ($RemoveFirewallRule) {
    # Scoped to the service name to match install.ps1 (`T139`). Unchanged for the
    # default install; a rule created by an older build under a *renamed* service
    # is named 'BedrockSentry Web UI' and will not be found here, which is
    # deliberate - removing another install's rule is the bug being fixed.
    $ruleName = "$ServiceName Web UI"
    $rule = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue

    if ($rule) {
        $rule | Remove-NetFirewallRule
        Write-Host "Removed the firewall rule '$ruleName'."
    } else {
        Write-Host "No firewall rule named '$ruleName' to remove."
    }
}

# THE EVENT SOURCE GOES BEFORE THE DELETE, and that ordering is `T243` rather
# than tidiness. It used to sit after, so a delete that threw left the source
# registered as well as the install root half gone -- and re-running is what
# removed it. This one cannot fail over a locked file, so doing it first means
# the expensive, fallible step is last and everything before it has landed.
foreach ($source in @($ServiceName, $brokerName)) {
    if ([System.Diagnostics.EventLog]::SourceExists($source)) {
        [System.Diagnostics.EventLog]::DeleteEventSource($source)
        Write-Host "Removed the event log source '$source'."
    }
}

# `T243`. The delete is the one step here that can fail part-way:
# Remove-Item -Recurse deletes as it walks and throws at the end (`T186`), so a
# throw leaves a PARTLY deleted install root. It used to throw out of the script
# with $ErrorActionPreference = 'Stop', and the operator was told nothing at all
# -- not what had happened, not that their data was safe, not that re-running
# finishes the job. The reassurance this script exists to give was the one thing
# they did not get.
#
# So it is caught, and the outcome is carried to the message rather than thrown.
# THREE STATES, NOT TWO (`T176`): removed, nothing to remove, or part-way. The
# data root is untouched on every one of them, which is why a partial removal is
# a "run it again" and not a loss.
$installRootRemoved = $true
$installRootError = $null

# The service already gone with the install root still here is what a part-way
# removal leaves, and a run that finishes one says so (`T243` clause 3).
$finishingPartialRemoval = (-not $service) -and (Test-Path -LiteralPath $InstallRoot)

if (Test-Path -LiteralPath $InstallRoot) {
    try {
        # The junction has to go before the recursive delete. Removing it as a link
        # leaves its target alone; letting Remove-Item recurse through it first would
        # be a good way to delete a version directory twice over.
        $currentLink = Join-Path $InstallRoot 'current'

        if (Test-Path -LiteralPath $currentLink) {
            (Get-Item -LiteralPath $currentLink).Delete()
        }

        Remove-Item -LiteralPath $InstallRoot -Recurse -Force
        Write-Host "Removed $InstallRoot."
    }
    catch {
        $installRootRemoved = $false
        $installRootError = $_.Exception.Message
    }
}

Write-Host ''

if ($installRootRemoved) {
    Write-Host 'BedrockSentry has been uninstalled.' -ForegroundColor Green

    if ($finishingPartialRemoval) {
        Write-Host "The service was already gone; this run removed what was left in $InstallRoot."
    }
} else {
    Write-Host 'BedrockSentry has been PARTLY uninstalled.' -ForegroundColor Yellow
    Write-Host ''
    Write-Host "The service is gone, but some files under $InstallRoot could not be removed:"
    Write-Host "  $installRootError"
    Write-Host ''
    Write-Host 'That usually means the service had not finished exiting. Nothing else is'
    Write-Host 'needed: run this script again and it will finish the job.'
}

Write-Host ''

# Printed on BOTH paths, which is the half of `T243` that matters most to whoever
# is reading. uninstall.ps1's own documentation promises this path is printed at
# the end so anyone who genuinely wants it gone knows where to look, and on the
# failing path it was not.
if (Test-Path -LiteralPath $DataRoot) {
    Write-Host 'Your data was left in place:'
    Write-Host "  $DataRoot"
    Write-Host ''
    Write-Host 'That folder holds configuration, certificates, player data and backups.'
    Write-Host 'Delete it by hand if you want a completely clean removal.'
}

# Non-zero so a caller that scripts this can tell, since the console message is
# for a person and an exit code is for whatever ran it. Zero is said too: a
# script that just ends leaves $LASTEXITCODE as the last native command left it,
# and a re-run skips sc.exe, so it read blank rather than 0 (`T243`).
if (-not $installRootRemoved) {
    exit 1
}

exit 0
