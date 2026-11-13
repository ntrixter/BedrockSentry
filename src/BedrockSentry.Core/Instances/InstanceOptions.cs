namespace BedrockSentry.Core.Instances;

/// <summary>
/// Crash-restart policy for one instance (<c>D15</c>, <c>docs/design.md</c> § 5.1).
///
/// The give-up is the point. A server that crashes during startup would
/// otherwise restart forever, and every cycle writes to the world files — so an
/// unattended loop is not merely noisy. The alert matters as much as the
/// stopping: a server that is quietly down is worse than one that is loudly down.
/// </summary>
public sealed class WatchdogOptions
{
    /// <summary>
    /// Consecutive failed starts before the instance is left
    /// <see cref="InstanceState.Faulted"/>.
    /// </summary>
    /// <remarks>
    /// <b>Zero does not disable the watchdog — it removes the limit</b>, and
    /// negative behaves the same. `InstanceSupervisor` gates its fault branch on
    /// <c>MaxConsecutiveFailures &gt; 0</c>, so at zero that branch never fires and
    /// a server that cannot start is retried indefinitely.
    ///
    /// This comment said the exact opposite until 2026-09-04 (`T145`), and the
    /// panel's ladder text was written from it and inherited the error — telling an
    /// operator the watchdog was off at the setting that makes it unbounded. The
    /// code and the page's help text were right the whole time. If this is ever
    /// changed, change the code first and this comment second.
    /// </remarks>
    public int MaxConsecutiveFailures { get; set; } = 3;

    /// <summary>Delay before the first retry.</summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Ceiling on the backoff, so a long outage does not push the retry hours out.</summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Multiplier applied to the backoff after each failure.</summary>
    public double BackoffMultiplier { get; set; } = 3;

    /// <summary>
    /// How long a start must stay up before the failure counter resets
    /// (<c>D17</c>, <c>docs/design.md</c> § 5.1). Without this, a server that crashes
    /// ten seconds after every launch clears its own counter on each attempt and
    /// the give-up rule never fires — the exact loop <c>D15</c> exists to stop.
    /// </summary>
    public TimeSpan StableAfter { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>The delay before retry number <paramref name="failureCount"/> (1-based).</summary>
    public TimeSpan BackoffFor(int failureCount)
    {
        if (failureCount <= 1)
        {
            return InitialBackoff;
        }

        var multiplier = Math.Pow(Math.Max(1, BackoffMultiplier), failureCount - 1);
        var scaled = InitialBackoff.TotalMilliseconds * multiplier;

        // Compare as doubles: a long backoff and a big multiplier overflow
        // TimeSpan before they exceed MaxBackoff, and TimeSpan.FromMilliseconds
        // throws rather than saturating.
        return scaled >= MaxBackoff.TotalMilliseconds
            ? MaxBackoff
            : TimeSpan.FromMilliseconds(scaled);
    }
}

/// <summary>
/// On-disk retention for an instance's console transcript (<c>docs/design.md</c> § 5.4).
/// </summary>
public sealed class ConsoleLogOptions
{
    /// <summary>Write transcripts at all. Off means the in-memory ring is the only copy.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Size at which the current file is rotated.</summary>
    public long MaxFileBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>How many rotated files to keep alongside the current one.</summary>
    public int MaxFiles { get; set; } = 5;

    /// <summary>Lines held in memory for the console page.</summary>
    public int BufferedLines { get; set; } = ConsoleBuffer.DefaultCapacity;
}

/// <summary>
/// One managed Bedrock server, as configured.
/// </summary>
public sealed class InstanceDefinition
{
    /// <summary>Display name, and the folder name under the instance root.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Overrides the instance root entirely, so a large world can live on
    /// another volume. The root is a convenience, not a constraint
    /// (<c>docs/design.md</c> § 4) — nothing may assume an instance lives beneath it.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Start this instance when the wrapper starts.</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>
    /// Where this server's tab sits, set by dragging the tabs (<c>T437</c>). Null
    /// for a server never moved, which sorts after every placed one by name
    /// (<see cref="InstanceOrder"/>).
    /// </summary>
    public int? DisplayOrder { get; set; }

