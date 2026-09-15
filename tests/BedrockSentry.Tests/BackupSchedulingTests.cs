using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Backups;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace BedrockSentry.Tests;

/// <summary>
/// The scheduling half of docs/design.md § 8.1: when a scheduled backup fires, and
/// when a shutdown backup rides the restart's stop-start gap.
/// </summary>
public sealed class BackupSchedulingTests : IAsyncDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"bs-sched-{Guid.NewGuid():N}");

    private static readonly DateTimeOffset Noon = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Noon);
    private readonly FakeProcessFactory _factory = new();
    private readonly InstanceRegistry _registry;
    private readonly BackupHistory _history;
    private readonly CountingStrategy _strategy = new();
    private readonly BackupOrchestrator _orchestrator;

    public BackupSchedulingTests()
    {
        Directory.CreateDirectory(_directory);

        var paths = new TestPaths(Path.Combine(_directory, "data"));

        _registry = new InstanceRegistry(
            new StaticOptions(new InstanceOptions
            {
                Root = Path.Combine(_directory, "instances"),
                Servers = { ["survival"] = new InstanceDefinition { Name = "Survival", AutoStart = false } },
            }),
            paths,
            _factory,
            _time,
            NullLoggerFactory.Instance,
            new Services.Packages.ServerPackageStore(
                paths, NullLogger<Services.Packages.ServerPackageStore>.Instance),
            new NoPlayerDataStore());

        _history = new BackupHistory(paths);

        // System time for the orchestrator: a stopped instance takes the static
        // path with no delays in it, so the fake clock is only for the scheduler.
        _orchestrator = new BackupOrchestrator(
            [_strategy], _history, TimeProvider.System, NullLogger<BackupOrchestrator>.Instance);

        // A world on disk, so the static walk has something to archive.
        var worlds = Path.Combine(
            _registry.All.Single().WorkingDirectory, "worlds", "Bedrock level");
        Directory.CreateDirectory(worlds);
        File.WriteAllText(Path.Combine(worlds, "level.dat"), "world");
    }

    public async ValueTask DisposeAsync()
    {
        await _responder.CancelAsync();
        _responder.Dispose();
        await _registry.DisposeAsync();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private BackupScheduler Scheduler() => new(
        _registry, _orchestrator, _history, _time, NullLogger<BackupScheduler>.Instance)
    {
        TimeZone = TimeZoneInfo.Utc,
    };

    private InstanceSupervisor Supervisor => _registry.All.Single();

    private BackupSettings Settings => Supervisor.Definition.Backup;

    /// <summary>
    /// Waits until <paramref name="expected"/> backups have been *recorded*,
    /// which happens after the run finishes rather than when it starts.
    /// </summary>
    /// <remarks>
    /// The distinction matters for the minimum-gap test: a trigger arriving
    /// while a backup is still in flight is deliberately dropped, so waiting on
    /// the strategy's counter alone would race the release and look like the gap
    /// misbehaving.
    /// </remarks>
    private async Task WaitForHistoryAsync(int expected)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            if ((await _history.ReadAsync("survival")).Count >= expected)
            {
                // The in-flight slot is released a moment after the record
                // lands; this is the only part that cannot be observed directly.
                await Task.Delay(50);
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"expected {expected} backup record(s)");
    }

    /// The run is fired off the tick loop, so give it a beat to land.
    private async Task WaitForRunsAsync(int expected)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            if (_strategy.Runs >= expected)
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"expected {expected} backup run(s), saw {_strategy.Runs}");
    }

    private readonly CancellationTokenSource _responder = new();

    /// <summary>
    /// Brings the instance up and plays the server's side of the save exchange.
    /// </summary>
    /// <remarks>
    /// A running instance takes the live hold/query/resume path rather than the
    /// static walk the scheduled tests use, so without a responder the backup
    /// waits out the query timeout instead of running.
    /// </remarks>
    private async Task StartAndRespondAsync()
    {
        await Supervisor.StartAsync();
        _factory.Latest.Ready();
        Assert.True(await Supervisor.WaitForReadyAsync(TimeSpan.FromSeconds(5)));

        var process = _factory.Latest;

        _ = Task.Run(async () =>
        {
            var answered = 0;
            while (!_responder.IsCancellationRequested)
            {
                var sent = process.Sent;
                for (; answered < sent.Count; answered++)
                {
                    if (sent[answered] == "save query")
                    {
                        process.Emit(
                            "Bedrock level/db/CURRENT:6, Bedrock level/level.dat:5");
                    }
                    else if (sent[answered] == "save resume")
                    {
                        process.Emit(
                            "[2026-08-06 12:00:00:000 INFO] Changes to the world are resumed.");
                    }
                }

                await Task.Delay(10);
            }
        });
    }

    [Fact]
    public async Task No_schedule_means_no_backups()
    {
        var scheduler = Scheduler();

        _time.Advance(TimeSpan.FromDays(2));
        await scheduler.EvaluateAsync();
        await Task.Delay(50);

        Assert.Equal(0, _strategy.Runs);
    }

    [Fact]
    public async Task Every_n_hours_fires_after_the_interval_not_at_first_tick()
    {
        Settings.Zip.Enabled = false;
        Settings.Schedule.Mode = RestartScheduleMode.EveryNHours;
        Settings.Schedule.IntervalHours = 6;

        var scheduler = Scheduler();

        // Fresh install: the anchor is now, so the first evaluation is not due.
        await scheduler.EvaluateAsync();
        await Task.Delay(50);
        Assert.Equal(0, _strategy.Runs);

        _time.Advance(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(1));
        await scheduler.EvaluateAsync();
        await WaitForRunsAsync(1);

        // And not again until another interval has passed.
        _time.Advance(TimeSpan.FromHours(1));
        await scheduler.EvaluateAsync();
        await Task.Delay(50);
        Assert.Equal(1, _strategy.Runs);
    }

    [Fact]
    public async Task Every_n_minutes_fires_on_the_shorter_interval()
    {
        Settings.Zip.Enabled = false;
        Settings.Schedule.Mode = RestartScheduleMode.EveryNMinutes;
        Settings.Schedule.IntervalMinutes = 20;

        var scheduler = Scheduler();

        await scheduler.EvaluateAsync();
        await Task.Delay(50);
        Assert.Equal(0, _strategy.Runs);

        // Well past an hours-schedule's shortest setting, and still not due on
        // the hours field — the mode picks which number is in force.
        _time.Advance(TimeSpan.FromMinutes(21));
        await scheduler.EvaluateAsync();
        await WaitForRunsAsync(1);
    }

    // Every schedule written before minutes existed keeps working untouched:
    // the mode says which field is read, so an old config is never reinterpreted.
    [Fact]
    public async Task An_hours_schedule_ignores_the_minutes_field()
    {
        Settings.Zip.Enabled = false;
        Settings.Schedule.Mode = RestartScheduleMode.EveryNHours;
        Settings.Schedule.IntervalHours = 6;
        Settings.Schedule.IntervalMinutes = 1;

        var scheduler = Scheduler();
        await scheduler.EvaluateAsync();

        _time.Advance(TimeSpan.FromMinutes(30));
        await scheduler.EvaluateAsync();
        await Task.Delay(50);

        Assert.Equal(0, _strategy.Runs);
    }

    [Fact]
    public async Task A_join_triggers_a_backup_when_that_toggle_is_on()
    {
        Settings.Zip.Enabled = false;
        Settings.OnPlayerActivity.OnJoin = true;

        var scheduler = Scheduler();

        // Subscription happens on the pass over the instances.
        await scheduler.EvaluateAsync();

        await StartAndRespondAsync();
        _factory.Latest.Emit(
            "[2026-08-06 12:00:01:000 INFO] Player connected: SteveExample, xuid: 2535000000000001");

        await WaitForRunsAsync(1);
    }

    [Fact]
    public async Task A_leave_triggers_a_backup_and_a_join_does_not_when_only_leave_is_on()
    {
        Settings.Zip.Enabled = false;
        Settings.OnPlayerActivity.OnLeave = true;

        var scheduler = Scheduler();
        await scheduler.EvaluateAsync();
        await StartAndRespondAsync();

        _factory.Latest.Emit(
            "[2026-08-06 12:00:01:000 INFO] Player connected: SteveExample, xuid: 2535000000000001");
        await Task.Delay(80);
        Assert.Equal(0, _strategy.Runs);

        _factory.Latest.Emit(
            "[2026-08-06 12:00:02:000 INFO] Player disconnected: SteveExample, xuid: 2535000000000001");
        await WaitForRunsAsync(1);
    }

    // A player on a poor connection can join and drop a dozen times in a minute,
    // and every one of those is a hold-query-copy-resume against a live server.
    [Fact]
    public async Task The_minimum_gap_stops_a_flapping_connection_backing_up_repeatedly()
    {
        Settings.Zip.Enabled = false;
        Settings.OnPlayerActivity.OnJoin = true;
        Settings.OnPlayerActivity.MinimumGapMinutes = 15;

        var scheduler = Scheduler();
        await scheduler.EvaluateAsync();
        await StartAndRespondAsync();

        for (var i = 0; i < 6; i++)
        {
            _factory.Latest.Emit(
                $"[2026-08-06 12:00:0{i}:000 INFO] Player connected: Flapper, xuid: 111");
        }

        await WaitForHistoryAsync(1);

        // Six arrivals, one backup.
        Assert.Equal(1, _strategy.Runs);

        // Past the gap, the next arrival is worth archiving again.
        _time.Advance(TimeSpan.FromMinutes(16));
        _factory.Latest.Emit(
            "[2026-08-06 12:16:00:000 INFO] Player connected: Flapper, xuid: 111");

        await WaitForRunsAsync(2);
    }

    [Fact]
    public async Task Player_activity_backups_are_off_unless_asked_for()
    {
        Settings.Zip.Enabled = false;

        var scheduler = Scheduler();
        await scheduler.EvaluateAsync();
        await StartAndRespondAsync();

        _factory.Latest.Emit(
            "[2026-08-06 12:00:01:000 INFO] Player connected: SteveExample, xuid: 2535000000000001");
        _factory.Latest.Emit(
            "[2026-08-06 12:00:02:000 INFO] Player disconnected: SteveExample, xuid: 2535000000000001");

        await Task.Delay(120);
        Assert.Equal(0, _strategy.Runs);
    }

    [Fact]
    public async Task Every_n_hours_anchors_on_history_across_a_wrapper_restart()
    {
        Settings.Schedule.Mode = RestartScheduleMode.EveryNHours;
        Settings.Schedule.IntervalHours = 6;

        // The last backup finished seven hours ago per history — overdue, so a
        // freshly started scheduler fires on its first look rather than waiting
        // a whole interval on top of the outage.
        await _history.AppendAsync("survival", new BackupRecord(
            Noon.AddHours(-7), Noon.AddHours(-7), BackupReason.Scheduled, null,
            false, null, true, [new BackupStrategyResult("counting", true, null, null)]));

        var scheduler = Scheduler();
        await scheduler.EvaluateAsync();
        await WaitForRunsAsync(1);
    }

    [Fact]
    public async Task Daily_fires_at_the_set_time_once()
    {
        Settings.Schedule.Mode = RestartScheduleMode.DailyAt;
        Settings.Schedule.TimeOfDay = new TimeOnly(23, 0);

        var scheduler = Scheduler();

        await scheduler.EvaluateAsync();
        await Task.Delay(50);
        Assert.Equal(0, _strategy.Runs);

        _time.Advance(TimeSpan.FromHours(11) + TimeSpan.FromMinutes(5));
        await scheduler.EvaluateAsync();
        await WaitForRunsAsync(1);

        // The next evaluation is tomorrow's, not a re-fire of today's.
        _time.Advance(TimeSpan.FromMinutes(5));
        await scheduler.EvaluateAsync();
        await Task.Delay(50);
        Assert.Equal(1, _strategy.Runs);
    }

    [Fact]
    public async Task The_scheduled_run_carries_the_scheduled_reason()
    {
        Settings.Schedule.Mode = RestartScheduleMode.EveryNHours;
        Settings.Schedule.IntervalHours = 1;

        var scheduler = Scheduler();

        // First look sets the anchor; the interval counts from there.
        await scheduler.EvaluateAsync();
        _time.Advance(TimeSpan.FromHours(2));
        await scheduler.EvaluateAsync();
        await WaitForRunsAsync(1);

        Assert.Equal(BackupReason.Scheduled, _strategy.LastContext!.Reason);
    }

    [Fact]
    public async Task A_restart_takes_the_shutdown_backup_only_when_its_toggle_says_so()
    {
        var shutdown = new ShutdownBackups(
            _orchestrator, TimeProvider.System, NullLogger<ShutdownBackups>.Instance);

        // Toggle off: the reason maps to nothing and no backup runs.
        await shutdown.RunIfEnabledAsync(Supervisor, StopReason.ScheduledRestart);
        Assert.Equal(0, _strategy.Runs);

        Settings.OnShutdown.OnScheduledRestart = true;

        await shutdown.RunIfEnabledAsync(Supervisor, StopReason.ScheduledRestart);
        Assert.Equal(1, _strategy.Runs);
        Assert.Equal(BackupReason.Shutdown, _strategy.LastContext!.Reason);

        // A service stop never backs up, toggles or no — Windows is waiting on
        // the service control manager (docs/design.md § 5.2).
        await shutdown.RunIfEnabledAsync(Supervisor, StopReason.ServiceStop);
        Assert.Equal(1, _strategy.Runs);
    }

    private sealed class CountingStrategy : IBackupStrategy
    {
        public int Runs;
        public BackupContext? LastContext;

        public string Name => "counting";

        public bool IsEnabled(BackupSettings settings) => true;

        public BlockedStrategy? Blocked(BackupSettings settings) => null;

        public Task<BackupStrategyResult> RunAsync(
            BackupContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Runs);
            LastContext = context;
            return Task.FromResult(new BackupStrategyResult(Name, true, null, null));
        }
    }

    private sealed class StaticOptions(InstanceOptions value) : IOptionsMonitor<InstanceOptions>
    {
        public InstanceOptions CurrentValue => value;
        public InstanceOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<InstanceOptions, string?> listener) => null;
    }

}
