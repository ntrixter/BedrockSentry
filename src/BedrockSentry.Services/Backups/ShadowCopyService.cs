using System.Text.Json;
using BedrockSentry.Core.Backups;
using BedrockSentry.Services.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Backups;

/// <summary>
/// Mapping a live path to the same path inside a shadow copy.
/// </summary>
/// <remarks>
/// Pure, and separate from the service, because this is the part that is easy to
/// get subtly wrong and impossible to notice: a mapping that is off by a
/// separator produces "file not found" for every file in the world, which reads
/// exactly like a snapshot that did not capture anything.
/// </remarks>
public static class ShadowCopyPaths
{
    /// <summary>
    /// The volume root of a path, in the form <c>Win32_ShadowCopy</c> wants.
    /// </summary>
    /// <remarks>
    /// Windows requires a trailing separator here — <c>"C:\"</c>, not
    /// <c>"C:"</c> — and answers a bare drive letter with an unhelpful generic
    /// failure rather than saying so.
    /// </remarks>
    public static string VolumeRootOf(string fullPath)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(fullPath));

        if (string.IsNullOrEmpty(root))
        {
            throw new InvalidOperationException(
                $"'{fullPath}' has no volume root, so it cannot be shadow-copied.");
        }

        return root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// Where <paramref name="fullPath"/> lives inside
    /// <paramref name="deviceObject"/>.
    /// </summary>
    /// <remarks>
    /// The device object stands in for the volume root, so the mapping is
    /// "strip the root, graft on the device". A UNC path has no shadow copy to
    /// be in and is refused rather than silently mapped to something that does
    /// not exist.
    /// </remarks>
    public static string Inside(string deviceObject, string fullPath)
    {
        var absolute = Path.GetFullPath(fullPath);
        var root = VolumeRootOf(absolute);

        if (absolute.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{fullPath}' is on a network share, which cannot be shadow-copied from here.");
        }

        var relative = absolute[root.Length..].TrimStart(Path.DirectorySeparatorChar);

        return relative.Length == 0
            ? deviceObject.TrimEnd(Path.DirectorySeparatorChar)
            : deviceObject.TrimEnd(Path.DirectorySeparatorChar)
              + Path.DirectorySeparatorChar
              + relative;
    }
}

/// <summary>
/// Volume Shadow Copies through WMI, driven from PowerShell.
/// </summary>
/// <remarks>
/// <para>
/// <c>Win32_ShadowCopy.Create</c> rather than <c>vssadmin create shadow</c>,
/// which exists only on Server SKUs, or <c>diskshadow</c>, which is a scripting
/// language of its own with prose output to parse. The WMI call returns a shadow
/// id directly and the object it names carries the device path, so nothing here
/// depends on the wording of a message that gets localised.
/// </para>
/// <para>
/// Called through <c>Invoke-CimMethod</c> rather than the older
/// <c>[wmiclass]</c> accelerator. Both work under <c>powershell.exe</c>, which is
/// what the runner starts today, but only the CIM cmdlets work under PowerShell
/// 7 — so this survives that move instead of failing at the one call nobody can
/// test until it runs on a real server.
/// </para>
/// <para>
/// <c>ClientAccessible</c> is the context every general-purpose backup tool
/// uses: a persistent, read-only snapshot that survives until it is deleted, as
/// opposed to the <c>Backup</c> contexts that expect a requester holding the VSS
/// session open for the life of the copy. This process is not that requester,
/// and pretending otherwise leaves shadow copies that vanish mid-read.
/// </para>
/// </remarks>
public sealed class ShadowCopyService : IShadowCopyService
{
    private readonly IPowerShellRunner _powerShell;
    private readonly ILogger<ShadowCopyService> _log;

    public ShadowCopyService(IPowerShellRunner powerShell, ILogger<ShadowCopyService> log)
    {
        _powerShell = powerShell;
        _log = log;
    }

