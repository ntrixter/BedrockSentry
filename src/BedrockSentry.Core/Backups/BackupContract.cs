namespace BedrockSentry.Core.Backups;

/// <summary>Why a backup ran. Recorded, and it changes the rules.</summary>
public enum BackupReason
{
    /// <summary>Asked for, in the UI or by chat command. Never skipped.</summary>
    Manual,

    /// <summary>On the schedule. Skipped when nobody has played since the last one.</summary>
    Scheduled,

    /// <summary>
    /// A player joined or left. Rate-limited by its own minimum gap rather than
    /// by the played-since-last rule — the trigger already *is* evidence that
    /// somebody played (<c>docs/design.md</c> § 8.1).
    /// </summary>
    PlayerActivity,

    /// <summary>
    /// Part of a graceful shutdown. The server is already stopped when this
    /// runs, so the hold/query/resume exchange is skipped — the files are
    /// static (<c>docs/design.md</c> § 8.1).
    /// </summary>
    Shutdown,

    /// <summary>
    /// Taken automatically before a restore overwrites the world
    /// (<c>docs/design.md</c> § 8.5), so "restore the wrong backup" is itself
    /// recoverable.
    /// </summary>
    Safety,
}

/// <summary>
/// Everything a strategy gets to work with while saves are held.
/// </summary>
/// <param name="WorldsDirectory">
/// The instance's <c>worlds</c> folder. Manifest paths are relative to this and
/// do not name it (<c>docs/design.md</c> § 5.5).
/// </param>
/// <param name="Manifest">
/// What belongs in the snapshot, with the byte count each copy must truncate
/// to. For a <see cref="BackupReason.Shutdown"/> backup this is a walk of the
/// stopped server's world files instead, with full lengths — the server is not
/// running, so nothing is mid-write and there is no manifest to ask for.
/// </param>
/// <param name="ArtifactDirectory">
/// Where this instance's backup artifacts belong. Created before any strategy
/// runs.
/// </param>
public sealed record BackupContext(
    string InstanceId,
    string InstanceName,
    string WorkingDirectory,
    string WorldsDirectory,
    IReadOnlyList<SaveManifestEntry> Manifest,
    string ArtifactDirectory,
    BackupSettings Settings,
    BackupReason Reason,
    string? Note,
    DateTimeOffset StartedUtc);

/// <summary>What one strategy did with its turn.</summary>
public sealed record BackupStrategyResult(
    string Strategy,
    bool Succeeded,
    string? Detail,
    string? ArtifactPath);

/// <summary>
/// A strategy the operator switched on that is not permitted to run on this
/// install (<c>D45</c>).
/// </summary>
/// <remarks>
/// <para><strong>Not the same thing as a strategy that is off, and the difference
/// is the whole reason this type exists.</strong> "Off" is a settings question: it
/// produces no record, and is rightly silent. This is configured work that did not
/// happen — and a backup that quietly does less than the operator asked for is the
/// failure the gate must not introduce while it is busy removing a remote
/// shell.</para>
///
/// <para>It carries the remedy as well as the reason, because the person reading
/// it in a history row days later is the person who has to act on it, and they
/// will not have the startup log in front of them.</para>
/// </remarks>
public sealed record BlockedStrategy(string Strategy, string Reason, string HowToEnable)
{
    /// <summary>The sentence a history row, a log line and the panel all carry.</summary>
    public string Detail => $"{Reason} {HowToEnable}";
}

/// <summary>
/// One backup strategy (<c>docs/design.md</c> § 8). All enabled strategies run in
/// parallel against the same held snapshot; adding a strategy later means
/// writing one new class, not touching the orchestrator.
/// </summary>
public interface IBackupStrategy
{
    /// <summary>Stable name, used in settings and history ("custom-command", "zip", "vss").</summary>
    string Name { get; }

    /// <summary>Whether this strategy is turned on for the given instance.</summary>
    bool IsEnabled(BackupSettings settings);

