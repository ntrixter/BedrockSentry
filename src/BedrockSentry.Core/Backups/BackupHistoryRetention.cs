namespace BedrockSentry.Core.Backups;

/// <summary>
/// Which history rows are kept, and which an administrator may remove (<c>D98</c>).
/// </summary>
/// <remarks>
/// <para>The history used to grow by a row for every run, skipped ones included, and
/// nothing ever took one away (<c>D68</c> recorded it as the state artifact notes had to
/// tolerate). The operator asked for retention and for a way to remove rows.</para>
///
/// <para><strong>Three kinds of row are never trimmed, because something reads
/// them.</strong> A row whose backup is still on disk is that file's verdict and note.
/// A row recording that a prune damaged a backup still on disk is what refuses to
/// restore that backup (<see cref="DamagedBackups"/>, <c>T236</c>). And the newest
/// successful run is "last backup" on the standing card and the start of the skip
/// rule's "since the last backup".</para>
/// </remarks>
public static class BackupHistoryRetention
{
    /// <summary>The rows to keep, in their order.</summary>
    /// <param name="records">The history, oldest first.</param>
    /// <param name="now">The moment the trim runs.</param>
    /// <param name="keepDays">Days a row is kept; zero or less keeps every row.</param>
    /// <param name="onDisk">
    /// Whether a backup is still on disk, given the path a strategy recorded or the
    /// name a prune recorded as damaged.
    /// </param>
    public static IReadOnlyList<BackupRecord> Keep(
        IReadOnlyList<BackupRecord> records,
        DateTimeOffset now,
        int keepDays,
        Func<string, bool> onDisk)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(onDisk);

        if (keepDays <= 0)
        {
            return records;
        }

        var cutoff = now - TimeSpan.FromDays(keepDays);
        var newestSuccess = NewestSuccess(records);

        return records
            .Where(r => r.StartedUtc >= cutoff
                        || ReferenceEquals(r, newestSuccess)
                        || StillNeeded(r, onDisk))
            .ToList();
    }

    /// <summary>Whether an administrator may remove this row.</summary>
    /// <remarks>
    /// A row still describing a backup on disk stays with it: removing the row would
    /// leave the file as a backup the history knows nothing about. A row recording
    /// damage to a backup still on disk stays too, or that backup would be offered
    /// for restore again.
    /// </remarks>
    public static bool CanRemove(BackupRecord record, Func<string, bool> onDisk)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(onDisk);

        return !StillNeeded(record, onDisk);
    }

    /// <summary>Whether two rows are the same run, for finding one to remove.</summary>
    /// <remarks>
    /// A run has no id. Its start, its finish and its reason together are one run: a
    /// server takes one backup at a time.
    /// </remarks>
    public static bool SameRun(BackupRecord a, BackupRecord b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        return a.StartedUtc == b.StartedUtc && a.FinishedUtc == b.FinishedUtc && a.Reason == b.Reason;
    }

    private static bool StillNeeded(BackupRecord record, Func<string, bool> onDisk) =>
        record.Strategies.Any(s =>
            (s.ArtifactPath is { Length: > 0 } path && onDisk(path))
            || s.Damaged.Any(onDisk));

    private static BackupRecord? NewestSuccess(IReadOnlyList<BackupRecord> records) =>
        records
            .Where(r => r.Succeeded && !r.Skipped && !r.IsImported)
            .OrderBy(r => r.FinishedUtc)
            .LastOrDefault();
}
