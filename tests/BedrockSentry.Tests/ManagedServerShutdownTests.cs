using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Backups;
using BedrockSentry.Services.Configuration;
using BedrockSentry.Services.Instances;
using BedrockSentry.Services.Packages;
using Microsoft.Extensions.Logging.Abstractions;

namespace BedrockSentry.Tests;

/// <summary>
/// docs/design.md § 12, milestone 13.5. A wrapper self-update takes every server on the
/// machine down. Before this, they went down with the service stop — which has
/// the service control manager waiting on it and gets the deliberately short
/// countdown of § 5.2. The point of draining first is that nothing is waiting.
/// </summary>
public sealed class ManagedServerShutdownTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Origin = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), "bedrocksentry-drain", Guid.NewGuid().ToString("n"));

    private readonly FakeProcessFactory _factory = new();

    private SentryPaths _paths = default!;
    private InstanceRegistry _registry = default!;
    private BackupHistory _history = default!;
    private ManagedServerShutdown _shutdown = default!;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_registry is not null)
        {
            await _registry.DisposeAsync();
        }

        try { Directory.Delete(_dataRoot, recursive: true); } catch (IOException) { }
    }

    /// <summary>Builds an install with <paramref name="names"/> servers.</summary>
    private void Arrange(bool backupOnWrapperUpdate = false, params string[] names)
    {
        _paths = new SentryPaths(_dataRoot);

        var options = new InstanceOptions();
        options.ConsoleLog.Enabled = false;

        foreach (var name in names)
        {
            var definition = new InstanceDefinition { Name = name };
            definition.Backup.OnShutdown.OnWrapperUpdate = backupOnWrapperUpdate;
            definition.Backup.Zip.Enabled = backupOnWrapperUpdate;
            options.Servers[name.ToLowerInvariant()] = definition;
        }

        var store = new ServerPackageStore(_paths, NullLogger<ServerPackageStore>.Instance);

        _registry = new InstanceRegistry(
            new StaticOptionsMonitor<InstanceOptions>(options),
            _paths, _factory, TimeProvider.System, NullLoggerFactory.Instance,
            store, new NoPlayerDataStore());

        _history = new BackupHistory(_paths);

        var orchestrator = new BackupOrchestrator(
            [new ZipBackupStrategy(TimeProvider.System, NullLogger<ZipBackupStrategy>.Instance)],
            _history, TimeProvider.System, NullLogger<BackupOrchestrator>.Instance);

        _shutdown = new ManagedServerShutdown(
            _registry,
            new ShutdownBackups(
                orchestrator, TimeProvider.System, NullLogger<ShutdownBackups>.Instance),
            TimeProvider.System,
            NullLogger<ManagedServerShutdown>.Instance);
    }

    private async Task StartAllAsync()
    {
        _factory.ProcessesExitOnStop = true;

        foreach (var supervisor in _registry.All)
        {
            await supervisor.StartAsync();
            _factory.Latest.Ready();
            Assert.True(await supervisor.WaitForReadyAsync(TimeSpan.FromSeconds(5)));
        }
    }

    private IReadOnlyList<string> Said =>
        _factory.Created.SelectMany(p => p.Sent).Where(s => s.StartsWith("say ")).ToList();

    [Fact]
    public async Task Every_running_server_is_stopped()
    {
        Arrange(false, "Survival", "Creative");
        await StartAllAsync();

        var result = await _shutdown.DrainAsync(StopReason.WrapperUpdate, TimeSpan.Zero);

        Assert.Equal(2, result.Drained);
        Assert.Empty(result.Failed);
        Assert.All(_registry.All, s => Assert.Equal(InstanceState.Stopped, s.Status.State));
    }

    /// <summary>
    /// The whole point. A service stop gets five seconds because Windows is
    /// waiting; this runs before the helper is launched, so it can give players
    /// a real countdown.
    /// </summary>
    [Fact]
    public async Task Players_are_warned_and_told_it_is_the_wrapper_updating()
    {
        Arrange(false, "Survival");
        await StartAllAsync();

        await _shutdown.DrainAsync(StopReason.WrapperUpdate, TimeSpan.FromSeconds(1));

        Assert.NotEmpty(Said);
        Assert.All(Said, line => Assert.Contains("BedrockSentry", line));
    }

    /// <summary>
    /// The countdown runs outside the instance gate now, which is the whole
    /// point: everything else must still be able to reach the instance while it
    /// is counting down.
    /// </summary>
    [Fact]
    public async Task The_instance_stays_reachable_while_the_countdown_runs()
    {
        Arrange(false, "Survival");
        await StartAllAsync();

        using var cts = new CancellationTokenSource();
        var drain = _shutdown.DrainAsync(StopReason.WrapperUpdate, TimeSpan.FromMinutes(30), cts.Token);

        // A console command would have blocked on the gate for the whole
        // countdown under the first implementation, which put the warning lead
        // inside StopAsync. Half a second against a thirty-minute window is a
        // wide enough margin to mean something.
        var command = _registry.Get("survival").SendCommandAsync("list");

        Assert.Same(command, await Task.WhenAny(command, Task.Delay(500)));
        await command;

        await cts.CancelAsync();
        await drain;
    }

    /// <summary>
    /// Cancellation means "stop waiting", not "stop draining". A host shutdown
    /// landing mid-countdown has run out of patience for the courtesy; it still
    /// wants the graceful stop underneath.
    /// </summary>
    [Fact]
    public async Task A_cancelled_countdown_still_stops_the_servers()
    {
        Arrange(false, "Survival");
        await StartAllAsync();

        using var cts = new CancellationTokenSource();
        var drain = _shutdown.DrainAsync(StopReason.WrapperUpdate, TimeSpan.FromMinutes(30), cts.Token);

        await Task.Delay(100);
        await cts.CancelAsync();

        var result = await drain;

        Assert.Equal(1, result.Drained);
        Assert.Equal(InstanceState.Stopped, _registry.Get("survival").Status.State);
    }

    [Fact]
    public async Task A_drain_that_is_abandoned_can_be_put_back()
    {
        Arrange(false, "Survival");
        await StartAllAsync();

        var result = await _shutdown.DrainAsync(StopReason.WrapperUpdate, TimeSpan.Zero);
        Assert.Equal(InstanceState.Stopped, _registry.Get("survival").Status.State);

        await _shutdown.RestoreAsync(result.Stopped);

        Assert.NotEqual(InstanceState.Stopped, _registry.Get("survival").Status.State);
    }

    [Fact]
    public async Task No_warning_time_means_no_countdown_at_all()
    {
        Arrange(false, "Survival");
        await StartAllAsync();

        await _shutdown.DrainAsync(StopReason.WrapperUpdate, TimeSpan.Zero);

        Assert.Empty(Said);
        Assert.Equal(InstanceState.Stopped, _registry.Get("survival").Status.State);
    }

    /// <summary>
    /// The toggle that has been in config since milestone 7 with nothing calling
    /// it — milestone 13.5 is the caller.
    /// </summary>
    [Fact]
    public async Task An_instance_that_asked_for_a_backup_gets_one()
    {
        Arrange(true, "Survival");
        await StartAllAsync();

        await _shutdown.DrainAsync(StopReason.WrapperUpdate, TimeSpan.Zero);

        var record = Assert.Single(await _history.ReadAsync("survival"));
        Assert.Equal(BackupReason.Shutdown, record.Reason);
        Assert.Contains("wrapper update", record.Note);
    }

    [Fact]
    public async Task An_instance_that_did_not_ask_does_not_get_one()
    {
        Arrange(false, "Survival");
        await StartAllAsync();

        await _shutdown.DrainAsync(StopReason.WrapperUpdate, TimeSpan.Zero);

        Assert.Empty(await _history.ReadAsync("survival"));
    }

    // A server that is already stopped is not a failure and not a drain — an
    // install with everything down should report nothing to do rather than
    // counting servers it never touched.
    [Fact]
    public async Task Servers_that_are_already_stopped_are_left_out_of_the_count()
    {
        Arrange(false, "Survival", "Creative");

        _factory.ProcessesExitOnStop = true;
        var survival = _registry.Get("survival");
        await survival.StartAsync();
        _factory.Latest.Ready();
        Assert.True(await survival.WaitForReadyAsync(TimeSpan.FromSeconds(5)));

        var result = await _shutdown.DrainAsync(StopReason.WrapperUpdate, TimeSpan.Zero);

        Assert.Equal(1, result.Drained);
    }

    [Fact]
    public async Task An_install_with_nothing_running_reports_nothing_to_do()
    {
        Arrange(false, "Survival");

        var result = await _shutdown.DrainAsync(StopReason.WrapperUpdate, TimeSpan.Zero);

        Assert.Equal(ManagedShutdownResult.Nothing, result);
        Assert.Empty(_factory.Created);
    }

    [Fact]
    public async Task An_install_with_no_servers_at_all_is_fine()
    {
        Arrange(false);

        Assert.Equal(
            ManagedShutdownResult.Nothing,
            await _shutdown.DrainAsync(StopReason.WrapperUpdate, TimeSpan.Zero));
    }
}

