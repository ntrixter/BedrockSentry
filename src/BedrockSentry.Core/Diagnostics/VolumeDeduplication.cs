namespace BedrockSentry.Core.Diagnostics;

/// <summary>
/// Whether Windows Server Data Deduplication is in play on a volume (<c>D55</c>).
/// </summary>
/// <remarks>
/// Four states rather than a boolean, and the distinctions are the point. Not
/// installed is the ordinary answer on a client edition, which cannot install
/// the role at all. Not enabled is a server that has the role and has not
/// turned it on for this volume. Unknown is installed but unreadable — most
/// likely a service account without the right to ask — and it is kept apart
/// from the first two deliberately: an administrator who enabled it should be
/// told the panel cannot see it, not that it is missing. <c>T136</c> is what
/// reading an absent value as a present one costs.
/// </remarks>
public enum DeduplicationState
{
    NotInstalled,
    NotEnabled,
    Enabled,
    Unknown,
}

/// <summary>
/// One volume as the deduplication service reports it, before it has been
/// matched to a volume the wrapper cares about.
/// </summary>
/// <param name="Volume">As the service names it — <c>D:</c>, without the separator.</param>
/// <param name="SavingsPercent">
/// The whole-volume <c>SavingsRate</c>, never <c>OptimizedFilesSavingsRate</c>.
/// The two have different denominators — the second counts only files in
/// policy for optimisation — and the whole-volume figure is the one that
/// belongs beside a free-space number with the same denominator. Showing the
/// other unlabelled would be two correct numbers that disagree.
/// </param>
/// <param name="LastOptimisationSucceeded">
/// <c>LastOptimizationResult == 0</c>. Deduplication is post-processing, so a
/// job that keeps failing is a volume slowly filling while the page says it is
/// saving space.
/// </param>
public sealed record DeduplicationRow(
    string Volume,
    bool Enabled,
    long SavedBytes,
    int SavingsPercent,
    DateTimeOffset? LastOptimised,
    bool LastOptimisationSucceeded,
    string? LastOptimisationMessage);

/// <summary>
/// The one Windows call behind the storage section's deduplication column.
/// </summary>
/// <remarks>
/// The <c>IShadowCopyService</c> shape, as <c>D46</c> asks for: an interface here,
/// the Windows implementation in <c>Services</c>, and a fake on a build machine.
/// It takes only what the wrapper derived itself — the volume roots it already
/// found — never a string from the panel.
/// </remarks>
public interface IVolumeDeduplicationInspector
{
    /// <summary>
    /// One entry per root passed in, keyed exactly as passed. Never throws:
    /// the role being absent, and the service being unreadable, are both
    /// states of the answer rather than reasons not to answer.
    /// </summary>
    IReadOnlyDictionary<string, VolumeDeduplication> Inspect(IReadOnlyCollection<string> volumeRoots);
}