    /// <summary>
    /// Why this strategy may not run on this install even though
    /// <see cref="IsEnabled"/> says it is on, or null when nothing is stopping it.
    /// </summary>
    /// <remarks>
    /// <para>Separate from <see cref="IsEnabled"/> on purpose. A strategy dropped
    /// by <c>IsEnabled</c> is one nobody asked for; one dropped by this is one
    /// somebody did, and the orchestrator records the difference — which is what
    /// stops a gate turning into a backup that silently does less than it was told
    /// to.</para>
    ///
    /// <para><strong>The gate belongs to the strategy that needs one</strong>,
    /// never to the orchestrator, which has to go on knowing nothing about which
    /// strategies exist. Today only the custom command answers anything but null;
    /// <c>VssBackupStrategy</c> is the obvious next one, since a service account
    /// without the backup privilege cannot take a shadow copy however the panel is
    /// configured.</para>
    ///
    /// <para>It takes the settings even though the one live implementation ignores
    /// them: symmetry with <see cref="IsEnabled"/>, no cost, and a block that is
    /// per-instance rather than install-wide is a plausible thing to want.</para>
    /// </remarks>
    BlockedStrategy? Blocked(BackupSettings settings);

    /// <summary>
    /// Does the work. Saves are held for the duration on a live server, so the
    /// world files will not change underneath it — but they may be longer on
    /// disk than the manifest says, and only the manifest lengths are real.
    /// </summary>
    /// <remarks>
    /// A strategy that fails must throw or return a failed result; either is
    /// recorded. It must never hang forever — the orchestrator will not resume
    /// saves until every strategy has come back, and a live server with saves
    /// held is the worst state in this codebase.
    /// </remarks>
    Task<BackupStrategyResult> RunAsync(BackupContext context, CancellationToken cancellationToken);
}

/// <summary>
/// What became of the <c>save resume</c> that follows every live backup.
/// </summary>
/// <remarks>
/// <c>T158</c>. Leaving a server holding saves is the
/// worst failure mode in this codebase, and until now the record that watches
/// backups could not see it: the resume ran in a <c>finally</c>, returned nothing,
/// and its only trace was a <c>LogCritical</c> in the Windows event log that
/// nobody read.
/// </remarks>
public enum SaveResumeOutcome
{
    /// <summary>The measured reply arrived. Saves are running.</summary>
    Confirmed,

    /// <summary>
    /// The process had already gone, so there was nothing left holding saves.
    /// Benign, and deliberately not a failure.
    /// </summary>
    ServerGone,

    /// <summary>The command could not be sent at all.</summary>
    NotSent,

    /// <summary>
    /// Sent, and the confirmation never came within the deadline. The server may
    /// still be holding saves, which means it is writing nothing to disk.
    /// </summary>
    Unconfirmed,
}

/// <summary>How a record reads on a page: a word, and how alarming it is.</summary>
/// <param name="Label">The badge text.</param>
/// <param name="Severity">Which badge style applies.</param>
/// <param name="Detail">A sentence when there is something to add, else null.</param>
public sealed record BackupOutcome(string Label, BackupSeverity Severity, string? Detail);

/// <summary>How alarming a <see cref="BackupOutcome"/> is.</summary>
public enum BackupSeverity
{
    /// <summary>Nothing happened and nothing was meant to.</summary>
    Skipped,

    /// <summary>It worked.</summary>
    Ok,

    /// <summary>It worked, but something about it needs a person.</summary>
    Warning,

    /// <summary>It did not work.</summary>
    Failed,
}

/// <summary>How a <see cref="BackupSeverity"/> is styled on a page.</summary>
/// <remarks>
/// <para>
/// <strong>A CSS class name in <c>Core</c> is deliberate, and the alternative was
/// worse.</strong> This mapping existed as two byte-identical copies, in
/// <c>BackupsTab.razor</c> and <c>Backups.razor</c> — two files the test project
/// cannot reach, one enum member away from disagreeing about how a backup looks.
/// That is the shape <c>T158</c> was filed about, found again while confirming it
/// (<c>T158</c>, 2026-09-12). <see cref="BackupOutcome.Label"/> is display text and
/// already lives here; the style that goes with the word belongs beside it.
/// </para>
/// <para>
/// The default arm is a neutral badge rather than a throw, because a render path
/// must not fail over an unknown enum member — but a new member landing there
/// silently is exactly how the two copies would have drifted, so
/// <c>BackupBadgeTests</c> asserts that every member except
/// <see cref="BackupSeverity.Skipped"/> maps to a style of its own. Adding one fails
/// a test instead of shipping an unstyled badge.
/// </para>
/// </remarks>
public static class BackupSeverityStyles
{
    /// <summary>The badge classes for <paramref name="severity"/>.</summary>
    public static string BadgeClass(this BackupSeverity severity) => severity switch
    {
        BackupSeverity.Ok => "state-badge state-running",
        BackupSeverity.Warning => "state-badge state-warning",
        BackupSeverity.Failed => "state-badge state-faulted",

        // Skipped, and anything added later that nobody has styled yet.
        _ => "state-badge",
    };
}

