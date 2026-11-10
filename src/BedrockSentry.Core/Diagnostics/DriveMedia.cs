namespace BedrockSentry.Core.Diagnostics;

/// <summary>What kind of drive a volume sits on, as the Storage table's Drive column reads it.</summary>
/// <remarks>
/// Unknown is its own state rather than a guess: a virtual machine's disk reports
/// no media type, and a Storage Space or hardware array is several drives behind
/// one disk number. Reading either as SSD or HDD would put a wrong word on the page.
/// </remarks>
public enum DriveMedia
{
    Unknown,
    Ssd,
    Hdd,
    StorageSpace,
}

/// <summary>
/// One partition, as <c>MSFT_Partition</c> reports it: every path it is reached by
/// (<c>AccessPaths</c>: a drive letter, a folder it is mounted in, its
/// <c>\\?\Volume{…}\</c> name) and its disk.
/// </summary>
public sealed record PartitionRow(IReadOnlyList<string> AccessPaths, uint DiskNumber)
{
    /// <summary>A partition reached by a drive letter only.</summary>
    public PartitionRow(char driveLetter, uint diskNumber)
        : this([$"{driveLetter}:\\"], diskNumber)
    {
    }
}

/// <summary>One disk, as <c>MSFT_Disk</c> reports it: its number and <c>BusType</c>.</summary>
public sealed record DiskRow(uint Number, ushort BusType);

/// <summary>One physical disk, as <c>MSFT_PhysicalDisk</c> reports it: <c>DeviceId</c> and <c>MediaType</c>.</summary>
public sealed record PhysicalDiskRow(string DeviceId, ushort MediaType);

/// <summary>
/// The one Windows call behind the Drive column. The <c>D46</c> shape: an
/// interface here, the CIM reading in <c>Services</c>, a fake on a build machine.
/// Never throws; a volume it cannot place reads <see cref="DriveMedia.Unknown"/>.
/// </summary>
public interface IVolumeMediaInspector
{
    IReadOnlyDictionary<string, DriveMedia> Inspect(IReadOnlyCollection<string> volumeRoots);
}

/// <summary>The rule from CIM rows to a volume's <see cref="DriveMedia"/>, and its words.</summary>
public static class DriveMediaRules
{
    // MSFT_Disk.BusType: 17 is Spaces. 8 (RAID) is not a sign of an array: Intel
    // RST and VMD report a single NVMe or SATA disk on a desktop as RAID (T430
    // clause 6), so a RAID-bus disk is read by its physical disk like any other,
    // and a hardware array's virtual disk reports MediaType 0 and reads a dash.
    private const ushort BusTypeSpaces = 17;

    // MSFT_PhysicalDisk.MediaType: 3 is HDD, 4 is SSD, 5 is SCM; 0 is unspecified.
    private const ushort MediaHdd = 3;
    private const ushort MediaSsd = 4;
    private const ushort MediaScm = 5;

    public static string Text(DriveMedia media) => media switch
    {
        DriveMedia.Ssd => "SSD",
        DriveMedia.Hdd => "HDD",
        DriveMedia.StorageSpace => "Array",
        _ => "—",
    };

    /// <summary>
    /// Volume root to partition to disk to physical disk. A disk that is a Storage
    /// Space reads <see cref="DriveMedia.StorageSpace"/> before any
    /// physical disk is consulted, because a pool member's <c>DeviceId</c> can equal
    /// the space's disk number and would name one member's media for the whole set.
    /// </summary>
    public static IReadOnlyDictionary<string, DriveMedia> Assemble(
        IEnumerable<string> volumeRoots,
        IReadOnlyList<PartitionRow> partitions,
        IReadOnlyList<DiskRow> disks,
        IReadOnlyList<PhysicalDiskRow> physicalDisks)
    {
        var result = new Dictionary<string, DriveMedia>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in volumeRoots)
        {
            result[root] = Resolve(root, partitions, disks, physicalDisks);
        }

        return result;
    }

    public static IReadOnlyDictionary<string, DriveMedia> AllUnknown(IEnumerable<string> volumeRoots) =>
        volumeRoots.Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(r => r, _ => DriveMedia.Unknown, StringComparer.OrdinalIgnoreCase);

    private static DriveMedia Resolve(
        string root,
        IReadOnlyList<PartitionRow> partitions,
        IReadOnlyList<DiskRow> disks,
        IReadOnlyList<PhysicalDiskRow> physicalDisks)
    {
        // A volume mounted in a folder (C:\Mounts\Server1\) is matched by that
        // folder, which MSFT_Partition lists among its access paths (T430 clause 5).
        var wanted = VolumeDeduplication.NormaliseVolume(root);
        var onDisks = partitions
            .Where(p => p.AccessPaths.Any(a => VolumeDeduplication.NormaliseVolume(a) == wanted))
            .Select(p => p.DiskNumber)
            .Distinct()
            .ToList();

        // No partition, or a volume spread over more than one disk: nothing one
        // word can say honestly.
        if (onDisks.Count != 1)
        {
            return DriveMedia.Unknown;
        }

        var number = onDisks[0];
        var disk = disks.FirstOrDefault(d => d.Number == number);

        if (disk is not null && disk.BusType == BusTypeSpaces)
        {
            return DriveMedia.StorageSpace;
        }

        var id = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var physical = physicalDisks.Where(p => p.DeviceId == id).ToList();

        return physical.Count != 1
            ? DriveMedia.Unknown
            : physical[0].MediaType switch
            {
                MediaSsd or MediaScm => DriveMedia.Ssd,
                MediaHdd => DriveMedia.Hdd,
                _ => DriveMedia.Unknown,
            };
    }
}
