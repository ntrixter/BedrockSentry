using System.Globalization;

namespace BedrockSentry.Core.Backups;

/// <summary>
/// Decides which backups a retention rule removes, from their NAMES.
/// </summary>
/// <remarks>
/// <para>
/// One implementation for every strategy that writes timestamped artifacts. Both
/// the ZIP and folder-copy strategies compose their names from <see cref="StampFormat"/>
/// and read them back through here, so the format cannot be written one way and
/// parsed another.
/// </para>
/// <para>
/// THE NAME IS THE CLOCK, NOT THE FILESYSTEM. Retention used to order by
/// <c>LastWriteTimeUtc</c> while naming from the backup's own start time — two
/// statements of one fact, and only the name is stable. A filesystem timestamp
/// moves whenever anything touches the artifact: a restore that reads it, a copy,
/// a scanner that rewrites it. It is also coarser than it looks, so two artifacts
/// written in quick succession can compare equal and order arbitrarily.
/// </para>
/// <para>
/// That mattered because of how the exclusion composed. The rule kept the newest
/// <c>Value</c> and deleted the rest, then refused to delete the artifact it had
/// just written — correctly, but WITHOUT PUTTING ANYTHING IN ITS PLACE. So
/// whenever the just-written artifact did not sort newest it fell into the delete
/// set, was skipped, and one fewer deletion happened: <c>Value + 1</c> kept, on a
/// rule that says <c>Value</c>, reported to nobody. Observed as
/// <c>Assert.Single</c> finding two folders (T198).
/// </para>
/// <para>
/// Both halves are fixed here rather than in each caller. Ordering is by the
/// parsed stamp, and the just-written artifact is PINNED to the front before the
/// count is applied, so it can never be in the removal set and can never cost a
/// keep slot. <c>BackupRetentionPlanTests</c> pins both, including the case where
/// the just-written stamp is not the newest.
/// </para>
/// <para>
/// An artifact whose name does not parse is never removed and never counted. It
/// matched the caller's pattern but this cannot say when it was taken, and a
/// retention rule that deletes what it cannot date is deleting on a guess.
/// </para>
/// </remarks>
public static class BackupRetentionPlan
{
    /// <summary>
    /// The stamp every timestamped backup artifact carries, in UTC. Written and
    /// parsed through this one constant so the two cannot drift.
    /// </summary>
    public const string StampFormat = "yyyyMMdd-HHmmss";

    /// <summary>
    /// What a save writes, given what the form actually rendered (<c>D76</c>).
    /// </summary>
    /// <param name="mode">The mode the form shows.</param>
    /// <param name="shown">The number in the input, when there is one.</param>
    /// <param name="stored">The value already in the settings.</param>
    /// <remarks>
    /// <para><strong>A form writes what it shows.</strong> The retention input is
    /// not rendered at all in <see cref="BackupRetentionMode.KeepAll"/> mode, and
    /// the tab seeds its field with <c>Math.Max(1, stored)</c> — so a save that was
    /// about a schedule or a trigger rewrote a retention value nobody had been
    /// shown, silently turning a stored 0 into a 1. Harmless while the mode stays
    /// <c>KeepAll</c>, and not harmless the day somebody switches to
    /// <see cref="BackupRetentionMode.Count"/>, where 1 means <em>delete every
    /// archive but the newest</em>.</para>
    ///
    /// <para>Here rather than in the tab because there are two of them, one per
    /// format, and a rule stated twice is enforced once. The tab cannot be reached
    /// by the test suite at all, which is the other half of the same reason.</para>
    /// </remarks>
    public static BackupRetention AsSaved(BackupRetentionMode mode, int shown, int stored) =>
        new()
        {
            Mode = mode,
            Value = mode is BackupRetentionMode.KeepAll ? stored : shown,
        };

