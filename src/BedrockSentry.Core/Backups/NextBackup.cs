using BedrockSentry.Core.Instances;

namespace BedrockSentry.Core.Backups;

/// <summary>
/// Whether anything is going to back this instance up on its own, and when.
/// </summary>
/// <remarks>
/// Four states, because "a time" cannot carry the three answers that are not a
/// time, and each of the three is a different thing to tell an administrator.
/// </remarks>
public enum NextBackupState
{
    /// <summary>A scheduled run is due at a time still in the future.</summary>
    At,

    /// <summary>
    /// The interval has already elapsed, so the next tick takes it.
    /// </summary>
    /// <remarks>
    /// Its own state rather than a time in the past, because the scheduler treats
    /// overdue as due <em>now</em> — it does not skip to the next slot the way a
    /// restart does — and rendering a negative countdown for that would be a
    /// number nothing is counting towards.
    /// </remarks>
    DueNow,

    /// <summary>
    /// No schedule, but something else will still take a backup: a player event,
    /// a scheduled restart, an update. Nothing is on a clock.
    /// </summary>
    EventsOnly,

    /// <summary>
    /// Nothing will back this instance up unless somebody asks. The one state
    /// worth saying loudly, and the one a bare time can never express.
    /// </summary>
    Nothing,
}

/// <summary>
/// When this instance's next unattended backup is due (<c>T248</c>).
/// </summary>
/// <param name="At">
/// The moment, on <see cref="NextBackupState.At"/> alone. Null on every other
/// state: <see cref="NextBackupState.DueNow"/> deliberately does not carry the
/// elapsed time it passed, because a caller that rendered it would print a
/// moment in the past as though it were coming.
/// </param>
public readonly record struct NextBackup(NextBackupState State, DateTimeOffset? At = null)
{
    /// <summary>
    /// Whether the schedule says a run is owed right now. What the scheduler
    /// asks.
    /// </summary>
    public bool IsDue => State == NextBackupState.DueNow;

    /// <summary>
    /// Where the schedule's interval counts from: the last run's finish, or
    /// <paramref name="now"/> when there has been none.
    /// </summary>
    /// <remarks>
    /// One rule for the scheduler and the Next run card (<c>T248</c>). An imported backup is
    /// not a run, and its time is when it was taken elsewhere (<c>D110</c>).
    /// </remarks>
    /// <param name="records">The history, oldest first.</param>
    public static DateTimeOffset AnchorFrom(IReadOnlyList<BackupRecord> records, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(records);
        return records.LastOrDefault(r => !r.IsImported)?.FinishedUtc ?? now;
    }

    /// <summary>
    /// When the next unattended backup of this instance is due.
    /// </summary>
    /// <param name="settings">This instance's backup settings.</param>
    /// <param name="anchor">
    /// What an interval is measured from — the last attempt's finish, which is
    /// what <c>BackupScheduler</c> anchors on. Passed in rather than read here
    /// so the scheduler and the panel cannot end up measuring from two different
    /// moments; <c>Core</c> has no history to read anyway.
    /// </param>
    /// <param name="now">The current moment, UTC.</param>
    /// <param name="timeZone">
    /// The zone a daily schedule's time of day is read in. Null means the
    /// machine's own, which is what the scheduler passes.
    /// </param>
    /// <remarks>
    /// <para><strong>This is the scheduler's own arithmetic, and the scheduler
    /// calls it.</strong> Writing the panel a second copy would be this
    /// repository's recurring defect in the form that lies to somebody: a card
    /// reading "in 3h 46m" beside a scheduler that fires at a different moment.
    /// Sharing the implementation is only safe because both callers want the
    /// <em>same</em> answer here — the panel wants the moment, the scheduler
    /// wants whether that moment has passed, and <see cref="IsDue"/> is the
    /// second read off the first (<c>T151</c> clause 6 is the case where they do
    /// not, and this is not one).</para>
    ///
    /// <para><strong>It says when a run is CONSIDERED, not when one happens.</strong>
    /// A scheduled run is skipped when nobody has played since the last one, so
    /// the moment this returns is when the question gets asked. A surface that
    /// renders it as a promise of an archive is overstating it.</para>
    /// </remarks>
    public static NextBackup For(
        BackupSettings settings,
        DateTimeOffset anchor,
        DateTimeOffset now,
        TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var schedule = settings.Schedule;

        if (schedule.Mode is RestartScheduleMode.EveryNHours or RestartScheduleMode.EveryNMinutes)
        {
            // A mode that names an interval and cannot produce one is not a
            // schedule. Falling through to the event answer is right: whatever
            // is wrong with the value, nothing is going to fire on a clock.
            if (schedule.Interval is { } interval)
            {
                var due = anchor + interval;

                return now >= due
                    ? new NextBackup(NextBackupState.DueNow)
                    : new NextBackup(NextBackupState.At, due);
            }
        }
        else if (schedule.Mode != RestartScheduleMode.None)
        {
            if (schedule.NextDailyOccurrence(now, timeZone) is { } daily)
            {
                return now >= daily
                    ? new NextBackup(NextBackupState.DueNow)
                    : new NextBackup(NextBackupState.At, daily);
            }
        }

        return AnythingElseFires(settings)
            ? new NextBackup(NextBackupState.EventsOnly)
            : new NextBackup(NextBackupState.Nothing);
    }

    /// <summary>
    /// Whether any non-schedule trigger would take a backup.
    /// </summary>
    /// <remarks>
    /// Every trigger the orchestrator has a caller for, so that
    /// <see cref="NextBackupState.Nothing"/> means it. A trigger added later and
    /// not added here turns "nothing backs this up" into a false alarm, which is
    /// the opposite failure from the one this state exists to prevent and is why
    /// <c>ATriggerAddedLaterIsNotSilentlyIgnored</c> walks this type reflectively
    /// rather than trusting the list.
    /// </remarks>
    private static bool AnythingElseFires(BackupSettings settings) =>
        settings.OnPlayerActivity.Enabled
        || settings.OnShutdown.OnScheduledRestart
        || settings.OnShutdown.OnServerUpdate
        || settings.OnShutdown.OnWrapperUpdate;
}
