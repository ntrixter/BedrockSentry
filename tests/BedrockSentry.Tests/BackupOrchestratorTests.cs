using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Players;
using BedrockSentry.Services.Backups;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Logging.Abstractions;

namespace BedrockSentry.Tests;

/// <summary>
/// docs/design.md § 8.1, driven with the measured console lines from § 5.5. The tests
/// that matter most are the ones where something goes wrong: whatever happens
/// between save hold and save resume, the resume fires.
/// </summary>
public sealed class BackupOrchestratorTests : IAsyncDisposable
{
    private const string ManifestLine =
        "Bedrock level/db/000005.ldb:509, Bedrock level/db/CURRENT:16, Bedrock level/level.dat:2543";

    private const string ResumeLine =
        "[2026-08-06 12:00:00:000 INFO] Changes to the world are resumed.";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"bs-backup-{Guid.NewGuid():N}");

    private readonly FakeProcessFactory _factory = new();
    private readonly InstanceSupervisor _supervisor;
    private readonly BackupHistory _history;
    private readonly CancellationTokenSource _responder = new();

    public BackupOrchestratorTests()
    {
        Directory.CreateDirectory(_directory);

        _supervisor = new InstanceSupervisor(
            "survival",
            new InstanceDefinition { Name = "Survival" },
            Path.Combine(_directory, "instance"),
            _factory,
            TimeProvider.System,
            NullLogger<InstanceSupervisor>.Instance);

        _history = new BackupHistory(new TestPaths(Path.Combine(_directory, "data")));
    }

    public async ValueTask DisposeAsync()
    {
        _responder.Cancel();
        _responder.Dispose();
        await _supervisor.DisposeAsync();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private BackupOrchestrator Orchestrator(
        IBackupStrategy strategy, IPlayerStore? players = null, TimeSpan? queryTimeout = null) =>
        Orchestrator([strategy], players, queryTimeout);

    private BackupOrchestrator Orchestrator(
        IBackupStrategy[] strategies, IPlayerStore? players = null, TimeSpan? queryTimeout = null) =>
        new(
            strategies,
            _history,
            TimeProvider.System,
            NullLogger<BackupOrchestrator>.Instance,
            players,
            queryTimeout: queryTimeout ?? TimeSpan.FromSeconds(5),
            resumeTimeout: TimeSpan.FromMilliseconds(300),
            pollInterval: TimeSpan.FromMilliseconds(20));

    /// <summary>
    /// `T170`. The confirmation arrives and is then buried by a burst of output
    /// before anybody reads it. It must still be seen.
    /// </summary>
    /// <remarks>
    /// This is the operator's report, reduced to its mechanism. The console is a
    /// bounded ring — <see cref="ConsoleBuffer.DefaultCapacity"/> lines — and the
    /// orchestrator used to look for the reply by re-reading a cursor on a timer.
    /// A Bedrock startup after an update is the fastest output this wrapper ever
    /// sees, so the reply was appended, evicted, and never matched: the world had
    /// been resumed and the panel said it might not have been.
    ///
    /// The poll interval is long here on purpose, so the reply and the flood both
    /// land inside one sleep. Against the old polling loop that is deterministic
    /// rather than lucky — and it failed exactly that way before the watch
    /// replaced the cursor.
    /// </remarks>
    [Fact]
    public async Task A_confirmation_buried_by_a_burst_of_output_is_still_seen()
    {
        await StartRunningAsync();

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
                        process.Emit(ManifestLine);
                    }
                    else if (sent[answered] == "save resume")
                    {
                        process.Emit(ResumeLine);

                        // Bury it. More than the ring holds, so the confirmation
                        // is gone from the buffer by the time a poll would look.
                        for (var i = 0; i < ConsoleBuffer.DefaultCapacity + 100; i++)
                        {
                            process.Emit($"[2026-08-06 12:00:00:000 INFO] Level dat loaded {i}");
                        }
                    }
                }

                await Task.Delay(5);
            }
        });

        var orchestrator = new BackupOrchestrator(
            [new FakeStrategy()],
            _history,
            TimeProvider.System,
            NullLogger<BackupOrchestrator>.Instance,
            queryTimeout: TimeSpan.FromSeconds(5),
            resumeTimeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromSeconds(2));

        var record = await orchestrator.RunAsync(_supervisor, new BackupRequest(BackupReason.Manual));

        Assert.Equal(SaveResumeOutcome.Confirmed, record.SavesResumed);
    }

    private async Task StartRunningAsync()
    {
        await _supervisor.StartAsync();
        _factory.Latest.Ready();
        Assert.True(await _supervisor.WaitForReadyAsync(TimeSpan.FromSeconds(5)));
    }

    /// Plays the server's side of the exchange: the manifest for save query, the
    /// measured confirmation for save resume. Which halves answer is the knob
    /// each test turns.
    private void Respond(bool toQuery = true, bool toResume = true)
    {
        var process = _factory.Latest;

        _ = Task.Run(async () =>
        {
            var answered = 0;
            while (!_responder.IsCancellationRequested)
            {
                var sent = process.Sent;
                for (; answered < sent.Count; answered++)
                {
                    if (toQuery && sent[answered] == "save query")
                    {
                        process.Emit(ManifestLine);
                    }
                    else if (toResume && sent[answered] == "save resume")
                    {
                        process.Emit(ResumeLine);
                    }
                }

                await Task.Delay(10);
            }
        });
    }

    [Fact]
    public async Task Holds_runs_the_strategy_against_the_manifest_and_resumes()
    {
        await StartRunningAsync();
        Respond();

        var strategy = new FakeStrategy();
        var record = await Orchestrator(strategy).RunAsync(
            _supervisor, new BackupRequest(BackupReason.Manual, "before the update"));

        Assert.True(record.Succeeded);
        Assert.False(record.Skipped);
        Assert.Equal("before the update", record.Note);
        Assert.Single(record.Strategies);

        var sent = _factory.Latest.Sent;
        Assert.Contains("save hold", sent);
        Assert.Contains("save query", sent);
        Assert.Contains("save resume", sent);
        Assert.True(
            sent.ToList().IndexOf("save hold") < sent.ToList().IndexOf("save resume"),
            "resume must follow hold");

        // The strategy saw the parsed manifest, byte counts included — the
        // lengths are the part a copy must not ignore.
        Assert.NotNull(strategy.Context);
        Assert.Equal(3, strategy.Context!.Manifest.Count);
        Assert.Equal(509, strategy.Context.Manifest[0].Length);
        Assert.EndsWith("worlds", strategy.Context.WorldsDirectory);

        // And it went into the history.
        var history = await _history.ReadAsync("survival");
        Assert.Single(history);
    }

    [Fact]
    public async Task A_failing_strategy_still_gets_saves_resumed()
    {
        await StartRunningAsync();
        Respond();

        var record = await Orchestrator(new FakeStrategy { Throws = true }).RunAsync(
            _supervisor, new BackupRequest(BackupReason.Manual));

        Assert.False(record.Succeeded);
        Assert.False(Assert.Single(record.Strategies).Succeeded);
        Assert.Contains("save resume", _factory.Latest.Sent);
    }

    [Fact]
    public async Task A_manifest_that_never_arrives_still_gets_saves_resumed()
    {
        await StartRunningAsync();
        Respond(toQuery: false);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            Orchestrator(new FakeStrategy(), queryTimeout: TimeSpan.FromMilliseconds(200))
                .RunAsync(_supervisor, new BackupRequest(BackupReason.Manual)));

        Assert.Contains("save resume", _factory.Latest.Sent);
    }

    [Fact]
    public async Task A_cancelled_backup_still_gets_saves_resumed()
    {
        await StartRunningAsync();
        Respond();

        var strategy = new FakeStrategy { HangsUntilCancelled = true };
        using var cancel = new CancellationTokenSource();

        var run = Orchestrator(strategy).RunAsync(
            _supervisor, new BackupRequest(BackupReason.Manual), cancel.Token);

        // Cancel only once the strategy is genuinely inside the held window.
        await WaitUntilAsync(() => strategy.Started, "the strategy never started");
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        // The caller's token was cancelled; the resume runs on its own token,
        // which is the entire point.
        await WaitUntilAsync(
            () => _factory.Latest.Sent.Contains("save resume"),
            "save resume was never sent after cancellation");
    }

    [Fact]
    public async Task A_stopped_server_is_archived_from_disk_with_no_console_exchange()
    {
        // Not started: the files are static, and there is nobody to ask for a
        // manifest — docs/design.md § 8.1's backup-on-shutdown shape.
        var worlds = Path.Combine(_directory, "instance", "worlds", "Bedrock level");
        Directory.CreateDirectory(worlds);
        await File.WriteAllTextAsync(Path.Combine(worlds, "level.dat"), "world data");

        var strategy = new FakeStrategy();
        var record = await Orchestrator(strategy).RunAsync(
            _supervisor, new BackupRequest(BackupReason.Shutdown));

        Assert.True(record.Succeeded);
        Assert.Empty(_factory.Created);

        var entry = Assert.Single(strategy.Context!.Manifest);
        Assert.Equal("Bedrock level/level.dat", entry.RelativePath);
        Assert.Equal("world data".Length, entry.Length);
    }

    [Fact]
    public async Task A_scheduled_backup_is_skipped_when_nobody_played_and_manual_never_is()
    {
        // A successful backup on record is what gives "since the last backup"
        // a meaning; the skip rule does not exist before the first one.
        await _history.AppendAsync("survival", new BackupRecord(
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(-1),
            BackupReason.Manual, null, false, null, true,
            [new BackupStrategyResult("fake", true, null, null)]));

        var players = new FakeActivity { Active = false };
        var strategy = new FakeStrategy();
        var orchestrator = Orchestrator(strategy, players);

        var scheduled = await orchestrator.RunAsync(
            _supervisor, new BackupRequest(BackupReason.Scheduled));

        Assert.True(scheduled.Skipped);
        Assert.False(strategy.Ran);

        var manual = await orchestrator.RunAsync(
            _supervisor, new BackupRequest(BackupReason.Manual));

        Assert.False(manual.Skipped);
        Assert.True(strategy.Ran);
    }

    [Fact]
    public async Task A_scheduled_backup_runs_when_somebody_played()
    {
        await _history.AppendAsync("survival", new BackupRecord(
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(-1),
            BackupReason.Manual, null, false, null, true,
            [new BackupStrategyResult("fake", true, null, null)]));

        var strategy = new FakeStrategy();
        var record = await Orchestrator(strategy, new FakeActivity { Active = true }).RunAsync(
            _supervisor, new BackupRequest(BackupReason.Scheduled));

        Assert.False(record.Skipped);
        Assert.True(strategy.Ran);
    }

    [Fact]
    public async Task Nothing_enabled_is_reported_not_silently_succeeded()
    {
        var record = await Orchestrator(new FakeStrategy { Enabled = false }).RunAsync(
            _supervisor, new BackupRequest(BackupReason.Manual));

        Assert.True(record.Skipped);
        Assert.Contains("No backup strategy", record.SkipReason);
        Assert.Empty(_factory.Created);
    }

    [Fact]
    public async Task A_second_backup_of_the_same_instance_is_refused_while_one_runs()
    {
        await StartRunningAsync();
        Respond();

        var strategy = new FakeStrategy { HangsUntilCancelled = true };
        using var cancel = new CancellationTokenSource();
        var orchestrator = Orchestrator(strategy);

        var first = orchestrator.RunAsync(
            _supervisor, new BackupRequest(BackupReason.Manual), cancel.Token);
        await WaitUntilAsync(() => strategy.Started, "the first backup never started");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.RunAsync(_supervisor, new BackupRequest(BackupReason.Manual)));

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail(because);
    }

    // ---- D45: a strategy the install refuses to run -------------------------

    private static BlockedStrategy Refused(string strategy = "fake") =>
        new(strategy, "Switched off for this install.", "Re-run install.ps1 with the switch.");

    /// <summary>
    /// THE ARM THAT WILL LIE. A blocked strategy beside one that worked is a
    /// warning, and the backup still counts as the last successful one.
    /// </summary>
    /// <remarks>
    /// <para>This is the common case in the field: anybody with ZIP plus a custom
    /// command who upgrades into the gate lands here. It is also the only arm where
    /// the naive implementation produces a green OK badge over a backup that did
    /// less than it was asked to — every other arm is loud by construction.</para>
    ///
    /// <para>It is bracketed by two different wrong answers. Record the block as a
    /// failed <c>BackupStrategyResult</c> and <c>Succeeded</c> flips false, which
    /// hides a perfectly good archive from <c>LastSuccessfulUtcAsync</c> — "last
    /// backup" goes blank and the skip rule starts backing up on every tick. Leave
    /// the block off the record entirely and the row reads plain OK. The assertion
    /// on <c>LastSuccessfulUtcAsync</c> is the one that catches the first, and it is
    /// why this is an orchestrator test rather than a unit test of
    /// <c>Outcome()</c>: an <c>Outcome()</c> test passes happily while the
    /// orchestrator writes the wrong record.</para>
    /// </remarks>
    [Fact]
    public async Task A_blocked_strategy_beside_one_that_worked_is_a_warning_and_still_counts()
    {
        var worked = new FakeStrategy { StrategyName = "zip" };
        var refused = new FakeStrategy { StrategyName = "custom-command", BlockedBy = Refused("custom-command") };

        var record = await Orchestrator([worked, refused])
            .RunAsync(_supervisor, new BackupRequest(BackupReason.Manual), CancellationToken.None);

        Assert.True(record.Succeeded);
        Assert.True(worked.Ran);
        Assert.False(refused.Started);

        var blocked = Assert.Single(record.Blocked);
        Assert.Equal("custom-command", blocked.Strategy);

        var outcome = record.Outcome();
        Assert.Equal(BackupSeverity.Warning, outcome.Severity);
        Assert.Contains("custom-command", outcome.Label);
        Assert.Contains("Switched off for this install.", outcome.Detail);
        Assert.Contains("install.ps1", outcome.Detail);

        // The assertion the wrong implementation fails.
        Assert.NotNull(await _history.LastSuccessfulUtcAsync("survival"));
    }

    /// <summary>
    /// And when the blocked one was the only one, the run fails rather than
    /// reporting that nothing was enabled.
    /// </summary>
    /// <remarks>
    /// The detail must not be null. <c>Describe</c> over an empty sequence returns
    /// null, and <c>Strategies</c> IS empty here — so without composing the block
    /// into the failed arm, the row renders "Failed" with no explanation at all, for
    /// the one case that most needs one.
    /// </remarks>
    [Fact]
    public async Task A_blocked_strategy_that_was_the_only_one_fails_and_says_why()
    {
        var refused = new FakeStrategy { BlockedBy = Refused() };

        var record = await Orchestrator(refused)
            .RunAsync(_supervisor, new BackupRequest(BackupReason.Manual), CancellationToken.None);

        Assert.False(record.Succeeded);

        // Not a skip: Skipped renders the same neutral badge as "nobody played",
        // which badly understates "you asked for a backup and have none".
        Assert.False(record.Skipped);
        Assert.Null(record.SkipReason);

        var outcome = record.Outcome();
        Assert.Equal(BackupSeverity.Failed, outcome.Severity);
        Assert.NotNull(outcome.Detail);
        Assert.Contains("fake", outcome.Detail);
        Assert.Contains("install.ps1", outcome.Detail);

        // Nothing was held: no console exchange for a run that could not have
        // produced anything.
        Assert.Empty(_factory.Created);
        Assert.Null(await _history.LastSuccessfulUtcAsync("survival"));
    }

    // A strategy nobody switched on is not a strategy that was refused. Without
    // this, every install with an untouched custom-command box grows a warning row
    // on every single backup.
    [Fact]
    public async Task A_strategy_that_is_merely_off_is_never_reported_as_blocked()
    {
        var off = new FakeStrategy { StrategyName = "custom-command", Enabled = false, BlockedBy = Refused("custom-command") };
        var worked = new FakeStrategy { StrategyName = "zip" };

        var record = await Orchestrator([worked, off])
            .RunAsync(_supervisor, new BackupRequest(BackupReason.Manual), CancellationToken.None);

        Assert.True(record.Succeeded);
        Assert.Empty(record.Blocked);
        Assert.Equal(BackupSeverity.Ok, record.Outcome().Severity);
    }

    [Fact]
    public async Task A_blocked_strategy_beside_one_that_failed_still_reads_as_failed_and_names_both()
    {
        var broke = new FakeStrategy { StrategyName = "zip", Throws = true };
        var refused = new FakeStrategy { StrategyName = "custom-command", BlockedBy = Refused("custom-command") };

        var record = await Orchestrator([broke, refused])
            .RunAsync(_supervisor, new BackupRequest(BackupReason.Manual), CancellationToken.None);

        var outcome = record.Outcome();
        Assert.Equal(BackupSeverity.Failed, outcome.Severity);
        Assert.Contains("disk full", outcome.Detail);
        Assert.Contains("Switched off for this install.", outcome.Detail);
    }

    private sealed class FakeStrategy : IBackupStrategy
    {
        public bool Enabled { get; init; } = true;
        public bool Throws { get; init; }
        public bool HangsUntilCancelled { get; init; }

        /// <summary>Non-null makes this strategy one the install refuses to run (`D45`).</summary>
        public BlockedStrategy? BlockedBy { get; init; }

        public string StrategyName { get; init; } = "fake";

        public volatile bool Started;
        public volatile bool Ran;
        public BackupContext? Context;

        public string Name => StrategyName;

        public bool IsEnabled(BackupSettings settings) => Enabled;

        public BlockedStrategy? Blocked(BackupSettings settings) => BlockedBy;

        public async Task<BackupStrategyResult> RunAsync(
            BackupContext context, CancellationToken cancellationToken)
        {
            Started = true;
            Context = context;

            if (HangsUntilCancelled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (Throws)
            {
                throw new IOException("disk full");
            }

            Ran = true;
            return new BackupStrategyResult(Name, true, null, null);
        }
    }

    private sealed class FakeActivity : IPlayerStore
    {
        public bool Active { get; init; }

        public Task<bool> AnyActivitySinceAsync(
            string instanceId, DateTimeOffset sinceUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(Active);

        public Task RecordAsync(string i, PlayerEvent e, DateTimeOffset a, CancellationToken c = default) =>
            throw new NotSupportedException();

        public Task<int> CloseOpenSessionsAsync(string i, DateTimeOffset e, CancellationToken c = default) =>
            throw new NotSupportedException();

        public Task<int> DropOpenSessionsAsync(string i, CancellationToken c = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<OnlinePlayer>> OnlineAsync(string? i = null, CancellationToken c = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PlayerSummary>> ListAsync(
            string? i, DateTimeOffset a, CancellationToken c = default) =>
            throw new NotSupportedException();

        public Task<PlayerProfile?> GetAsync(string x, DateTimeOffset a, CancellationToken c = default) =>
            throw new NotSupportedException();

        public Task<bool> UpdateProfileAsync(
            string x, string? n, string? o, bool w, CancellationToken c = default) =>
            throw new NotSupportedException();

        public Task<int> ForgetPlayerAsync(string x, CancellationToken c = default) =>
            throw new NotSupportedException();
    }

}
