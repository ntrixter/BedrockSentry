namespace BedrockSentry.Core.Diagnostics;

/// <summary>The addresses of the machine the wrapper is running on.</summary>
/// <param name="PublicAddress">
/// Null when discovery is off, has not answered yet, or failed. The three are
/// distinguished by <paramref name="PublicAddressNote"/> rather than collapsed
/// into one silence, because "turned off" and "could not reach the internet"
/// call for different actions.
/// </param>
public sealed record HostAddresses(
    string HostName,
    IReadOnlyList<string> LocalAddresses,
    string? PublicAddress,
    string? PublicAddressNote);

/// <summary>
/// The result of comparing a domain name against the machine's public address.
/// </summary>
/// <remarks>
/// This is the whole of "why can't anyone connect" for a dynamic address whose
/// DNS record has gone stale: the name still resolves, so nothing looks broken,
/// but it resolves somewhere else.
/// </remarks>
public sealed record DomainCheck(
    string Domain,
    IReadOnlyList<string> ResolvedAddresses,
    string? PublicAddress,
    DomainCheckResult Result,
    string? Detail);

public enum DomainCheckResult
{
    /// <summary>The name resolves to the machine's public address.</summary>
    Matches,

    /// <summary>It resolves somewhere else. Almost always a stale record.</summary>
    Mismatch,

    /// <summary>The name did not resolve at all.</summary>
    NotFound,

    /// <summary>
    /// It resolved, but there is nothing to compare against — public address
    /// discovery is off or did not answer.
    /// </summary>
    Unknown,
}

/// <summary>What the host is, and whether it is about to become a problem.</summary>
/// <param name="RebootReasons">
/// What Windows has pending that can restart the machine by itself, in the order
/// <see cref="RebootNotice.Signals"/> lists them; empty when nothing is.
/// </param>
/// <param name="RebootUnreadable">
/// Why a signal could not be read, or null when every one was (<see cref="RebootNotice.Read"/>).
/// </param>
public sealed record HostSystem(
    string OperatingSystem,
    int ProcessorCount,
    long TotalMemoryBytes,
    long AvailableMemoryBytes,
    TimeSpan Uptime,
    DateTimeOffset LocalTime,
    string TimeZone,
    IReadOnlyList<string> RebootReasons,
    string? RebootUnreadable = null);

/// <summary>How much room a volume has left, by its share free (<see cref="VolumeUsage.FreeLevel"/>).</summary>
public enum FreeSpaceLevel
{
    Ok,
    Low,
    Critical,
}

/// <summary>Space on one volume the wrapper keeps something on.</summary>
/// <param name="Deduplication">
/// Whether Windows Server Data Deduplication is holding space back on this
/// volume (<c>D55</c>). Never null: "the role is not installed" is a state of
/// its own, because an absent value read as a present one is <c>T136</c>.
/// </param>
public sealed record VolumeUsage(
    string Name,
    string RootPath,
    long TotalBytes,
    long FreeBytes,
    VolumeDeduplication Deduplication,
    DriveMedia Media = DriveMedia.Unknown)
{
    /// <summary>
    /// Below this share free the Free cell turns amber and a line under the table
    /// names the volume. A server, a backup or a deduplication job that runs a
    /// volume out of space fails, and the volume may hold none of the backups.
    /// </summary>
    public const double LowFreeFraction = 0.15;

    /// <summary>
    /// Below this share free the cell turns red. Windows marks a drive low at 10%
    /// free in File Explorer, so the panel agrees with what the administrator sees there.
    /// </summary>
    public const double CriticalFreeFraction = 0.10;

    public double FreeFraction => TotalBytes > 0 ? (double)FreeBytes / TotalBytes : 1d;

    public FreeSpaceLevel FreeLevel =>
        TotalBytes <= 0 ? FreeSpaceLevel.Ok
        : FreeFraction < CriticalFreeFraction ? FreeSpaceLevel.Critical
        : FreeFraction < LowFreeFraction ? FreeSpaceLevel.Low
        : FreeSpaceLevel.Ok;

    /// <summary>The line under the table for a volume running low, or null.</summary>
    public string? FreeWarning => FreeLevel switch
    {
        FreeSpaceLevel.Critical =>
            $"{RootPath} is critically low on space: {FreeText} free.",
        FreeSpaceLevel.Low =>
            $"{RootPath} is low on space: {FreeText} free.",
        _ => null,
    };

    public double UsedFraction => TotalBytes > 0 ? 1d - ((double)FreeBytes / TotalBytes) : 0d;

    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);

    /// <summary>The Free cell: <c>14.2 GB (14%)</c>.</summary>
    public string FreeText => $"{ByteSize.Format(FreeBytes)} ({Percent(TotalBytes > 0 ? FreeFraction : 0d)})";

    /// <summary>The Used cell: <c>86.8 GB (86%)</c>.</summary>
    public string UsedText => $"{ByteSize.Format(UsedBytes)} ({Percent(UsedFraction)})";

    // Whole percent, the same in every culture: "P0" reads "14 %" in some.
    private static string Percent(double fraction) =>
        Math.Round(fraction * 100, MidpointRounding.AwayFromZero)
            .ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";
}