    /// <summary>
    /// Retired instances (<c>D14</c>) keep their player history but are never
    /// launched and get no per-instance page. Removal at milestone 16 sets this
    /// rather than deleting rows, because deleting them would silently shrink
    /// every affected player's all-time playtime.
    /// </summary>
    public bool Retired { get; set; }

    /// <summary>Executable name inside the working directory.</summary>
    public string Executable { get; set; } = "bedrock_server.exe";

    /// <summary>
    /// Where this instance came from, when it was made by restoring a backup into
    /// a new server (<c>D40</c>). Null for every other instance.
    /// </summary>
    public RestoredFrom? RestoredFrom { get; set; }

    public WatchdogOptions Watchdog { get; set; } = new();

    public RestartSchedule RestartSchedule { get; set; } = new();

    public Backups.BackupSettings Backup { get; set; } = new();

    public BdsUpdateSettings ServerUpdate { get; set; } = new();

    public CompanionSettings Companion { get; set; } = new();

    public InstanceAddonSettings Addons { get; set; } = new();

    /// <summary>The server this one takes its world from, and the areas it keeps (<c>D103</c>).</summary>
    public Sync.SyncSettings Sync { get; set; } = new();

    /// <summary>
    /// Which of the world's ticking areas are outlined in game, and in what colour (<c>D107</c>).
    /// The areas themselves live in the world; this only says how the panel shows them.
    /// Written whole, as <see cref="Sync.SyncSettings.KeptAreas"/> is.
    /// </summary>
    public List<Structures.TickingAreaSettings> TickingAreas { get; set; } = [];
}

/// <summary>What the Add-ons tab keeps for one instance.</summary>
public sealed class InstanceAddonSettings
{
    /// <summary>
    /// The issues an administrator hid, by <c>AddonIssues.KeyOf</c> (<c>D96</c>).
    /// </summary>
    /// <remarks>
    /// Written whole, never merged: the configuration binder appends to a list,
    /// so a shorter list merged over a longer one would keep the keys it dropped.
    /// Empty by default for the same reason, since binding appends to whatever the
    /// default holds.
    /// </remarks>
    public List<string> HiddenIssues { get; set; } = [];

    /// <summary>
    /// Whether each start first moves switched-on add-ons to the newest version the
    /// library holds (<c>D112</c>). Off by default: a pack that changes under a world
    /// is the administrator's call.
    /// </summary>
    public bool AutoUpdate { get; set; }

    /// <summary>
    /// The add-ons held at a version the administrator chose, which auto-update leaves
    /// alone (<c>D112</c>). Written whole, for the reason <see cref="HiddenIssues"/> gives.
    /// </summary>
    public List<AddonHold> Holds { get; set; } = [];

    /// <summary>A copy, so a writer that changes one field keeps the others.</summary>
    public InstanceAddonSettings Copy() => new()
    {
        HiddenIssues = [.. HiddenIssues],
        AutoUpdate = AutoUpdate,
        Holds = [.. Holds.Select(h => new AddonHold { Uuid = h.Uuid, Version = h.Version })],
    };
}

/// <summary>One add-on held at a version (<c>D112</c>).</summary>
public sealed class AddonHold
{
    /// <summary>The pack's header uuid, its identity (<c>D79</c>).</summary>
    public string Uuid { get; set; } = "";

    /// <summary>The version chosen, as text, so the panel can say what it is held at.</summary>
    public string Version { get; set; } = "";
}

/// <summary>
/// What the wrapper does with what the companion pack relays on one instance.
/// </summary>
/// <remarks>
/// Each applies the moment it is saved, because each decides what the wrapper
/// does with a line rather than what the pack prints. What a player is told in
/// game is the pack's own setting, changed with its gear, because only the pack
/// can act on it.
/// </remarks>
public sealed class CompanionSettings
{
    /// <summary>Count each relayed death against the player who died.</summary>
    public bool RecordDeaths { get; set; } = true;

