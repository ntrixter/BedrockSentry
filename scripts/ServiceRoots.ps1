<#
    Where an installed service keeps its binaries and its data (`D99`).

    install.ps1 writes the service's command line as
    "<InstallRoot>\current\BedrockSentry.Host.exe" --dataroot "<DataRoot>", so the
    registered service is the record of both roots. Both scripts used to take them
    from their own defaults instead: an upgrade run without -DataRoot pointed an
    install kept on another volume at the default data root, where it started as a
    fresh install, and uninstall.ps1 deleted the default install root whichever
    service it had just removed.

    Dot-sourced by install.ps1 and uninstall.ps1, and shipped beside them; see
    ReleaseContents.ps1. install.ps1 reads the release archive's copy first, and
    uninstall.ps1 falls back to the copy in the release the service runs from
    (`T336`). A script that finds it in neither place refuses to guess a root for a
    registered service that no switch names.
#>

Set-StrictMode -Version Latest

<#
    .SYNOPSIS
    The registered service's command line, '' when it cannot be read, or $null
    when no service of that name is registered.

    .DESCRIPTION
    Read from the registry value install.ps1 writes, unexpanded, so the string
    compared is the one that was written.
#>
function Get-SentryServiceCommandLine {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $ServiceName)

    if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
        return $null
    }

    $key = Get-Item -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -ErrorAction SilentlyContinue

    if (-not $key) {
        return ''
    }

    return [string] $key.GetValue('ImagePath', '', 'DoNotExpandEnvironmentNames')
}

<#
    .SYNOPSIS
    Reads the two roots out of a command line install.ps1 wrote.

    .DESCRIPTION
    Returns an object with InstallRoot and DataRoot, or $null when the executable
    is not <root>\current\BedrockSentry.Host.exe, since any other path says nothing
    about where a root is. DataRoot is $null when the command line carries no
    --dataroot, as an install older than `D36` wrote; the wrapper then uses its
    default.

    The executable is quoted, as install.ps1 writes it, or unquoted, as an install
    last upgraded to build.69 still carries it (`T341`). Windows runs an unquoted path
    by trying each prefix that ends at a space, so one with spaces in it, as under
    Program Files, is read up to the first prefix that names the Host, and never past
    an earlier prefix ending in .exe, which Windows would run instead. Read only up to
    its first space it named no root, and a plain upgrade was refused.
#>
function ConvertFrom-SentryCommandLine {
    [CmdletBinding()]
    param([AllowEmptyString()] [string] $CommandLine)

    if ([string]::IsNullOrWhiteSpace($CommandLine)) {
        return $null
    }

    if (-not ($CommandLine -match '^\s*"(?<exe>[^"]+)"(?<rest>.*)$' -or
              $CommandLine -match '^\s*(?<exe>(?:(?!\.exe\s)[^"])*?\\+current\\+BedrockSentry\.Host\.exe)(?<rest>(?:\s.*)?)$' -or
              $CommandLine -match '^\s*(?<exe>[^\s"]+)(?<rest>.*)$')) {
        return $null
    }

    $executable = $Matches['exe']
    $arguments = $Matches['rest']

    if ($executable -notmatch '^(?<root>.+?)\\+current\\+BedrockSentry\.Host\.exe$') {
        return $null
    }

    $installRoot = $Matches['root']
    $dataRoot = $null

    if ($arguments -match '(?:^|\s)--dataroot(?:=|\s+)(?:"(?<quoted>[^"]+)"|(?<bare>[^\s"]+))') {
        $dataRoot = $Matches['quoted']

        if (-not $dataRoot) {
            $dataRoot = $Matches['bare']
        }
    }

    return [pscustomobject]@{
        InstallRoot = $installRoot
        DataRoot    = $dataRoot
    }
}

function Test-SentrySamePath {
    param([string] $Left, [string] $Right)

    return $Left.TrimEnd('\', '/') -ieq $Right.TrimEnd('\', '/')
}

<#
    .SYNOPSIS
    Decides each root: the switch when one was passed, else the registered
    service's, else the default.

    .DESCRIPTION
    CommandLine is Get-SentryServiceCommandLine's answer. Returns the two roots,
    where each came from ('switch', 'service' or 'default'), a sentence per switch
    that names a different root than the service uses, and a refusal when the
    service is registered with a command line install.ps1 did not write and a root
    it would have named was not passed. The callers differ on a disagreement:
    install.ps1 warns and uses the switch, uninstall.ps1 removes nothing.
#>
function Resolve-SentryRoots {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $ServiceName,
        $CommandLine,
        [Parameter(Mandatory)] [string] $InstallRoot,
        [Parameter(Mandatory)] [string] $DataRoot,
        [bool] $InstallRootGiven,
        [bool] $DataRootGiven
    )

    $result = [pscustomobject]@{
        InstallRoot     = $InstallRoot
        DataRoot        = $DataRoot
        InstallRootFrom = 'default'
        DataRootFrom    = 'default'
        Disagreements   = @()
        Refusal         = $null
    }

    if ($InstallRootGiven) { $result.InstallRootFrom = 'switch' }
    if ($DataRootGiven) { $result.DataRootFrom = 'switch' }

    # Not registered: a fresh install, or a re-run after the service was removed.
    # Nothing records the roots any more, so the switches and defaults stand.
    if ($null -eq $CommandLine) {
        return $result
    }

    $fromService = ConvertFrom-SentryCommandLine -CommandLine $CommandLine

    if (-not $fromService) {
        if (-not ($InstallRootGiven -and $DataRootGiven)) {
            $result.Refusal = "The service '$ServiceName' is registered, but its command line is not one install.ps1 writes, so it does not say where its roots are: $CommandLine. Pass -InstallRoot and -DataRoot."
        }

        return $result
    }

    if ($InstallRootGiven) {
        if (-not (Test-SentrySamePath $InstallRoot $fromService.InstallRoot)) {
            $result.Disagreements += "The service '$ServiceName' runs from $($fromService.InstallRoot), but -InstallRoot names $InstallRoot."
        }
    } else {
        $result.InstallRoot = $fromService.InstallRoot
        $result.InstallRootFrom = 'service'
    }

    # A command line with no --dataroot runs on the default data root, so without a
    # switch the default stands, and a switch has nothing named to disagree with.
    if ($DataRootGiven) {
        if ($fromService.DataRoot -and -not (Test-SentrySamePath $DataRoot $fromService.DataRoot)) {
            $result.Disagreements += "The service '$ServiceName' keeps its data in $($fromService.DataRoot), but -DataRoot names $DataRoot."
        }
    } elseif ($fromService.DataRoot) {
        $result.DataRoot = $fromService.DataRoot
        $result.DataRootFrom = 'service'
    }

    return $result
}

<#
    .SYNOPSIS
    Where a root came from, for a line a person reads.
#>
function Format-SentryRootSource {
    param(
        [Parameter(Mandatory)] [string] $From,
        [Parameter(Mandatory)] [string] $ParameterName,
        [Parameter(Mandatory)] [string] $ServiceName
    )

    if ($From -eq 'service') {
        return "read from the service '$ServiceName'"
    }

    if ($From -eq 'switch') {
        return "passed as $ParameterName"
    }

    return 'the default'
}