/// <summary>
/// What the storage section says about deduplication on one volume, and the
/// rule for saying it.
/// </summary>
/// <remarks>
/// The wording is here rather than in the page for the reason that has now
/// produced four defects (<c>T126</c>, <c>T132</c>, <c>T136</c>, <c>T145</c>):
/// a rule in a <c>.razor</c> file is verified on a real page or not at all.
/// </remarks>
public sealed record VolumeDeduplication(
    DeduplicationState State,
    long SavedBytes,
    int SavingsPercent,
    DateTimeOffset? LastOptimised,
    bool LastOptimisationSucceeded,
    string? Detail)
{
    public static VolumeDeduplication NotInstalled { get; } =
        new(DeduplicationState.NotInstalled, 0, 0, null, true, null);

    public static VolumeDeduplication NotEnabled { get; } =
        new(DeduplicationState.NotEnabled, 0, 0, null, true, null);

    public static VolumeDeduplication Unknown(string detail) =>
        new(DeduplicationState.Unknown, 0, 0, null, true, detail);

    /// <summary>
    /// Shown once under the table when no volume has the role, in place of a
    /// column that would read "Not installed" on every row.
    /// </summary>
    public const string NotInstalledNote =
        "Data Deduplication is not installed on this machine. It is a Windows Server "
        + "role — a client edition of Windows cannot install it — and on a volume that "
        + "holds backups it is worth having, since many archives of one world share "
        + "most of their contents.";

    /// <summary>
    /// Enabled, and the last optimisation job failed. Savings erode from here
    /// while the headline number still looks healthy, which is why this and not
    /// a low percentage is what the page flags.
    /// </summary>
    public bool NeedsAttention =>
        State == DeduplicationState.Enabled && !LastOptimisationSucceeded;

    /// <summary>
    /// The column earns its place only if some volume has something to say.
    /// Unknown counts: "could not be read" is worth a cell, "not installed" on
    /// every row is not.
    /// </summary>
    public static bool ColumnWorthShowing(IEnumerable<VolumeDeduplication> volumes) =>
        volumes.Any(v => v.State != DeduplicationState.NotInstalled);

    public string Describe(DateTimeOffset now) => State switch
    {
        DeduplicationState.NotInstalled => "Not installed",

        DeduplicationState.Unknown => string.IsNullOrWhiteSpace(Detail)
            ? "Could not be read."
            : $"Could not be read: {Detail}",

        // Disabled after use: the data is still deduplicated until the volume
        // is un-optimised, and saying only "not enabled" would hide a real
        // number the free-space column depends on.
        DeduplicationState.NotEnabled when SavedBytes > 0 =>
            $"Disabled — {ByteSize.Format(SavedBytes)} still deduplicated",

        DeduplicationState.NotEnabled => "Not enabled",

        // Enabled, no job yet. Post-processing means nothing has happened until
        // the schedule fires, and "saving 0 B (0%)" would read as broken.
        DeduplicationState.Enabled when LastOptimised is null => "Enabled — not yet optimised",

        DeduplicationState.Enabled when !LastOptimisationSucceeded =>
            $"Last optimisation failed {Ago(now)}"
            + (string.IsNullOrWhiteSpace(Detail) ? string.Empty : $": {Detail}"),

        _ => $"Saving {ByteSize.Format(SavedBytes)} ({SavingsPercent}% of the volume), optimised {Ago(now)}",
    };

    private string Ago(DateTimeOffset now)
    {
        var elapsed = now - LastOptimised!.Value;

        // A job stamped in the future is a clock that moved, not a job that has
        // not happened; "just now" is the honest reading of either.
        return elapsed <= TimeSpan.Zero
            ? "just now"
            : UptimeFormatter.Format(elapsed) + " ago";
    }

    /// <summary>
    /// Matches what the service reported to the volumes the wrapper asked about.
    /// </summary>
    /// <param name="rows">
    /// Null means the role is not installed — every root comes back
    /// <see cref="NotInstalled"/>. An empty list means installed and enabled
    /// nowhere — every root comes back <see cref="NotEnabled"/>. The difference
    /// is the whole reason this takes a nullable list rather than an empty one.
    /// </param>
    public static IReadOnlyDictionary<string, VolumeDeduplication> Assemble(
        IEnumerable<string> volumeRoots, IReadOnlyList<DeduplicationRow>? rows)
    {
        var byVolume = new Dictionary<string, DeduplicationRow>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows ?? [])
        {
            byVolume[NormaliseVolume(row.Volume)] = row;
        }

        var result = new Dictionary<string, VolumeDeduplication>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in volumeRoots)
        {
            result[root] = rows is null
                ? NotInstalled
                : byVolume.TryGetValue(NormaliseVolume(root), out var row)
                    ? From(row)
                    : NotEnabled;
        }

        return result;
    }

    /// <summary>The service could not be asked; every root gets the same reason.</summary>
    public static IReadOnlyDictionary<string, VolumeDeduplication> AssembleUnknown(
        IEnumerable<string> volumeRoots, string detail)
    {
        var unknown = Unknown(detail);
        var result = new Dictionary<string, VolumeDeduplication>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in volumeRoots)
        {
            result[root] = unknown;
        }

        return result;
    }

    private static VolumeDeduplication From(DeduplicationRow row) => new(
        row.Enabled ? DeduplicationState.Enabled : DeduplicationState.NotEnabled,
        row.SavedBytes,
        row.SavingsPercent,
        row.LastOptimised,
        row.LastOptimisationSucceeded,
        row.LastOptimisationMessage);

    /// <summary>
    /// <c>DriveInfo</c> says <c>C:\</c>; the deduplication service says <c>C:</c>.
    /// Both become <c>C:</c>, so the two can be joined.
    /// </summary>
    public static string NormaliseVolume(string volume) =>
        volume.Trim().TrimEnd('\\', '/').ToUpperInvariant();
}
