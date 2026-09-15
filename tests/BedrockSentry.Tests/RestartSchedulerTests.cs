using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Configuration;
using BedrockSentry.Services.Instances;
using BedrockSentry.Services.Packages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BedrockSentry.Tests;

/// <summary>
/// Drives <see cref="RestartScheduler.Evaluate"/> directly rather than letting
/// the background loop tick. That loop is a <see cref="PeriodicTimer"/> and
/// nothing else; advancing a fake clock across the hours these schedules span
/// fires it thousands of times to observe one decision, and the decision is the
/// part that can be wrong.
/// </summary>
public sealed class RestartSchedulerTests : IAsyncLifetime
{
    private static readonly TimeZoneInfo Eastern =
        TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    /// 08:00 Eastern on a summer day, so a 09:00 daily restart is an hour out.
    private static readonly DateTimeOffset Origin = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot;
    private readonly SentryPaths _paths;
    private readonly FakeProcessFactory _factory = new();
    private readonly FakeTimeProvider _time = new(Origin);

    private InstanceRegistry? _registry;
    private RestartScheduler? _scheduler;

    public RestartSchedulerTests()
    {
        _dataRoot = Path.Combine(
            Path.GetTempPath(), "bedrocksentry-tests", Guid.NewGuid().ToString("n"));
        _paths = new SentryPaths(_dataRoot);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Async teardown, via xUnit's IAsyncLifetime rather than IDisposable.
    /// Blocking on this from a synchronous Dispose deadlocks — the supervisor's
    /// monitor task wants to complete on the same synchronisation context the
    /// blocking wait occupies, and the run then hangs with no output at all.
    /// </summary>
    public async Task DisposeAsync()
    {
        _scheduler?.Dispose();

        if (_registry is not null)
        {
            await _registry.DisposeAsync();
        }

        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a green run over.
        }
    }

    private async Task<(InstanceSupervisor Supervisor, FakeBedrockProcess Process)> StartAsync(
        RestartSchedule schedule, int maxConsecutiveFailures = 3)
    {
        var definition = new InstanceDefinition { Name = "Survival", RestartSchedule = schedule };
        definition.Watchdog.MaxConsecutiveFailures = maxConsecutiveFailures;

        var options = new InstanceOptions();
        options.ConsoleLog.Enabled = false;
        options.Servers["survival"] = definition;

        _registry = new InstanceRegistry(
            new StaticOptionsMonitor<InstanceOptions>(options),
            _paths, _factory, _time, NullLoggerFactory.Instance, Store(), new NoPlayerDataStore());

        _scheduler = new RestartScheduler(
            _registry, _time, NullLogger<RestartScheduler>.Instance) { TimeZone = Eastern };

        var supervisor = _registry.Get("survival");
        await supervisor.StartAsync();
        return (supervisor, _factory.Latest);
    }

    private ServerPackageStore Store() =>
        new(_paths, NullLogger<ServerPackageStore>.Instance);

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    /// <summary>Moves the clock, then evaluates once, exactly as a real tick would.</summary>
    private async Task TickAtAsync(TimeSpan fromOrigin)
    {
        _time.SetUtcNow(Origin + fromOrigin);
        _scheduler!.Evaluate();
        await Task.Delay(40);
    }

    /// <summary>
    /// Lets a countdown already under way play out. The lead is minutes, so a
    /// handful of advances covers it — unlike the schedule spans above.
    /// </summary>
    private async Task<bool> LetCountdownRunAsync(Func<bool> condition, TimeSpan step, int maxSteps = 20)
    {
        for (var i = 0; i < maxSteps && !condition(); i++)
        {
            await Task.Delay(20);
            _time.Advance(step);
        }

        await Task.Delay(60);
        return condition();
    }

