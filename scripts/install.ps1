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

.PARAMETER GrantFolder
    Gives the service's account, and a server running there, Modify on a folder
    outside the data root that already holds files, such as a server's folder that
    is added as an existing server. Run elevated against an installed service; it
    installs nothing. The folder is refused when it is a system folder, a drive
    root, the install tree or the data root, or when a link or a file with a second
    name is anywhere in it. With more than one BedrockSentry service installed,
    -ServiceName must name the one to grant to.

        .\install.ps1 -GrantFolder "D:\Minecraft\survival"

.PARAMETER ClearPanelMfa
    Switches off the panel's authenticator-app sign-in step (D125) when the phone
    and the recovery codes are both lost. Run elevated against an installed
    service; it installs nothing. The service is stopped, the step is removed from
    auth.json with every session signed out, and the service is started again.
    The password is unchanged.

        .\install.ps1 -ClearPanelMfa

.PARAMETER SkipDrain
    Stops the service without first asking the running wrapper to warn players,
    drain its servers and take any backup-on-shutdown. Worlds are still saved -
    the wrapper's own shutdown runs either way - but players get a few seconds'
    notice instead of the configured countdown.

.PARAMETER InstallRoot
    Where binaries live. Replaced wholesale by updates; keep nothing here. On an
    upgrade it is read from the registered service when not passed (D99), and
    passing one the service does not use moves the install there, with a warning.

.PARAMETER DataRoot
    Where configuration, certificates, keys, backups and player data live. Never
    deleted by an update or by uninstall. This script creates it and gives it to
    SYSTEM, Administrators and the service's account, and never writes into it
    otherwise: the service's account can change anything there.

    A data root that already exists is granted only when it already carries the
    grant, or when it and every folder in it are owned by SYSTEM or Administrators,
    only they could write there, and no link is among them: the grant is written
    down the whole tree as an administrator, and a link somebody else left in it
    would carry that write elsewhere. Anything else is refused before the running
    install is touched. A data root that does not exist yet is created with the
    grant already on it.

    Written into the service's command line on every run. On an upgrade it is
    read from there when not passed, like -InstallRoot.

.PARAMETER ServiceName
    The Windows service name. Pass the same value on every upgrade and to
    uninstall.ps1: it decides which service the roots are read from. A second
    install beside an existing one is not supported (D49).

    Letters, digits, '-' and '_', up to 64 characters. Written into the service's
    command line as --service, and every name the install uses derives from it: the
    broker service, both pipes, the service account and the event log source.

.PARAMETER ServiceAccount
    The account the service runs as. Left out, it is the service's own virtual
    account, NT SERVICE\<ServiceName>, which needs no password; on an upgrade a named
    account the service already runs as is kept. Pass DOMAIN\user (or .\user) for an
    administrator who needs a named one, for example to reach a network share: it is
    given the right to log on as a service, which uninstall.ps1 leaves in place,
    since the account may hold it for another service too. LocalSystem,
    LocalService and NetworkService are refused. The data root is granted to one
    account: changing it on an existing install is refused, and is done by
    uninstall.ps1, then this script with the new account.

.PARAMETER ServiceAccountPassword
    The named account's password. Asked for when -ServiceAccount is passed without it.

.PARAMETER NoBroker
    Leaves out the privilege broker, the second service that does the administrative
    work for the first (D46). Servers and backups still run; the firewall, Defender
    and loopback fixes, shadow-copy backups and self-update are unavailable, and the
    Readiness panel and Settings say why. An existing broker service is removed.

.PARAMETER OpenFirewall
    Adds an inbound rule named '<ServiceName> Web UI' for the panel's port, read
    from Sentry:Tls:Port and 8443 when that is unset, or moves an existing rule to
    that port. The rule admits only the service's own processes, so a port named
    in that file opens nothing else.

.PARAMETER AllowRemoteSetup
    Lets the first-run wizard be reached from the network instead of loopback only.
    Needed on headless Server Core boxes with no local browser; understand that it
    exposes an unauthenticated "claim the administrator account" page until setup
    is completed. Written to policy.json in the install root.

.PARAMETER AllowCustomBackupCommand
    Lets the custom-command backup strategy run on this install. A command typed
    into a server's Backups tab runs as the account this service runs as, and the
    panel binds every interface once setup is done - so anyone who can sign in to
    the panel can use it. It is off on a fresh install and stays off until this
    switch is used.

    -ZipPath is still required. To turn it on against an install that already
    exists, re-run this script with the same or a newer zip and add the switch,
    the way -AllowRemoteSetup works.

    The switch is written to policy.json in the install root, which the service's
    own account cannot write, so code running as that account cannot turn it on.
    The point is that a default install is not a remote shell.

.PARAMETER DenyCustomBackupCommand
    Turns it back off. Takes effect without a restart.

.PARAMETER AllowPanelFolderGrants
    Lets the panel hand the service a folder outside the data root itself, through
    the privilege broker, when a server, backups or downloads are pointed there
    (D120). Off by default: then only -GrantFolder, run as administrator, hands one
    over, and the panel prints that exact command when it cannot use a folder.

    The broker grants only a folder it creates or an empty one. Written to
    policy.json in the install root as allowPanelFolderGrants; an administrator may
    set it there by hand too. Takes effect without a restart.

.PARAMETER DenyPanelFolderGrants
    Turns it back off. Folders already granted stay granted.

.EXAMPLE
    .\install.ps1 -ZipPath ..\bedrocksentry-0.4.1-build.92_abc1234.zip

    Run from a new folder the zip was extracted into, so the installer is the one
    that shipped in that zip.
#>

