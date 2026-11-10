namespace BedrockSentry.Core.Backups;

/// <summary>
/// Whether this server has a backup somebody could put back, and whether the
/// machinery that makes them is currently working.
/// </summary>
/// <remarks>
/// <para><strong>Two independent facts, which is why this is neither a
/// <c>bool</c> nor a date.</strong> What the most recent run <em>did</em> comes
/// from the history; what could be <em>restored</em> comes from the files on
/// disk. They disagree in both directions and each direction is a different
/// thing to tell somebody. A failed run over a folder still holding good
/// backups is a warning. A history of successes over an empty folder reads as
/// protection and is not, which is the arm worth the space.</para>
///
/// <para><see cref="BackupStandingState.NothingToRestore"/> is ordinary rather
/// than defensive: a custom-command strategy writes a format the wrapper
/// cannot read, so its runs succeed while the restore picker — which lists
/// only what a restore could use — has nothing to show. An instance backed up
/// that way alone sits in this state permanently and correctly.</para>
///
/// <para><strong>A run that was skipped is not evidence either way.</strong>
/// Skipping because nobody has played since the last backup is the scheduler
/// working, so the newest run that says anything about the machinery is the
/// newest <em>non-skipped</em> one. An instance whose every record is a skip
/// has nothing failing.</para>
///
/// <para>Severity is taken from <see cref="BackupRecord.Outcome"/> rather than
/// from <see cref="BackupRecord.Succeeded"/>, so this and the history table
/// cannot come to different views of the same row.
/// <c>BackupStandingTests</c> pins each arm.</para>
/// </remarks>
public enum BackupStandingState
{
    /// <summary>
    /// There is nothing to put back and nothing has gone wrong.
    /// </summary>
    /// <remarks>
    /// Covers two histories that read identically to somebody looking at this
    /// card: no runs at all, and runs that were every one of them skipped. The
    /// second is ordinary on a new instance with a schedule and no players —
    /// the scheduler declines a run when nobody has played since the last
    /// backup, so it can skip for weeks. Grouping it with
    /// <see cref="NeverSucceeded"/> instead would tell an administrator that
    /// every backup had failed when not one had been tried.
    /// </remarks>
    NothingYet,

    /// <summary>Something can be restored and nothing says the machinery is failing.</summary>
    Protected,

    /// <summary>Something can be restored, but the newest run that meant anything failed.</summary>
    ProtectedButFailing,

    /// <summary>
    /// Something can be restored, and the newest run that meant anything worked for
    /// one backup type and failed for another (<c>T302</c>).
    /// </summary>
    /// <remarks>
    /// Its own state because <see cref="ProtectedButFailing"/> says the newest
    /// backup is older than it should be, and after a run whose ZIP worked and
    /// whose folder copy failed the newest backup is that run's ZIP. The run's own
    /// verdict is <em>Failed</em>, since it succeeds only when every type does, so
    /// the difference is read from its per-type verdicts (<c>T282</c>).
    /// </remarks>
    ProtectedButPartlyFailing,

    /// <summary>Runs have succeeded, and nothing the wrapper can restore is on disk.</summary>
    NothingToRestore,

    /// <summary>Runs have happened and not one of them has ever worked.</summary>
    NeverSucceeded,
}