    /// <summary>
    /// Keep where each player was when their session ended, and before a planned
    /// stop ask the pack for every online player's position (<c>D35</c>).
    /// </summary>
    public bool RecordLastLocation { get; set; } = true;

    /// <summary>
    /// Show the pack's join and leave position lines as arrivals and departures.
    /// Off, the lines are still recorded and shown dimmed.
    /// </summary>
    public bool EchoPositions { get; set; } = true;

    /// <summary>A copy with the named toggles changed and every other one kept.</summary>
    public CompanionSettings With(
        bool? recordDeaths = null, bool? recordLastLocation = null, bool? echoPositions = null) => new()
    {
        RecordDeaths = recordDeaths ?? RecordDeaths,
        RecordLastLocation = recordLastLocation ?? RecordLastLocation,
        EchoPositions = echoPositions ?? EchoPositions,
    };
}

/// <summary>
/// How this server treats a new Bedrock build (<c>docs/design.md</c> § 11).
/// </summary>
/// <remarks>
/// Per-instance rather than install-wide, because the appetite differs: a
/// creative test server can follow every build the day it lands, and the
/// survival world people have played for a year probably should not. Where the
/// builds are found is install-wide — see
/// <see cref="Configuration.BdsUpdateOptions"/>.
/// </remarks>
public sealed class BdsUpdateSettings
{
    /// <summary>What to do when a newer build appears.</summary>
    public BdsUpdateMode Mode { get; set; } = BdsUpdateMode.Notify;

    /// <summary>Which line of builds to follow.</summary>
    public Packages.BdsChannel Channel { get; set; } = Packages.BdsChannel.Release;

    /// <summary>
    /// Stay on this exact version whatever the channel offers. Empty means
    /// follow the channel.
    /// </summary>
    /// <remarks>
    /// A pin outranks <see cref="Mode"/> entirely, including
    /// <see cref="BdsUpdateMode.Automatic"/> — otherwise "pin this version" and
    /// "update by yourself" would be two settings that contradict each other
    /// with no stated winner. Pinning is also the manual rollback: set it to the
    /// build that worked and the wrapper stops offering the one that did not.
    /// </remarks>
    public string PinnedVersion { get; set; } = string.Empty;

    public bool IsPinned => PinnedVersion.Trim().Length > 0;

    /// <summary>
    /// How long after a build is first seen before
    /// <see cref="BdsUpdateMode.Automatic"/> applies it to this server, in
    /// minutes.
    /// </summary>
    /// <remarks>
    /// <b>This is the per-instance update schedule.</b> Not a time of day and
    /// not a cron: the event being scheduled around is Mojang publishing, which
    /// nobody controls, so the only thing worth setting is how much warning this
    /// server's players get — and that is exactly what a delay is. A creative
    /// test server can take it in five minutes; the survival world people have
    /// played for a year can sit on it for a day.
    ///
    /// The delay <em>is</em> the countdown. Players are told the moment the
    /// build is seen and again as the window closes, so half an hour's delay is
    /// half an hour's notice rather than half an hour of silence followed by a
    /// surprise.
    ///
    /// Zero is allowed and means "as soon as it is seen" — the short countdown
    /// the stop itself gives is all anybody gets. Thirty minutes by default:
    /// long enough to finish what you were doing, short enough that nobody is
    /// waiting on it.
    /// </remarks>
    public int AutoApplyDelayMinutes { get; set; } = 30;

    /// <summary>The delay as a <see cref="TimeSpan"/>, never negative.</summary>
    public TimeSpan AutoApplyDelay =>
        TimeSpan.FromMinutes(Math.Max(0, AutoApplyDelayMinutes));
}

/// <summary>
/// The backup a restored-as-new server was made from (<c>D40</c>).
/// </summary>
/// <remarks>
/// <para>Recorded because a copy stops looking like a copy quickly. Three weeks
/// on, a second server with a similar name and a world full of familiar buildings
/// is indistinguishable from the real one, and the mistake it invites is acting on
/// it — changing settings, adding players, taking backups of a world nobody is
/// playing in.</para>
///
/// <para>It holds the source instance's <em>id</em> as well as its name, because
/// the name is what somebody reads and the id is what survives a rename. Neither
/// is a live reference: the server this was copied from may have been removed
/// since, and this is a note about history rather than a link.</para>
/// </remarks>
public sealed class RestoredFrom
{
    /// <summary>The backup's file or folder name.</summary>
    public string BackupName { get; set; } = string.Empty;

