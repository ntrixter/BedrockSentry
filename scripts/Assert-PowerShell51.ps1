<#
.SYNOPSIS
    Fails if any script uses a cmdlet parameter the running engine does not have.

.DESCRIPTION
    Run this under Windows PowerShell 5.1 and it answers the question `T157`
    was: does every parameter these scripts pass actually bind on the engine a
    real server runs them with?

    `ConvertFrom-Json -AsHashtable` sat in `install.ps1` for weeks. It is
    PowerShell 6+, it is invisible to a parse check because the syntax is
    perfectly valid, and it would have thrown on a Server Core upgrade after
    the junction was repointed and before Start-Service - leaving the new build
    installed and the service stopped, under an error naming neither the switch
    nor the installer.

    `install.ps1:357-358` already carried the rule in a comment - "this script
    has to run under Windows PowerShell 5.1" - and line 745 contradicted it.
    That is this project's recurring defect class: a rule stated twice and
    enforced once. This script is the enforcement.

    A parse check cannot find this. `#requires -Version 5.1` cannot either: it
    means *at least* 5.1, so pwsh 7 satisfies it, and this is runtime parameter
    binding rather than version gating. The only thing that finds it is asking
    the 5.1 engine itself, which is why `ci.yml` runs this step with
    `shell: powershell` while every other PowerShell step uses `shell: pwsh`.

.NOTES
    Two things it deliberately does not do.

    It does not resolve commands it cannot see - external executables like
    sc.exe, and functions defined inside the script under test - because a
    parameter it cannot check is not a parameter it should accuse.

    It does not treat "unknown to this engine" as proof on its own. Provider
    dynamic parameters are absent from a bare (Get-Command).Parameters under
    BOTH engines - Set-ItemProperty -Type is the worked example, and it
    demonstrably runs on a real install (`D36`/`T110`). Those live in
    $KnownDynamicParameters below, each with a Why, following
    ReleaseContents.ps1: whoever reads a failure has to be able to tell a real
    find from a stale rule, or they will delete the entry instead of fixing the
    script.
#>

