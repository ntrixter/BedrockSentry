<#
.SYNOPSIS
    Builds a BedrockSentry release zip on your own machine.

.DESCRIPTION
    Produces exactly what the CI workflow produces, so install.ps1 cannot tell the
    difference. Use it to test a change without publishing a release.

    Requires the .NET 10 SDK on the machine running this script — but NOT on the
    machine the resulting zip gets installed on. The output is self-contained.

    It also runs on Linux or macOS under PowerShell 7, and the archive passes the
    same contents check there. release.yml builds on Windows, and no test installs
    a zip built anywhere else.

.PARAMETER Version
    Version to stamp. Defaults to the VersionPrefix in Directory.Build.props with a
    '-local' suffix, so a hand-built zip is never mistaken for a CI build.

.PARAMETER OutputDirectory
    Where the zip is written. A relative path, the default '.' included, resolves
    against the repository root.

.PARAMETER Channel
    The build channel stamped into the wrapper: local, build or stable. Defaults
    to local.

.EXAMPLE
    .\scripts\package-local.ps1
    # -> bedrocksentry-0.5.0-local_<sha>.zip in the repository root
#>

[CmdletBinding()]
param(
    [string] $Version,

    [string] $OutputDirectory = '.',

    [ValidateSet('local', 'build', 'stable')]
    [string] $Channel = 'local'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

try {
    if (-not $Version) {
        # XPath rather than $xml.Project.PropertyGroup.VersionPrefix: the props file
        # carries several PropertyGroup elements and only one of them holds the
        # version, so walking the property across all of them asks the other two for
        # something they have not got — which Set-StrictMode above turns from a
        # harmless null into a terminating error. SelectSingleNode just returns
        # nothing when there is no match, which is what the check below expects.
        $props = [xml](Get-Content -Raw (Join-Path $repoRoot 'Directory.Build.props'))
        $prefixNode = $props.SelectSingleNode('/Project/PropertyGroup/VersionPrefix')

        if (-not $prefixNode) { throw 'VersionPrefix not found in Directory.Build.props.' }

        $Version = "$($prefixNode.InnerText.Trim())-local"
    }

    $sha = (& git rev-parse --short=7 HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $sha) { $sha = 'nogit' }

    $identity = "$Version+$sha"

    Write-Host "Packaging $identity (channel: $Channel)" -ForegroundColor Cyan

    $stage = Join-Path ([System.IO.Path]::GetTempPath()) ("bedrocksentry-stage-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage -Force | Out-Null

    try {
        # --self-contained is explicit: since .NET 8, passing -r alone quietly
        # produces a framework-dependent publish that needs a runtime installed on
        # the target machine.
        $common = @(
            '-c', 'Release'
            '-r', 'win-x64'
            '--self-contained', 'true'
            "-p:Version=$Version"
            "-p:BedrockSentryChannel=$Channel"
            # As in release.yml: maps source paths to /_/ in the binaries and PDBs, so
            # no absolute path of the building machine (its user folder among them)
            # reaches the package or a logged stack trace.
            '-p:ContinuousIntegrationBuild=true'
        )

        Write-Host 'Publishing host...'
        & dotnet publish src/BedrockSentry.Host/BedrockSentry.Host.csproj @common -o $stage
        if ($LASTEXITCODE -ne 0) { throw "Publishing the host failed ($LASTEXITCODE)." }

        Write-Host 'Publishing updater...'
        & dotnet publish src/BedrockSentry.Updater/BedrockSentry.Updater.csproj @common -o (Join-Path $stage 'updater')
        if ($LASTEXITCODE -ne 0) { throw "Publishing the updater failed ($LASTEXITCODE)." }

        # Its own folder, as in release.yml: two self-contained publishes in one folder
        # would overwrite each other's copies of the libraries they share.
        Write-Host 'Publishing broker...'
        & dotnet publish src/BedrockSentry.Broker/BedrockSentry.Broker.csproj @common -o (Join-Path $stage 'broker')
        if ($LASTEXITCODE -ne 0) { throw "Publishing the broker failed ($LASTEXITCODE)." }

        # install.ps1 reads this to name the version directory, and the running
        # service reports the same string from its health endpoint.
        [System.IO.File]::WriteAllText((Join-Path $stage 'version.txt'), $identity)

        Copy-Item (Join-Path $repoRoot 'scripts/install.ps1') -Destination $stage
        Copy-Item (Join-Path $repoRoot 'scripts/uninstall.ps1') -Destination $stage
        # Both of the two above dot-source it, and both DEGRADE with a warning
        # rather than throwing when it is absent (`T243`) -- so leaving it out of
        # the archive produces an install that quietly stops waiting for the
        # service's process to exit. ReleaseContents.ps1 names it for that reason
        # and Assert-ReleaseArchive refuses a zip without it.
        Copy-Item (Join-Path $repoRoot 'scripts/ServiceStop.ps1') -Destination $stage
        # Dot-sourced by the same two to read the roots from the registered service
        # (`D99`); without it both refuse a registered service that no switch names.
        Copy-Item (Join-Path $repoRoot 'scripts/ServiceRoots.ps1') -Destination $stage
        # Same as release.yml: the wrapper's licence and the third-party notices for
        # everything a self-contained publish redistributes. This script's whole
        # point is to produce what CI produces, so it ships them too.
        Copy-Item (Join-Path $repoRoot 'LICENSE') -Destination $stage
        Copy-Item (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.txt') -Destination $stage
        # Ships too: it is a field diagnostic, not just a milestone 4 exercise.
        # "Can the wrapper still talk to bedrock_server.exe" is a question worth
        # being able to answer on a machine that only ever received a zip.
        Copy-Item (Join-Path $repoRoot 'scripts/stdin-spike.ps1') -Destination $stage

        Add-Type -AssemblyName System.IO.Compression.FileSystem

        $zipName = "bedrocksentry-$($identity.Replace('+','_')).zip"
        $zipPath = Join-Path (Resolve-Path $OutputDirectory).Path $zipName

        if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $stage,
            $zipPath,
            [System.IO.Compression.CompressionLevel]::Optimal,
            $false)

        # The same check CI runs, against the same manifest. This script exists to
        # produce what CI produces, so it has to fail where CI would fail.
        . (Join-Path $PSScriptRoot 'ReleaseContents.ps1')
        Assert-ReleaseArchive -ZipPath $zipPath

        $size = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
        $hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()

        Write-Host ''
        Write-Host "$zipName  (${size} MB)" -ForegroundColor Green
        Write-Host "sha256: $hash"
        Write-Host ''
        Write-Host 'Copy it to the server and install with:'
        Write-Host "  .\install.ps1 -ZipPath .\$zipName"
    }
    finally {
        if (Test-Path -LiteralPath $stage) {
            Remove-Item -LiteralPath $stage -Recurse -Force
        }
    }
}
finally {
    Pop-Location
}
