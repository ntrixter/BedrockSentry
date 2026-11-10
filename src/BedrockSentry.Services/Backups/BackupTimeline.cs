using BedrockSentry.Core.Backups;

namespace BedrockSentry.Services.Backups;

/// <summary>Which rows of the timeline a filter chip asks for.</summary>
/// <remarks>In the order the chips are drawn; the tab opens on <see cref="Restorable"/> (<c>D110</c>).</remarks>
public enum BackupTimelineFilter
{
    /// <summary>Only the rows a restore could use.</summary>
    Restorable,

    /// <summary>Only the runs that were skipped.</summary>
    Skipped,

    /// <summary>Only the rows that need a person.</summary>
    Problems,

    /// <summary>Every run and every file.</summary>
    All,
}

/// <summary>
/// One line of the Backups tab: one backup a run took, a run that took none, or a
/// backup whose run is no longer in the history.
/// </summary>
/// <remarks>
/// <para><strong>One backup per row</strong> (<c>T282</c>). A run with both formats
/// on takes two backups of one instant, and they used to share a row: two chips,
/// one size that was the sum of both, and two sets of buttons stacked in one cell.
/// Each is its own row now, with its own verdict, because the two can differ — one
/// format can fail while the other works.</para>
///
/// <para><strong>Three shapes, and a factory for each</strong>, rather than a
/// constructor taking four nullable fields. A strategy result without its run
/// cannot be given a verdict, and a row with neither a run nor a file has nothing
/// to show; neither can be built (<c>T176</c>).</para>
/// </remarks>
public sealed record BackupTimelineRow
{
    private BackupTimelineRow(
        DateTimeOffset atUtc,
        BackupRecord? record,
        BackupStrategyResult? strategy,
        RestorableBackup? artifact)
    {
        AtUtc = atUtc;
        Record = record;
        Strategy = strategy;
        Artifact = artifact;
    }

    /// <summary>
    /// One format's backup from a run, with its file when that is still on disk.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="strategy"/> is not one of the run's results, or
    /// <paramref name="artifact"/> is not the file it wrote. Either would put one
    /// backup's Restore button beside another backup's verdict.
    /// </exception>
    public static BackupTimelineRow ForStrategy(
        BackupRecord record, BackupStrategyResult strategy, RestorableBackup? artifact)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(strategy);

        if (!record.Strategies.Contains(strategy))
        {
            throw new ArgumentException(
                $"The {strategy.Strategy} result is not one of this run's strategies.",
                nameof(strategy));
        }

        if (artifact is not null
            && !string.Equals(strategy.ArtifactPath, artifact.FullName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"{artifact.Name} is not the file the {strategy.Strategy} strategy wrote.",
                nameof(artifact));
        }

        return new BackupTimelineRow(record.StartedUtc, record, strategy, artifact);
    }

    /// <summary>
    /// A run that reached no strategy: skipped, stopped before the first one started
    /// (<c>T267</c>), or refused because every strategy it had was blocked
    /// (<c>D45</c>).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The run has strategy results. Those are one row each, and a single row for all
    /// of them is the shape <c>T282</c> removed.
    /// </exception>
    public static BackupTimelineRow ForRun(BackupRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.Strategies.Count > 0)
        {
            throw new ArgumentException(
                "A run with strategy results is one row per result.", nameof(record));
        }

        return new BackupTimelineRow(record.StartedUtc, record, null, null);
    }

    /// <summary>A file on disk whose run is no longer in the history.</summary>
    public static BackupTimelineRow ForFile(RestorableBackup artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        return new BackupTimelineRow(artifact.TakenUtc, null, null, artifact);
    }

    /// <summary>When it happened: the run's start, or the file's stamp.</summary>
    public DateTimeOffset AtUtc { get; }

    /// <summary>The run, or null for a file with no surviving record.</summary>
    public BackupRecord? Record { get; }

    /// <summary>
    /// The one strategy this row is about, or null for a run that reached none and
    /// for a file with no record.
    /// </summary>
    public BackupStrategyResult? Strategy { get; }

    /// <summary>
    /// The file this row's backup left, while it is still on disk. Null when it never
    /// wrote one, when retention has since removed it, or when a newer run claimed
    /// the same path.
    /// </summary>
    public RestorableBackup? Artifact { get; }

    /// <summary>Can a restore use this row?</summary>
    public bool IsRestorable => Artifact is not null;

    /// <summary>
    /// The name this row's backup has on disk — the ZIP's file name or the folder
    /// copy's folder name — or null for a run that wrote nothing.
    /// </summary>
    /// <remarks>
    /// Taken from the file while it is there, and from the path the strategy recorded
    /// once retention has removed it, so a pruned row still says which file it was.
    /// The name carries the run's start in UTC, which is how the backups folder sorts
    /// it, while the Taken column shows local time.
    /// </remarks>
    public string? DiskName =>
        Artifact?.Name
        ?? (Strategy?.ArtifactPath is { Length: > 0 } path
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(path))
            : null);

    /// <summary>
    /// How this row reads, or null for a file whose run is not in the history.
    /// </summary>
    /// <remarks>
    /// Null rather than <see cref="BackupSeverity.Ok"/>: nothing here has read a
    /// verdict, and answering <em>fine</em> for a row nobody assessed is the false
    /// clear this codebase keeps recording (<c>T202</c>, <c>T229</c>).
    /// </remarks>
    public BackupOutcome? Outcome() =>
        Record is null ? null
        : Strategy is null ? Record.Outcome()
        : Record.OutcomeFor(Strategy);

    /// <summary>How alarming this row is, or null when nothing assessed it.</summary>
    public BackupSeverity? Severity => Outcome()?.Severity;

    /// <summary>Does this row need somebody to look at it?</summary>
    public bool IsProblem => Severity is BackupSeverity.Warning or BackupSeverity.Failed;

    /// <summary>
    /// Which kind of backup this row is, in the restore picker's words, or null for a
    /// run that reached no strategy.
    /// </summary>
    /// <remarks>
    /// <para>The file answers when it is there. When it is not, the strategy that
    /// wrote it does — otherwise two rows of one run whose files were both pruned
    /// would read identically, and one of them would look like a duplicate.</para>
    ///
    /// <para>A strategy this does not map keeps its own recorded name, which is what
    /// its history row already says in the Result column.</para>
    /// </remarks>
    public string? Kind
    {
        get
        {
            if (Artifact is not null)
            {
                return Artifact.Kind;
            }

            return Strategy?.Strategy switch
            {
                null => null,
                ZipBackupStrategy.StrategyName => RestorableBackup.ZipKind,
                FolderBackupStrategy.StrategyName => RestorableBackup.FolderKind,
                RestorableBackup.McWorldKind => RestorableBackup.McWorldKind,
                var other => other,
            };
        }
    }
}

