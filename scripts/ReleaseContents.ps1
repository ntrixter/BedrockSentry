<#
.SYNOPSIS
    What every BedrockSentry release archive must contain, and the check that
    enforces it.

.DESCRIPTION
    Dot-source this and call Assert-ReleaseArchive on the finished zip. It is
    dot-sourced by both scripts/package-local.ps1 and the release workflow, which
    is the point: the list lives in one place and both packaging paths are held
    to it.

    This exists because the licence went missing. Releases were published
    self-contained - carrying the .NET runtime, SQLitePCLRaw and the SQLite build
    it packages - for months with no LICENSE and no third-party notices in the
    zip at all, while the project was documented as MIT throughout. A rule
    written in prose is a rule nobody runs. This one fails the release.

    Two rules for editing it. Add to $RequiredReleaseEntries rather than to a
    comment somewhere, and give the entry a Why - when this check fires, the
    person reading the failure needs to know whether the file matters or whether
    the check is stale. An entry with no reason is one somebody will delete.

    It deliberately does not enumerate the runtime DLLs. Those change with every
    .NET servicing update and pinning them here would produce a check that fails
    for the wrong reason; the publish either produced a working self-contained
    tree or it did not, and Host.exe missing is the symptom that catches it.
#>

$script:RequiredReleaseEntries = @(
    @{
        Path = 'LICENSE'
        Why  = "MIT asks that the copyright and permission notice travel with every copy. A release is a copy."
    }
    @{
        Path = 'THIRD-PARTY-NOTICES.txt'
        Why  = "A self-contained publish redistributes the .NET runtime, SQLitePCLRaw and SQLite. MIT and Apache-2.0 both ask for their notice to come along, and parts of the Windows runtime are under the .NET Library License, which the notices must name."
    }
    @{
        Path = 'version.txt'
        Why  = "install.ps1 reads it to name the version directory, and the health endpoint reports the same string so the updater can confirm what came up."
    }
    @{
        Path = 'install.ps1'
        Why  = "The archive is the whole delivery. A zip somebody cannot install from is not a release."
    }
    @{
        Path = 'uninstall.ps1'
        Why  = "Shipping a service with no supported way to remove it is not acceptable, and T129-UninstallKeepsData is the proof that this one is safe."
    }
    @{
        Path = 'ServiceStop.ps1'
        Why  = "install.ps1 and uninstall.ps1 both dot-source it, so a release without it leaves both able to delete files the stopping service still holds open. T243 is what that costs: a half-removed install root and nothing printed. Both degrade with a warning rather than throwing when it is absent, which is exactly why its absence would otherwise be silent."
    }
    @{
        Path = 'ServiceRoots.ps1'
        Why  = "install.ps1 and uninstall.ps1 both dot-source it to read the install and data roots from the registered service (D99). Without it both refuse to act on a registered service unless every root is passed, so its absence stops an upgrade or an uninstall that names none."
    }
    @{
        Path = 'stdin-spike.ps1'
        Why  = "A field diagnostic. 'Can the wrapper still talk to bedrock_server.exe' is worth being answerable on a box that only ever received a zip."
    }
    @{
        Path = 'BedrockSentry.Host.exe'
        Why  = "The wrapper itself. Its absence means the publish silently produced nothing."
    }
    @{
        Path = 'updater/BedrockSentry.Updater.exe'
        Why  = "Self-update swaps files the running service is holding open, so the helper has to be in the build being installed - not fetched later."
    }
    @{
        Path = 'broker/BedrockSentry.Broker.exe'
        Why  = "The privilege broker (D46): install.ps1 runs it to grant the data root before anything else changes, and registers it as the service that does the administrative work. Without it the install stops before the old build is touched."
    }
    @{
        Path = 'companion-pack/manifest.json'
        Why  = "The companion pack is added to the add-on library from this folder at every start (D93). Without the manifest there is no pack, and the wrapper only logs a warning."
    }
    @{
        Path = 'companion-pack/scripts/main.js'
        Why  = "The companion pack's script. A manifest without it is a pack that loads and reports nothing."
    }
    @{
        Path = 'companion-pack/scripts/lib.js'
        Why  = "main.js imports it. Without it the script fails to load, so the pack reports nothing and registers no commands (D106)."
    }
    @{
        Path = 'companion-resources/manifest.json'
        Why  = "The companion's resource pack is added to the add-on library from this folder at every start and switched on with the companion (D109). Without the manifest there is no pack, and outlines are not drawn."
    }
    @{
        Path = 'companion-resources/particles/outline_line.json'
        Why  = "The particle the companion draws outlines with. A resource pack without it draws nothing."
    }
    @{
        Path = 'companion-resources/textures/particle/bedrocksentry_line.png'
        Why  = "The particle's texture. Without it the game draws the line with its missing-texture pattern."
    }
)

