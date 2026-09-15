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
public sealed record HostSystem(
    string OperatingSystem,
    int ProcessorCount,
    long TotalMemoryBytes,
    long AvailableMemoryBytes,
    TimeSpan Uptime,
    DateTimeOffset LocalTime,
    string TimeZone,
    IReadOnlyList<RebootSignal> RebootSignals)
{
    /// <summary>
    /// What the pending work actually means — not a <c>bool</c>, because two of the
    /// three signals can restart the machine and one cannot (<c>T188</c>).
    /// </summary>
    public RebootUrgency RebootUrgency => RebootNotice.UrgencyOf(RebootSignals);

    /// <summary>The reasons, in the order they were found.</summary>
    public IReadOnlyList<string> RebootReasons =>
        [.. RebootSignals.Select(s => s.Reason)];
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
    VolumeDeduplication Deduplication)
{
    public double UsedFraction => TotalBytes > 0 ? 1d - ((double)FreeBytes / TotalBytes) : 0d;
}