/// <summary>
/// Joins what a server's history says happened with what is actually on its disk.
/// </summary>
/// <remarks>
/// <para><strong>They disagree in both directions, which is why this exists rather
/// than one list being rendered.</strong> A run's files can be pruned while its
/// record stays; a file can outlive the record that made it, because the history is
/// bounded and the retention rule is not the same rule. <see cref="BackupStanding"/>
/// already reasons about that disagreement for the summary card — this is the same
/// fact at row level.</para>
///
/// <para>In <c>Services</c> rather than <c>Core</c> because
/// <see cref="RestorableBackup"/> is: the join belongs beside the type it joins on,
/// and the test project reaches both.</para>
/// </remarks>
public static class BackupTimeline
{
    /// <summary>
    /// Every backup, every run that took none, and every restorable file as one list,
    /// newest first.
    /// </summary>
    /// <remarks>
    /// <para><strong>A run is one row per strategy result</strong>, in the order the
    /// run recorded them, so a run with both formats on is two rows sharing a time
    /// (<c>T282</c>). A run with no results is one row.</para>
    ///
    /// <para><strong>An artifact belongs to exactly one row.</strong> Records are
    /// walked newest first and each result claims the file it names, so a path named
    /// by two records is attached to the newer of them rather than to both — which
    /// would offer the same file two Restore buttons and let a person restore what
    /// they believe is a different backup.</para>
    ///
    /// <para>Paths are matched with <see cref="StringComparer.OrdinalIgnoreCase"/>.
    /// These are Windows paths and the two sides are written by different code —
    /// the strategy records what it wrote, the picker enumerates the directory — so
    /// an ordinal match would drop a row over a drive letter's case.</para>
    /// </remarks>
    public static IReadOnlyList<BackupTimelineRow> Build(
        IReadOnlyList<BackupRecord> history,
        IReadOnlyList<RestorableBackup> archives)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(archives);

        var byPath = new Dictionary<string, RestorableBackup>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in archives)
        {
            byPath[archive.FullName] = archive;
        }

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<BackupTimelineRow>(history.Count + archives.Count);

        foreach (var record in history.OrderByDescending(r => r.StartedUtc))
        {
            if (record.Strategies.Count == 0)
            {
                rows.Add(BackupTimelineRow.ForRun(record));
                continue;
            }

            foreach (var strategy in record.Strategies)
            {
                RestorableBackup? mine = null;

                if (strategy.ArtifactPath is { Length: > 0 } path
                    && byPath.TryGetValue(path, out var archive)
                    && claimed.Add(path))
                {
                    mine = archive;
                }

                rows.Add(BackupTimelineRow.ForStrategy(record, strategy, mine));
            }
        }

        foreach (var archive in archives)
        {
            if (!claimed.Contains(archive.FullName))
            {
                rows.Add(BackupTimelineRow.ForFile(archive));
            }
        }

        // A stable sort, so the rows of one run keep the order it recorded them in.
        return [.. rows.OrderByDescending(r => r.AtUtc)];
    }

    /// <summary>Does this row belong under that chip?</summary>
    /// <remarks>
    /// The <c>_ =&gt; throw</c> is the guard. A filter added later and not given an
    /// arm would otherwise fall through to <em>show everything</em>, which is the
    /// one wrong answer a chip can give without looking broken.
    /// </remarks>
    public static bool Matches(BackupTimelineRow row, BackupTimelineFilter filter)
    {
        ArgumentNullException.ThrowIfNull(row);

        return filter switch
        {
            BackupTimelineFilter.All => true,
            BackupTimelineFilter.Restorable => row.IsRestorable,
            BackupTimelineFilter.Skipped => row.Severity is BackupSeverity.Skipped,
            BackupTimelineFilter.Problems => row.IsProblem,

            _ => throw new NotSupportedException(
                $"No rule for the {filter} filter. Add an arm here rather than letting " +
                "an unknown chip quietly show every row."),
        };
    }

    /// <summary>How many rows each chip would show.</summary>
    public static int Count(
        IReadOnlyList<BackupTimelineRow> rows, BackupTimelineFilter filter)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Count(r => Matches(r, filter));
    }
}