# Runtime files that are not MIT (Microsoft's license-information-windows.md). When
# the archive carries one, anywhere in it, THIRD-PARTY-NOTICES.txt must name it.
# Keyed on the file name rather than pinned as required entries, for the reason the
# header gives: a servicing update may move or drop one, and that is not a failure.
$script:NoticedRuntimeFiles = @(
    @{
        Name = 'coreclr.dll'
        Why  = "Microsoft licenses it under the .NET Library License, not MIT, so the notices must say which terms cover it."
    }
    @{
        Name = 'Microsoft.DiaSymReader.Native.amd64.dll'
        Why  = "Under the .NET Library License too, by the same list."
    }
)

<#
.SYNOPSIS
    Throws unless the archive contains everything a release must contain.

.PARAMETER ZipPath
    The finished archive. Checked rather than the staging directory on purpose:
    the zip is what people download, and it is the only thing that proves the
    packaging step did what the copy statements said it would.
#>
function Assert-ReleaseArchive {
    param([Parameter(Mandatory = $true)][string] $ZipPath)

    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ZipPath).Path)

    try {
        # Zip entries always use forward slashes, whatever built them.
        $entries = @{}
        foreach ($entry in $archive.Entries) {
            $entries[$entry.FullName.Replace('\', '/')] = $entry.Length
        }

        $problems = @()

        foreach ($required in $script:RequiredReleaseEntries) {
            $path = $required.Path

            if (-not $entries.ContainsKey($path)) {
                $problems += "  MISSING  $path`n           $($required.Why)"
                continue
            }

            # A zero-byte licence passes a presence check and satisfies nobody.
            if ($entries[$path] -le 0) {
                $problems += "  EMPTY    $path`n           $($required.Why)"
            }
        }

        # A runtime file under other terms must be named in the notices that ship.
        $noticesEntry = $archive.GetEntry('THIRD-PARTY-NOTICES.txt')
        if ($null -ne $noticesEntry -and $noticesEntry.Length -gt 0) {
            $reader = New-Object System.IO.StreamReader($noticesEntry.Open())
            try { $notices = $reader.ReadToEnd() } finally { $reader.Dispose() }

            foreach ($noticed in $script:NoticedRuntimeFiles) {
                $name = $noticed.Name
                $shipped = @($entries.Keys | Where-Object { ($_ -split '/')[-1] -eq $name })

                if ($shipped.Count -gt 0 -and $notices.IndexOf($name, [System.StringComparison]::Ordinal) -lt 0) {
                    $problems += "  UNNAMED  $name (in THIRD-PARTY-NOTICES.txt)`n           $($noticed.Why)"
                }
            }
        }

        if ($problems.Count -gt 0) {
            throw (
                "This archive is missing things every release must carry:`n`n" +
                ($problems -join "`n") +
                "`n`nAdd them to the packaging step (an UNNAMED file goes in THIRD-PARTY-NOTICES.txt), or - if one genuinely no longer belongs " +
                "in a release - remove it from `$RequiredReleaseEntries in " +
                "scripts/ReleaseContents.ps1 and say why in the commit."
            )
        }

        Write-Host "Release contents verified: $($script:RequiredReleaseEntries.Count) required entries present in $(Split-Path -Leaf $ZipPath)."
    }
    finally {
        $archive.Dispose()
    }
}
