using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Configuration;
using BedrockSentry.Services.Instances;
using BedrockSentry.Services.Packages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace BedrockSentry.Tests;

/// <summary>Minimal IOptionsMonitor over a fixed value.</summary>
internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

public sealed class InstanceRegistryTests : IDisposable
{
    private readonly string _dataRoot;
    private readonly SentryPaths _paths;
    private readonly FakeProcessFactory _factory = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 8, 3, 7, 0, 0, TimeSpan.Zero));

    public InstanceRegistryTests()
    {
        _dataRoot = Path.Combine(
            Path.GetTempPath(), "bedrocksentry-tests", Guid.NewGuid().ToString("n"));
        _paths = new SentryPaths(_dataRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a green run over.
        }
    }

    private ServerPackageStore Store() =>
        new(_paths, NullLogger<ServerPackageStore>.Instance);

    private InstanceRegistry Build(InstanceOptions options) =>
        new(new StaticOptionsMonitor<InstanceOptions>(options),
            _paths, _factory, _time, NullLoggerFactory.Instance, Store(), new NoPlayerDataStore());

    private static InstanceOptions WithServers(params (string Id, InstanceDefinition Definition)[] servers)
    {
        var options = new InstanceOptions();

        // Transcripts off by default here: these tests are about the registry,
        // and a writer per instance would leave background pumps running.
        options.ConsoleLog.Enabled = false;

        foreach (var (id, definition) in servers)
        {
            options.Servers[id] = definition;
        }

        return options;
    }

    [Fact]
    public async Task Builds_a_supervisor_per_configured_instance()
    {
        await using var registry = Build(WithServers(
            ("survival", new InstanceDefinition { Name = "Survival" }),
            ("creative", new InstanceDefinition { Name = "Creative" })));

        Assert.Equal(2, registry.All.Count);
        Assert.True(registry.TryGet("survival", out _));
        Assert.True(registry.TryGet("SURVIVAL", out _));   // ids are folder names; case must not split them
    }

    // D14: a retired instance keeps its player history but is never launched
    // again, so it gets no supervisor to launch it with.
    [Fact]
    public async Task Skips_retired_instances()
    {
        await using var registry = Build(WithServers(
            ("survival", new InstanceDefinition { Name = "Survival" }),
            ("old", new InstanceDefinition { Name = "Old", Retired = true })));

        Assert.Single(registry.All);
        Assert.False(registry.TryGet("old", out _));
    }

    [Fact]
    public async Task Defaults_the_root_to_the_data_root()
    {
        await using var registry = Build(WithServers(("survival", new InstanceDefinition())));

        Assert.Equal(_paths.Instances, registry.Root);
    }

    [Fact]
    public async Task Honours_a_configured_root()
    {
        var elsewhere = Path.Combine(_dataRoot, "custom-root");
        var options = WithServers(("survival", new InstanceDefinition { Name = "Survival" }));
        options.Root = elsewhere;

        await using var registry = Build(options);

        Assert.Equal(Path.GetFullPath(elsewhere), registry.Root);
        Assert.True(Directory.Exists(Path.Combine(elsewhere, "Survival")));
    }

    // Created eagerly so there is somewhere obvious to put bedrock_server.exe,
    // and so the path is logged before the first failed start rather than after.
    [Fact]
    public async Task Creates_each_working_directory_up_front()
    {
        await using var registry = Build(WithServers(
            ("survival", new InstanceDefinition { Name = "Survival" })));

        Assert.True(Directory.Exists(Path.Combine(_paths.Instances, "Survival")));
        Assert.Equal(
            Path.Combine(_paths.Instances, "Survival"),
            registry.Get("survival").WorkingDirectory);
    }

    [Fact]
    public async Task Honours_a_per_instance_working_directory_override()
    {
        var elsewhere = Path.Combine(_dataRoot, "other-volume", "big-world");

        await using var registry = Build(WithServers(
            ("survival", new InstanceDefinition { Name = "Survival", WorkingDirectory = elsewhere })));

        Assert.Equal(Path.GetFullPath(elsewhere), registry.Get("survival").WorkingDirectory);
        Assert.True(Directory.Exists(elsewhere));
    }

    [Fact]
    public async Task An_unknown_instance_is_a_clear_error()
    {
        await using var registry = Build(WithServers(("survival", new InstanceDefinition())));

        var ex = Assert.Throws<KeyNotFoundException>(() => registry.Get("nope"));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public async Task Statuses_are_ordered_by_display_name()
    {
        await using var registry = Build(WithServers(
            ("z", new InstanceDefinition { Name = "Zulu" }),
            ("a", new InstanceDefinition { Name = "Alpha" }),
            ("m", new InstanceDefinition { Name = "Mike" })));

        Assert.Equal(["Alpha", "Mike", "Zulu"], registry.Statuses().Select(s => s.Name));
    }

    [Fact]
    public async Task No_configured_instances_is_a_valid_state()
    {
        await using var registry = Build(WithServers());

        Assert.Empty(registry.All);
        Assert.Empty(registry.Statuses());
    }
}