/// <summary>What the Backups tab says at the top, before anybody scrolls.</summary>
/// <param name="State">Which of the five the server is in.</param>
/// <param name="NewestTakenUtc">
/// When the newest backup on disk that this server took was taken, leaving
/// imports out (<see cref="NewestTaken"/>), or null when there is none.
/// </param>
/// <param name="RestorableCount">How many backups a restore could use.</param>
/// <param name="RestorableBytes">
/// What they occupy, summed. <strong>Logical size</strong>: on a volume running
/// Windows Server Data Deduplication this reads far larger than the disk it
/// costs, which is a property of the measurement rather than a fault
/// (<c>D55</c>, <c>D66</c>), and the page has to say so rather than leave
/// somebody to size a disk from it.
/// </param>
public readonly record struct BackupStanding(
    BackupStandingState State,
    DateTimeOffset? NewestTakenUtc,
    int RestorableCount,
    long RestorableBytes)
{
    /// <summary>Is there anything a restore could put back?</summary>
    public bool CanRestore => State is BackupStandingState.Protected
        or BackupStandingState.ProtectedButFailing
        or BackupStandingState.ProtectedButPartlyFailing;

    /// <summary>Should this be shown as something needing a person?</summary>
    /// <remarks>
    /// <see cref="BackupStandingState.NothingYet"/> is deliberately absent.
    /// A server nobody has backed up yet is a server nobody has backed up yet;
    /// what is going to happen about that is <see cref="NextBackup"/>'s
    /// question, and answering it here in a second voice is how one rule comes
    /// to be stated twice.
    /// </remarks>
    public bool NeedsAttention => State is BackupStandingState.ProtectedButFailing
        or BackupStandingState.ProtectedButPartlyFailing
        or BackupStandingState.NothingToRestore
        or BackupStandingState.NeverSucceeded;

    /// <summary>
    /// Work out where a server stands from its history and what is on its disk.
    /// </summary>
    /// <param name="history">
    /// Every run recorded for this instance, in any order. Sorted here rather
    /// than trusted, because a caller handing this list oldest-first would get
    /// the <em>oldest</em> run's verdict silently and it would read as fine.
    /// </param>
    /// <param name="restorableCount">How many backups the restore picker lists.</param>
    /// <param name="newestTakenUtc"><see cref="NewestTaken"/>'s answer for those.</param>
    /// <param name="restorableBytes">What those backups occupy, summed.</param>
    public static BackupStanding For(
        IReadOnlyList<BackupRecord> history,
        int restorableCount,
        DateTimeOffset? newestTakenUtc,
        long restorableBytes)
    {
        ArgumentNullException.ThrowIfNull(history);

        // Walked newest-first and stopped at the first success rather than graded
        // wholesale, because Outcome() BUILDS SENTENCES: it composes the blocked,
        // fallback and damaged detail into a string for every record it is asked
        // about. The history table already pays that per row it renders, and this
        // runs on every render of a list that grows one entry per backup for the
        // life of the instance. Once a success is seen, nothing older can change
        // either answer, so the common case reads one record.
        BackupSeverity? newestVerdict = null;
        var newestPartly = false;
        var everSucceeded = false;

        foreach (var record in history.Where(r => !r.IsImported).OrderByDescending(r => r.StartedUtc))
        {
            var severity = record.Outcome().Severity;

            // Skips say nothing about whether backups work, so the newest run
            // carrying a verdict is the newest one that is not a skip.
            if (severity == BackupSeverity.Skipped)
            {
                continue;
            }

            // A run that failed for one backup type and worked for another left a
            // backup, so it is a success here as well as a failure (`T302`).
            var partly = severity == BackupSeverity.Failed && WorkedForAType(record);

            if (newestVerdict is null)
            {
                newestVerdict = severity;
                newestPartly = partly;
            }

            if (severity is BackupSeverity.Ok or BackupSeverity.Warning || partly)
            {
                everSucceeded = true;
                break;
            }
        }

        // Warning is a success with something to mention — the backup exists —
        // so only Failed counts against the machinery here.
        var failing = newestVerdict == BackupSeverity.Failed;

        var state = (restorableCount > 0, history.Count > 0, everSucceeded, failing) switch
        {
            (true, _, _, true) when newestPartly => BackupStandingState.ProtectedButPartlyFailing,
            (true, _, _, true) => BackupStandingState.ProtectedButFailing,
            (true, _, _, false) => BackupStandingState.Protected,

            // Nothing to restore. An empty history means nothing has been tried;
            // otherwise it turns on whether anything has ever worked, because
            // "every run failed" and "the runs worked and left nothing a restore
            // can use" are different problems with different remedies.
            (false, false, _, _) => BackupStandingState.NothingYet,
            (false, true, true, _) => BackupStandingState.NothingToRestore,

            // Nothing has ever succeeded. `failing` separates the two ways that
            // happens: a newest verdict of Failed means runs were tried and lost,
            // while no verdict at all means every record is a skip and nothing
            // has been tried. Only the first is something to tell somebody about.
            (false, true, false, true) => BackupStandingState.NeverSucceeded,
            (false, true, false, false) => BackupStandingState.NothingYet,
        };

        return new BackupStanding(
            state,
            restorableCount > 0 ? newestTakenUtc : null,
            restorableCount,
            restorableCount > 0 ? restorableBytes : 0);
    }

    /// <summary>
    /// When the newest backup on disk that this server took itself was taken, or
    /// null when every one on disk was imported or there are none: the card's
    /// Last backup.
    /// </summary>
    /// <remarks>
    /// An imported backup is restorable, so it counts in
    /// <see cref="BackupStanding.RestorableCount"/>, but it says nothing about
    /// when this server was last backed up, and the Backups overview's Last backup
    /// already leaves imports out (<c>T359</c> clause 7). A file is an import when
    /// an imported record names it. Matched by file name, as the notes are, since a
    /// stored path goes stale when the backup folder moves (<c>T124</c>) and both
    /// sides belong to one server.
    /// </remarks>
    /// <param name="onDisk">Each backup on disk: its path and when it was taken.</param>
    /// <param name="history">Every run recorded for this server.</param>
    public static DateTimeOffset? NewestTaken(
        IEnumerable<(string Path, DateTimeOffset TakenUtc)> onDisk,
        IReadOnlyList<BackupRecord> history)
    {
        ArgumentNullException.ThrowIfNull(onDisk);
        ArgumentNullException.ThrowIfNull(history);

        var imported = history
            .Where(r => r.IsImported)
            .SelectMany(r => r.Strategies)
            .Where(s => !string.IsNullOrEmpty(s.ArtifactPath))
            .Select(s => FileNameOf(s.ArtifactPath!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        DateTimeOffset? newest = null;

        foreach (var (path, taken) in onDisk)
        {
            if (!imported.Contains(FileNameOf(path)) && (newest is null || taken > newest))
            {
                newest = taken;
            }
        }

        return newest;
    }

    /// <summary>The last segment of a path written on either platform.</summary>
    private static string FileNameOf(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');

        return trimmed[(trimmed.LastIndexOfAny(['\\', '/']) + 1)..];
    }

    /// <summary>Did any backup type in this run write its backup?</summary>
    /// <remarks>
    /// Asked through <see cref="BackupRecord.OutcomeFor"/>, the verdict each of the
    /// run's rows on the timeline shows, so the card and the rows under it cannot
    /// disagree about whether a type worked.
    /// </remarks>
    private static bool WorkedForAType(BackupRecord record) =>
        record.Strategies.Any(s =>
            record.OutcomeFor(s).Severity is BackupSeverity.Ok or BackupSeverity.Warning);
}