/// <summary>What happened, kept in the per-instance history.</summary>
public sealed record BackupRecord(
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    BackupReason Reason,
    string? Note,
    bool Skipped,
    string? SkipReason,
    bool Succeeded,
    IReadOnlyList<BackupStrategyResult> Strategies,
    /// <summary>
    /// What became of this backup's <c>save resume</c>, or null when there was
    /// not one to have an outcome (<c>T158</c>).
    /// </summary>
    /// <remarks>
    /// <para><strong>Nullable, and with a default, for a reason that is easy to
    /// get wrong.</strong> These records are serialised to <c>history.json</c>. A
    /// non-nullable field would bind to its <c>default</c> on every record written
    /// before it existed — so a <c>bool</c> would read <c>false</c> and turn every
    /// historic backup into one that left a server holding saves. Null means "no
    /// console exchange happened, or this record predates the field", and a reader
    /// must say nothing rather than guess.</para>
    ///
    /// <para>Null is also correct for the cases that never resume: a backup of a
    /// stopped server, which is archived straight off disk with no console at all;
    /// both skip paths; the safety copy a restore takes; and the timeline note
    /// left when an orphan is ended.</para>
    /// </remarks>
    SaveResumeOutcome? SavesResumed = null,

    /// <summary>
    /// Strategies this instance had switched on that were not permitted to run
    /// (<c>D45</c>), or null when there were none.
    /// </summary>
    /// <remarks>
    /// <para><strong>Nullable and defaulted for the same reason
    /// <see cref="SavesResumed"/> is, and it is the same trap in another
    /// shape.</strong> These records are serialised to <c>history.json</c>, and
    /// every record written before this field existed binds it to
    /// <c>default</c>. Null and empty both mean "nothing was blocked" and no
    /// reader may tell them apart — which is why nothing reads this property
    /// directly. Read <see cref="Blocked"/>.</para>
    ///
    /// <para>The orchestrator writes null rather than an empty list on an
    /// ordinary run, so <c>WhenWritingNull</c> keeps the key out of the file
    /// altogether and an install that never trips the gate sees no change to a
    /// file it is invited to read by hand.</para>
    /// </remarks>
    IReadOnlyList<BlockedStrategy>? BlockedStrategies = null)
{
    public TimeSpan Duration => FinishedUtc - StartedUtc;

    /// <summary>The blocked strategies, never null. Records from before have none.</summary>
    public IReadOnlyList<BlockedStrategy> Blocked => BlockedStrategies ?? [];

    /// <summary>
    /// How this record reads on a page.
    /// </summary>
    /// <remarks>
    /// <para>Here rather than in the pages because there are <em>two</em> of them —
    /// the instance Backups tab and the all-instances Backups page — and each
    /// carried its own <c>Skipped</c> / <c>Succeeded</c> / else ladder. That is
    /// this project's recurring defect sitting in two files waiting to diverge,
    /// and neither file can be reached by the test suite.</para>
    ///
    /// <para><strong><c>Succeeded</c> still means "every strategy succeeded", and
    /// an unconfirmed resume does not change it.</strong> The archive really is
    /// fine, and making it read as failed would also hide it from
    /// <c>LastSuccessfulUtcAsync</c> — so "last backup" would go blank and the
    /// skip rule would start backing up on every tick. What an unconfirmed resume
    /// changes is how loud the row is, which is the thing that was missing.</para>
    /// </remarks>
    public BackupOutcome Outcome()
    {
        if (Skipped)
        {
            return new BackupOutcome("Skipped", BackupSeverity.Skipped, SkipReason);
        }

        // Asked once and joined into every arm below (`D45`). A block is never the
        // whole of what a row has to say, and it is never a reason to leave
        // something else unsaid.
        var blocked = BlockedDetail();

        if (!Succeeded)
        {
            // Composed, not replaced, and this is the load-bearing line. Describe()
            // over an empty sequence returns null, and Strategies IS empty when the
            // only strategy the instance had was blocked — so without the join this
            // arm renders "Failed" with no explanation at all, for the one case that
            // most needs one.
            return new BackupOutcome(
                "Failed",
                BackupSeverity.Failed,
                Sentences(Describe(Strategies.Where(s => !s.Succeeded)), blocked));
        }

        return SavesResumed switch
        {
            // `T173`. This used to end "check its console, and send 'save resume' by
            // hand if it is still held" — the same advice `T170` took out of the
            // console notice, left behind here. It is worse here than it was there.
            // BDS answers a hand-typed `save resume` with "A previous save has not
            // been completed." whenever no hold is outstanding, which is to say
            // whenever the resume already worked, so following it produces a
            // frightening line about a world that is fine. And this text is read on
            // a Backups page days later, where there is nothing to check against:
            // the reader has no way to tell whether saves are held, which is
            // exactly what the old sentence asked them to decide.
            SaveResumeOutcome.Unconfirmed => new BackupOutcome(
                "OK, saves unconfirmed",
                BackupSeverity.Warning,
                Sentences(
                    "The backup is complete, but no confirmation that world saves resumed arrived "
                    + "before the wrapper stopped waiting. Either the server is still holding saves "
                    + "— in which case it is writing nothing to disk — or the confirmation was "
                    + "missed. The console transcript for this time settles it: \"Changes to the "
                    + "world are resumed\" means the resume worked and this row is a false alarm.",
                    blocked)),

            // Deliberately NOT reworded alongside the branch above, and the
            // difference is the whole point: here the command never reached the
            // server, so the world genuinely IS held and typing it by hand is both
            // correct and necessary. Unconfirmed means we do not know; this means we
            // do. Anybody unifying these two sentences would restore `T173` on the
            // branch that cannot afford it.
            SaveResumeOutcome.NotSent => new BackupOutcome(
                "OK, resume not sent",
                BackupSeverity.Warning,
                Sentences(
                    "The backup is complete, but 'save resume' could not be sent to the server. "
                    + "Check its console and send it by hand.",
                    blocked)),

            // ServerGone is not a warning: a process that has exited is holding
            // nothing, and flagging it would make every backup-on-shutdown look
            // like a problem.
            _ => blocked is null
                ? new BackupOutcome("OK", BackupSeverity.Ok, Describe(Strategies))
                : new BackupOutcome(
                    BlockedLabel(), BackupSeverity.Warning, Sentences(Describe(Strategies), blocked)),
        };
    }

    /// <summary>
    /// The badge when the archive is fine and something the operator switched on
    /// was refused.
    /// </summary>
    /// <remarks>
    /// <c>Warning</c> rather than a severity of its own: it already means "it
    /// worked, but a person is needed", which is exactly this, and a new member
    /// would trip the guard that every severity has a style without buying
    /// anything.
    /// </remarks>
    private string BlockedLabel() =>
        Blocked.Count == 1 ? $"OK, {Blocked[0].Strategy} blocked" : "OK, strategies blocked";

    private string? BlockedDetail() =>
        Blocked.Count == 0
            ? null
            : string.Join(" ", Blocked.Select(b => $"{b.Strategy}: {b.Detail}"));

    /// <summary>
    /// Joins two optional sentences, keeping whichever are there.
    /// </summary>
    /// <remarks>
    /// Used so a block is <em>appended</em> to a resume warning rather than
    /// replacing it. Both are <c>Warning</c>, so the only question is which wins
    /// the label, and a possibly-held world outranks a configuration fact that is
    /// also on the panel, in the startup log and on every later row. Unify these
    /// into one sentence and the branch that cannot afford it loses its own — the
    /// shape `T173` was reintroduced by.
    /// </remarks>
    private static string? Sentences(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return string.IsNullOrWhiteSpace(second) ? null : second;
        }

        return string.IsNullOrWhiteSpace(second) ? first : $"{first} {second}";
    }

    private static string? Describe(IEnumerable<BackupStrategyResult> strategies)
    {
        var detail = string.Join(
            "; ",
            strategies
                .Where(s => !string.IsNullOrWhiteSpace(s.Detail))
                .Select(s => $"{s.Strategy}: {s.Detail}"));

        return detail.Length > 0 ? detail : null;
    }
}

