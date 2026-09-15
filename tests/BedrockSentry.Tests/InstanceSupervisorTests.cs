using System.Collections.Concurrent;
using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BedrockSentry.Tests;

/// <summary>
/// A controllable stand-in for a launched server. Everything the supervisor can
/// observe — output, exit, kill — is driven by the test.
/// </summary>
internal sealed class FakeBedrockProcess : IBedrockProcess
{
    private readonly TaskCompletionSource<int> _exit =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly ConcurrentQueue<string> _sent = new();

    public bool ThrowOnStart { get; set; }

    /// <summary>
    /// Exit when told to stop, the way a real server does. Opt-in because
    /// several tests here drive the exit by hand in order to time the drain
    /// budget and the kill that follows it.
    /// </summary>
    public bool ExitsOnStop { get; set; }

    public int? ProcessId { get; private set; }

    public bool HasExited { get; private set; }

    public bool Killed { get; private set; }

    public IReadOnlyList<string> Sent => _sent.ToArray();

    public event Action<ConsoleOrigin, string>? OutputReceived;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (ThrowOnStart)
        {
            throw new FileNotFoundException("no such executable");
        }

        ProcessId = 4242;
        return Task.CompletedTask;
    }

    /// <summary>
    /// What this fake server does about a command it is given — the hook a test
    /// uses to imitate BDS rewriting one of its own files in answer.
    /// </summary>
    /// <remarks>
    /// Causal rather than timed, and that is the whole point. The access-control
    /// tests used to answer on a 300 ms timer started alongside the call, which
    /// raced the confirmation window: it passed every time locally and failed
    /// build.62 on a loaded Windows runner, where the send took longer than the
    /// window it was being measured against. A rewrite that can only happen after
    /// the command is one no runner's timing can get wrong.
    /// </remarks>
    public Func<string, Task>? Answers { get; set; }

    public async Task SendAsync(string command, CancellationToken cancellationToken = default)
    {
        _sent.Enqueue(command);

        if (ExitsOnStop && command.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            Exit(0);
        }

        if (Answers is { } answer)
        {
            await answer(command).ConfigureAwait(false);
        }
    }

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) =>
        _exit.Task.WaitAsync(cancellationToken);

    public void Kill()
    {
        Killed = true;
        Exit(137);
    }

    public void Emit(string line) => OutputReceived?.Invoke(ConsoleOrigin.Stdout, line);

    public void Ready() => Emit("[2026-08-03 07:00:16:314 INFO] Server started.");

    public void Exit(int code)
    {
        HasExited = true;
        _exit.TrySetResult(code);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeProcessFactory : IBedrockProcessFactory
{
    private readonly ConcurrentQueue<FakeBedrockProcess> _created = new();

    public bool FailEveryStart { get; set; }

    /// <summary>Applied to every process this factory creates.</summary>
    public bool ProcessesExitOnStop { get; set; }

    public IReadOnlyList<FakeBedrockProcess> Created => _created.ToArray();

    public FakeBedrockProcess Latest => Created[^1];

    public IBedrockProcess Create(string instanceId, InstanceDefinition definition, string workingDirectory)
    {
        var process = new FakeBedrockProcess
        {
            ThrowOnStart = FailEveryStart,
            ExitsOnStop = ProcessesExitOnStop,
        };
        _created.Enqueue(process);
        return process;
    }
}

/// <summary>
/// A scripted answer to "is anything in the way of this server's ports". Each
/// call consumes the next answer; the last one repeats, so a test that wants
/// "clear, then held" says so in two entries and no more.
/// </summary>
internal sealed class FakeStartPreflight : IStartPreflight
{
    private readonly Queue<IReadOnlyList<PortConflict>> _answers = new();
    private IReadOnlyList<PortConflict> _last = [];

    public List<(string InstanceId, string WorkingDirectory)> Asked { get; } = [];

    public FakeStartPreflight Then(params PortConflict[] conflicts)
    {
        _answers.Enqueue(conflicts);
        return this;
    }

    public IReadOnlyList<PortConflict> ConflictsForStart(string instanceId, string workingDirectory)
    {
        Asked.Add((instanceId, workingDirectory));

        if (_answers.TryDequeue(out var next))
        {
            _last = next;
        }

        return _last;
    }
}

/// <summary>
/// A scripted answer to "is a server this wrapper did not start already on this
/// world" (`D56`). Records what it was asked, so a test can assert the
/// supervisor excluded its own child rather than inferring it.
/// </summary>
internal sealed class FakeOrphanInspector : IOrphanInspector
{
    private readonly Queue<IReadOnlyList<OrphanedServer>> _answers = new();
    private IReadOnlyList<OrphanedServer> _last = [];

    public List<(string WorkingDirectory, int[] Ours)> Asked { get; } = [];

    public FakeOrphanInspector Then(params OrphanedServer[] orphans)
    {
        _answers.Enqueue(orphans);
        return this;
    }

    public IReadOnlyList<OrphanedServer> Find(string workingDirectory, IReadOnlyCollection<int> ours)
    {
        Asked.Add((workingDirectory, [.. ours]));

        if (_answers.TryDequeue(out var next))
        {
            _last = next;
        }

        return _last;
    }

    public OrphanTermination Terminate(int processId, string workingDirectory) =>
        throw new InvalidOperationException("no test here terminates anything");
}

public class InstanceSupervisorTests
{
    private static readonly DateTimeOffset Origin = new(2026, 8, 3, 7, 0, 0, TimeSpan.Zero);

    private static (InstanceSupervisor Supervisor, FakeProcessFactory Factory, FakeTimeProvider Time)
        Build(
            Action<InstanceDefinition>? configure = null,
            IStartPreflight? preflight = null,
            IOrphanInspector? orphans = null)
    {
        var definition = new InstanceDefinition { Name = "Survival" };
        definition.Watchdog.InitialBackoff = TimeSpan.FromSeconds(10);
        definition.Watchdog.MaxConsecutiveFailures = 3;
        definition.Watchdog.StableAfter = TimeSpan.FromMinutes(2);
        configure?.Invoke(definition);

        var factory = new FakeProcessFactory();
        var time = new FakeTimeProvider(Origin);
        var supervisor = new InstanceSupervisor(
            "survival", definition, "/instances/survival", factory, time,
            NullLogger<InstanceSupervisor>.Instance,
            startPreflight: preflight is null ? null : () => preflight,
            orphans: orphans is null ? null : () => orphans);

        return (supervisor, factory, time);
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

    /// <summary>
    /// Advances the fake clock only once the supervisor is actually waiting on
    /// it. Advancing before the delay is registered leaves the timer measuring
    /// from the new "now", and the wait never ends.
    /// </summary>
    private static async Task Advance(FakeTimeProvider time, TimeSpan by)
    {
        await Task.Delay(60);
        time.Advance(by);
    }

    [Fact]
    public async Task Becomes_running_only_once_the_server_reports_ready()
    {
        var (supervisor, factory, _) = Build();
        await using var _s = supervisor;

        await supervisor.StartAsync();
        Assert.Equal(InstanceState.Starting, supervisor.Status.State);
        Assert.Equal(4242, supervisor.Status.ProcessId);
        Assert.Null(supervisor.Status.ReadyUtc);

        factory.Latest.Ready();

        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));
        Assert.Equal(Origin, supervisor.Status.ReadyUtc);
    }

    // A server with no log file of its own prefixes every line with
    // "NO LOG FILE! - ", including the one readiness is detected by. The
    // instance ran, took players, and sat on Starting indefinitely.
    [Fact]
    public async Task Becomes_running_even_when_the_server_writes_no_log_file()
    {
        var (supervisor, factory, _) = Build();
        await using var _s = supervisor;

        await supervisor.StartAsync();

        factory.Latest.Emit("NO LOG FILE! - setting up server logging...");
        factory.Latest.Emit("NO LOG FILE! - [2026-08-03 07:00:16:200 INFO] Version: 1.26.36.1");
        factory.Latest.Emit("NO LOG FILE! - [2026-08-03 07:00:16:314 INFO] Server started.");

        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));

        // The same prefix hid the version, which is read the same way.
        Assert.Equal("1.26.36.1", supervisor.Status.ServerVersion);
    }

    // Each run of the server opens with a boundary line rather than another
    // notice, because a restart drops hundreds of lines of startup on top of
    // hundreds that look exactly the same. Without a break, the previous run's
    // fault reads as this one's.
    [Fact]
    public async Task Each_launch_opens_with_a_boundary_line()
    {
        var (supervisor, factory, time) = Build();
        await using var _s = supervisor;

        await supervisor.StartAsync();
        factory.Latest.Ready();
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));

        factory.Latest.Exit(1);
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Backoff));
        await Advance(time, TimeSpan.FromSeconds(10));
        Assert.True(await WaitUntil(() => factory.Created.Count == 2));

        var launches = supervisor.Console.Read(0).Lines
            .Where(l => l.Origin == ConsoleOrigin.Launch)
            .ToList();

        Assert.Equal(2, launches.Count);
        Assert.All(launches, l => Assert.StartsWith("starting (pid ", l.Message));
    }

    // Once, not on all six thousand lines of a startup.
    [Fact]
    public async Task Explains_the_missing_log_file_a_single_time()
    {
        var (supervisor, factory, _) = Build();
        await using var _s = supervisor;

        await supervisor.StartAsync();

        factory.Latest.Emit("NO LOG FILE! - [2026-08-03 07:00:16:200 INFO] Starting Server");
        factory.Latest.Emit("NO LOG FILE! - [2026-08-03 07:00:16:314 INFO] Server started.");

        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));

        var notices = supervisor.Console.Read(0).Lines
            .Where(l => l.Origin == ConsoleOrigin.Notice && l.Message.Contains("NO LOG FILE"));

        Assert.Single(notices);
    }

    [Fact]
    public async Task An_unexpected_exit_backs_off_then_relaunches()
    {
        var (supervisor, factory, time) = Build();
        await using var _s = supervisor;

        await supervisor.StartAsync();
        factory.Latest.Ready();
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));

        factory.Latest.Exit(1);

        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Backoff));
        Assert.Equal(1, supervisor.Status.ConsecutiveFailures);
        Assert.Equal(1, supervisor.Status.LastExitCode);
        Assert.Equal(Origin.AddSeconds(10), supervisor.Status.NextRetryUtc);
        Assert.Single(factory.Created);

        await Advance(time, TimeSpan.FromSeconds(10));

        Assert.True(await WaitUntil(() => factory.Created.Count == 2));
        Assert.Equal(InstanceState.Starting, supervisor.Status.State);
    }

    // D15: the give-up rule. Retrying forever turns one bad start into permanent
    // world-file churn.
    [Fact]
    public async Task Gives_up_and_faults_after_the_configured_failure_count()
    {
        var (supervisor, factory, time) = Build();
        await using var _s = supervisor;

        await supervisor.StartAsync();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Assert.True(await WaitUntil(() => factory.Created.Count == attempt));
            factory.Latest.Exit(1);

            if (attempt < 3)
            {
                Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Backoff));
                await Advance(time, supervisor.Status.NextRetryUtc!.Value - time.GetUtcNow());
            }
        }

        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Faulted));
        Assert.Equal(3, supervisor.Status.ConsecutiveFailures);
        Assert.Contains("gave up", supervisor.Status.FaultReason);
        Assert.Null(supervisor.Status.NextRetryUtc);

        // The alert is as load-bearing as the stopping: a server that is quietly
        // down is worse than one that is loudly down.
        var console = supervisor.Console.Read(0).Lines;
        Assert.Contains(console, l => l.Origin == ConsoleOrigin.Notice && l.Raw.Contains("ALERT"));

        // And it really has stopped trying.
        await Advance(time, TimeSpan.FromHours(1));
        Assert.Equal(3, factory.Created.Count);
    }

    // T149. The thing this prevents is a second process opening a world that a
    // first one still has open: BDS opens and repairs the level before it finds
    // the port is taken, so "let it fail at the bind" is three repair passes on
    // a live database. The refusal has to happen before a process exists.
    [Fact]
    public async Task Start_is_refused_when_a_port_is_held_and_names_the_holder()
    {
        var held = new PortConflict(19232, "is already open on this machine by bedrock_server (PID 8172)");
        var preflight = new FakeStartPreflight().Then(held);
        var (supervisor, factory, time) = Build(preflight: preflight);
        await using var _s = supervisor;

        await supervisor.StartAsync();

        // Nothing launched — not "launched and died", nothing.
        Assert.Empty(factory.Created);
        Assert.Equal(InstanceState.Faulted, supervisor.Status.State);
        Assert.Null(supervisor.Status.ProcessId);
        Assert.Null(supervisor.Status.NextRetryUtc);

        // The reason names the holder, process and PID, and says the world was
        // not opened. That is what an operator needs to trust that the wrapper is
        // about to blame a real bedrock_server and not something unrelated.
        Assert.Contains("bedrock_server", supervisor.Status.FaultReason);
        Assert.Contains("8172", supervisor.Status.FaultReason);
        Assert.Contains("19232", supervisor.Status.FaultReason);
        Assert.Contains("world was not opened", supervisor.Status.FaultReason);
        Assert.DoesNotContain("gave up", supervisor.Status.FaultReason);

        // Said on the console too, as an ALERT, the same way the watchdog gives up.
        var console = supervisor.Console.Read(0).Lines;
        Assert.Contains(console, l =>
            l.Origin == ConsoleOrigin.Notice && l.Raw.Contains("ALERT") && l.Raw.Contains("8172"));

        // Asked about the right server in the right directory.
        Assert.Single(preflight.Asked);
        Assert.Equal(("survival", "/instances/survival"), preflight.Asked[0]);

        // And not retried: a refusal is not a crash, and a crash loop is the
        // thing being prevented. Created cannot move either way here — a retry
        // would be refused again by the same fake — so the assertion that can
        // actually fail is that nobody ASKED again.
        await Advance(time, TimeSpan.FromHours(1));
        Assert.Empty(factory.Created);
        Assert.Single(preflight.Asked);
        Assert.Equal(InstanceState.Faulted, supervisor.Status.State);
    }

    // The operator kills the orphan, or changes the port, and presses Start.
    // That has to work without a service restart, or the refusal has turned a
    // recoverable situation into a support call.
    [Fact]
    public async Task A_refused_start_recovers_once_the_port_is_free()
    {
        var held = new PortConflict(19232, "is already open on this machine by bedrock_server (PID 8172)");
        var preflight = new FakeStartPreflight().Then(held).Then();
        var (supervisor, factory, _) = Build(preflight: preflight);
        await using var _s = supervisor;

        await supervisor.StartAsync();
        Assert.Equal(InstanceState.Faulted, supervisor.Status.State);
        Assert.Empty(factory.Created);

        await supervisor.StartAsync();

        Assert.Single(factory.Created);
        Assert.NotEqual(InstanceState.Faulted, supervisor.Status.State);
        Assert.Null(supervisor.Status.FaultReason);
        Assert.NotNull(supervisor.Status.ProcessId);
        Assert.Equal(2, preflight.Asked.Count);
    }

    // The watchdog's retry is a launch too, and it is the one that did the damage
    // in T149: the port was free when the server first started, and held by the
    // time the watchdog tried again. It must fault with the port named, not with
    // "gave up after 3 consecutive failures".
    [Fact]
    public async Task A_watchdog_retry_is_refused_if_the_port_is_taken_meanwhile()
    {
        var held = new PortConflict(19136, "is already open on this machine by bedrock_server (PID 8172)");
        var preflight = new FakeStartPreflight().Then().Then(held);
        var (supervisor, factory, time) = Build(preflight: preflight);
        await using var _s = supervisor;

        await supervisor.StartAsync();
        Assert.True(await WaitUntil(() => factory.Created.Count == 1));

        factory.Latest.Exit(1);
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Backoff));
        await Advance(time, supervisor.Status.NextRetryUtc!.Value - time.GetUtcNow());

        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Faulted));

        // One process ever, and the reason is the port, not the count. Asked
        // twice: the first launch consumed the clear answer, the retry got the
        // held one — stated, not inferred from the process count.
        Assert.Single(factory.Created);
        Assert.Equal(2, preflight.Asked.Count);
        Assert.Contains("8172", supervisor.Status.FaultReason);
        Assert.Contains("19136", supervisor.Status.FaultReason);
        Assert.DoesNotContain("gave up", supervisor.Status.FaultReason);
        Assert.Null(supervisor.Status.NextRetryUtc);

        // Nobody asks again either: the loop ended on the refusal.
        await Advance(time, TimeSpan.FromHours(1));
        Assert.Single(factory.Created);
        Assert.Equal(2, preflight.Asked.Count);
        Assert.Equal(InstanceState.Faulted, supervisor.Status.State);
    }

    // A check that cannot be made is not a reason to refuse. The panel is
    // advisory; a start that would have worked must not be blocked by the
    // inspector throwing.
    [Fact]
    public async Task A_preflight_that_throws_does_not_block_the_start()
    {
        var (supervisor, factory, _) = Build(preflight: new ThrowingStartPreflight());
        await using var _s = supervisor;

        await supervisor.StartAsync();

        Assert.Single(factory.Created);
        Assert.NotEqual(InstanceState.Faulted, supervisor.Status.State);
    }

    // Found in review, and it is the health gate lying. _ready is only replaced
    // after the pre-flight, so a refusal used to leave the PREVIOUS run's completed
    // task in place - and WaitForReadyAsync would answer "started" for a server
    // that was never launched. BdsUpdateInstaller deletes the rollback build on
    // that answer. A refused start must read as not ready.
    [Fact]
    public async Task WaitForReady_is_false_after_a_refused_start_even_if_the_last_run_was_ready()
    {
        var held = new PortConflict(19232, "is already open on this machine by bedrock_server (PID 8172)");
        var preflight = new FakeStartPreflight().Then().Then(held);
        var (supervisor, factory, _) = Build(preflight: preflight);
        await using var _s = supervisor;

        // A full run that really did report started, so _ready is a completed task.
        await supervisor.StartAsync();
        Assert.True(await WaitUntil(() => factory.Created.Count == 1));
        factory.Latest.Ready();
        Assert.True(await supervisor.WaitForReadyAsync(TimeSpan.FromSeconds(1)));

        factory.Latest.Exit(1);
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Backoff));

        // Now the port is held and the next start is refused.
        await supervisor.StartAsync();
        Assert.Equal(InstanceState.Faulted, supervisor.Status.State);
        Assert.Single(factory.Created);

        Assert.False(await supervisor.WaitForReadyAsync(TimeSpan.FromSeconds(1)));
    }

    // Also from review. StartAsync used to dispose the previous run's token
    // without cancelling it, so a monitor asleep in its backoff kept running on a
    // token that could never fire, woke up later, and launched a second process
    // behind the new one. With a launch now able to be refused, that second
    // launch would refuse itself against the wrapper's own child and forget the
    // live one - an orphan made by the wrapper. The old monitor is now cancelled
    // and awaited first.
    [Fact]
    public async Task A_start_during_backoff_does_not_leave_the_old_monitor_to_launch_again()
    {
        var (supervisor, factory, time) = Build();
        await using var _s = supervisor;

        await supervisor.StartAsync();
        Assert.True(await WaitUntil(() => factory.Created.Count == 1));
        factory.Latest.Exit(1);
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Backoff));

        // A raw second start while the first run's monitor is still asleep.
        await supervisor.StartAsync();
        Assert.True(await WaitUntil(() => factory.Created.Count == 2));

        // Walk the clock well past the old backoff. If the old monitor were still
        // alive it would wake here and launch a third process.
        await Advance(time, TimeSpan.FromMinutes(2));
        Assert.Equal(2, factory.Created.Count);
        Assert.NotEqual(InstanceState.Faulted, supervisor.Status.State);
    }

    // D56. The port pre-flight catches the usual orphan because it holds the
    // port — but an orphan whose configured port has since changed would pass
    // that check and still open the world a second time, which is the T149
    // damage. This asks the question directly, and ahead of the port check.
    [Fact]
    public async Task Start_is_refused_when_a_server_this_wrapper_did_not_start_is_on_the_world()
    {
        var orphan = new OrphanedServer(8172, "/instances/survival/bedrock_server.exe", null);
        var inspector = new FakeOrphanInspector().Then(orphan);

        // Ports deliberately clear, so the refusal can only have come from here.
        var (supervisor, factory, time) = Build(
            preflight: new FakeStartPreflight().Then(), orphans: inspector);
        await using var _s = supervisor;

        await supervisor.StartAsync();

        Assert.Empty(factory.Created);
        Assert.Equal(InstanceState.Faulted, supervisor.Status.State);
        Assert.Contains("8172", supervisor.Status.FaultReason);
        Assert.Contains("was not opened twice", supervisor.Status.FaultReason);
        Assert.Null(supervisor.Status.NextRetryUtc);

        var console = supervisor.Console.Read(0).Lines;
        Assert.Contains(console, l => l.Origin == ConsoleOrigin.Notice && l.Raw.Contains("ALERT"));

        Assert.Equal("/instances/survival", inspector.Asked[0].WorkingDirectory);

        // Not retried, for the same reason a held port is not: every retry would
        // be one more process on the world.
        await Advance(time, TimeSpan.FromHours(1));
        Assert.Empty(factory.Created);
        Assert.Equal(InstanceState.Faulted, supervisor.Status.State);
    }

    // Our own child is excluded from the search, and the rest of this suite is
    // what proves it: if a supervisor reported its own running bedrock_server as
    // an orphan, every restart in every test here would refuse itself and fault.
    // A dedicated test would have to drive a restart against the fake process to
    // observe it, which deadlocks on the fake's exit, and the assertion it could
    // then make is weaker than the one the suite makes by still passing.

    // Same posture as the port pre-flight: not being able to ask is not a reason
    // to refuse a start that would otherwise work.
    [Fact]
    public async Task An_orphan_check_that_throws_does_not_block_the_start()
    {
        var (supervisor, factory, _) = Build(orphans: new ThrowingOrphanInspector());
        await using var _s = supervisor;

        await supervisor.StartAsync();

        Assert.Single(factory.Created);
        Assert.NotEqual(InstanceState.Faulted, supervisor.Status.State);
    }

    private sealed class ThrowingOrphanInspector : IOrphanInspector
    {
        public IReadOnlyList<OrphanedServer> Find(string workingDirectory, IReadOnlyCollection<int> ours) =>
            throw new InvalidOperationException("the process table is unavailable");

        public OrphanTermination Terminate(int processId, string workingDirectory) =>
            throw new InvalidOperationException();
    }

    private sealed class ThrowingStartPreflight : IStartPreflight
    {
        public IReadOnlyList<PortConflict> ConflictsForStart(string instanceId, string workingDirectory) =>
            throw new InvalidOperationException("the socket table is unavailable");
    }

    // D17: only a run that lasted clears the counter. Resetting on any launch
    // silently disables D15 for the fast-crash case, which is the case D15 is for.
    [Fact]
    public async Task A_run_that_stays_up_resets_the_failure_counter()
    {
        var (supervisor, factory, time) = Build();
        await using var _s = supervisor;

        await supervisor.StartAsync();

        // Two quick crashes: failures 1 and 2, one short of giving up.
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            Assert.True(await WaitUntil(() => factory.Created.Count == attempt));
            factory.Latest.Exit(1);
            Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Backoff));
            Assert.Equal(attempt, supervisor.Status.ConsecutiveFailures);
            await Advance(time, supervisor.Status.NextRetryUtc!.Value - time.GetUtcNow());
        }

        Assert.True(await WaitUntil(() => factory.Created.Count == 3));
        factory.Latest.Ready();
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));

        // This one stays up past the stability window, then dies.
        await Advance(time, TimeSpan.FromMinutes(5));
        factory.Latest.Exit(1);

        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Backoff));

        // Counted as failure 1 of a fresh run, not failure 3 of the old one — so
        // it backs off rather than faulting.
        Assert.Equal(1, supervisor.Status.ConsecutiveFailures);
        Assert.NotEqual(InstanceState.Faulted, supervisor.Status.State);
    }

    // A mistyped executable path must not be retried forever either: a launch
    // that throws is the same kind of event as a crash two seconds in.
    [Fact]
    public async Task A_launch_that_throws_counts_as_a_failure()
    {
        var (supervisor, factory, time) = Build();
        await using var _s = supervisor;

        factory.FailEveryStart = true;
        await supervisor.StartAsync();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (supervisor.Status.State == InstanceState.Faulted)
            {
                break;
            }

            await Advance(time, TimeSpan.FromMinutes(10));
        }

        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Faulted));
        Assert.Contains(
            supervisor.Console.Read(0).Lines,
            l => l.Raw.Contains("failed to launch"));
    }

    [Fact]
    public async Task A_graceful_stop_warns_on_a_countdown_then_stops()
    {
        var (supervisor, factory, time) = Build();
        await using var _s = supervisor;

        await supervisor.StartAsync();
        factory.Latest.Ready();
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));

        var process = factory.Latest;
        var request = new GracefulStopRequest(
            StopReason.ScheduledRestart,
            TimeSpan.FromMinutes(15),
            [TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)],
            TimeSpan.FromSeconds(45));

        var stop = supervisor.StopAsync(request);

        // The 15-minute warning fires at once, since the lead is 15 minutes.
        Assert.True(await WaitUntil(() => process.Sent.Count == 1));
        Assert.Equal("say Server restarting in 15 minutes", process.Sent[0]);
        Assert.Equal(InstanceState.Stopping, supervisor.Status.State);

        await Advance(time, TimeSpan.FromMinutes(10));
        Assert.True(await WaitUntil(() => process.Sent.Count == 2));
        Assert.Equal("say Server restarting in 5 minutes", process.Sent[1]);

        await Advance(time, TimeSpan.FromMinutes(4));
        Assert.True(await WaitUntil(() => process.Sent.Count == 3));
        Assert.Equal("say Server restarting in 1 minute", process.Sent[2]);

        await Advance(time, TimeSpan.FromMinutes(1));
        Assert.True(await WaitUntil(() => process.Sent.Count == 4));
        Assert.Equal("stop", process.Sent[3]);

        process.Exit(0);
        await stop;

        Assert.Equal(InstanceState.Stopped, supervisor.Status.State);
        Assert.Equal(0, supervisor.Status.ConsecutiveFailures);
        Assert.False(process.Killed);
    }

    // A stop is not a crash. The watchdog must not treat it as one and restart
    // the server the operator just asked to shut down.
    [Fact]
    public async Task A_requested_stop_does_not_trigger_the_watchdog()
    {
        var (supervisor, factory, time) = Build();
        await using var _s = supervisor;

        await supervisor.StartAsync();
        factory.Latest.Ready();
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));

        var process = factory.Latest;
        var stop = supervisor.StopAsync(
            GracefulStopRequest.Immediate(StopReason.Manual, TimeSpan.FromSeconds(45)));

        Assert.True(await WaitUntil(() => process.Sent.Contains("stop")));
        process.Exit(0);
        await stop;

        await Advance(time, TimeSpan.FromHours(1));
        Assert.Single(factory.Created);
        Assert.Equal(InstanceState.Stopped, supervisor.Status.State);
    }

    [Fact]
    public async Task A_server_that_ignores_stop_is_killed_when_the_budget_runs_out()
    {
        var (supervisor, factory, time) = Build();
        await using var _s = supervisor;

        await supervisor.StartAsync();
        factory.Latest.Ready();
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));

        var process = factory.Latest;
        var stop = supervisor.StopAsync(
            GracefulStopRequest.Immediate(StopReason.ServiceStop, TimeSpan.FromSeconds(30)));

        Assert.True(await WaitUntil(() => process.Sent.Contains("stop")));

        await Advance(time, TimeSpan.FromSeconds(30));

        await stop;
        Assert.True(process.Killed);
        Assert.Equal(InstanceState.Stopped, supervisor.Status.State);
    }

    // A stop issued during a long backoff must not be overtaken by the restart
    // it was trying to prevent.
    [Fact]
    public async Task A_stop_during_backoff_cancels_the_pending_restart()
    {
        var (supervisor, factory, time) = Build();
        await using var _s = supervisor;

        await supervisor.StartAsync();
        factory.Latest.Exit(1);
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Backoff));

        await supervisor.StopAsync(
            GracefulStopRequest.Immediate(StopReason.Manual, TimeSpan.FromSeconds(45)));

        Assert.Equal(InstanceState.Stopped, supervisor.Status.State);

        await Advance(time, TimeSpan.FromHours(1));
        Assert.Single(factory.Created);
    }

    [Fact]
    public async Task A_service_stop_warns_once_and_briefly()
    {
        var (supervisor, factory, time) = Build();
        await using var _s = supervisor;

        await supervisor.StartAsync();
        factory.Latest.Ready();
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));

        var process = factory.Latest;
        var stop = supervisor.StopAsync(GracefulStopRequest.ServiceStop(
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(45)));

        Assert.True(await WaitUntil(() => process.Sent.Count == 1));
        Assert.Equal("say Server shutting down in 5 seconds", process.Sent[0]);

        await Advance(time, TimeSpan.FromSeconds(5));
        Assert.True(await WaitUntil(() => process.Sent.Contains("stop")));

        process.Exit(0);
        await stop;
    }

    [Fact]
    public async Task Commands_are_echoed_into_the_console()
    {
        var (supervisor, factory, _) = Build();
        await using var _s = supervisor;

        await supervisor.StartAsync();
        factory.Latest.Ready();
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));

        await supervisor.SendCommandAsync("save hold");

        var lines = supervisor.Console.Read(0).Lines;
        Assert.Contains(lines, l => l.Origin == ConsoleOrigin.Command && l.Raw == "save hold");
        Assert.Contains("save hold", factory.Latest.Sent);
    }

    // An operator typing `stop` means it. Before this, the command reached BDS
    // untouched, the server exited, and the watchdog started it again — both
    // halves working as designed, with nothing connecting them.
    [Fact]
    public async Task Typing_stop_at_the_console_is_not_treated_as_a_crash()
    {
        var (supervisor, factory, time) = Build();
        await using var _s = supervisor;

        await supervisor.StartAsync();
        factory.Latest.Ready();
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));

        await supervisor.SendCommandAsync("stop");
        Assert.Equal(InstanceState.Stopping, supervisor.Status.State);

        factory.Latest.Exit(0);

        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Stopped));
        Assert.Equal(0, supervisor.Status.ConsecutiveFailures);

        // And it stays stopped.
        await Advance(time, TimeSpan.FromHours(1));
        Assert.Single(factory.Created);
    }

    [Theory]
    [InlineData("STOP")]
    [InlineData("  stop  ")]
    [InlineData("Stop")]
    public async Task Recognises_stop_however_it_was_typed(string typed)
    {
        var (supervisor, factory, time) = Build();
        await using var _s = supervisor;

        await supervisor.StartAsync();
        factory.Latest.Ready();
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));

        await supervisor.SendCommandAsync(typed);
        factory.Latest.Exit(0);

        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Stopped));
        await Advance(time, TimeSpan.FromHours(1));
        Assert.Single(factory.Created);
    }

    // The command still reaches the server exactly as typed; only the wrapper's
    // reading of the exit changes.
    [Fact]
    public async Task The_stop_command_is_still_sent_verbatim()
    {
        var (supervisor, factory, _) = Build();
        await using var _s = supervisor;

        await supervisor.StartAsync();
        factory.Latest.Ready();
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));

        await supervisor.SendCommandAsync("stop");

        Assert.Contains("stop", factory.Latest.Sent);
    }

    // Suppressing the watchdog on anything merely *starting* with "stop" would
    // disable it exactly when a real crash follows an unrelated command.
    [Fact]
    public async Task Another_command_leaves_the_watchdog_alone()
    {
        var (supervisor, factory, time) = Build();
        await using var _s = supervisor;

        await supervisor.StartAsync();
        factory.Latest.Ready();
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));

        await supervisor.SendCommandAsync("stopsound @a");
        factory.Latest.Exit(1);

        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Backoff));

        await Advance(time, TimeSpan.FromSeconds(10));
        Assert.True(await WaitUntil(() => factory.Created.Count == 2));
    }

    // Stopping from the console is not a fault, so Start has to work afterwards.
    [Fact]
    public async Task An_instance_stopped_from_the_console_can_be_started_again()
    {
        var (supervisor, factory, _) = Build();
        await using var _s = supervisor;

        await supervisor.StartAsync();
        factory.Latest.Ready();
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));

        await supervisor.SendCommandAsync("stop");
        factory.Latest.Exit(0);
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Stopped));

        await supervisor.StartAsync();

        Assert.Equal(2, factory.Created.Count);
        Assert.Equal(InstanceState.Starting, supervisor.Status.State);
    }

    // The route that matters most: an operator running /stop in-game. The wrapper
    // never sees that command, so the only evidence is the server announcing its
    // own shutdown.
    [Theory]
    [InlineData("[2026-08-03 07:00:18:294 INFO] Server stop requested.")]
    [InlineData("[2026-08-03 07:00:18:357 INFO] Stopping server...")]
    public async Task A_shutdown_the_wrapper_did_not_ask_for_is_still_not_a_crash(string announcement)
    {
        var (supervisor, factory, time) = Build();
        await using var _s = supervisor;

        await supervisor.StartAsync();
        factory.Latest.Ready();
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));

        // Nobody told the wrapper anything; the server simply says it is going.
        factory.Latest.Emit(announcement);
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Stopping));

        factory.Latest.Exit(0);

        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Stopped));
        Assert.Equal(0, supervisor.Status.ConsecutiveFailures);

        await Advance(time, TimeSpan.FromHours(1));
        Assert.Single(factory.Created);
    }

    [Fact]
    public async Task An_instance_stopped_from_in_game_can_be_started_again()
    {
        var (supervisor, factory, _) = Build();
        await using var _s = supervisor;

        await supervisor.StartAsync();
        factory.Latest.Ready();
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));

        factory.Latest.Emit("[2026-08-03 07:00:18:294 INFO] Server stop requested.");
        factory.Latest.Exit(0);
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Stopped));

        await supervisor.StartAsync();

        Assert.Equal(2, factory.Created.Count);
    }

    // Ordinary output must not be mistaken for a shutdown, or the watchdog stops
    // protecting anything the moment a server says something similar.
    [Theory]
    [InlineData("[2026-08-03 07:00:16:314 INFO] Player disconnected: Steve")]
    [InlineData("[2026-08-03 07:00:16:314 INFO] Running AutoCompaction...")]
    [InlineData("[2026-08-03 07:00:16:314 WARN] Could not stop the previous save")]
    public async Task Ordinary_output_does_not_suppress_the_watchdog(string line)
    {
        var (supervisor, factory, time) = Build();
        await using var _s = supervisor;

        await supervisor.StartAsync();
        factory.Latest.Ready();
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));

        factory.Latest.Emit(line);
        Assert.Equal(InstanceState.Running, supervisor.Status.State);

        factory.Latest.Exit(1);
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Backoff));

        await Advance(time, TimeSpan.FromSeconds(10));
        Assert.True(await WaitUntil(() => factory.Created.Count == 2));
    }

    // The wrapper's own stop already set the flag, so the announcement that
    // follows must not add a second notice to the transcript.
    [Fact]
    public async Task A_wrapper_initiated_stop_is_noted_once()
    {
        var (supervisor, factory, _) = Build();
        await using var _s = supervisor;

        await supervisor.StartAsync();
        factory.Latest.Ready();
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));

        var process = factory.Latest;
        var stop = supervisor.StopAsync(
            GracefulStopRequest.Immediate(StopReason.Manual, TimeSpan.FromSeconds(45)));

        Assert.True(await WaitUntil(() => process.Sent.Contains("stop")));
        process.Emit("[2026-08-03 07:00:18:294 INFO] Server stop requested.");
        process.Exit(0);
        await stop;

        var notices = supervisor.Console.Read(0).Lines
            .Count(l => l.Origin == ConsoleOrigin.Notice && l.Raw.Contains("shutting down"));

        Assert.Equal(0, notices);
    }

    // Read from the server's own output rather than from the package it was
    // installed from: those disagree the moment anyone updates the files by hand,
    // and the running server is the one telling the truth.
    [Fact]
    public async Task Captures_the_version_the_server_reports()
    {
        var (supervisor, factory, _) = Build();
        await using var _s = supervisor;

        await supervisor.StartAsync();
        Assert.Null(supervisor.Status.ServerVersion);

        factory.Latest.Emit("[2026-08-03 07:00:14:765 INFO] Version: 1.26.36.1");

        Assert.True(await WaitUntil(() => supervisor.Status.ServerVersion == "1.26.36.1"));
    }

    // The version line arrives before readiness, so handling it must not consume
    // the line that follows or swallow the readiness check.
    [Fact]
    public async Task The_version_line_does_not_disturb_readiness()
    {
        var (supervisor, factory, _) = Build();
        await using var _s = supervisor;

        await supervisor.StartAsync();

        factory.Latest.Emit("[2026-08-03 07:00:14:765 INFO] Version: 1.26.36.1");
        factory.Latest.Ready();

        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));
        Assert.Equal("1.26.36.1", supervisor.Status.ServerVersion);
    }

    [Fact]
    public async Task Sending_a_command_to_a_stopped_instance_throws()
    {
        var (supervisor, _, _) = Build();
        await using var _s = supervisor;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => supervisor.SendCommandAsync("list"));
    }

    // D14: a retired instance keeps its history but is never launched again.
    [Fact]
    public async Task A_retired_instance_cannot_be_started()
    {
        var (supervisor, _, _) = Build(d => d.Retired = true);
        await using var _s = supervisor;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.StartAsync());
        Assert.Contains("retired", ex.Message);
    }

    [Fact]
    public async Task Starting_twice_does_not_launch_a_second_process()
    {
        var (supervisor, factory, _) = Build();
        await using var _s = supervisor;

        await supervisor.StartAsync();
        await supervisor.StartAsync();

        Assert.Single(factory.Created);
    }

    [Fact]
    public async Task Uptime_is_derived_and_stops_when_the_instance_does()
    {
        var (supervisor, factory, time) = Build();
        await using var _s = supervisor;

        await supervisor.StartAsync();
        factory.Latest.Ready();
        Assert.True(await WaitUntil(() => supervisor.Status.State == InstanceState.Running));

        Assert.Equal(
            TimeSpan.FromMinutes(3),
            supervisor.Status.UptimeAt(Origin.AddMinutes(3)));

        var process = factory.Latest;
        var stop = supervisor.StopAsync(
            GracefulStopRequest.Immediate(StopReason.Manual, TimeSpan.FromSeconds(45)));
        Assert.True(await WaitUntil(() => process.Sent.Contains("stop")));
        process.Exit(0);
        await stop;

        Assert.Null(supervisor.Status.UptimeAt(Origin.AddMinutes(3)));
    }
}