    /// <summary>Composes an artifact name: <paramref name="prefix"/>, the stamp, then <paramref name="suffix"/>.</summary>
    public static string ComposeName(string prefix, DateTimeOffset startedUtc, string suffix = "") =>
        prefix + startedUtc.UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture) + suffix;

    /// <summary>
    /// Reads the stamp back out of an artifact name. False when the name does not
    /// carry one, which is a reason to leave the artifact alone rather than an error.
    /// </summary>
    public static bool TryReadStamp(string name, string prefix, string suffix, out DateTime stampUtc)
    {
        stampUtc = default;

        if (string.IsNullOrEmpty(name)) { return false; }
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { return false; }
        if (suffix.Length > 0 && !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { return false; }

        var length = name.Length - prefix.Length - suffix.Length;
        if (length <= 0) { return false; }

        var core = name.Substring(prefix.Length, length);

        return DateTime.TryParseExact(
            core,
            StampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out stampUtc);
    }

    /// <summary>
    /// The artifact names this retention rule removes, newest kept first.
    /// </summary>
    /// <param name="names">Candidate names already narrowed to this strategy's own pattern.</param>
    /// <param name="justWritten">
    /// The artifact this backup run produced, never removed and never costing a
    /// keep slot. Null when the caller has none to protect.
    /// </param>
    /// <param name="locked">
    /// Artifacts an administrator has locked (<c>D68</c>). Never removed, <strong>and
    /// never counted</strong> — keep 5 with 2 locked is 5 unlocked plus 2 locked on
    /// disk, which is the only reading under which a lock does not silently shrink
    /// the retention somebody asked for.
    /// </param>
    /// <remarks>
    /// Locked names are dropped before the count is applied rather than filtered
    /// out of the result afterwards. Filtering afterwards gives "never removed" and
    /// not "never counted": five locked artifacts under a keep-5 rule would occupy
    /// every slot and quietly delete every unlocked one. The two properties are one
    /// line apart and only one arrangement has both.
    /// </remarks>
    public static IReadOnlyList<string> NamesToRemove(
        IEnumerable<string> names,
        string prefix,
        string suffix,
        BackupRetention retention,
        DateTime nowUtc,
        string? justWritten,
        IReadOnlySet<string>? locked = null)
    {
        if (retention.Mode is BackupRetentionMode.KeepAll || retention.Value <= 0)
        {
            return [];
        }

        var dated = new List<(string Name, DateTime Stamp)>();

        foreach (var name in names)
        {
            if (locked is not null && locked.Contains(name))
            {
                continue;
            }

            if (TryReadStamp(name, prefix, suffix, out var stamp))
            {
                dated.Add((name, stamp));
            }
        }

        // Newest first. The secondary key costs nothing and keeps the order
        // deterministic if the stamp ever becomes coarse enough for two artifacts
        // to share one - which is the failure the filesystem timestamp had.
        var ordered = dated
            .OrderByDescending(d => d.Stamp)
            .ThenByDescending(d => d.Name, StringComparer.Ordinal)
            .Select(d => d)
            .ToList();

        if (justWritten is not null)
        {
            var index = ordered.FindIndex(d =>
                string.Equals(d.Name, justWritten, StringComparison.OrdinalIgnoreCase));

            if (index > 0)
            {
                var entry = ordered[index];
                ordered.RemoveAt(index);
                ordered.Insert(0, entry);
            }
        }

        var doomed = retention.Mode switch
        {
            BackupRetentionMode.Count => ordered.Skip(retention.Value),
            BackupRetentionMode.Days => ordered.Where(d => d.Stamp < nowUtc.AddDays(-retention.Value)),
            _ => [],
        };

        // The pin above already keeps it out of the Count arm. Days has no count to
        // pin against, and stating the invariant once here is cheaper than relying
        // on each arm to have honoured it.
        return doomed
            .Select(d => d.Name)
            .Where(n => justWritten is null ||
                        !string.Equals(n, justWritten, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