/// <summary>Per-instance backup configuration, bound from the config file.</summary>
public sealed class BackupSettings
{
    public CustomCommandBackupSettings CustomCommand { get; set; } = new();

    public ZipBackupSettings Zip { get; set; } = new();

    public VssBackupSettings Vss { get; set; } = new();

    public BackupSchedule Schedule { get; set; } = new();

    public BackupOnShutdownSettings OnShutdown { get; set; } = new();

    public BackupOnPlayerActivitySettings OnPlayerActivity { get; set; } = new();

    /// <summary>
    /// Whether <paramref name="other"/> holds the same settings, field for field.
    /// </summary>
    /// <remarks>
    /// <para><c>T150</c>. The Backups tab decided whether its Save button was
    /// enabled with a hand-written seventeen-clause comparison, and two of the
    /// seventeen were missing — so ticking "Back up before a Bedrock update" on its
    /// own left Save disabled and the change was discarded on the next load, with
    /// no warning and no unsaved indicator. Both were saved correctly; only the
    /// predicate was short.</para>
    ///
    /// <para>These are mutable classes bound from configuration, so they cannot
    /// become records and get this for free: the binder writes through setters,
    /// several places mutate them in place, and <c>Definition.Backup</c> is
    /// reassigned wholesale on save. So the comparison is written out — but the
    /// <em>guard</em> is a reflective test that walks every settable leaf of this
    /// type and its six children and fails if any one of them does not make two
    /// instances compare as different. A field added later is covered whether
    /// anybody remembers this method or not, which is the only way this class of
    /// defect stops recurring.</para>
    ///
    /// <para>Three other panels compare field by field the same way and can fail
    /// the same way — <c>PlayerDetail</c>, and both dirty checks on the Settings
    /// page. Only this one had a proven omission, so only this one moved; the
    /// pattern to copy if another turns up is here.</para>
    /// </remarks>
    public bool SameAs(BackupSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return CustomCommand.Enabled == other.CustomCommand.Enabled
            && string.Equals(CustomCommand.Command, other.CustomCommand.Command, StringComparison.Ordinal)

            && Zip.Enabled == other.Zip.Enabled
            && Zip.Compression == other.Zip.Compression
            && Zip.Retention.Mode == other.Zip.Retention.Mode
            && Zip.Retention.Value == other.Zip.Retention.Value

            && Vss.Enabled == other.Vss.Enabled
            && Vss.Retention.Mode == other.Vss.Retention.Mode
            && Vss.Retention.Value == other.Vss.Retention.Value

            && Schedule.Mode == other.Schedule.Mode
            && Schedule.IntervalHours == other.Schedule.IntervalHours
            && Schedule.IntervalMinutes == other.Schedule.IntervalMinutes
            && Schedule.TimeOfDay == other.Schedule.TimeOfDay

            && OnShutdown.OnScheduledRestart == other.OnShutdown.OnScheduledRestart
            && OnShutdown.OnWrapperUpdate == other.OnShutdown.OnWrapperUpdate
            && OnShutdown.OnServerUpdate == other.OnShutdown.OnServerUpdate

            && OnPlayerActivity.OnJoin == other.OnPlayerActivity.OnJoin
            && OnPlayerActivity.OnLeave == other.OnPlayerActivity.OnLeave
            && OnPlayerActivity.MinimumGapMinutes == other.OnPlayerActivity.MinimumGapMinutes;
    }
}