    /// <summary>The script that takes the snapshot. Public so a test can parse it.</summary>
    public static string CreateScript(string volumeRoot) =>
        $$"""
        $ErrorActionPreference = 'Stop'
        try {
          $result = Invoke-CimMethod -ClassName Win32_ShadowCopy -MethodName Create -Arguments @{
            Volume = '{{Escape(volumeRoot)}}'
            Context = 'ClientAccessible'
          }

          if ($result.ReturnValue -ne 0) {
            Write-Error "Win32_ShadowCopy.Create returned $($result.ReturnValue)."
          }

          $shadow = Get-CimInstance -ClassName Win32_ShadowCopy |
            Where-Object { $_.ID -eq $result.ShadowID }

          if (-not $shadow) {
            Write-Error "Shadow copy $($result.ShadowID) was created but cannot be found."
          }

          [pscustomobject]@{
            ShadowId = $shadow.ID
            DeviceObject = $shadow.DeviceObject
          } | ConvertTo-Json -Compress
        } catch { Write-Error $_.Exception.Message }
        """;

    /// <summary>The script that releases one. Public so a test can parse it.</summary>
    public static string ReleaseScript(string shadowId) =>
        $$"""
        $ErrorActionPreference = 'Stop'
        try {
          Get-CimInstance -ClassName Win32_ShadowCopy |
            Where-Object { $_.ID -eq '{{Escape(shadowId)}}' } |
            Remove-CimInstance
        } catch { Write-Error $_.Exception.Message }
        """;

    public async Task<ShadowCopy> CreateAsync(
        string instanceId, string volumeRoot, CancellationToken cancellationToken = default)
    {
        var result = await _powerShell
            .RunAsync(CreateScript(volumeRoot), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            // The full rendering goes to the log, where the position block is
            // worth having; the operator gets the reason (`T127`).
            _log.LogWarning(
                "Could not take a shadow copy of {VolumeRoot}: {Error}", volumeRoot, result.Error);

            throw new InvalidOperationException(
                $"Could not take a shadow copy of {volumeRoot}: {Describe(result.Reason)}");
        }

        return Parse(result.Output, volumeRoot);
    }

    /// <summary>
    /// Reads what the script printed.
    /// </summary>
    /// <remarks>
    /// Separate, and public for the same reason the scripts are: a test can feed
    /// it real captured output. A succeeded-but-unparseable answer is treated as
    /// a failure rather than producing a <see cref="ShadowCopy"/> with empty
    /// fields, which would go on to read every world file out of the path
    /// <c>"\file"</c> and report a missing world rather than a snapshot that
    /// never happened.
    /// </remarks>
    public static ShadowCopy Parse(string json, string volumeRoot)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            var id = document.RootElement.TryGetProperty("ShadowId", out var idNode)
                ? idNode.GetString()
                : null;

            var device = document.RootElement.TryGetProperty("DeviceObject", out var deviceNode)
                ? deviceNode.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(device))
            {
                throw new InvalidOperationException(
                    $"The shadow copy of {volumeRoot} reported no id or device path.");
            }

            return new ShadowCopy(id, device);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Could not read the shadow copy Windows reported for {volumeRoot}: {ex.Message}");
        }
    }

    public async Task ReleaseAsync(ShadowCopy shadow, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _powerShell
                .RunAsync(ReleaseScript(shadow.ShadowId), cancellationToken)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                _log.LogWarning(
                    "Shadow copy {ShadowId} could not be released and is still using disk: {Error}",
                    shadow.ShadowId, Describe(result.Reason));
            }
        }
        catch (Exception ex)
        {
            // Runs in a finally. The exception on its way out is almost always
            // the more useful one, and losing it to a cleanup failure would hide
            // why the backup failed at all.
            _log.LogWarning(ex, "Shadow copy {ShadowId} could not be released", shadow.ShadowId);
        }
    }

    /// <summary>
    /// Windows' own words, or something honest when it did not offer any.
    /// </summary>
    private static string Describe(string error) =>
        string.IsNullOrWhiteSpace(error)
            ? "Windows gave no reason. The usual causes are the service account not holding "
              + "the backup privilege, the Volume Shadow Copy service being disabled, or the "
              + "volume not supporting snapshots — the Readiness panel on the Status page checks "
              + "the first two."
            : error.Trim();

    /// <summary>
    /// Single quotes are the only metacharacter inside a PowerShell literal.
    /// </summary>
    /// <remarks>
    /// Both values reaching this are machine-generated — a drive root and a GUID
    /// — so this is a belt rather than a fix for a live hole. It is here because
    /// the day one of them becomes operator-supplied, the escaping should already
    /// be in place rather than remembered.
    /// </remarks>
    private static string Escape(string value) => value.Replace("'", "''");
}