public sealed class InstanceHostedServiceTests : IDisposable
{
    private readonly string _dataRoot;
    private readonly SentryPaths _paths;
    private readonly FakeProcessFactory _factory = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 8, 3, 7, 0, 0, TimeSpan.Zero));

    public InstanceHostedServiceTests()
    {
        _dataRoot = Path.Combine(
            Path.GetTempPath(), "bedrocksentry-tests", Guid.NewGuid().ToString("n"));
        _paths = new SentryPaths(_dataRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a green run over.
        }
    }

    private (InstanceRegistry Registry, InstanceHostedService Service) Build(
        InstanceOptions options, IStartPreflight? preflight = null)
    {
        var registry = new InstanceRegistry(
            new StaticOptionsMonitor<InstanceOptions>(options),
            _paths, _factory, _time, NullLoggerFactory.Instance, Store(), new NoPlayerDataStore());

        return (registry, new InstanceHostedService(
            registry, NullLogger<InstanceHostedService>.Instance, startPreflight: preflight,
            time: _time));
    }

    private static InstanceOptions Options(params (string Id, InstanceDefinition Definition)[] servers)
    {
        var options = new InstanceOptions();
        options.ConsoleLog.Enabled = false;
        foreach (var (id, definition) in servers)
        {
            options.Servers[id] = definition;
        }

        return options;
    }

    private ServerPackageStore Store() =>
        new(_paths, NullLogger<ServerPackageStore>.Instance);

    /// <summary>
    /// Lets the re-check loop reach its <c>Task.Delay</c> before the fake clock
    /// moves past it.
    /// </summary>
    /// <remarks>
    /// <c>StartAsync</c> schedules the loop and returns — deliberately, it is on the
    /// SCM's clock — so the timer is registered on a thread-pool thread a moment
    /// later. Advancing a <see cref="FakeTimeProvider"/> before that happens means
    /// the delay is registered against the <em>new</em> now and waits out its full
    /// span all over again, which never arrives. Same real-time pause the graceful
    /// stop test above uses for the same reason.
    /// </remarks>
    private async Task Advance(TimeSpan by)
    {
        await Task.Delay(60);
        _time.Advance(by);
    }

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

    [Fact]
    public async Task Starts_instances_marked_for_auto_start()
    {
        var (registry, service) = Build(Options(
            ("survival", new InstanceDefinition { Name = "Survival", AutoStart = true })));
        await using var _ = registry;

        await service.StartAsync(CancellationToken.None);

        Assert.Single(_factory.Created);
        Assert.Equal(InstanceState.Starting, registry.Get("survival").Status.State);
    }

    [Fact]
    public async Task Leaves_instances_that_opt_out_alone()
    {
        var (registry, service) = Build(Options(
            ("survival", new InstanceDefinition { Name = "Survival", AutoStart = false })));
        await using var _ = registry;

        await service.StartAsync(CancellationToken.None);

        Assert.Empty(_factory.Created);
        Assert.Equal(InstanceState.Stopped, registry.Get("survival").Status.State);
    }

    // T149's actual scenario: the wrapper comes up, an auto-start instance's port
    // is held by something else, and the launch must be refused at boot rather
    // than crash-looped against a world another process has open. The only test
    // that goes through the real wiring — InstanceHostedService assigning
    // registry.StartPreflight, the registry handing the supervisor a lambda over
    // it — rather than handing a supervisor a fake directly. If either of those
    // two lines went, this is what would notice.
    [Fact]
    public async Task An_auto_start_whose_port_is_held_is_refused_at_boot_and_names_the_holder()
    {
        var held = new PortConflict(19132, "is already open on this machine by bedrock_server (PID 8172)");
        var preflight = new FakeStartPreflight().Then(held);
        var (registry, service) = Build(
            Options(("survival", new InstanceDefinition { Name = "Survival", AutoStart = true })),
            preflight);
        await using var _ = registry;

        await service.StartAsync(CancellationToken.None);

        Assert.Same(preflight, registry.StartPreflight);
        Assert.Empty(_factory.Created);

        var status = registry.Get("survival").Status;
        Assert.Equal(InstanceState.Faulted, status.State);
        Assert.Contains("bedrock_server (PID 8172)", status.FaultReason);
        Assert.Contains("19132", status.FaultReason);

        Assert.Single(preflight.Asked);
        Assert.Equal("survival", preflight.Asked[0].InstanceId);
    }

    // One bad instance must not stop the others, and must not stop the web
    // interface — which is where an administrator would go to find out why.
    [Fact]
    public async Task One_instance_failing_to_launch_does_not_block_the_rest()
    {
        _factory.FailEveryStart = true;

        var (registry, service) = Build(Options(
            ("a", new InstanceDefinition { Name = "A" }),
            ("b", new InstanceDefinition { Name = "B" })));
        await using var _ = registry;

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(2, _factory.Created.Count);
    }

    [Fact]
    public async Task Stops_every_running_instance_gracefully()
    {
        var options = Options(
            ("a", new InstanceDefinition { Name = "A" }),
            ("b", new InstanceDefinition { Name = "B" }));
        options.ServiceStopWarning = TimeSpan.FromSeconds(5);

        var (registry, service) = Build(options);
        await using var _ = registry;

        await service.StartAsync(CancellationToken.None);
        foreach (var process in _factory.Created)
        {
            process.Ready();
        }

        Assert.True(await WaitUntil(
            () => registry.All.All(s => s.Status.State == InstanceState.Running)));

        var stop = service.StopAsync(CancellationToken.None);

        // Both get the same short warning, in parallel — the drain budget is
        // per-instance, not cumulative, because the SCM's patience does not scale
        // with the number of servers.
        Assert.True(await WaitUntil(() => _factory.Created.All(p => p.Sent.Count == 1)));
        Assert.All(_factory.Created, p =>
            Assert.Equal("say Server shutting down in 5 seconds", p.Sent[0]));

        await Task.Delay(60);
        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.True(await WaitUntil(() => _factory.Created.All(p => p.Sent.Contains("stop"))));

        foreach (var process in _factory.Created)
        {
            process.Exit(0);
        }

        await stop;
        Assert.All(registry.All, s => Assert.Equal(InstanceState.Stopped, s.Status.State));
        Assert.All(_factory.Created, p => Assert.False(p.Killed));
    }

    [Fact]
    public async Task Stopping_with_nothing_running_is_a_no_op()
    {
        var (registry, service) = Build(Options(
            ("survival", new InstanceDefinition { Name = "Survival", AutoStart = false })));
        await using var _ = registry;

        await service.StopAsync(CancellationToken.None);

        Assert.Empty(_factory.Created);
    }

    // A faulted instance has already given up; asking it to stop would only wait
    // out a drain budget for a process that is not there.
    [Fact]
    public async Task Does_not_try_to_stop_a_faulted_instance()
    {
        var options = Options(("survival", new InstanceDefinition { Name = "Survival" }));
        options.Servers["survival"].Watchdog.MaxConsecutiveFailures = 1;

        var (registry, service) = Build(options);
        await using var _ = registry;

        await service.StartAsync(CancellationToken.None);
        _factory.Latest.Exit(1);

        Assert.True(await WaitUntil(
            () => registry.Get("survival").Status.State == InstanceState.Faulted));

        // Completes immediately rather than waiting out a warning and a drain.
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        // And it is a give-up rather than a refusal, which is what keeps the
        // re-check below away from it (`T167`). Asserted here rather than in a test
        // of its own because this is the only rig in the suite that produces one.
        Assert.Equal(InstanceFault.GaveUp, registry.Get("survival").Status.FaultKind);
    }

    // ---- T167: a refusal at boot is re-checked --------------------------------

    /// <summary>
    /// The defect, in the shape it was found in: the service comes up while its own
    /// predecessor's <c>bedrock_server.exe</c> is still draining, the auto-start is
    /// correctly refused, and then the holder exits.
    /// </summary>
    /// <remarks>
    /// Before the fix this box stayed down with nothing running and nothing
    /// retrying — the auto-start loop is a single pass, and a refusal does not
    /// throw, so it simply moved on. The second answer from the pre-flight is the
    /// predecessor having gone.
    /// </remarks>
    [Fact]
    public async Task A_refusal_at_boot_is_rechecked_and_starts_once_the_holder_goes()
    {
        var held = new PortConflict(19132, "is already open on this machine by bedrock_server (PID 46496)");
        var preflight = new FakeStartPreflight().Then(held).Then();
        var (registry, service) = Build(
            Options(("survival", new InstanceDefinition { Name = "Survival", AutoStart = true })),
            preflight);
        await using var _ = registry;

        await service.StartAsync(CancellationToken.None);

        Assert.Empty(_factory.Created);
        Assert.Equal(InstanceFault.RefusedToStart, registry.Get("survival").Status.FaultKind);

        await Advance(RefusedStartRetry.Schedule[0]);

        Assert.True(await WaitUntil(() => _factory.Created.Count == 1));
        Assert.Equal(InstanceState.Starting, registry.Get("survival").Status.State);
        Assert.Null(registry.Get("survival").Status.FaultReason);
        Assert.Equal(InstanceFault.None, registry.Get("survival").Status.FaultKind);

        // No StopAsync here on purpose: the instance is Starting now, so a stop
        // would drain it — a warning and a budget measured on the fake clock this
        // test has stopped advancing. Registry disposal is the right teardown for a
        // server that is up, and the stop path has its own test below.
    }

    /// <summary>
    /// And a conflict that is not transient settles into a fault rather than
    /// re-checking forever.
    /// </summary>
    /// <remarks>
    /// The bound is the point: a port held by something permanent would otherwise
    /// log an error every thirty seconds for as long as the service runs. One ask
    /// at boot plus one per scheduled delay, and then nothing.
    /// </remarks>
    [Fact]
    public async Task A_refusal_that_never_clears_stops_after_the_schedule()
    {
        var held = new PortConflict(19132, "is already open on this machine by something else");
        var preflight = new FakeStartPreflight().Then(held);
        var (registry, service) = Build(
            Options(("survival", new InstanceDefinition { Name = "Survival", AutoStart = true })),
            preflight);
        await using var _ = registry;

        await service.StartAsync(CancellationToken.None);

        foreach (var delay in RefusedStartRetry.Schedule)
        {
            var before = preflight.Asked.Count;
            await Advance(delay);
            Assert.True(await WaitUntil(() => preflight.Asked.Count > before));
        }

        // One more full window, to catch a loop that does not know how to finish.
        await Advance(RefusedStartRetry.Window);
        await Task.Delay(60);

        Assert.Equal(1 + RefusedStartRetry.Schedule.Length, preflight.Asked.Count);
        Assert.Empty(_factory.Created);

        var status = registry.Get("survival").Status;
        Assert.Equal(InstanceState.Faulted, status.State);
        Assert.Equal(InstanceFault.RefusedToStart, status.FaultKind);

        // The reason is left as it was found: it still names what is holding the
        // port, which is the sentence somebody needs.
        Assert.Contains("19132", status.FaultReason);

        // And the instance's own console says both halves, because that is the
        // surface an administrator is looking at. Only the Windows event log having
        // them would leave the panel reading "refused" for two minutes with no sign
        // that anything was trying, and then reading the same thing forever.
        var console = registry.Get("survival").Console.Read(0).Lines
            .Where(l => l.Origin == ConsoleOrigin.Notice)
            .Select(l => l.Message)
            .ToList();

        Assert.Contains(console, line => line.Contains("re-checking up to"));
        Assert.Contains(console, line =>
            line.StartsWith("ALERT:", StringComparison.Ordinal)
            && line.Contains("still refusing to start after"));

        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// THE ARM THAT CAN LIE. Stopping the host must stop the re-check before the
    /// drain, not alongside it.
    /// </summary>
    /// <remarks>
    /// A re-check firing during shutdown would launch a server the host is in the
    /// middle of stopping, and it would not be in the drain's list — that list is
    /// taken once, and this instance was faulted when it was taken. The result is a
    /// <c>bedrock_server.exe</c> left running with no parent, holding the world
    /// open for the next service start to refuse: <c>T167</c> recreated by
    /// <c>T167</c>'s own fix. So the ordering in <c>StopAsync</c> is load-bearing
    /// and this is the test that pins it.
    /// </remarks>
    [Fact]
    public async Task Stopping_the_host_stops_the_recheck_before_anything_can_be_launched()
    {
        var held = new PortConflict(19132, "is already open on this machine by bedrock_server (PID 46496)");

        // Would clear on the first re-check, so if the re-check runs at all after
        // the stop, a process gets created and this fails.
        var preflight = new FakeStartPreflight().Then(held).Then();
        var (registry, service) = Build(
            Options(("survival", new InstanceDefinition { Name = "Survival", AutoStart = true })),
            preflight);
        await using var _ = registry;

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        var asked = preflight.Asked.Count;
        await Advance(RefusedStartRetry.Window + RefusedStartRetry.Schedule[0]);
        await Task.Delay(60);

        Assert.Equal(asked, preflight.Asked.Count);
        Assert.Empty(_factory.Created);
    }

    /// <summary>
    /// The reason and the kind move together, asserted over every status the
    /// supervisor publishes rather than against a table.
    /// </summary>
    /// <remarks>
    /// Two fields stating one thing is this codebase's recurring defect class, and a
    /// fourth writer setting one and forgetting the other is exactly how the
    /// re-check would start reading a give-up as a refusal. This sequence crosses
    /// both of the sites that write them — the refusal, and the launch that clears
    /// them — so the pairing is checked where it is produced.
    /// </remarks>
    [Fact]
    public async Task The_fault_reason_and_the_fault_kind_are_never_set_without_each_other()
    {
        var held = new PortConflict(19132, "is already open on this machine by bedrock_server (PID 46496)");
        var preflight = new FakeStartPreflight().Then(held).Then();
        var (registry, service) = Build(
            Options(("survival", new InstanceDefinition { Name = "Survival", AutoStart = true })),
            preflight);
        await using var _ = registry;

        var published = new List<InstanceStatus>();
        registry.Get("survival").StatusChanged += status =>
        {
            lock (published)
            {
                published.Add(status);
            }
        };

        await service.StartAsync(CancellationToken.None);
        await Advance(RefusedStartRetry.Schedule[0]);
        Assert.True(await WaitUntil(() => _factory.Created.Count == 1));

        InstanceStatus[] seen;
        lock (published)
        {
            seen = [.. published];
        }

        // Both sites were crossed, or this proves nothing.
        Assert.Contains(seen, s => s.FaultKind is InstanceFault.RefusedToStart);
        Assert.Contains(seen, s => s.FaultKind is InstanceFault.None);

        Assert.All(seen, status => Assert.Equal(
            status.FaultKind is InstanceFault.None, status.FaultReason is null));
    }

    // Nothing refused, so nothing is scheduled and a stop has nothing to wait for.
    [Fact]
    public async Task A_clean_boot_schedules_no_recheck()
    {
        var preflight = new FakeStartPreflight();
        var (registry, service) = Build(
            Options(("survival", new InstanceDefinition { Name = "Survival", AutoStart = true })),
            preflight);
        await using var _ = registry;

        await service.StartAsync(CancellationToken.None);
        Assert.Single(_factory.Created);

        await Advance(RefusedStartRetry.Window);
        await Task.Delay(60);

        Assert.Single(preflight.Asked);
        Assert.Single(_factory.Created);
    }
}
