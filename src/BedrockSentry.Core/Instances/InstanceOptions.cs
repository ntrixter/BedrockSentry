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
    /// Retired instances (<c>D14</c>) keep their player history but are never
    /// launched and get no per-instance page. Removal at milestone 16 sets this
    /// rather than deleting rows, because deleting them would silently shrink
    /// every affected player's all-time playtime.
    /// </summary>
    public bool Retired { get; set; }

    /// <summary>Executable name inside the working directory.</summary>
    public string Executable { get; set; } = "bedrock_server.exe";

    public WatchdogOptions Watchdog { get; set; } = new();

    public RestartSchedule RestartSchedule { get; set; } = new();

    public Backups.BackupSettings Backup { get; set; } = new();

    public BdsUpdateSettings ServerUpdate { get; set; } = new();
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