    /// <summary>The instance it was a backup of, as an id.</summary>
    public string SourceInstanceId { get; set; } = string.Empty;

    /// <summary>That instance's display name at the time.</summary>
    public string SourceName { get; set; } = string.Empty;

    /// <summary>When the copy was made.</summary>
    public DateTimeOffset WhenUtc { get; set; }

    /// <summary>
    /// When the backup it was made from was taken — which is how old its world is,
    /// and a different fact from <see cref="WhenUtc"/>.
    /// </summary>
    /// <remarks>
    /// Both, because each answers a question the other cannot and they are easy to
    /// conflate: <see cref="WhenUtc"/> says how long this copy has been sitting
    /// around, and this says what state its world started in. A copy made this
    /// morning from a backup taken in March is old in one sense and new in the
    /// other, and one field would have had to pick which.
    /// </remarks>
    public DateTimeOffset BackupTakenUtc { get; set; }

    /// <summary>
    /// What the Status page says, here rather than in the page because the suite
    /// cannot see a <c>.razor</c> file.
    /// </summary>
    /// <remarks>
    /// It says the world is <em>as it was</em> rather than that it is old, because
    /// those are different claims and only the first one is certain: somebody may
    /// have been playing on this copy ever since.
    /// </remarks>
    public string Sentence() =>
        $"This server is a copy. It was made on {WhenUtc.UtcDateTime:d MMMM yyyy} by "
        + $"restoring {BackupName} — a backup of {SourceName} taken on "
        + $"{BackupTakenUtc.UtcDateTime:d MMMM yyyy} — into a new server, so its world "
        + "started out as that backup's rather than as one of its own.";
}

/// <summary>What an instance does about a newer Bedrock build.</summary>
public enum BdsUpdateMode
{
    /// <summary>
    /// Say so and wait to be told. The default, because Mojang ships the
    /// occasional build that breaks something and this is somebody's live
    /// server.
    /// </summary>
    Notify,

    /// <summary>
    /// Download and apply on the wrapper's own schedule, with the same warn,
    /// drain, back up and health-gate sequence every other restart uses.
    /// </summary>
    Automatic,

    /// <summary>Do not even look. A pin does this for one version; this does it for all.</summary>
    Off,
}

/// <summary>
/// The <c>Sentry:Instances</c> configuration section.
/// </summary>
public sealed class InstanceOptions
{
    public const string SectionName = "Sentry:Instances";

    /// <summary>
    /// Where instance folders are created by default. Chosen by the
    /// administrator during first-run setup; empty means "use
    /// <c>&lt;DataRoot&gt;\instances</c>".
    /// </summary>
    public string Root { get; set; } = "";

    /// <summary>
    /// Keyed by instance id — stable, and not the display name, which an
    /// administrator may rename. Ordinal-ignore-case because these ids end up as
    /// folder names on a case-insensitive filesystem, and two instances differing
    /// only in case would collide there while looking distinct here.
    /// </summary>
    public Dictionary<string, InstanceDefinition> Servers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Total time every instance is allowed for a graceful drain during a service
    /// stop. The invariant of <c>docs/design.md</c> § 5.2 runs top to bottom: the
    /// updater's stop timeout exceeds the host's shutdown timeout, which exceeds
    /// this. Inverting any pair force-kills a server that is legitimately
    /// mid-save.
    /// </summary>
    public TimeSpan ShutdownDrainBudget { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How long players are warned before a *service stop* takes the servers
    /// down. Short by design: Windows is waiting on the service control manager,
    /// and a long countdown risks being killed mid-warning — precisely the
    /// outcome the graceful path exists to prevent.
    /// </summary>
    public TimeSpan ServiceStopWarning { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Console transcript retention, shared by every instance.</summary>
    public ConsoleLogOptions ConsoleLog { get; set; } = new();
}
