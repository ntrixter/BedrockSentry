using System.Text.Json.Serialization;

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

    /// <summary>
    /// Brought in from outside through the Backups tab (<c>D110</c>). Never a run: it
    /// copies no world, and its time is when the backup was taken, not when it arrived.
    /// </summary>
    Imported,
}

/// <summary>Which backups wait while their server is claimed by a job, such as a sync.</summary>
public static class BackupClaimRule
{
    /// <summary>
    /// Whether a backup for <paramref name="reason"/> waits while its server is
    /// claimed (<see cref="Instances.InstanceStatus.ClaimedFor"/>).
    /// </summary>
    /// <remarks>
    /// <see cref="BackupReason.Shutdown"/> and <see cref="BackupReason.Safety"/>
    /// backups are taken by a claimed job inside its own stop. The others are asked
    /// for from outside it, and would copy a world the job is stopping, replacing or
    /// starting again.
    /// </remarks>
    public static bool WaitsForAClaim(BackupReason reason) => reason switch
    {
        BackupReason.Manual or BackupReason.Scheduled or BackupReason.PlayerActivity => true,
        BackupReason.Shutdown or BackupReason.Safety => false,

        // Copies nothing out of the world, so a job's stop changes nothing it reads.
        BackupReason.Imported => false,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unhandled backup reason."),
    };
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
/// <param name="SourceWorldsDirectory">
/// Where to READ the world files from: <paramref name="WorldsDirectory"/> itself,
/// or the same path inside a shadow copy when the orchestrator took one
/// (<c>D66</c>).
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
    string SourceWorldsDirectory,
    IReadOnlyList<SaveManifestEntry> Manifest,
    string ArtifactDirectory,
    BackupSettings Settings,
    BackupReason Reason,
    string? Note,
    DateTimeOffset StartedUtc,

    /// <summary>
    /// Artifact names an administrator has locked, which retention must neither
    /// remove nor count (<c>D68</c>). Empty when nothing is locked.
    /// </summary>
    /// <remarks>
    /// Read once per run by the orchestrator rather than once per strategy, for
    /// the reason the shadow copy is: two formats enabled together must prune
    /// against the same answer, and a second read could legitimately differ if
    /// somebody ticks a lock while a backup is running.
    /// </remarks>
    IReadOnlySet<string>? LockedArtifacts = null,

    /// <summary>
    /// The rest of the <c>worlds</c> folder, copied whole beside the manifest
    /// (<c>D97</c>). Null where the manifest is already every file, which is a
    /// stopped server's walk.
    /// </summary>
    BeyondTheSave? Others = null,

    /// <summary>
    /// Why the rest of the folder could not be listed, when it could not. The
    /// manifest is still copied: a backup holding the world and not the files beside
    /// it is worth more than none, the rule the configuration files follow.
    /// </summary>
    string? OthersUnlisted = null,

    /// <summary>
    /// The Bedrock version the server last reported, written beside the backup as
    /// <c>bds_version.txt</c> (<c>D110</c>); null when it has not reported one, and the
    /// file is then left out.
    /// </summary>
    string? ServerVersion = null,