[CmdletBinding()]
param(
    [string] $ScriptDirectory
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot IS EMPTY INSIDE A param() DEFAULT under Windows PowerShell 5.1
# invoked as `powershell.exe -File`, so resolving the root there binds an empty
# string and Split-Path/Join-Path throw before the script does any work at all
# (T199). It is populated normally in the BODY on both engines, which is why
# Assert-Anonymous.ps1 - the one script that resolves it below its param block -
# was the only one of the four that ran. Resolve here, not up there.
#
# The failure is narrower than "a 5.1 difference": 5.1 AND -File. The call
# operator works on the same engine, and `shell: pwsh` in CI works, so nothing
# automated saw it for as long as it existed. private-checks.yml now runs each
# script through `powershell.exe -File` with no arguments, which is the only
# invocation that reproduces it.
if (-not $ScriptDirectory) { $ScriptDirectory = Join-Path $PSScriptRoot '' }

# Provider dynamic parameters: real, but invisible to Get-Command on both
# engines, so "unknown here" says nothing about the version. Give every entry a
# Why.
$KnownDynamicParameters = @(
    @{
        Command   = 'Set-ItemProperty'
        Parameter = 'Type'
        Why       = 'Registry provider dynamic parameter. Absent from a bare (Get-Command).Parameters under BOTH 5.1 and 7, which is the tell that it is not a version difference. The line it is on runs on every real install (D36/T110).'
    }
)

function Test-KnownDynamic {
    param([string] $Command, [string] $Parameter)

    foreach ($known in $KnownDynamicParameters) {
        if ($known.Command -eq $Command -and $known.Parameter -eq $Parameter) {
            return $true
        }
    }

    return $false
}

$findings = @()
$checked = 0

# Usages this engine could not judge, because the cmdlet does not exist here. On
# Linux that is New-Service, Get-CimInstance and the rest of the Windows-only
# surface - which is exactly where a 5.1 difference is most likely to be hiding.
# Counted and printed rather than inferred: "152 checked, all bind" read as though
# it had looked at everything, and on Linux it had seen about three-quarters of
# what real Windows PowerShell sees (152 against 197, measured 2026-09-10).
$unresolved = 0
$unresolvedCommands = @{}

foreach ($file in Get-ChildItem -Path (Join-Path $ScriptDirectory '*.ps1')) {
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
        $file.FullName, [ref] $null, [ref] $errors)

    if ($errors) {
        $findings += [pscustomobject]@{
            File = $file.Name; Line = 0; Command = ''; Parameter = ''
            Reason = 'the file does not parse'
        }
        continue
    }

    # Commands the script defines itself. Get-Command cannot see them - this
    # process never dot-sourced the file - so they are not evidence of anything.
    $ownFunctions = @(
        $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true) |
            ForEach-Object { $_.Name })

    $commands = $ast.FindAll(
        { $args[0] -is [System.Management.Automation.Language.CommandAst] }, $true)

    foreach ($command in $commands) {
        $name = $command.GetCommandName()

        # An invocation through a variable or expression - & $exe. Nothing to ask.
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        if ($ownFunctions -contains $name) { continue }

        $resolved = Get-Command -Name $name -ErrorAction SilentlyContinue
        if (-not $resolved) {
            # Count the parameter usages that go unjudged, not the commands: the
            # usages are what the summary line claims to have checked.
            foreach ($element in $command.CommandElements) {
                if ($element -is [System.Management.Automation.Language.CommandParameterAst]) {
                    $unresolved++
                }
            }

            $unresolvedCommands[$name] = $true
            continue
        }

        # An external executable takes whatever it likes; its "parameters" are
        # argv, not PowerShell's.
        if ($resolved.CommandType -eq 'Application') { continue }

        $valid = @()
        foreach ($entry in $resolved.Parameters.GetEnumerator()) {
            $valid += $entry.Key
            foreach ($alias in $entry.Value.Aliases) { $valid += $alias }
        }

        if ($valid.Count -eq 0) { continue }

        foreach ($element in $command.CommandElements) {
            if ($element -isnot [System.Management.Automation.Language.CommandParameterAst]) { continue }

            $parameter = $element.ParameterName
            $checked++

            # Exact, then the legal unambiguous prefix PowerShell itself accepts.
            # Named $hits, not $matches: $Matches is an automatic variable that
            # -match writes to, and quietly reusing it is the kind of thing this
            # script exists to stop other scripts doing.
            $hits = @($valid | Where-Object { $_ -eq $parameter })
            if ($hits.Count -eq 0) {
                $hits = @($valid | Where-Object { $_ -like "$parameter*" })
            }

            if ($hits.Count -ge 1) { continue }
            if (Test-KnownDynamic -Command $resolved.Name -Parameter $parameter) { continue }

            $findings += [pscustomobject]@{
                File      = $file.Name
                Line      = $element.Extent.StartLineNumber
                Command   = $name
                Parameter = $parameter
                Reason    = 'this engine has no such parameter'
            }
        }
    }
}

$engine = "PowerShell $($PSVersionTable.PSVersion) ($($PSVersionTable.PSEdition))"

if ($findings.Count -gt 0) {
    foreach ($finding in $findings) {
        Write-Host "::error file=scripts/$($finding.File),line=$($finding.Line)::$($finding.Command) -$($finding.Parameter): $($finding.Reason) - $engine"
    }

    throw "$($findings.Count) parameter usage(s) will not bind under $engine. If one is a provider dynamic parameter rather than a version difference, add it to `$KnownDynamicParameters with a Why - but check first that it is unknown to BOTH engines, which is the tell."
}

if ($unresolved -gt 0) {
    $names = ($unresolvedCommands.Keys | Sort-Object) -join ', '

    # Not a warning: on Linux this is expected and unavoidable, and a warning that
    # fires on every ordinary run is one nobody reads. It has to be visible, though,
    # or the line below overstates the coverage.
    Write-Host "${engine}: $unresolved parameter usage(s) NOT checked - $($unresolvedCommands.Count) command(s) this engine cannot resolve: $names"
    Write-Host "${engine}: those are the usages most likely to carry a 5.1 difference. Only the Windows run covers them."
}

Write-Host "${engine}: $checked parameter usages checked, all bind; $unresolved not checked."