/// <summary>
/// When scheduled backups run. The same two shapes as the restart schedule and
/// deliberately not cron, for the same § 5.3 reason — and the arithmetic,
/// including both daylight-saving cases (<c>D18</c>), is delegated to
/// <see cref="Instances.RestartSchedule"/> rather than solved twice.
/// </summary>
public sealed class BackupSchedule
{
    public Instances.RestartScheduleMode Mode { get; set; } = Instances.RestartScheduleMode.None;

    /// <summary>Hours between backups, measured from the last attempt.</summary>
    public int IntervalHours { get; set; } = 6;

    /// <summary>Minutes between backups, measured from the last attempt.</summary>
    public int IntervalMinutes { get; set; } = 30;

    /// <summary>Time of day for a daily backup, local time.</summary>
    public TimeOnly TimeOfDay { get; set; } = new(4, 0);

    /// <summary>
    /// The gap this schedule asks for, or null when it is not an interval
    /// schedule. Delegated rather than recomputed, so the two schedules cannot
    /// disagree about what "every N" means.
    /// </summary>
    public TimeSpan? Interval =>
        new Instances.RestartSchedule
        {
            Mode = Mode,
            IntervalHours = IntervalHours,
            IntervalMinutes = IntervalMinutes,
        }.Interval;

    /// <summary>The next daily occurrence after <paramref name="after"/>.</summary>
    public DateTimeOffset? NextDailyOccurrence(DateTimeOffset after, TimeZoneInfo? timeZone = null) =>
        new Instances.RestartSchedule { Mode = Mode, TimeOfDay = TimeOfDay }
            .NextOccurrence(after, null, timeZone);
}