    /// <summary>
    /// Told each time a strategy that <see cref="IBackupStrategy.CountsFiles"/> has passed one
    /// file of <see cref="Manifest"/> or <see cref="Beyond"/>, copied or left out, for the backup
    /// strip's bar (<c>D113</c>). Null where nothing is watching. Called from every such strategy
    /// at once, so it must be safe to call from several threads.
    /// </summary>
    Action? OnFileDone = null)
{
    /// <summary>Says one file is passed; see <see cref="OnFileDone"/>.</summary>
    public void FileDone() => OnFileDone?.Invoke();

    /// <summary>The files beyond the save, never null.</summary>
    public BeyondTheSave Beyond => Others ?? BeyondTheSave.None;

    /// <summary>The locked names, never null.</summary>
    public IReadOnlySet<string> Locked =>
        LockedArtifacts ?? System.Collections.Immutable.ImmutableHashSet<string>.Empty;

    /// <summary>
    /// Whether the world is being read from somewhere other than where it lives.
    /// </summary>
    /// <remarks>
    /// <para><strong>A strategy should not need this and none of them uses it to
    /// decide anything.</strong> It exists so a message can say where the bytes
    /// came from, which is the one thing the two paths genuinely differ about from
    /// a strategy's point of view — every other difference was deliberately moved
    /// into the orchestrator, because the whole of <c>D66</c> is that acquisition
    /// is not a format.</para>
    ///
    /// <para>Compared as paths rather than as a flag so there is no second field
    /// that can disagree with the first. Two statements of one fact is this
    /// codebase's recurring defect, and a <c>bool</c> beside two paths is exactly
    /// that shape.</para>
    /// </remarks>
    public bool ReadingFromACopy =>
        !string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(WorldsDirectory)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(SourceWorldsDirectory)),
            StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// How the world's bytes were obtained for one backup run (<c>D66</c>,
/// <c>D67</c>).
/// </summary>
/// <remarks>
/// <para><strong>Four states, because three of them are different in a way the
/// operator can act on and a <c>bool</c> can carry two.</strong> <c>T176</c> is
/// the standing lesson: a type with too few states will be made to lie, and the
/// tempting shape here — <c>bool UsedShadowCopy</c> — cannot tell <em>nobody
/// asked</em> from <em>asked and it could not be taken</em>, which is the only
/// distinction anybody reading a history row cares about.</para>
///
/// <para>It is separate from <see cref="BackupSettings.Acquisition"/> for the
/// same reason a record is separate from its settings everywhere else here: the
/// setting says what was wanted at some later moment of reading, and this says
/// what happened at the time.</para>
/// </remarks>
public enum BackupAcquisition
{
    /// <summary>The shadow-copy toggle was off. Read straight from the live world.</summary>
    NotRequested,

    /// <summary>
    /// The toggle was on and the server was not running, so there was nothing to
    /// hold and a snapshot would have bought nothing.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="NotRequested"/>: the toggle <em>is</em> on, and
    /// a row reading "not requested" on an install that requested it every time is
    /// the kind of small lie somebody eventually files a defect about.
    /// </remarks>
    NotApplicable,

    /// <summary>
    /// A shadow copy was taken and the world was read out of it, so saves were
    /// held only long enough to snapshot rather than for the whole copy.
    /// </summary>
    ShadowCopy,

    /// <summary>
    /// A shadow copy was asked for and could not be taken, so the world was read
    /// live and saves were held for the whole copy (<c>D67</c>).
    /// </summary>
    /// <remarks>
    /// A slower backup, not a lost one — which is why this never makes a run fail.
    /// </remarks>
    LiveFallback,
}