[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Install')]
    [string] $ZipPath,

    [Parameter(Mandatory = $true, ParameterSetName = 'GrantFolder')]
    [string] $GrantFolder,

    [Parameter(Mandatory = $true, ParameterSetName = 'ClearPanelMfa')]
    [switch] $ClearPanelMfa,

    [string] $InstallRoot = "$env:ProgramFiles\BedrockSentry",

    [string] $DataRoot = "$env:ProgramData\BedrockSentry",

    [ValidatePattern('^[A-Za-z0-9_-]{1,64}\z')]
    [string] $ServiceName = 'BedrockSentry',

    [string] $ServiceAccount,

    [securestring] $ServiceAccountPassword,

    [switch] $NoBroker,

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
    [switch] $DenyCustomBackupCommand,

    # D120. Lets the panel ask the broker to grant a folder outside the data root;
    # without it only -GrantFolder does, and the panel prints that command.
    [switch] $AllowPanelFolderGrants,

    [switch] $DenyPanelFolderGrants
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Read by the trap that removes a failed install's staging directory. A trap covers its
# whole scope, the lines above it included, so it runs for a failure anywhere in this
# script; before this line it read a variable nothing had set yet, strict mode refused,
# and that error replaced every early failure's own message (`T340`).
$staging = $null

<#
    .SYNOPSIS
    The bytes of a file at the top of a zip, or $null when the zip has none.

    .DESCRIPTION
    Matched by name exactly, then ignoring case: the release workflow writes these
    files at the root, and a hand-assembled archive is exactly the case worth
    catching. Throws when the archive cannot be read.
#>
function Read-ArchiveRootEntry {
    param(
        [Parameter(Mandatory)] [string] $ArchivePath,
        [Parameter(Mandatory)] [string] $Name
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)

    try {
        $entry = $archive.GetEntry($Name)

        if (-not $entry) {
            $entry = $archive.Entries |
                Where-Object { $_.FullName -ieq $Name } |
                Select-Object -First 1
        }

        if (-not $entry) {
            return $null
        }

        $reader = $entry.Open()
        $memory = New-Object System.IO.MemoryStream

        try {
            $reader.CopyTo($memory)
            $bytes = $memory.ToArray()
        }
        finally {
            $memory.Dispose()
            $reader.Dispose()
        }

        # The comma keeps the array whole: returned bare, PowerShell would send
        # it down the pipeline one byte at a time.
        return ,$bytes
    }
    finally {
        $archive.Dispose()
    }
}

# ---------------------------------------------------------------------------
# The two helpers: ServiceStop.ps1 (`T243`) and ServiceRoots.ps1 (`D99`).
#
# Read from the release archive first (`T336`). Its copies belong to the build
# being installed, an installer copied out of the zip on its own finds them
# there, and a copy left beside this script by an earlier extraction can be older
# than both. Then from beside this script, for an archive that predates a helper.
# Beside is resolved in the body and from two sources, because $PSScriptRoot is
# not always populated -- `T199` is this project's own instance of that.
#
# Dot-sourced at the top level rather than in a function, so their functions land
# in this script's scope. A helper found in neither place is a THIRD STATE rather
# than a failure: the stop has a floor below, and the roots are refused on a
# registered service that no switch names.
# ---------------------------------------------------------------------------
$serviceStopHelper = $null
$serviceRootsHelper = $null

if ($ZipPath -and (Test-Path -LiteralPath $ZipPath -PathType Leaf)) {
    $archivePath = (Resolve-Path -LiteralPath $ZipPath).Path

    foreach ($helperName in 'ServiceStop.ps1', 'ServiceRoots.ps1') {
        try {
            $helperBytes = Read-ArchiveRootEntry -ArchivePath $archivePath -Name $helperName
        }
        catch {
            # A zip that cannot be read fails at the extraction below, with the reason.
            Write-Verbose "Could not read $helperName from $archivePath`: $($_.Exception.Message)"
            $helperBytes = $null
        }

        if ($null -eq $helperBytes -or $helperBytes.Length -eq 0) {
            continue
        }

        . ([ScriptBlock]::Create([System.Text.Encoding]::UTF8.GetString($helperBytes).TrimStart([char]0xFEFF)))

        if ($helperName -eq 'ServiceStop.ps1') {
            $serviceStopHelper = "$helperName in $archivePath"
        }
        else {
            $serviceRootsHelper = "$helperName in $archivePath"
        }
    }
}

foreach ($base in @($PSScriptRoot, (Split-Path -Parent $PSCommandPath))) {
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

    Write-Warning 'ServiceStop.ps1 is in neither the archive nor the folder this script is in, so this run is using a built-in copy of the stop-and-wait rule. That is a fallback: the archive should carry that file, and one that does not is incomplete.'
}


if ($AllowCustomBackupCommand -and $DenyCustomBackupCommand) {
    throw 'Pass -AllowCustomBackupCommand or -DenyCustomBackupCommand, not both.'
}

if ($AllowPanelFolderGrants -and $DenyPanelFolderGrants) {
    throw 'Pass -AllowPanelFolderGrants or -DenyPanelFolderGrants, not both.'
}

function Assert-LastExitCode {
    param([string] $What)

    if ($LASTEXITCODE -ne 0) {
        throw "$What failed with exit code $LASTEXITCODE."
    }
}

# The right to log on as a service, for a named account. Through the LSA because
# nothing PowerShell 5.1 ships sets a user right without secedit's whole-policy
# export and import.
function Grant-SentryServiceLogonRight {
    param(
        [Security.Principal.SecurityIdentifier] $Sid,
        [switch] $DefineOnly
    )

    if (-not ('SentryLsa' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class SentryLsa
{
    [StructLayout(LayoutKind.Sequential)]
    struct LsaString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    struct LsaObjectAttributes
    {
        public int Length; public IntPtr RootDirectory; public IntPtr ObjectName;
        public uint Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService;
    }

    [DllImport("advapi32.dll")] static extern uint LsaOpenPolicy(IntPtr system, ref LsaObjectAttributes attributes, uint access, out IntPtr handle);
    [DllImport("advapi32.dll")] static extern uint LsaAddAccountRights(IntPtr handle, byte[] sid, LsaString[] rights, uint count);
    [DllImport("advapi32.dll")] static extern uint LsaClose(IntPtr handle);
    [DllImport("advapi32.dll")] static extern int LsaNtStatusToWinError(uint status);

    // POLICY_CREATE_ACCOUNT | POLICY_LOOKUP_NAMES
    const uint Access = 0x00000010 | 0x00000800;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool LogonUserW(string user, string domain, IntPtr password, int type, int provider, out IntPtr token);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    // LOGON32_LOGON_NETWORK, LOGON32_PROVIDER_DEFAULT: checks the password and
    // nothing else, since a network logon needs no right the account may lack.
    public static int CheckPassword(string user, string domain, IntPtr password)
    {
        IntPtr token;

        if (!LogonUserW(user, domain, password, 3, 0, out token))
        {
            int error = Marshal.GetLastWin32Error();

            // ERROR_LOGON_TYPE_NOT_GRANTED: the password was right, and policy denies
            // this account a network logon, which a service does not need.
            return error == 1385 ? 0 : error;
        }

        CloseHandle(token);
        return 0;
    }

    public static int Grant(byte[] sid, string right)
    {
        LsaObjectAttributes attributes = new LsaObjectAttributes();
        IntPtr handle;
        uint status = LsaOpenPolicy(IntPtr.Zero, ref attributes, Access, out handle);

        if (status != 0)
        {
            return LsaNtStatusToWinError(status);
        }

        IntPtr buffer = Marshal.StringToHGlobalUni(right);

        try
        {
            LsaString name = new LsaString();
            name.Buffer = buffer;
            name.Length = (ushort)(right.Length * 2);
            name.MaximumLength = (ushort)((right.Length + 1) * 2);
            status = LsaAddAccountRights(handle, sid, new LsaString[] { name }, 1);
            return status == 0 ? 0 : LsaNtStatusToWinError(status);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            LsaClose(handle);
        }
    }
}
'@
    }

    if ($DefineOnly) {
        return
    }

    $bytes = [byte[]]::new($Sid.BinaryLength)
    $Sid.GetBinaryForm($bytes, 0)
    $result = [SentryLsa]::Grant($bytes, 'SeServiceLogonRight')

    if ($result -ne 0) {
        throw "Granting $Sid the right to log on as a service failed with Windows error $result."
    }
}

# Whether the password logs the account on, so a wrong one stops the install
# before anything is stopped rather than at the service's start.
function Assert-SentryAccountPassword {
    param(
        [Parameter(Mandatory = $true)] [string] $Account,
        [Parameter(Mandatory = $true)] [securestring] $Password
    )

    # Defines SentryLsa when no grant has yet.
    if (-not ('SentryLsa' -as [type])) {
        Grant-SentryServiceLogonRight -DefineOnly
    }

    $domain = $null
    $user = $Account

    if ($Account.Contains('\')) {
        $domain, $user = $Account -split '\\', 2
    }

    if ($domain -eq '.') {
        $domain = $env:COMPUTERNAME
    }

    $plain = [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($Password)

    try {
        $result = [SentryLsa]::CheckPassword($user, $domain, $plain)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($plain)
    }

    if ($result -ne 0) {
        throw "The password for $Account was refused: $(([ComponentModel.Win32Exception] $result).Message)"
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
# Whether a file under the data root is a plain file: neither it nor its folder a
# link, and the file has one name. The service's account can make either a link.
function Test-SentryPlainFile {
    param([Parameter(Mandatory = $true)] [string] $Path)

    foreach ($item in @((Get-Item -LiteralPath (Split-Path -Parent $Path) -Force), (Get-Item -LiteralPath $Path -Force))) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            return $false
        }
    }

    return -not (Get-Item -LiteralPath $Path -Force).LinkType
}

function Get-TlsPort {
    param([string] $DataRoot)

    $configPath = Join-Path $DataRoot 'config\bedrocksentry.json'

    if (-not (Test-Path -LiteralPath $configPath)) {
        return 8443
    }

    if (-not (Test-SentryPlainFile -Path $configPath)) {
        Write-Host "$configPath is a link, so it is not read; assuming 8443."
        return 8443
    }

    try {
        $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
        $configured = Get-Property (Get-Property (Get-Property $config 'Sentry') 'Tls') 'Port'

        if ($null -ne $configured) {
            $port = [int] $configured

            if ($port -ge 1 -and $port -le 65535) {
                return $port
            }

            Write-Host "The panel port in $configPath, $port, is not a port; assuming 8443."
        }
    } catch {
        Write-Host "Could not read the panel port from $configPath ($($_.Exception.Message)); assuming 8443."
    }

    return 8443
}

<#
.SYNOPSIS
    One request on the wrapper's admin pipe, and its answer (D62).

.DESCRIPTION
    The frame is the frozen installer contract (docs/design.md section 12.4): a
    four-byte little-endian length, then that many bytes of UTF-8 JSON, one request
    and one answer per connection. Only an elevated administrator or LocalSystem may
    open the pipe.

    Returns $null when nothing serves the pipe; the caller reads that as no drain.
    Every wrapper older than the pipe runs as LocalSystem, which this script refuses
    before it drains (T386 clause 4, T387 clause 1), so $null means a current wrapper
    whose pipe was not created. The client asks for Identification only, so
    whatever answers can learn who asked and never act as them, and ServerCheck,
    when given, is asked before a byte is sent.

    The read polls rather than blocks, so Ctrl+C stops it between polls: a blocking
    .NET read would hold the console until the drain ended, and the call-off in
    Invoke-GracefulDrain's finally would never run in time (D100).

    With -Raw and no ServerCheck it runs no cmdlet, so it works in that finally once
    Ctrl+C has stopped the pipeline: -Raw returns the answer's text unparsed.
#>
function Invoke-SentryAdminPipe {
    param(
        [Parameter(Mandatory)] [string] $PipeName,
        [Parameter(Mandatory)] [ValidateSet('drain', 'callOff')] [string] $Operation,
        [int] $TimeoutSeconds = 30,
        [scriptblock] $ServerCheck,
        [switch] $Raw
    )

    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.', $PipeName,
        [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::Asynchronous,
        [System.Security.Principal.TokenImpersonationLevel]::Identification)

    try {
        try {
            $pipe.Connect(3000)
        } catch {
            $failure = $_.Exception
            while ($failure.InnerException) { $failure = $failure.InnerException }

            if ($failure -is [System.TimeoutException] -or $failure -is [System.IO.FileNotFoundException]) {
                return $null
            }

            throw
        }

        if ($ServerCheck) {
            $why = & $ServerCheck $pipe
            if ($why) {
                throw "the admin pipe is held by another process: $why"
            }
        }

        $body = [System.Text.Encoding]::UTF8.GetBytes('{"operation":"' + $Operation + '"}')
        $pipe.Write([BitConverter]::GetBytes([int] $body.Length), 0, 4)
        $pipe.Write($body, 0, $body.Length)
        $pipe.Flush()

        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        $length = [byte[]]::new(4)
        $read = 0

        while ($read -lt 4) {
            $task = $pipe.ReadAsync($length, $read, 4 - $read)
            while (-not $task.Wait(250)) {
                if ([DateTime]::UtcNow -gt $deadline) { throw "the wrapper did not answer within $TimeoutSeconds seconds" }
            }
            if ($task.Result -eq 0) { throw 'the wrapper closed the admin pipe without answering' }
            $read += $task.Result
        }

        $size = [BitConverter]::ToInt32($length, 0)
        if ($size -le 0 -or $size -gt 1048576) { throw "the wrapper's answer claims $size bytes" }

        $answer = [byte[]]::new($size)
        $read = 0

        while ($read -lt $size) {
            $task = $pipe.ReadAsync($answer, $read, $size - $read)
            while (-not $task.Wait(250)) {
                if ([DateTime]::UtcNow -gt $deadline) { throw "the wrapper did not answer within $TimeoutSeconds seconds" }
            }
            if ($task.Result -eq 0) { throw 'the wrapper closed the admin pipe mid-answer' }
            $read += $task.Result
        }

        $text = [System.Text.Encoding]::UTF8.GetString($answer)

        if ($Raw) {
            return $text
        }

        return $text | ConvertFrom-Json
    } finally {
        $pipe.Dispose()
    }
}

<#
.SYNOPSIS
    Why the process serving the admin pipe is not the service's, or $null when it is.

.DESCRIPTION
    A process the service started carries its account and could create the pipe's
    name while the service is down. Its answer would only mislead this installer,
    never act as it, but the drain is reported from what the service says.
#>
function Test-SentryPipeServer {
    param(
        [Parameter(Mandatory)] $Pipe,
        [Parameter(Mandatory)] [string] $ServiceName
    )

    if (-not ('SentryPipeNative' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SentryPipeNative {
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint processId);
}
'@
    }

    $server = [uint32] 0

    if (-not [SentryPipeNative]::GetNamedPipeServerProcessId($Pipe.SafePipeHandle.DangerousGetHandle(), [ref] $server)) {
        return 'its process could not be identified'
    }

    $servicePid = (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue).ProcessId

    if ($server -ne $servicePid) {
        return "process $server is not $ServiceName (process $servicePid)"
    }

    return $null
}

<#
.SYNOPSIS
    Calls off the drain this installer asked for, from the finally its Ctrl+C runs (D100).

.DESCRIPTION
    Over the admin pipe, the only way a drain is asked for. .NET calls only, and [Console] rather than Write-Host: once Ctrl+C has
    stopped the pipeline, a cmdlet in a finally block may not run, and a method call
    does. So the pipe's server is not checked here; a call-off tells whatever answers
    nothing it could use. The wrapper answers how many countdowns it called off in
    time and how many had already begun stopping servers.
#>
function Invoke-DrainCallOff {
    param(
        [Parameter(Mandatory)] [string] $PipeName
    )

    try {
        $body = Invoke-SentryAdminPipe -PipeName $PipeName -Operation 'callOff' -TimeoutSeconds 10 -Raw

        if ($null -eq $body) {
            throw 'the admin pipe is gone'
        }

        if ($body -match '"calledOff"\s*:\s*[1-9]') {
            [Console]::WriteLine('Called off: the servers keep running, and nothing was installed.')
        } elseif ($body -match '"tooLate"\s*:\s*[1-9]') {
            [Console]::WriteLine('Too late to call it off: the servers were already stopping. Nothing was installed; start them again from the panel.')
        } else {
            [Console]::WriteLine('Nothing was left to call off. Nothing was installed.')
        }
    } catch {
        [Console]::WriteLine("Could not call the drain off ($($_.Exception.Message)). The servers stop when its countdown ends; start them again from the panel. Nothing was installed.")
    }
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

    Nothing is waiting on us. So we ask first, over the wrapper's admin pipe (D62),
    and only then stop the service. By that point every server is already down and the
    short budget has nothing left to hurry.

    Best effort throughout. A wrapper that is not running, or whose pipe nothing
    serves, answers nothing useful - and neither is a reason to refuse to install. The stop that follows still saves
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

    $warningSeconds = 60
    $configPath = Join-Path $DataRoot 'config\bedrocksentry.json'

    if (Test-Path -LiteralPath $configPath) {
        try {
            $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
            $update = Get-Property (Get-Property $config 'Sentry') 'Update'

            if ($null -ne $update) {
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
        Write-Host 'This is the graceful half of the upgrade. Press Ctrl+C to call it off: the servers keep running and nothing is installed.'
    } else {
        Write-Host 'Asking BedrockSentry to drain its servers...'
    }

    # The countdown plus the drain budget plus room for a backup-on-shutdown of a
    # large world. Generous rather than tight: timing out here would stop the
    # service underneath a backup that was still running.
    $timeout = $warningSeconds + 600

    # D100. Ctrl+C stops the request below without reaching either branch, and the
    # wrapper's countdown carries on and stops every server when it ends. So the finally
    # calls it off unless an answer or an error arrived first.
    $drainAnswered = $false
    $calledOff = $false
    $pipeName = "$ServiceName.admin"
    $serverCheck = { param($pipe) Test-SentryPipeServer -Pipe $pipe -ServiceName $ServiceName }

    try {
        try {
            # $null means nothing serves the pipe. No wrapper this script upgrades drains
            # any other way: the ones older than the pipe run as LocalSystem, refused
            # before this runs (T386 clause 4), and this build's /prepare-shutdown maps
            # to nothing (T111). So it is no drain, said as such.
            $response = Invoke-SentryAdminPipe -PipeName $pipeName -Operation 'drain' `
                -TimeoutSeconds $timeout -ServerCheck $serverCheck

            if ($null -eq $response) {
                throw 'nothing serves the admin pipe'
            }

            $drainAnswered = $true

            # A refusal from the pipe drained nothing, and is no answer of "nothing ran".
            $refused = Get-Property $response 'error'
            if ($null -ne $refused) {
                throw "the wrapper refused the drain: $refused"
            }

            # Filtered, not just array-wrapped: @($null) in PowerShell is an array of
            # one null, not an empty one, so a response missing these fields would
            # otherwise report "Drained: ." and claim something had happened.
            $stopped = @((Get-Property $response 'stopped') | Where-Object { $null -ne $_ })
            $failed = @((Get-Property $response 'failed') | Where-Object { $null -ne $_ })

            if ((Get-Property $response 'calledOff') -eq $true) {
                # Called off by a DELETE from somewhere, this installer's Ctrl+C or another's.
                # Nothing stopped, so stopping the service now would take the servers down
                # with no warning at all.
                $calledOff = $true
            } elseif ($stopped.Count -gt 0) {
                Write-Host "Drained: $($stopped -join ', ')."
            } else {
                Write-Host 'Nothing was running.'
            }

            if ($failed.Count -gt 0) {
                Write-Warning "These did not stop cleanly and will be stopped with the service: $($failed -join ', ')."
            }
        } catch {
            $drainAnswered = $true

            # Everything lands here: a wrapper whose admin pipe would not be created, a
            # pipe answered by a process that is not the service, a refusal, and a
            # wrapper that simply did not reply. All fall through to the ordinary service
            # stop, which still saves worlds. What matters is that the operator is told, rather than reading a
            # line that says nothing needed draining.
            Write-Host "No graceful drain available ($($_.Exception.Message)). Falling back to the service stop, which still saves worlds but gives players only a few seconds."
        }
    } finally {
        if (-not $drainAnswered) {
            Invoke-DrainCallOff -PipeName $pipeName
        }
    }

    if ($calledOff) {
        throw 'The drain was called off, so nothing was installed. The servers are still running.'
    }
}


Assert-Elevated

if ($PSCmdlet.ParameterSetName -eq 'Install' -and -not (Test-Path -LiteralPath $ZipPath)) {
    throw "Release archive not found: $ZipPath"
}

# ---------------------------------------------------------------------------
# `D99`. A root no switch names is read from the registered service, whose
# command line this script wrote and is the record of where the install lives.
# Taken from the defaults instead, an upgrade run without -DataRoot pointed an
# install kept elsewhere at the default data root, where it started as a fresh
# install. A switch naming a different root than the service uses is warned
# about and then used, since moving an install is what passing one does.
#
# Decided here, before anything is written: every later step, the drain's
# config read included, uses these two values.
#
# Without ServiceRoots.ps1, in the archive or beside this script, nothing can be
# read, so a registered service needs both switches rather than a guess.
# ---------------------------------------------------------------------------

# ---------------------------------------------------------------------------
# -GrantFolder and -ClearPanelMfa act on one installed service. With more than one
# BedrockSentry service registered, a missing -ServiceName would fall to the default
# name and act on that install, which may not be the one meant (T419). Refused
# instead, naming the services, so the choice is the administrator's.
# ---------------------------------------------------------------------------

if ($PSCmdlet.ParameterSetName -ne 'Install' -and -not $PSBoundParameters.ContainsKey('ServiceName')) {
    $installed = @(
        Get-CimInstance Win32_Service -ErrorAction SilentlyContinue |
            Where-Object { $_.PathName -and $_.PathName -match 'BedrockSentry\.Host\.exe' } |
            ForEach-Object { $_.Name })

    if ($installed.Count -gt 1) {
        throw "More than one BedrockSentry service is installed ($($installed -join ', ')). Pass -ServiceName to say which one this is for."
    }
}

$installRootGiven = $PSBoundParameters.ContainsKey('InstallRoot')
$dataRootGiven = $PSBoundParameters.ContainsKey('DataRoot')

if ($serviceRootsHelper) {
    $roots = Resolve-SentryRoots -ServiceName $ServiceName `
        -CommandLine (Get-SentryServiceCommandLine -ServiceName $ServiceName) `
        -InstallRoot $InstallRoot -DataRoot $DataRoot `
        -InstallRootGiven $installRootGiven -DataRootGiven $dataRootGiven

    if ($roots.Refusal) {
        throw $roots.Refusal
    }

    $InstallRoot = $roots.InstallRoot
    $DataRoot = $roots.DataRoot

    Write-Host "Using install root $InstallRoot, $(Format-SentryRootSource -From $roots.InstallRootFrom -ParameterName '-InstallRoot' -ServiceName $ServiceName)."
    Write-Host "Using data root $DataRoot, $(Format-SentryRootSource -From $roots.DataRootFrom -ParameterName '-DataRoot' -ServiceName $ServiceName)."

    foreach ($disagreement in $roots.Disagreements) {
        Write-Warning "$disagreement This run points the service at the root the switch names and leaves the other where it is."
    }
}
elseif ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) -and -not ($installRootGiven -and $dataRootGiven)) {
    throw "ServiceRoots.ps1 is in neither the archive nor the folder this script is in, so the roots the service '$ServiceName' uses cannot be read. Pass -InstallRoot and -DataRoot."
}

# ---------------------------------------------------------------------------
# -GrantFolder (`D115` (b)). The broker grants the service only a folder it makes
# or one that is empty, since the folder it is asked for comes from a configuration
# the service's account writes. A folder that already holds files, such as a server
# adopted from outside the data root, is handed over here by an administrator who
# names it. The installed broker does the work, with the same path rules, a walk
# that refuses a link or a file with a second name, and its own record, so the
# folder can then be excluded from scanning. Nothing else is installed or changed.
# ---------------------------------------------------------------------------

if ($PSCmdlet.ParameterSetName -eq 'GrantFolder') {
    if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
        throw "The service '$ServiceName' is not installed, so there is nothing to grant '$GrantFolder' to."
    }

    $grantBroker = Join-Path $InstallRoot 'current\broker\BedrockSentry.Broker.exe'

    if (-not (Test-Path -LiteralPath $grantBroker -PathType Leaf)) {
        throw "$grantBroker is missing. A service installed with -NoBroker, or by a build older than the broker, has nothing to grant a folder with; install with the broker first."
    }

    & $grantBroker --grant-folder $GrantFolder --dataroot $DataRoot --service $ServiceName
    exit $LASTEXITCODE
}

# ---------------------------------------------------------------------------
# -ClearPanelMfa (`D125`). The way back in when the authenticator phone and its
# recovery codes are both gone. auth.json is readable by administrators only, so
# being able to run this is the proof of ownership the second step asked for. The
# service is stopped first because it holds the credential in memory and rewrites
# the file on its own changes; a new security stamp signs out every session that
# passed the old step. Comments or trailing commas in a hand-edited file stop it
# here, before anything is written.
# ---------------------------------------------------------------------------

function Clear-SentryPanelMfa {
    param([Parameter(Mandatory)] [string] $AuthFile)

    $json = [IO.File]::ReadAllText($AuthFile)
    $auth = $json | ConvertFrom-Json
    $admin = $null

    if ($auth.PSObject.Properties['Sentry']) {
        $admin = $auth.Sentry.PSObject.Properties['Admin']
    }

    if (-not $admin -or -not $admin.Value.PSObject.Properties['Mfa'] -or $null -eq $admin.Value.Mfa) {
        return $false
    }

    $admin.Value.PSObject.Properties.Remove('Mfa')
    $admin.Value | Add-Member -NotePropertyName SecurityStamp -NotePropertyValue ([Guid]::NewGuid().ToString('N')) -Force

    # Written over the existing file, which keeps its permissions; without a BOM,
    # as the service writes it.
    [IO.File]::WriteAllText($AuthFile, ($auth | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding($false)))
    return $true
}

if ($PSCmdlet.ParameterSetName -eq 'ClearPanelMfa') {
    if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
        throw "The service '$ServiceName' is not installed, so there is no panel sign-in to clear."
    }

    $authFile = Join-Path $DataRoot 'config\auth.json'

    if (-not (Test-Path -LiteralPath $authFile -PathType Leaf)) {
        throw "$authFile is missing, so there is no panel sign-in to clear."
    }

    Write-Host "Stopping $ServiceName to clear the authenticator-app sign-in step."
    $null = Stop-SentryServiceAndWait -ServiceName $ServiceName

    try {
        if (Clear-SentryPanelMfa -AuthFile $authFile) {
            Write-Host 'The authenticator-app step is off; every session is signed out. Sign in with the password, then set the step up again in Settings if you want it.'
        }
        else {
            Write-Host 'The authenticator-app step was already off. Nothing was changed.'
        }
    }
    finally {
        Start-Service -Name $ServiceName
    }

    exit 0
}

# ---------------------------------------------------------------------------
# The account the main service runs as (milestone 18.5). The broker, a second
# service, does the administrative work as LocalSystem over a closed list of
# operations; the main service runs as its own virtual account, NT SERVICE\<name>,
# or as a named account an administrator chooses.
#
# An install that runs as LocalSystem is not converted in place. Everything it
# wrote, outside the data root as well as in it, was written by SYSTEM, and a
# switch half made would leave a service that cannot read its own files. The data
# survives an uninstall (`T129`), so the way across is uninstall, then install.
# Decided here, before anything is written.
# ---------------------------------------------------------------------------

$virtualAccount = "NT SERVICE\$ServiceName"
$brokerName = "${ServiceName}Broker"
$registered = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
$keepAccount = $false

# By SID, not by name: LocalSystem has several spellings, and LocalService and
# NetworkService are shared by every service that runs as them, so none of the
# three is an account of this service's own.
$builtInSids = @('S-1-5-18', 'S-1-5-19', 'S-1-5-20')

function Get-SentryAccountSid {
    param([string] $Name)

    if ($Name -in @('LocalSystem', '.\LocalSystem')) {
        return 'S-1-5-18'
    }

    try {
        return (New-Object Security.Principal.NTAccount($Name)).Translate([Security.Principal.SecurityIdentifier]).Value
    }
    catch {
        return $null
    }
}

$registeredSid = $null

if ($registered -and $registered.StartName) {
    $registeredSid = Get-SentryAccountSid $registered.StartName
}

if ($registeredSid -eq 'S-1-5-18') {
    throw "The service '$ServiceName' runs as LocalSystem, and this build runs it under its own account with a second service for the administrative work. An install is not converted in place. Run uninstall.ps1 -ServiceName $ServiceName from this folder, which keeps worlds, backups and settings, then run this script again with the same roots."
}

if ($ServiceAccount -and ((Get-SentryAccountSid $ServiceAccount) -in $builtInSids)) {
    throw "-ServiceAccount cannot be LocalSystem, LocalService or NetworkService: the service runs as an account of its own. Leave the switch out for its virtual account."
}

if (-not $ServiceAccount -and $registered -and $registered.StartName -and $registered.StartName -ne $virtualAccount -and $registeredSid -notin $builtInSids) {
    # A named account chosen on an earlier run is kept, password and all.
    $ServiceAccount = $registered.StartName
    $keepAccount = $true
    Write-Host "Keeping the service's account $ServiceAccount."
}
elseif ($ServiceAccount -eq $virtualAccount) {
    $ServiceAccount = $null
}

$namedAccount = [bool] $ServiceAccount

if ($namedAccount -and -not (Get-SentryAccountSid $ServiceAccount)) {
    # The broker translates the name too, but a name that does not resolve should
    # stop the install here rather than half way through it.
    throw "The account '$ServiceAccount' does not exist on this machine or its domain."
}

if ($namedAccount -and -not $keepAccount) {
    if (-not $ServiceAccountPassword) {
        $credential = Get-Credential -UserName $ServiceAccount -Message "The password of $ServiceAccount, which the service will run as."

        if (-not $credential) {
            throw 'No password was given for -ServiceAccount.'
        }

        $ServiceAccountPassword = $credential.Password
    }

    # Both before anything is stopped: Windows takes a wrong password when the
    # service is set and refuses it only at the start, after the swap.
    Assert-SentryAccountPassword -Account $ServiceAccount -Password $ServiceAccountPassword
    Grant-SentryServiceLogonRight -Sid (New-Object Security.Principal.SecurityIdentifier(Get-SentryAccountSid $ServiceAccount))
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
        $shippedBytes = Read-ArchiveRootEntry -ArchivePath (Resolve-Path -LiteralPath $ZipPath).Path -Name 'install.ps1'

        if ($null -ne $shippedBytes -and $shippedBytes.Length -gt 0) {
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

# Written by an administrator alone, owned by Administrators, and readable by
# everyone. Beneath %ProgramFiles% that is what the folder inherits anyway; anywhere
# else, such as the root of a data volume, it may inherit Authenticated Users Modify,
# and then anyone on the machine could write policy.json (D45) or replace a version.
# The owner is set because a folder somebody else created first keeps its owner, and
# an owner may rewrite the list whatever it says.
function Protect-SentryFolder {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [switch] $ReadableByUsers
    )

    # Set-Acl follows a junction or mount point and would rewrite whatever folder it
    # names. Anything that can write beneath the parent could have made one here.
    if ((Get-Item -LiteralPath $Path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "$Path is a link to another folder, not a folder of its own. Remove the link, or pass a different path, and run this again."
    }

    $acl = Get-Acl -LiteralPath $Path
    $acl.SetAccessRuleProtection($true, $false)

    foreach ($rule in @($acl.Access)) {
        $acl.RemoveAccessRule($rule) | Out-Null
    }

    foreach ($sidType in @('LocalSystemSid', 'BuiltinAdministratorsSid')) {
        $sid = New-Object Security.Principal.SecurityIdentifier([Security.Principal.WellKnownSidType]::$sidType, $null)
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
            $sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }

    if ($ReadableByUsers) {
        $users = New-Object Security.Principal.SecurityIdentifier([Security.Principal.WellKnownSidType]::BuiltinUsersSid, $null)
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
            $users, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }

    $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier([Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)))
    Set-Acl -LiteralPath $Path -AclObject $acl
}

New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
Protect-SentryFolder -Path $InstallRoot -ReadableByUsers
Write-Host "Install root secured: $InstallRoot"

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
# copy of the release, tens of megabytes, every time (`T107`). Written beside what it
# cleans up, but a trap covers the whole script: a failure before this point runs it
# too, with $staging still $null from the top (`T340`). `break` rethrows, so a failure
# still fails with its own message.
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

$stagedBroker = Join-Path $staging 'broker\BedrockSentry.Broker.exe'

if (-not (Test-Path -LiteralPath $stagedBroker)) {
    throw "BedrockSentry.Broker.exe is missing from the archive (expected at $stagedBroker)."
}

# ---------------------------------------------------------------------------
# Data root. Granted to SYSTEM, Administrators and the service's account by the
# staged broker, run once as this elevated script: its rules (DataRootPreparation)
# are tested code rather than a second copy here. It refuses a root that already
# holds files somebody other than an administrator could write, since the grant
# is spread over the tree as an administrator, and it re-grants each secret file,
# which keeps a list of its own, through a handle that does not follow links.
#
# Before the drain, because a refusal must cost no outage. On an upgrade the root
# already carries the grant and only the secret files are written again.
# ---------------------------------------------------------------------------

$prepareArguments = @('--prepare-data-root', '--dataroot', $DataRoot, '--service', $ServiceName)

if ($namedAccount) {
    $prepareArguments += @('--account', $ServiceAccount)
}

& $stagedBroker @prepareArguments
Assert-LastExitCode 'Granting the data root'
Write-Host "Data root secured: $DataRoot"

# ---------------------------------------------------------------------------
# Event log sources, one per service. Must be created while elevated — a service
# that logs to a source that does not exist fails silently, and on Server Core the
# event log is the channel most likely to actually reach an operator.
# ---------------------------------------------------------------------------

foreach ($source in @($ServiceName, $brokerName)) {
    if (-not [System.Diagnostics.EventLog]::SourceExists($source)) {
        [System.Diagnostics.EventLog]::CreateEventSource($source, 'Application')
        Write-Host "Created event log source '$source'."
    }
}

# ---------------------------------------------------------------------------
# Service registration.
# ---------------------------------------------------------------------------

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$existingBroker = Get-Service -Name $brokerName -ErrorAction SilentlyContinue

if ($existing -and $existing.Status -ne 'Stopped') {
    # Before the stop, not after: once Stop-Service is issued the service
    # control manager is waiting, and the wrapper's shutdown path
    # deliberately hurries to fit inside that.
    if (-not $SkipDrain) {
        Invoke-GracefulDrain -DataRoot $DataRoot -ServiceName $ServiceName
    }

    Write-Host "Stopping $ServiceName..."

    # "Stopped" is the service control manager's opinion, and it arrives before the
    # process has necessarily exited. Windows keeps a process's mapped images until
    # it really goes, so a swap that starts on the SCM's word finds the executable
    # and its DLLs still locked and deletes only what is not mapped. Waiting for the
    # process itself is the difference between a clean replacement and a half-deleted
    # directory.
    #
    # MOVED INTO ServiceStop.ps1 AND SHARED WITH uninstall.ps1 (`T243`). The rule was
    # right and it existed here ONLY -- uninstall.ps1 deleted on the SCM's word and
    # left a half-removed install root with nothing printed. Two callers of one
    # Windows behaviour, taught once.
    # `D88`: one call, because the fallback defined near the top of this script
    # answers the same question when the helper was found in neither place. The branch that
    # used to be here stopped on the SCM's word and warned, which is the defect
    # this whole comment describes, wearing a disclaimer.
    if (-not (Stop-SentryServiceAndWait -ServiceName $ServiceName)) {
        Write-Warning "$ServiceName reports stopped but its process is still running; files it holds may not be replaceable."
    }
}

# After the main service, which asks the broker for its work until it stops. The
# broker runs from the same version folder, so it is stopped before the swap too.
if ($existingBroker -and $existingBroker.Status -ne 'Stopped') {
    Write-Host "Stopping $brokerName..."

    if (-not (Stop-SentryServiceAndWait -ServiceName $brokerName)) {
        Write-Warning "$brokerName reports stopped but its process is still running; files it holds may not be replaceable."
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
    # The service's own virtual account from the first moment it exists, so it
    # never runs as LocalSystem; the account a run asks for is set below, on every
    # run. Its administrative work is the broker's, registered after it.
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
        obj= $virtualAccount | Out-Null
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

#
# --broker pipe goes in here too, so the broker's registration below and the
# service's use of it are written by one run and can be changed by an
# administrator only.
$imagePath = '"{0}\BedrockSentry.Host.exe" --dataroot "{1}" --service "{2}"' -f $currentLink, $DataRoot, $ServiceName

if (-not $NoBroker) {
    $imagePath += ' --broker pipe'
}

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
# The main service's account and token (milestone 18.5), set on every run so an
# upgrade lands on what this run asks for. A run that names another account than
# the one the data root was granted to is refused by the grant, before anything
# stops: the old account could have left links in the tree. Changing the account
# is uninstall, then install.
# ---------------------------------------------------------------------------

if (-not $namedAccount) {
    & sc.exe config $ServiceName obj= $virtualAccount | Out-Null
    Assert-LastExitCode 'sc.exe config obj='
    Write-Host "$ServiceName runs as $virtualAccount."
}
elseif (-not $keepAccount) {
    # Through CIM rather than sc.exe, so the password is never on a command line
    # another process could read. The right to log on as a service, which sc.exe
    # and CIM do not grant, was granted before the drain.
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ServiceAccountPassword)

    try {
        $change = Invoke-CimMethod -InputObject (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'") -MethodName Change -Arguments @{
            StartName = $ServiceAccount
            StartPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
        }
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }

    if ($change.ReturnValue -ne 0) {
        throw "Setting $ServiceName to run as $ServiceAccount failed with Win32_Service.Change result $($change.ReturnValue)."
    }

    Write-Host "$ServiceName runs as $ServiceAccount."
}

# Only the privileges the service needs. Every service token is otherwise given
# SeImpersonatePrivilege and SeAssignPrimaryTokenPrivilege, a short path to SYSTEM,
# and the servers and backup commands it starts inherit its token.
& sc.exe privs $ServiceName 'SeChangeNotifyPrivilege/SeCreateGlobalPrivilege' | Out-Null
Assert-LastExitCode 'sc.exe privs'

# The service SID in the token on a named account as well as the virtual one, so
# the pipe lists can name it whichever account runs the service.
& sc.exe sidtype $ServiceName unrestricted | Out-Null
Assert-LastExitCode 'sc.exe sidtype'

# ---------------------------------------------------------------------------
# The broker (`D46`): LocalSystem, a closed list of operations on its own pipe, and
# only the main service's process may ask (docs/design.md section 12.4). Started at
# boot ahead of the main service, which starts delayed; a main service that starts
# first is refused its fixes, with the reason, until the broker answers.
# ---------------------------------------------------------------------------

if ($NoBroker) {
    if ($existingBroker) {
        & sc.exe delete $brokerName | Out-Null
        Assert-LastExitCode 'sc.exe delete (broker)'
        Write-Host "Removed the broker service '$brokerName'."
    }
}
else {
    if (-not $existingBroker) {
        & sc.exe create $brokerName `
            binPath= "`"$currentLink\broker\BedrockSentry.Broker.exe`"" `
            DisplayName= $brokerName `
            start= auto `
            obj= 'LocalSystem' | Out-Null
        Assert-LastExitCode 'sc.exe create (broker)'

        & sc.exe description $brokerName "Does the administrative work $ServiceName asks for." | Out-Null
        Assert-LastExitCode 'sc.exe description (broker)'

        Write-Host "Registered service '$brokerName'."
    }

    Set-ItemProperty `
        -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$brokerName" `
        -Name 'ImagePath' `
        -Value ('"{0}\broker\BedrockSentry.Broker.exe" --dataroot "{1}" --service "{2}"' -f $currentLink, $DataRoot, $ServiceName) `
        -Type ExpandString

    & sc.exe failure $brokerName reset= 86400 actions= restart/120000/restart/300000/restart/600000 | Out-Null
    Assert-LastExitCode 'sc.exe failure (broker)'
}

# ---------------------------------------------------------------------------
# Optional settings, written to policy.json in the install root, which survives
# updates. Never into the data root: the service's account can change anything
# there, a link included, and this script runs as an administrator.
# ---------------------------------------------------------------------------

$configDir = Join-Path $DataRoot 'config'

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

# D45. The switch lives in the install folder, which only an administrator can
# write, not in the data root's config, which the service account can: a gate
# kept where the thing it guards can write is a gate it can open. The service
# reads the file on every question, so either switch takes effect without a
# restart. An older installer's Sentry:Backups:AllowCustomCommand is not read.
$policyFile = Join-Path $InstallRoot 'policy.json'

if ($AllowRemoteSetup) {
    # Read beside Sentry:AllowRemoteSetup, which an older installer wrote into the
    # data root's config and which still counts.
    Set-ConfigValue -ConfigFile $policyFile -Path @('allowRemoteSetup') -Value $true

    Write-Warning 'Remote setup is enabled. The first-run wizard is reachable from the network until an administrator account is created. Complete setup promptly.'
}

if ($AllowCustomBackupCommand) {
    Set-ConfigValue -ConfigFile $policyFile -Path @('allowCustomBackupCommand') -Value $true

    Write-Warning "Custom backup commands are enabled. A command typed into a server's Backups tab will run on this machine as the account the service runs as, and anyone who can sign in to the panel can use it. To turn it back off, re-run with -DenyCustomBackupCommand, or set allowCustomBackupCommand to false in $policyFile."
}

if ($DenyCustomBackupCommand) {
    Set-ConfigValue -ConfigFile $policyFile -Path @('allowCustomBackupCommand') -Value $false

    Write-Host 'Custom backup commands are disabled. Any command already configured stays saved and stops running.'
}

if ($AllowPanelFolderGrants) {
    Set-ConfigValue -ConfigFile $policyFile -Path @('allowPanelFolderGrants') -Value $true

    Write-Warning "The panel may now hand the service a folder outside the data root through the broker, so anyone who can sign in to the panel can give the service a new or empty folder anywhere on this machine. To turn it back off, re-run with -DenyPanelFolderGrants, or set allowPanelFolderGrants to false in $policyFile."
}

if ($DenyPanelFolderGrants) {
    Set-ConfigValue -ConfigFile $policyFile -Path @('allowPanelFolderGrants') -Value $false

    Write-Host 'Folders outside the data root are granted only by -GrantFolder again. Folders already granted stay granted.'
}

# ---------------------------------------------------------------------------
# Start, and surface what the operator needs to finish setup.
# ---------------------------------------------------------------------------

if (-not $NoBroker) {
    Write-Host "Starting $brokerName..."
    Start-Service -Name $brokerName
}

Write-Host "Starting $ServiceName..."
Start-Service -Name $ServiceName

$tokenFile = Join-Path $configDir 'setup-token.txt'
$authFile = Join-Path $configDir 'auth.json'
$deadline = (Get-Date).AddSeconds(90)
$token = $null

while ((Get-Date) -lt $deadline) {
    if (Test-Path -LiteralPath $authFile) { break }

    if (Test-Path -LiteralPath $tokenFile) {
        # Read as an administrator from a folder the service's account can change,
        # so a link, which would print a file only an administrator may read, is
        # left unread.
        if (-not (Test-SentryPlainFile -Path $tokenFile)) { break }

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

    # Scoped to the service as well as the port. The port is read from a file the
    # service's account can write, so it could name a port another program listens
    # on; a rule that admits only the service's own processes opens nothing else.
    # The service SID it matches on is why sc.exe sidtype is set above.
    if (-not $existingRule) {
        New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow `
            -Protocol TCP -LocalPort $firewallPort -Service $ServiceName -Profile Any | Out-Null
        Write-Host "Opened TCP $firewallPort ($ruleName)."
    } else {
        # The guard used to stop at "a rule of this name exists", ignoring the
        # port it opens. So changing Sentry:Tls:Port and re-running with
        # -OpenFirewall left the old port open and the new one closed - the panel
        # unreachable, and a port nothing is listening on still open (`T139`).
        $openPort = ($existingRule | Get-NetFirewallPortFilter).LocalPort
        $existingRule | Get-NetFirewallServiceFilter | Set-NetFirewallServiceFilter -Service $ServiceName

        if ("$openPort" -ne "$firewallPort") {
            Set-NetFirewallRule -DisplayName $ruleName -LocalPort $firewallPort | Out-Null
            Write-Host "Moved $ruleName from TCP $openPort to TCP $firewallPort."
        } else {
            Write-Host "TCP $firewallPort is already open ($ruleName)."
        }
    }
}