public class CountdownMessageTests
{
    [Theory]
    [InlineData(0, "1 second")]
    [InlineData(1, "1 second")]
    [InlineData(30, "30 seconds")]
    [InlineData(60, "1 minute")]
    [InlineData(300, "5 minutes")]
    [InlineData(3600, "1 hour")]
    [InlineData(7200, "2 hours")]
    public void Describes_a_countdown_in_words(int seconds, string expected) =>
        Assert.Equal(expected, CountdownMessage.Describe(TimeSpan.FromSeconds(seconds)));

    // A warning fired a few milliseconds late must not announce "4 minutes" at
    // the five-minute mark.
    [Fact]
    public void Rounds_rather_than_truncating()
    {
        Assert.Equal("5 minutes", CountdownMessage.Describe(TimeSpan.FromSeconds(299.9)));
    }

    [Fact]
    public void Never_describes_a_negative_countdown()
    {
        Assert.Equal("1 second", CountdownMessage.Describe(TimeSpan.FromSeconds(-10)));
    }

    [Fact]
    public void Names_the_reason_for_the_stop()
    {
        Assert.Equal(
            "say Server restarting to update Minecraft in 5 minutes",
            CountdownMessage.ForStop(StopReason.ServerUpdate, TimeSpan.FromMinutes(5)));
    }