/// <summary>
/// The countdown ladder, shared by the two planned outages that have a window
/// (<c>docs/design.md</c> § 11.3 and § 12) rather than written once in each.
/// </summary>
public class CountdownLadderTests
{
    [Fact]
    public void The_first_point_is_always_the_whole_window()
    {
        Assert.Equal(TimeSpan.FromMinutes(25), CountdownLadder.PointsFor(TimeSpan.FromMinutes(25))[0]);
    }

    [Fact]
    public void Points_are_ordered_longest_first_and_never_outside_the_window()
    {
        foreach (var minutes in new[] { 1, 5, 25, 30, 90, 1440 })
        {
            var window = TimeSpan.FromMinutes(minutes);
            var points = CountdownLadder.PointsFor(window);

            Assert.NotEmpty(points);
            Assert.Equal(points.OrderByDescending(p => p), points);
            Assert.All(points, p => Assert.InRange(p, TimeSpan.Zero, window));
            Assert.Equal(points.Count, points.Distinct().Count());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void No_window_means_no_points(int minutes)
    {
        Assert.Empty(CountdownLadder.PointsFor(TimeSpan.FromMinutes(minutes)));
    }

    /// <summary>
    /// Not a rung. A ten-second point cannot be honoured by a caller that speaks
    /// from a fifteen-second sweep, so the ladder leaves it out and the callers
    /// that can hit it exactly add it themselves.
    /// </summary>
    [Fact]
    public void The_last_call_is_not_in_the_ladder()
    {
        Assert.DoesNotContain(
            CountdownLadder.LastCall, CountdownLadder.PointsFor(TimeSpan.FromMinutes(30)));
    }
}

/// <summary>
/// How long is left, said out loud. The hour scale is the one that was wrong,
/// and wrong in the direction that matters — it told players they had more time
/// than they did.
/// </summary>
public class CountdownDescriptionTests
{
    [Theory]
    [InlineData(10, "10 seconds")]
    [InlineData(60, "1 minute")]
    [InlineData(300, "5 minutes")]
    [InlineData(3600, "1 hour")]
    [InlineData(5400, "1 hour 30 minutes")]
    [InlineData(4500, "1 hour 15 minutes")]
    [InlineData(7200, "2 hours")]
    [InlineData(7260, "2 hours 1 minute")]
    [InlineData(43200, "12 hours")]
    public void A_duration_is_described_as_it_is(int seconds, string expected)
    {
        Assert.Equal(expected, CountdownMessage.Describe(TimeSpan.FromSeconds(seconds)));
    }

    /// <summary>
    /// The property that broke: 90 minutes announced as "2 hours" handed players
    /// half an hour they did not have. Nothing may ever overstate the time left
    /// by a minute or more.
    /// </summary>
    [Fact]
    public void Nothing_is_ever_announced_as_longer_than_it_is()
    {
        for (var minutes = 1; minutes <= 24 * 60; minutes++)
        {
            var actual = TimeSpan.FromMinutes(minutes);
            var said = CountdownMessage.Describe(actual);

            var hours = Read(said, "hour");
            var mins = Read(said, "minute");
            var announced = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(mins);

            Assert.True(
                announced <= actual,
                $"{actual} was announced as \"{said}\", which is longer than it is");
        }
    }

    private static int Read(string text, string unit)
    {
        var words = text.Split(' ');

        for (var i = 1; i < words.Length; i++)
        {
            if (words[i].StartsWith(unit, StringComparison.Ordinal))
            {
                return int.Parse(words[i - 1]);
            }
        }

        return 0;
    }
}