/// <summary>
/// Backups triggered by players arriving and leaving (<c>docs/design.md</c> § 8.1).
/// </summary>
/// <remarks>
/// The appetite these serve is "capture what somebody built before they log
/// off", which a fixed schedule serves badly — it either runs constantly on an
/// empty server or misses the session that mattered.
///
/// <see cref="MinimumGap"/> is not optional decoration. A player on a poor
/// connection can join and drop a dozen times in a minute, and every one of
/// those is a hold-query-copy-resume against a live server. The gap is what
/// separates a useful trigger from a way to pause saves permanently.
/// </remarks>
public sealed class BackupOnPlayerActivitySettings
{
    /// <summary>Back up when somebody joins.</summary>
    public bool OnJoin { get; set; }

    /// <summary>
    /// Back up when somebody leaves — the more useful of the two, since their
    /// session's work is on disk by then.
    /// </summary>
    public bool OnLeave { get; set; }

    /// <summary>
    /// Shortest gap between two activity-triggered backups on one instance.
    /// Ignored by manual and scheduled backups, which have their own rules.
    /// </summary>
    public int MinimumGapMinutes { get; set; } = 15;

    public bool Enabled => OnJoin || OnLeave;

    public TimeSpan MinimumGap => TimeSpan.FromMinutes(Math.Max(0, MinimumGapMinutes));
}

/// <summary>
/// The § 8.1 backup-on-shutdown toggles — one per caller of the graceful stop,
/// independently, because "back up before an update" and "back up on the
/// nightly restart" are different appetites for disk.
/// </summary>
public sealed class BackupOnShutdownSettings
{
    public bool OnScheduledRestart { get; set; }

    public bool OnWrapperUpdate { get; set; }

    public bool OnServerUpdate { get; set; }
}

/// <summary>The ZIP strategy (<c>docs/design.md</c> § 8.3).</summary>
public sealed class ZipBackupSettings
{
    public bool Enabled { get; set; }

    public ZipCompression Compression { get; set; } = ZipCompression.Balanced;

    public BackupRetention Retention { get; set; } = new();
}

/// <summary>
/// The § 8.3 compression slider. Three stops rather than a raw level, because
/// the trade is not linear: <see cref="None"/> stores bytes as they are, which
/// is what deduplicating backup tools want underneath them; <see cref="Maximum"/>
/// buys ratio with time on a snapshot whose saves are held while it runs.
/// </summary>
public enum ZipCompression
{
    None,
    Balanced,
    Maximum,
}

/// <summary>
/// The VSS strategy (<c>docs/design.md</c> § 8.4): a Windows point-in-time snapshot of
/// the volume, copied out to a folder.
/// </summary>
/// <remarks>
/// Off by default and not merely because it is new. Taking a shadow copy needs
/// the service account to hold the backup privilege, and a strategy enabled on a
/// machine that cannot grant it fails every scheduled backup at three in the
/// morning — which is the failure the Readiness panel exists to predict
/// (<c>docs/design.md</c> § 14).
/// </remarks>
public sealed class VssBackupSettings
{
    public bool Enabled { get; set; }

    public BackupRetention Retention { get; set; } = new();
}

/// <summary>
/// How long archives live (<c>docs/design.md</c> § 8.3): by count or by age, shared by
/// ZIP and VSS.
/// </summary>
/// <remarks>
/// A mode and one value, rather than two nullable knobs — with both a count and
/// an age set there is no answer to "which wins" that is not a surprise to
/// somebody.
/// </remarks>
public sealed class BackupRetention
{
    public BackupRetentionMode Mode { get; set; } = BackupRetentionMode.KeepAll;

    /// <summary>Archives for <see cref="BackupRetentionMode.Count"/>, days for <see cref="BackupRetentionMode.Days"/>.</summary>
    public int Value { get; set; }
}

public enum BackupRetentionMode
{
    KeepAll,
    Count,
    Days,
}

/// <summary>
/// The custom-command strategy (<c>docs/design.md</c> § 8.2): a user-provided command,
/// run once the snapshot is ready.
/// </summary>
public sealed class CustomCommandBackupSettings
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Run through the platform shell, so pipelines and script files both work.
    /// Context arrives as environment variables rather than arguments, because
    /// paths with spaces survive that without any quoting rules to get wrong.
    /// </summary>
    public string Command { get; set; } = "";
}