    [Fact]
    public async Task Does_nothing_when_no_schedule_is_configured()
    {
        var (supervisor, process) = await StartAsync(new RestartSchedule());
        process.Ready();
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));

        await TickAtAsync(TimeSpan.FromHours(30));

        Assert.Empty(process.Sent);
        Assert.Single(_factory.Created);
    }

    [Fact]
    public async Task Waits_until_the_restart_is_inside_the_warning_window()
    {
        var (supervisor, process) = await StartAsync(new RestartSchedule
        {
            Mode = RestartScheduleMode.DailyAt,
            TimeOfDay = new TimeOnly(9, 0),
            Warnings = [TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(1)],
        });
        process.Ready();
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));

        // 08:40 — twenty minutes out, still beyond the fifteen-minute lead.
        await TickAtAsync(TimeSpan.FromMinutes(40));

        Assert.Empty(process.Sent);
    }

    [Fact]
    public async Task Warns_then_restarts_when_a_daily_restart_falls_due()
    {
        var (supervisor, process) = await StartAsync(new RestartSchedule
        {
            Mode = RestartScheduleMode.DailyAt,
            TimeOfDay = new TimeOnly(9, 0),
            Warnings = [TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(1)],
        });
        process.Ready();
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));

        // 08:46 — inside the window, so the countdown begins.
        await TickAtAsync(TimeSpan.FromMinutes(46));

        Assert.True(await WaitUntil(() => process.Sent.Count >= 1));
        Assert.Equal("say Server restarting in 15 minutes", process.Sent[0]);

        Assert.True(await LetCountdownRunAsync(
            () => process.Sent.Contains("stop"), TimeSpan.FromMinutes(2)));
        Assert.Contains("say Server restarting in 1 minute", process.Sent);

        // And it comes back, which is what makes this a restart and not a stop.
        process.Exit(0);
        Assert.True(await WaitUntil(() => _factory.Created.Count == 2));
    }

    // The window check carries a tick of slack precisely so the longest warning
    // is not skipped for arriving a few seconds late — which would silently drop
    // the warning most likely to be why warnings were configured at all.
    [Fact]
    public async Task The_longest_warning_still_fires_when_the_tick_lands_late()
    {
        var (supervisor, process) = await StartAsync(new RestartSchedule
        {
            Mode = RestartScheduleMode.EveryNHours,
            IntervalHours = 1,
            Warnings = [TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5)],
        });
        process.Ready();
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));

        // Due at +60m, so the fifteen-minute mark is +45m. Landing five seconds
        // late is inside the slack; without it this warning would never fire.
        await TickAtAsync(TimeSpan.FromMinutes(45) + TimeSpan.FromSeconds(5));

        Assert.True(await WaitUntil(() => process.Sent.Count >= 1));
        Assert.Equal("say Server restarting in 15 minutes", process.Sent[0]);
    }

    // A restart spends its whole countdown before the process state changes, so
    // without in-flight tracking every tick would stack another countdown on top.
    [Fact]
    public async Task Does_not_start_a_second_countdown_while_one_is_running()
    {
        var (supervisor, process) = await StartAsync(new RestartSchedule
        {
            Mode = RestartScheduleMode.EveryNHours,
            IntervalHours = 1,
            Warnings = [TimeSpan.FromMinutes(10)],
        });
        process.Ready();
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));

        await TickAtAsync(TimeSpan.FromMinutes(51));
        Assert.True(await WaitUntil(() => process.Sent.Count >= 1));

        await TickAtAsync(TimeSpan.FromMinutes(52));
        await TickAtAsync(TimeSpan.FromMinutes(53));
        await TickAtAsync(TimeSpan.FromMinutes(54));

        Assert.Equal(
            1, process.Sent.Count(c => c.StartsWith("say Server restarting", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Leaves_an_instance_that_is_not_running_alone()
    {
        var (supervisor, process) = await StartAsync(new RestartSchedule
        {
            Mode = RestartScheduleMode.EveryNHours,
            IntervalHours = 1,
            Warnings = [TimeSpan.FromMinutes(5)],
        });
        process.Ready();
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));

        // Not awaited before the exit: the stop waits on either the process
        // ending or the drain budget elapsing, and on a fake clock that nobody
        // advances neither ever happens. Let it get as far as sending `stop`,
        // then end the process, then collect the result.
        var stop = supervisor.StopAsync(
            GracefulStopRequest.Immediate(StopReason.Manual, TimeSpan.FromSeconds(45)));
        Assert.True(await WaitUntil(() => process.Sent.Contains("stop")));
        process.Exit(0);
        await stop;

        Assert.Equal(InstanceState.Stopped, supervisor.Status.State);

        var sentBefore = process.Sent.Count;
        await TickAtAsync(TimeSpan.FromHours(3));

        Assert.Equal(sentBefore, process.Sent.Count);
        Assert.Single(_factory.Created);
    }

    // A faulted instance has already given up (D15). Restarting it on a schedule
    // would quietly undo that rule.
    [Fact]
    public async Task Leaves_a_faulted_instance_alone()
    {
        var (supervisor, process) = await StartAsync(
            new RestartSchedule
            {
                Mode = RestartScheduleMode.EveryNHours,
                IntervalHours = 1,
                Warnings = [TimeSpan.FromMinutes(5)],
            },
            maxConsecutiveFailures: 1);

        process.Exit(1);
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Faulted));

        await TickAtAsync(TimeSpan.FromHours(3));

        Assert.Single(_factory.Created);
    }

    // "Every N hours" counts from the last start, so a restart re-anchors the
    // schedule rather than coming due again straight away.
    [Fact]
    public async Task Every_n_hours_re_anchors_after_a_restart()
    {
        var (supervisor, first) = await StartAsync(new RestartSchedule
        {
            Mode = RestartScheduleMode.EveryNHours,
            IntervalHours = 1,
            Warnings = [TimeSpan.FromMinutes(1)],
        });
        first.Ready();
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));

        await TickAtAsync(TimeSpan.FromMinutes(59));
        Assert.True(await LetCountdownRunAsync(
            () => first.Sent.Contains("stop"), TimeSpan.FromSeconds(30)));

        first.Exit(0);
        Assert.True(await WaitUntil(() => _factory.Created.Count == 2));

        var second = _factory.Created[1];
        second.Ready();
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));

        var restartedAt = supervisor.Status.StartedUtc!.Value - Origin;

        // Half an interval past the restart is not due.
        await TickAtAsync(restartedAt + TimeSpan.FromMinutes(30));
        Assert.DoesNotContain(
            second.Sent, c => c.StartsWith("say Server restarting", StringComparison.Ordinal));

        // A full interval past it is.
        await TickAtAsync(restartedAt + TimeSpan.FromSeconds(59 * 60 + 30));
        Assert.True(await WaitUntil(
            () => second.Sent.Any(c => c.StartsWith("say Server restarting", StringComparison.Ordinal))));
    }
}
