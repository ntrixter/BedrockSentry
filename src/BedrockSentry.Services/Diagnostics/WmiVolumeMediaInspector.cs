using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BedrockSentry.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Diagnostics;

/// <summary>
/// Reads whether each volume sits on an SSD or an HDD, through CIM in
/// <c>root\Microsoft\Windows\Storage</c>: the classes <c>Get-PhysicalDisk</c>
/// wraps, read with no shell, as <see cref="WmiVolumeDeduplicationInspector"/> does.
/// </summary>
/// <remarks>
/// Only the reading is here; the join and every word the page shows are
/// <see cref="DriveMediaRules"/> in <c>Core</c>, where a test can reach them.
/// </remarks>
public sealed class WmiVolumeMediaInspector : IVolumeMediaInspector
{
    private const string Scope = @"\\.\root\Microsoft\Windows\Storage";

    private readonly ILogger<WmiVolumeMediaInspector> _log;

    public WmiVolumeMediaInspector(ILogger<WmiVolumeMediaInspector> log)
    {
        _log = log;
    }

    public IReadOnlyDictionary<string, DriveMedia> Inspect(IReadOnlyCollection<string> volumeRoots)
    {
        if (!OperatingSystem.IsWindows())
        {
            return DriveMediaRules.AllUnknown(volumeRoots);
        }

        try
        {
            return DriveMediaRules.Assemble(volumeRoots, Partitions(), Disks(), PhysicalDisks());
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            // The Drive column is a convenience: a dash beats a failed page.
            _log.LogWarning(ex, "The drive type of the storage volumes could not be read.");
            return DriveMediaRules.AllUnknown(volumeRoots);
        }
    }

    [SupportedOSPlatform("windows")]
    private static List<PartitionRow> Partitions()
    {
        var rows = new List<PartitionRow>();

        foreach (var item in Read("SELECT AccessPaths, DiskNumber FROM MSFT_Partition"))
        {
            using (item)
            {
                // AccessPaths holds the drive letter, any folder the partition is
                // mounted in and its volume GUID path; null for a partition with none.
                if (item["AccessPaths"] is string[] { Length: > 0 } paths)
                {
                    rows.Add(new PartitionRow(paths, Convert.ToUInt32(item["DiskNumber"])));
                }
            }
        }

        return rows;
    }

    [SupportedOSPlatform("windows")]
    private static List<DiskRow> Disks()
    {
        var rows = new List<DiskRow>();

        foreach (var item in Read("SELECT Number, BusType FROM MSFT_Disk"))
        {
            using (item)
            {
                rows.Add(new DiskRow(Convert.ToUInt32(item["Number"]), Convert.ToUInt16(item["BusType"])));
            }
        }

        return rows;
    }

    [SupportedOSPlatform("windows")]
    private static List<PhysicalDiskRow> PhysicalDisks()
    {
        var rows = new List<PhysicalDiskRow>();

        foreach (var item in Read("SELECT DeviceId, MediaType FROM MSFT_PhysicalDisk"))
        {
            using (item)
            {
                if (item["DeviceId"] is string id)
                {
                    rows.Add(new PhysicalDiskRow(id, Convert.ToUInt16(item["MediaType"])));
                }
            }
        }

        return rows;
    }

    [SupportedOSPlatform("windows")]
    private static List<ManagementBaseObject> Read(string query)
    {
        using var searcher = new ManagementObjectSearcher(Scope, query);
        using var results = searcher.Get();
        return [.. results.Cast<ManagementBaseObject>()];
    }
}