    // `T134`: every rung of the automatic countdown names the build, and this
    // line — the last one players see, and the only one the Update now path
    // sends at all — did not.
    [Fact]
    public void Names_the_build_when_the_caller_knows_it()
    {
        Assert.Equal(
            "say Server restarting to update Minecraft 1.26.60.21 in 10 seconds",
            CountdownMessage.ForStop(
                StopReason.ServerUpdate, TimeSpan.FromSeconds(10), "1.26.60.21"));
    }

    // Every other reason has no build to name, and a stray version must not
    // reach a line that has nowhere sensible to put it.
    [Fact]
    public void A_version_changes_nothing_for_a_reason_that_has_no_build()
    {
        Assert.Equal(
            "say Server restarting in 1 minute",
            CountdownMessage.ForStop(
                StopReason.ScheduledRestart, TimeSpan.FromMinutes(1), "1.26.60.21"));
    }
}

public class GracefulStopRequestTests
{
    [Fact]
    public void Warnings_longer_than_the_lead_are_ignored_rather_than_rejected()
    {
        var request = new GracefulStopRequest(
            StopReason.ScheduledRestart,
            TimeSpan.FromMinutes(5),
            [TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)],
            TimeSpan.FromSeconds(45));

        Assert.Equal(
            [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)],
            request.EffectiveWarnings());
    }

    [Fact]
    public void Warnings_are_deduplicated_and_ordered_latest_first()
    {
        var request = new GracefulStopRequest(
            StopReason.Manual,
            TimeSpan.FromMinutes(10),
            [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)],
            TimeSpan.FromSeconds(45));

        Assert.Equal(
            [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)],
            request.EffectiveWarnings());
    }

    [Fact]
    public void An_immediate_stop_has_no_warnings()
    {
        var request = GracefulStopRequest.Immediate(StopReason.Manual, TimeSpan.FromSeconds(45));

        Assert.Empty(request.EffectiveWarnings());
        Assert.Equal(TimeSpan.Zero, request.WarningLead);
    }

    [Fact]
    public void A_scheduled_stop_leads_by_its_longest_warning()
    {
        var schedule = new RestartSchedule
        {
            Warnings = [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(1)],
        };


        var request = GracefulStopRequest.Scheduled(schedule, TimeSpan.FromSeconds(45));

        Assert.Equal(TimeSpan.FromMinutes(15), request.WarningLead);
        Assert.Equal(3, request.EffectiveWarnings().Count);
    }
}
