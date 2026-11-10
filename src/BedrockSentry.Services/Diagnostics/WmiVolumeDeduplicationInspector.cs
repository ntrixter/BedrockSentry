using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BedrockSentry.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Diagnostics;

/// <summary>
/// Reads Windows Server Data Deduplication state through CIM (<c>D55</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Not PowerShell, deliberately.</b> <c>Get-DedupStatus</c> is the documented
/// way in and it needs elevation, which would make this the fourth inspector
/// shelling out to <c>powershell.exe</c> — the thing this codebase deliberately avoids
/// add and <c>D46</c>'s broker exists to remove. But that cmdlet is a thin wrapper
/// over <c>MSFT_DedupVolumeStatus</c> in
/// <c>root\Microsoft\Windows\Deduplication</c>, so the same rows are reachable
/// through WMI with no shell at all. One named operation, an argument the
/// wrapper derives itself, and an interface in <c>Core</c> — the
/// <see cref="Core.Backups.IShadowCopyService"/> shape.
/// </para>
/// <para>
/// This class does the reading and nothing else. Matching a row to a volume the
/// wrapper cares about, and everything the page will say about it, is
/// <see cref="VolumeDeduplication.Assemble"/> in <c>Core</c>, where a test can
/// reach it on any machine. What is left here cannot be exercised off Windows and
/// is kept small for that reason.
/// </para>
/// </remarks>
public sealed class WmiVolumeDeduplicationInspector : IVolumeDeduplicationInspector
{
    // The service's own namespace. Absent entirely when the role is not
    // installed — which is every client edition of Windows, and a server that
    // has not had it added — and that absence is the whole of the
    // "not installed" state rather than an error.
    private const string Scope = @"\\.\root\Microsoft\Windows\Deduplication";

    private readonly ILogger<WmiVolumeDeduplicationInspector> _log;

    public WmiVolumeDeduplicationInspector(ILogger<WmiVolumeDeduplicationInspector> log)
    {
        _log = log;
    }

    public IReadOnlyDictionary<string, VolumeDeduplication> Inspect(IReadOnlyCollection<string> volumeRoots)
    {
        if (!OperatingSystem.IsWindows())
        {
            return VolumeDeduplication.AssembleUnknown(
                volumeRoots, "Data Deduplication can only be read on Windows.");
        }

        try
        {
            return VolumeDeduplication.Assemble(volumeRoots, Query());
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.InvalidNamespace)
        {
            // Not a failure. The namespace only exists once the role is
            // installed, so this is the ordinary answer on most machines.
            return VolumeDeduplication.Assemble(volumeRoots, rows: null);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            // Installed but unreadable — most likely a service account without
            // the right to ask (`D44`). Distinct from "not installed" on purpose:
            // an administrator who enabled it deserves to hear that the panel
            // cannot see it, not that it is missing.
            _log.LogWarning(ex, "Data Deduplication is installed but could not be read.");

            return VolumeDeduplication.AssembleUnknown(volumeRoots, ex.Message);
        }
    }

    /// <summary>
    /// Every volume the service knows about, whether or not it is still enabled.
    /// </summary>
    /// <remarks>
    /// Two classes, joined on the volume, because neither carries the whole
    /// picture: <c>MSFT_DedupVolume</c> knows whether a volume is enabled and
    /// <c>MSFT_DedupVolumeStatus</c> knows what the last job did. A volume that
    /// has been enabled but never optimised appears in the first and not yet in
    /// the second, and a volume that has been disabled with data still
    /// deduplicated appears in both — so the union is the truthful set.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static List<DeduplicationRow> Query()
    {
        var rows = new Dictionary<string, DeduplicationRow>(StringComparer.OrdinalIgnoreCase);

        using (var searcher = new ManagementObjectSearcher(Scope, "SELECT Volume, Enabled FROM MSFT_DedupVolume"))
        using (var results = searcher.Get())
        {
            foreach (var found in results)
            {
                using var item = found;
                var volume = item["Volume"] as string;

                if (string.IsNullOrWhiteSpace(volume))
                {
                    continue;
                }

                rows[volume] = new DeduplicationRow(
                    volume,
                    Enabled: item["Enabled"] is true,
                    SavedBytes: 0,
                    SavingsPercent: 0,
                    LastOptimised: null,
                    LastOptimisationSucceeded: true,
                    LastOptimisationMessage: null);
            }
        }

        const string statusQuery =
            "SELECT Volume, SavedSpace, SavingsRate, LastOptimizationTime, "
            + "LastOptimizationResult, LastOptimizationResultMessage FROM MSFT_DedupVolumeStatus";

        using (var searcher = new ManagementObjectSearcher(Scope, statusQuery))
        using (var results = searcher.Get())
        {
            foreach (var found in results)
            {
                using var item = found;
                var volume = item["Volume"] as string;

                if (string.IsNullOrWhiteSpace(volume))
                {
                    continue;
                }

                // A volume with status but no MSFT_DedupVolume row has been
                // disabled and still holds optimised data. Enabled=false is the
                // honest reading; the saved bytes below say what is still there.
                var enabled = rows.TryGetValue(volume, out var known) && known.Enabled;

                rows[volume] = new DeduplicationRow(
                    volume,
                    enabled,
                    SavedBytes: ToInt64(item["SavedSpace"]),
                    SavingsPercent: (int)Math.Clamp(ToInt64(item["SavingsRate"]), 0, 100),
                    LastOptimised: ToDateTimeOffset(item["LastOptimizationTime"]),
                    // 0 is success, per the cmdlet's own documentation. Anything
                    // else is a failed job, and the message says which.
                    LastOptimisationSucceeded: ToInt64(item["LastOptimizationResult"]) == 0,
                    LastOptimisationMessage: item["LastOptimizationResultMessage"] as string);
            }
        }

        return [.. rows.Values];
    }

    private static long ToInt64(object? value) => value switch
    {
        null => 0,
        ulong u => u > long.MaxValue ? long.MaxValue : (long)u,
        _ => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// WMI hands a CIM datetime back as a DMTF string
    /// (<c>20260904140215.000000+000</c>), or null when the job has never run.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static DateTimeOffset? ToDateTimeOffset(object? value)
    {
        if (value is not string text || text.Length == 0)
        {
            return null;
        }

        try
        {
            return new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(text));
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return null;
        }
    }
}
