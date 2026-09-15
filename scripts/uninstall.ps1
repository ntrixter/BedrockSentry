<#
.SYNOPSIS
    Removes the BedrockSentry service and its binaries.

.DESCRIPTION
    Stops and deregisters the service, then deletes the install directory.

    It deliberately does NOT touch the data root. Worlds, backups, player data,
    certificates and configuration all live there, and an uninstaller that deletes
    a Minecraft world is a data-loss bug rather than a thorough cleanup. The path
    is printed at the end so anyone who genuinely wants it gone knows where to look.

.PARAMETER RemoveFirewallRule
    Also removes the inbound rule for the web UI, if install.ps1 created one.

.EXAMPLE
    .\uninstall.ps1
#>

[CmdletBinding()]
param(
    [string] $InstallRoot = "$env:ProgramFiles\BedrockSentry",

    [string] $DataRoot = "$env:ProgramData\BedrockSentry",

    [string] $ServiceName = 'BedrockSentry',

    [switch] $RemoveFirewallRule
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)

if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this from an elevated PowerShell session.'
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if ($service) {
    if ($service.Status -ne 'Stopped') {
        Write-Host "Stopping $ServiceName..."
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(120))
    }

    & sc.exe delete $ServiceName | Out-Null

    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe delete failed with exit code $LASTEXITCODE."
    }

    Write-Host "Removed service '$ServiceName'."
} else {
    Write-Host "Service '$ServiceName' is not registered."
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

if (Test-Path -LiteralPath $InstallRoot) {
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

if ([System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
    [System.Diagnostics.EventLog]::DeleteEventSource($ServiceName)
    Write-Host "Removed the event log source."
}

Write-Host ''
Write-Host 'BedrockSentry has been uninstalled.' -ForegroundColor Green
Write-Host ''

if (Test-Path -LiteralPath $DataRoot) {
    Write-Host 'Your data was left in place:'
    Write-Host "  $DataRoot"
    Write-Host ''
    Write-Host 'That folder holds configuration, certificates, player data and backups.'
    Write-Host 'Delete it by hand if you want a completely clean removal.'
}