/// <summary>What one strategy did with its turn.</summary>
/// <param name="DamagedByPrune">
/// Artifacts this run's retention started deleting and could not finish, which is
/// a different thing from artifacts it left alone (<c>T229</c>).
/// </param>
/// <remarks>
/// <para><strong>The prune has three outcomes and a count could only say one of
/// them</strong> (<c>T176</c>). Removed is one; refused-and-intact is another, and
/// costs nothing but a stale artifact; <strong>refused-and-partly-deleted</strong>
/// is the third, and it is a backup that no longer exists reported as a backup that
/// does. <see cref="Directory.Delete(string, bool)"/> records the first exception,
/// keeps walking and keeps deleting, so the folder format reaches that third state
/// whenever anything inside refuses — measured at nine files down to one, the world
/// gone. The ZIP format cannot: <see cref="File.Delete"/> is atomic, so its refusal
/// is always the second state.</para>
///
/// <para>Swallowing it was <c>T229</c>. Before the catch was widened an
/// administrator got a failed run and a gutted archive; afterwards they got a
/// successful run and a gutted archive, and only a log line said otherwise. Better
/// on data — the new backup survives — and worse on disclosure, which is the half
/// this carries back to the record.</para>
/// </remarks>
public sealed record BackupStrategyResult(
    string Strategy,
    bool Succeeded,
    string? Detail,
    string? ArtifactPath,
    IReadOnlyList<string>? DamagedByPrune = null)
{
    /// <summary>The artifacts this run's prune damaged. Empty when none.</summary>
    /// <remarks>
    /// Not serialised: this record is written to <c>history.json</c>, and a
    /// computed property with no setter would be emitted beside the one it is
    /// computed from and silently dropped on the way back in — a second spelling of
    /// one fact, in a file that is read by hand.
    /// </remarks>
    [JsonIgnore]
    public IReadOnlyList<string> Damaged => DamagedByPrune ?? [];
}

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
    /// <remarks>
    /// <c>[JsonIgnore]</c> for <see cref="BackupRecord.Duration"/>'s reason. This
    /// one was the FOURTH and nobody had named it: it reached
    /// <c>history.json</c> as a third spelling of <c>Reason</c> and
    /// <c>HowToEnable</c>, and was found by the test written over the type rather
    /// than by the audit that listed the other three (<c>T236</c>'s commit).
    /// </remarks>
    [JsonIgnore]
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

    /// <summary>
    /// Whether this strategy copies the world's files itself and calls
    /// <see cref="BackupContext.FileDone"/> once for each, which is what the backup strip's
    /// bar counts (<c>D113</c>). A custom command hands the copy to another program and
    /// cannot say.
    /// </summary>
    bool CountsFiles => false;
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
    IReadOnlyList<BlockedStrategy>? BlockedStrategies = null,

    /// <summary>
    /// How this run got at the world's bytes (<c>D66</c>), or null when the
    /// record predates the field.
    /// </summary>
    /// <remarks>
    /// <para><strong>Nullable for the reason <see cref="SavesResumed"/> is, and the
    /// enum makes the trap sharper rather than softer.</strong> A non-nullable
    /// <see cref="BackupAcquisition"/> binds to <c>default</c> on every record
    /// written before it existed — and <c>default</c> is
    /// <see cref="BackupAcquisition.NotRequested"/>, a positive claim that the
    /// operator had the toggle off. Null says nothing, which is the only honest
    /// thing a record from before the toggle existed can say.</para>
    /// </remarks>
    BackupAcquisition? Acquisition = null,

    /// <summary>
    /// Why a shadow copy could not be taken, when
    /// <see cref="Acquisition"/> is <see cref="BackupAcquisition.LiveFallback"/>.
    /// Null otherwise.
    /// </summary>
    /// <remarks>
    /// Windows's own message, kept verbatim. It is usually the actionable half —
    /// a missing privilege, a stopped VSS service, a volume that cannot be
    /// snapshotted — and paraphrasing it would lose the part somebody searches
    /// for.
    /// </remarks>
    string? AcquisitionDetail = null,

    /// <summary>
    /// Why the run stopped before anything was copied, or null when it got as far
    /// as the strategies — whose own results then say how each one went
    /// (<c>T267</c>).
    /// </summary>
    /// <remarks>
    /// <para><strong>A run that never reached a strategy used to leave no record at
    /// all.</strong> The world walk, the <c>save hold</c> and the manifest wait all
    /// run before the first strategy, and anything they threw went past the only
    /// call that appends to the history. So an unreadable <c>worlds</c> directory
    /// made every backup fail while the standing card read <em>"No backups
    /// yet"</em> or <em>"Backed up"</em>, and a scheduled run's only witness was a
    /// line in the Windows Application log.</para>
    ///
    /// <para>Written only with <see cref="Succeeded"/> false and
    /// <see cref="Strategies"/> empty. It is the exception's own message, kept
    /// verbatim for the reason <see cref="AcquisitionDetail"/> is: it names the path
    /// or the reply that was missing, which is the half somebody acts on.</para>
    ///
    /// <para>Nullable and defaulted for <see cref="SavesResumed"/>'s reason: every
    /// record written before the field existed binds it to null, which says nothing
    /// and is the only honest thing an old record can say.</para>
    /// </remarks>
    string? Failure = null)
{
    /// <summary>
    /// Whether this backup was imported rather than taken (<c>D110</c>).
    /// </summary>
    /// <remarks>
    /// An imported backup may hold another server's world and carries the time it was
    /// taken, so it is never this server's "last backup": not on the standing card, not as
    /// the skip rule's "since the last backup", and not as the schedule's anchor.
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsImported => Reason is BackupReason.Imported;

    /// <summary>How long the run took.</summary>
    /// <remarks>
    /// <c>[JsonIgnore]</c> for the reason stated on <c>BackupStrategyResult.Damaged</c>
    /// two types up: this record is written to <c>history.json</c>, and a computed
    /// property with no setter is emitted beside the fields it is computed from and
    /// silently dropped on the way back in — a second spelling of one fact, in a
    /// file that is read by hand. That comment was added with one of the three and
    /// the other two were left; <c>BackupRecordSerialisationTests</c> now holds all
    /// of them, so the next computed member is caught rather than noticed.
    /// </remarks>
    [JsonIgnore]
    public TimeSpan Duration => FinishedUtc - StartedUtc;

    /// <summary>The blocked strategies, never null. Records from before have none.</summary>
    /// <remarks><c>[JsonIgnore]</c> for <see cref="Duration"/>'s reason.</remarks>
    [JsonIgnore]
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
    public BackupOutcome Outcome() => Compose(Strategies, Succeeded);

    /// <summary>
    /// How one format's backup from this run reads on a page (<c>T282</c>).
    /// </summary>
    /// <remarks>
    /// <para><strong>The same ladder as <see cref="Outcome"/>, asked of one
    /// strategy.</strong> A run with both formats on takes two backups of one
    /// instant, and the Backups tab lists each as its own row. The run's verdict
    /// cannot stand in for either: if the ZIP failed and the folder copy worked,
    /// the run is <em>Failed</em>, and reading that onto the folder row would flag
    /// a good backup — while reading the folder's <em>OK</em> onto the ZIP row
    /// would be the false clear.</para>
    ///
    /// <para><strong>What belongs to the run is said on every row of it</strong>:
    /// the resume, the shadow-copy fallback and a blocked strategy. Each row can be
    /// shown without the other — the Restorable filter drops a row whose file was
    /// pruned — so a sentence carried by only one of them can vanish from the
    /// page. What belongs to the strategy is said only on its own row: its detail,
    /// its success, and what its own retention destroyed.</para>
    ///
    /// <para>For a run with one strategy this is exactly <see cref="Outcome"/>,
    /// because the orchestrator writes <see cref="Succeeded"/> as every strategy's
    /// success and there is one. <c>BackupOutcomeTests</c> pins that.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="strategy"/> is not one of this run's <see cref="Strategies"/>.
    /// A row composed from one run's warnings and another run's result would
    /// describe a backup that never happened.
    /// </exception>
    public BackupOutcome OutcomeFor(BackupStrategyResult strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        if (!Strategies.Contains(strategy))
        {
            throw new ArgumentException(
                $"The {strategy.Strategy} result is not one of this run's strategies.",
                nameof(strategy));
        }

        return Compose([strategy], strategy.Succeeded);
    }

    /// <summary>
    /// The ladder <see cref="Outcome"/> and <see cref="OutcomeFor"/> share: the
    /// run's own facts, and whichever of its strategies the caller is asking about.
    /// </summary>
    /// <remarks>
    /// One ladder with two callers rather than two ladders, because a second copy is
    /// how the two pages that each had one came to disagree (<c>T158</c>). The
    /// callers differ only in which results they pass and what counts as success
    /// for them.
    /// </remarks>
    private BackupOutcome Compose(IReadOnlyList<BackupStrategyResult> strategies, bool succeeded)
    {
        if (Skipped)
        {
            return new BackupOutcome("Skipped", BackupSeverity.Skipped, SkipReason);
        }

        // Asked once and joined into every arm below (`D45`). A block is never the
        // whole of what a row has to say, and it is never a reason to leave
        // something else unsaid.
        var blocked = BlockedDetail();

        // The same rule one field along (`D67`). A fallback to the live world is a
        // third thing a row may have to say, and none of the three is a reason to
        // swallow either of the others.
        var fellBack = FallbackDetail();

        // And a fourth (`T229`). Retention that began deleting an older backup and
        // could not finish has destroyed it, and nothing else on this row would say
        // so — the strategy still succeeded, because the backup it was asked to take
        // was taken. Scoped to the strategies being asked about: each format prunes
        // its own backups, so what the ZIP's retention destroyed is the ZIP row's to
        // say.
        var damaged = DamagedDetail(strategies);

        if (!succeeded)
        {
            // Composed, not replaced, and this is the load-bearing line. Describe()
            // over an empty sequence returns null, and Strategies IS empty when the
            // only strategy the instance had was blocked — so without the join this
            // arm renders "Failed" with no explanation at all, for the one case that
            // most needs one. It is also empty when the run stopped before any
            // strategy started, which is what FailureDetail() is for (`T267`).
            //
            // The resume is joined too (`T274`). The failure takes the label, and
            // that is right, but a world that may still be held is about the live
            // server rather than about the archive — so a failed backup is no reason
            // to leave it unsaid, and until this it was the one sentence this arm
            // dropped.
            return new BackupOutcome(
                "Failed",
                BackupSeverity.Failed,
                Sentences(
                    FailureDetail(),
                    Describe(strategies.Where(s => !s.Succeeded)),
                    ResumeDetail(backupComplete: false),
                    damaged,
                    fellBack,
                    blocked));
        }

        var resume = ResumeDetail(backupComplete: true);

        return SavesResumed switch
        {
            SaveResumeOutcome.Unconfirmed => new BackupOutcome(
                "OK, saves unconfirmed",
                BackupSeverity.Warning,
                Sentences(resume, damaged, fellBack, blocked)),

            SaveResumeOutcome.NotSent => new BackupOutcome(
                "OK, resume not sent",
                BackupSeverity.Warning,
                Sentences(resume, damaged, fellBack, blocked)),

            // ServerGone is not a warning: a process that has exited is holding
            // nothing, and flagging it would make every backup-on-shutdown look
            // like a problem.
            _ => (damaged, fellBack, blocked) switch
            {
                (null, null, null) => new BackupOutcome("OK", BackupSeverity.Ok, Describe(strategies)),

                // Above the fallback and the block for the LABEL, below a possibly
                // held world, and on the same reasoning as both: this is the only
                // one of the four that reports something DESTROYED rather than
                // something configured or something slower than hoped. Neither
                // swallows the others' sentences.
                (not null, _, _) => new BackupOutcome(
                    "OK, a backup was destroyed",
                    BackupSeverity.Warning,
                    Sentences(Describe(strategies), damaged, fellBack, blocked)),

                // The fallback outranks the block for the label, one rung below a
                // possibly-held world, and on the same reasoning: it describes what
                // this run DID — saves held for the whole copy — while a block
                // describes a configuration fact that is also on the panel, in the
                // startup log and on every later row. Neither swallows the other's
                // sentence.
                (_, not null, _) => new BackupOutcome(
                    "OK, saves held longer",
                    BackupSeverity.Warning,
                    Sentences(Describe(strategies), fellBack, blocked)),

                _ => new BackupOutcome(
                    BlockedLabel(),
                    BackupSeverity.Warning,
                    Sentences(Describe(strategies), blocked)),
            },
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
    /// The sentence for a run that stopped before any strategy started, or null
    /// (<c>T267</c>).
    /// </summary>
    /// <remarks>
    /// The consequence first and the cause second, <c>D67</c>'s rule three: that no
    /// backup was taken is what the reader has to act on, and the exception's own
    /// message follows verbatim because it names the path or the missing reply.
    /// </remarks>
    private string? FailureDetail() =>
        string.IsNullOrWhiteSpace(Failure)
            ? null
            : $"No backup was taken: the run stopped before anything was copied. {Failure}";

    /// <summary>
    /// What an unconfirmed resume leaves unknown, true whether or not the backup
    /// worked. <see cref="ResumeDetail"/> gives it a lead and an ending per arm.
    /// </summary>
    /// <remarks>
    /// <c>T173</c>. This used to end "check its console, and send 'save resume' by
    /// hand if it is still held" — the same advice <c>T170</c> took out of the
    /// console notice, left behind here. It is worse here than it was there. BDS
    /// answers a hand-typed <c>save resume</c> with "A previous save has not been
    /// completed." whenever no hold is outstanding, which is to say whenever the
    /// resume already worked, so following it produces a frightening line about a
    /// world that is fine. And this text is read on a Backups page days later, where
    /// there is nothing to check against: the reader has no way to tell whether
    /// saves are held, which is exactly what the old sentence asked them to decide.
    /// </remarks>
    private const string UnconfirmedResume =
        "no confirmation that world saves resumed arrived before the wrapper stopped waiting. "
        + "Either the server is still holding saves — in which case it is writing nothing to "
        + "disk — or the confirmation was missed. The console transcript for this time settles "
        + "it: \"Changes to the world are resumed\" means the resume worked";

    /// <summary>What a resume that never reached the server leaves the reader to do.</summary>
    /// <remarks>
    /// Deliberately NOT worded like <see cref="UnconfirmedResume"/>, and the
    /// difference is the whole point: here the command never reached the server, so
    /// the world genuinely IS held and typing it by hand is both correct and
    /// necessary. Unconfirmed means we do not know; this means we do. Anybody
    /// unifying these two sentences would restore <c>T173</c> on the branch that
    /// cannot afford it.
    /// </remarks>
    private const string NotSentResume =
        "'save resume' could not be sent to the server. Check its console and send it by hand.";

    /// <summary>
    /// The sentence about this run's <c>save resume</c> when it is one a reader has
    /// to see, or null.
    /// </summary>
    /// <remarks>
    /// <para>One place for both arms, because the facts are the same whichever way
    /// the backup went and only the frame differs (<c>T274</c>). A complete backup
    /// says so first; a failed one says saves were paused for it, which is what
    /// connects a failed backup to a world that may still be held.</para>
    ///
    /// <para>The unconfirmed ending is the one real difference. On a complete backup
    /// the resume is the only thing wrong, so a confirmation in the transcript makes
    /// the row a false alarm; on a failed one the row stays true either way, and
    /// saying otherwise would be a sentence with no execution path on which it holds
    /// (<c>T190</c>).</para>
    ///
    /// <para>Every other outcome says nothing on either arm; the success ladder
    /// carries the reason for <see cref="SaveResumeOutcome.ServerGone"/>.</para>
    /// </remarks>
    private string? ResumeDetail(bool backupComplete)
    {
        var lead = backupComplete
            ? "The backup is complete, but "
            : "Saves were paused for this backup, and ";

        return SavesResumed switch
        {
            SaveResumeOutcome.Unconfirmed =>
                lead + UnconfirmedResume
                + (backupComplete ? " and this row is a false alarm." : "."),

            SaveResumeOutcome.NotSent => lead + NotSentResume,

            _ => null,
        };
    }

    /// <summary>
    /// What retention destroyed and could not finish destroying, or null.
    /// </summary>
    /// <remarks>
    /// <para>It names the artifacts, because the wrapper cannot remove what is left
    /// — the delete refused for a reason that has not gone away — so the only person
    /// who can finish the job is the one reading this, and they need to know which
    /// folder. And it says the backup is gone rather than that a deletion failed:
    /// a failed deletion sounds like nothing happened, and what happened is that a
    /// backup was destroyed.</para>
    ///
    /// <para>The run still SUCCEEDED, deliberately. The backup it was asked to take
    /// was taken, and reporting false would hide a good artifact from
    /// <c>LastSuccessfulUtcAsync</c>, blank "last backup" and make the skip rule
    /// back up on every tick — <c>D45</c>'s reasoning, and the reason this is a
    /// sentence and a badge rather than a failure.</para>
    /// </remarks>
    private static string? DamagedDetail(IEnumerable<BackupStrategyResult> strategies)
    {
        var damaged = strategies.SelectMany(s => s.Damaged).Distinct().ToList();

        if (damaged.Count == 0)
        {
            return null;
        }

        return damaged.Count == 1
            ? $"An older backup was destroyed by retention and could not be fully removed: "
              + $"{damaged[0]} is incomplete and has to be deleted by hand."
            : "Older backups were destroyed by retention and could not be fully removed: "
              + $"{string.Join(", ", damaged)} are incomplete and have to be deleted by hand.";
    }

    /// <summary>
    /// The sentence for a run that wanted a shadow copy and did not get one, or
    /// null when there is nothing to say.
    /// </summary>
    /// <remarks>
    /// <para><strong>The consequence first, the cause second</strong> (<c>D67</c>).
    /// "VSS failed" tells an administrator nothing they can weigh; "saves were held
    /// for the whole copy" is the thing that happened to their server, and it is
    /// what makes the difference between ignoring this row and turning the toggle
    /// off. Windows's own message follows, verbatim, because it is the half that is
    /// searchable.</para>
    ///
    /// <para>Only <see cref="BackupAcquisition.LiveFallback"/> says anything. The
    /// other three states are either what was asked for or nothing was asked, and a
    /// row narrating a successful ordinary backup is a row nobody reads.</para>
    /// </remarks>
    private string? FallbackDetail()
    {
        if (Acquisition is not BackupAcquisition.LiveFallback)
        {
            return null;
        }

        const string consequence =
            "World saves were held for the whole copy: a shadow copy was asked for and could not "
            + "be taken, so the backup read the live world instead. The backup itself is complete.";

        return string.IsNullOrWhiteSpace(AcquisitionDetail)
            ? consequence
            : $"{consequence} Windows said: {AcquisitionDetail}";
    }

    /// <summary>
    /// Joins two optional sentences, keeping whichever are there.
    /// </summary>
    /// <remarks>
    /// <para>Used so a block is <em>appended</em> to a resume warning rather than
    /// replacing it. All of them are <c>Warning</c>, so the only question is which
    /// wins the label, and a possibly-held world outranks both the others. Unify
    /// these into one sentence and the branch that cannot afford it loses its own —
    /// the shape `T173` was reintroduced by.</para>
    ///
    /// <para><strong>Two arguments became many when the acquisition fallback
    /// arrived</strong> (<c>D67</c>), which is the tell this was always a list. A
    /// fixed pair would have made the third warning somebody's choice of which two
    /// to keep.</para>
    /// </remarks>
    private static string? Sentences(params string?[] parts)
    {
        var kept = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();

        return kept.Count == 0 ? null : string.Join(" ", kept);
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

    public FolderBackupSettings Folder { get; set; } = new();

    /// <summary>
    /// How the world's bytes are obtained, applying to every strategy
    /// (<c>D66</c>).
    /// </summary>
    public BackupAcquisitionSettings Acquisition { get; set; } = new();

    public BackupSchedule Schedule { get; set; } = new();

    public BackupOnShutdownSettings OnShutdown { get; set; } = new();

    public BackupOnPlayerActivitySettings OnPlayerActivity { get; set; } = new();

    /// <summary>How long the Backups tab's history keeps a row (<c>D98</c>).</summary>
    public BackupHistorySettings History { get; set; } = new();

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

            && Folder.Enabled == other.Folder.Enabled
            && Folder.Retention.Mode == other.Folder.Retention.Mode
            && Folder.Retention.Value == other.Folder.Retention.Value

            && Acquisition.UseShadowCopy == other.Acquisition.UseShadowCopy

            && Schedule.Mode == other.Schedule.Mode
            && Schedule.IntervalHours == other.Schedule.IntervalHours
            && Schedule.IntervalMinutes == other.Schedule.IntervalMinutes
            && Schedule.TimeOfDay == other.Schedule.TimeOfDay

            && OnShutdown.OnScheduledRestart == other.OnShutdown.OnScheduledRestart
            && OnShutdown.OnWrapperUpdate == other.OnShutdown.OnWrapperUpdate
            && OnShutdown.OnServerUpdate == other.OnShutdown.OnServerUpdate

            && OnPlayerActivity.OnJoin == other.OnPlayerActivity.OnJoin
            && OnPlayerActivity.OnLeave == other.OnPlayerActivity.OnLeave
            && OnPlayerActivity.MinimumGapMinutes == other.OnPlayerActivity.MinimumGapMinutes

            && History.KeepDays == other.History.KeepDays;
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

/// <summary>How long a backup run's history row is kept (<c>D98</c>).</summary>
public sealed class BackupHistorySettings
{
    /// <summary>The default: a month of rows.</summary>
    public const int DefaultKeepDays = 30;

    /// <summary>
    /// Days a row is kept once its run left nothing on disk. Zero keeps every row.
    /// </summary>
    public int KeepDays { get; set; } = DefaultKeepDays;
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
/// The folder-copy strategy (<c>docs/design.md</c> § 8.4): the world copied out
/// uncompressed, one file per file.
/// </summary>
/// <remarks>
/// <para><strong>This was called <c>Vss</c> and produced no VSS snapshot of its
/// own</strong> (<c>D66</c>). It took a shadow copy, read the world out of the
/// frozen view, wrote a plain folder and released the snapshot — so the panel
/// offered "ZIP" against "VSS", which reads as <em>file versus snapshot</em> and
/// was actually <em>compressed file versus uncompressed folder</em>. The snapshot
/// was a means and is now <see cref="BackupAcquisitionSettings"/>, applying to
/// both formats; what is left here is the format, named for what it is.</para>
///
/// <para>Backups are <strong>not</strong> kept in shadow storage under either
/// name. Every folder costs full world size on disk, because materialising a
/// snapshot throws away the copy-on-write sharing that made people expect
/// otherwise.</para>
/// </remarks>
public sealed class FolderBackupSettings
{
    public bool Enabled { get; set; }

    public BackupRetention Retention { get; set; } = new();
}

/// <summary>
/// How the world's bytes are obtained, shared by every strategy (<c>D66</c>).
/// </summary>
/// <remarks>
/// <para>One shadow copy per <em>run</em>, not per strategy: two strategies
/// enabled together read the same frozen view, which is both cheaper and the only
/// arrangement in which their two artifacts are of the same instant.</para>
///
/// <para>Off by default, and off is a legitimate choice rather than a lapse — an
/// administrator who does not want VSS on their volume, or a volume that cannot
/// be snapshotted at all. Taking a shadow copy also needs the service account to
/// hold the backup privilege, which is the failure the Readiness panel exists to
/// predict (<c>docs/design.md</c> § 14). When it cannot be taken the backup still
/// happens, from the live world, and says so (<c>D67</c>).</para>
/// </remarks>
public sealed class BackupAcquisitionSettings
{
    /// <summary>
    /// Snapshot the volume and read the world out of that, so saves are held only
    /// long enough to take the snapshot rather than for the whole copy.
    /// </summary>
    public bool UseShadowCopy { get; set; }
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

    /// <summary>
    /// Ticked, with something typed in: when the custom-command strategy runs, and when
    /// the Backups tab's cards count it (<c>T281</c>). The install-wide gate (<c>D45</c>)
    /// is a separate question each of them asks.
    /// </summary>
    public bool IsSet => Enabled && !string.IsNullOrWhiteSpace(Command);
}
