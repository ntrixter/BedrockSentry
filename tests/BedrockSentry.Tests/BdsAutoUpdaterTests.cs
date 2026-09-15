using System.IO.Compression;
using System.Net;
using System.Text;
using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Backups;
using BedrockSentry.Services.Configuration;
using BedrockSentry.Services.Instances;
using BedrockSentry.Services.Packages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace BedrockSentry.Tests;

/// <summary>
/// docs/design.md § 11.3. The per-instance update schedule is a delay, and the delay is
/// the countdown — so what has to hold is that players are told, that the
/// instance stays usable while it runs, and that an operator can call it off.
/// </summary>
public sealed class BdsAutoUpdaterTests : IAsyncLifetime
{
    private const string OfferedVersion = "1.26.44.3";

    private static readonly DateTimeOffset Origin = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), "bedrocksentry-auto", Guid.NewGuid().ToString("n"));

    private readonly FakeProcessFactory _factory = new();
    private readonly FakeTimeProvider _time = new(Origin);
    private readonly FeedHandler _handler = new();

    private SentryPaths _paths = default!;
    private InstanceRegistry _registry = default!;
    private BdsUpdateService _updates = default!;
    private BdsAutoUpdater _updater = default!;
    private InstanceSupervisor _supervisor = default!;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _updater?.Dispose();

        if (_registry is not null)
        {
            await _registry.DisposeAsync();
        }

        try { Directory.Delete(_dataRoot, recursive: true); } catch (IOException) { }
    }

    /// <summary>Builds the world: one instance, a feed offering a newer build.</summary>
    private async Task ArrangeAsync(
        BdsUpdateMode mode = BdsUpdateMode.Automatic,
        int delayMinutes = 30,
        string? pin = null,
        bool running = true)
    {
        _paths = new SentryPaths(_dataRoot);
        Directory.CreateDirectory(_paths.ServerPackages);

        var definition = new InstanceDefinition { Name = "Survival" };
        definition.ServerUpdate.Mode = mode;
        definition.ServerUpdate.AutoApplyDelayMinutes = delayMinutes;
        definition.ServerUpdate.PinnedVersion = pin ?? string.Empty;

        var options = new InstanceOptions();
        options.ConsoleLog.Enabled = false;
        options.Servers["survival"] = definition;

        var store = new ServerPackageStore(_paths, NullLogger<ServerPackageStore>.Instance);

        _registry = new InstanceRegistry(
            new StaticOptionsMonitor<InstanceOptions>(options),
            _paths, _factory, _time, NullLoggerFactory.Instance, store, new NoPlayerDataStore());

        _updates = new BdsUpdateService(
            new StubClientFactory(_handler),
            store,
            new StaticOptionsMonitor<BdsUpdateOptions>(
                new BdsUpdateOptions { LinksUrl = "https://example.invalid/feed" }),
            _time,
            NullLogger<BdsUpdateService>.Instance,
            new ServerVersionLog(_paths, NullLogger<ServerVersionLog>.Instance));

        var installer = new BdsUpdateInstaller(
            store,
            new ShutdownBackups(
                new BackupOrchestrator(
                    [], new BackupHistory(_paths), _time, NullLogger<BackupOrchestrator>.Instance),
                _time,
                NullLogger<ShutdownBackups>.Instance),
            new ServerVersionLog(_paths, NullLogger<ServerVersionLog>.Instance),
            _time,
            NullLogger<BdsUpdateInstaller>.Instance,
            TimeSpan.FromSeconds(5));

        _updater = new BdsAutoUpdater(
            _registry, _updates, installer, _time, NullLogger<BdsAutoUpdater>.Instance);

        _supervisor = _registry.Get("survival");

        // The installed version comes off the console, so the server has to have
        // said it before anything can be "newer" than it.
        if (running)
        {
            _factory.ProcessesExitOnStop = true;
            await _supervisor.StartAsync();
            _factory.Latest.Emit("[2026-08-17 12:00:00:000 INFO] Version: 1.26.40.8");
            _factory.Latest.Ready();
            Assert.True(await _supervisor.WaitForReadyAsync(TimeSpan.FromSeconds(5)));
        }

        await _updates.RefreshAsync();
    }

    /// <summary>A sweep, then a moment for anything it started off-thread.</summary>
    private async Task SweepAsync()
    {
        await _updater.SweepAsync();
        await Task.Delay(60);
    }

    /// <summary>
    /// Sweeps until <paramref name="until"/> holds, rather than sweeping once and
    /// hoping the off-thread work finished.
    /// </summary>
    /// <remarks>
    /// <para><strong>Why this exists (`T194`).</strong> <see cref="SweepAsync"/> ends in a fixed
    /// <c>Task.Delay(60)</c> — a guess at how long a sweep's background work takes, and its own
    /// comment says so: <em>"a moment for anything it started off-thread"</em>. A guess is right
    /// until the machine is busy. Under a cold test host running the full suite, 60ms was not
    /// enough exactly once, and the assertion that followed read as a genuine regression.</para>
    ///
    /// <para><strong>A flaky test is worse here than elsewhere</strong>, because the suite is a
    /// gate in <c>Publish-Snapshot.ps1</c>: the export must build and pass on its own before
    /// anything is published. A gate that refuses at random is a gate somebody starts re-running
    /// until it goes green, and then deletes.</para>
    ///
    /// <para>The remedy is the one <see cref="SweepUntilDownloadedAsync"/> was already using:
    /// wait for the CONDITION, not for a duration. It still fails loudly — fifty sweeps is about
    /// three seconds, so a real regression is caught, not slept through.</para>
    ///
    /// <para><strong>Only the positive assertions can use this.</strong> Polling for something to
    /// become absent is not the same shape: a withdrawal happens inside the sweep itself, so
    /// <c>Assert.Null</c> after one sweep is already deterministic, and polling for it would
    /// merely take longer to notice a value that never goes away.</para>
    /// </remarks>
    private async Task SweepUntilAsync(Func<bool> until, string what)
    {
        for (var i = 0; i < 50; i++)
        {
            await SweepAsync();
            if (until()) { return; }
        }

        throw new InvalidOperationException($"after 50 sweeps: {what}");
    }

    private Task SweepUntilPendingAsync() =>
        SweepUntilAsync(() => _updater.PendingFor("survival") is not null, "no update is pending for survival");

    /// <summary>Sweeps until the download lands on the pending update.</summary>
    private async Task<PendingBdsUpdate> SweepUntilDownloadedAsync()
    {
        for (var i = 0; i < 50; i++)
        {
            await SweepAsync();

            if (_updater.PendingFor("survival") is { PackageFileName: not null } ready)
            {
                return ready;
            }
        }

        throw new InvalidOperationException("the package never arrived");
    }

    private IReadOnlyList<string> Said =>
        _factory.Created.SelectMany(p => p.Sent).Where(s => s.StartsWith("say ")).ToList();

    /// <summary>The interval a countdown line names, for asserting on a ladder.</summary>
    private static string Spoken(string line) =>
        System.Text.RegularExpressions.Regex.Match(line, @"\d+ (?:minutes?|hours?|seconds?)").Value;

    [Fact]
    public async Task An_automatic_instance_schedules_the_build_a_delay_ahead()
    {
        await ArrangeAsync(delayMinutes: 30);
        await SweepAsync();

        var pending = _updater.PendingFor("survival");

        Assert.NotNull(pending);
        Assert.Equal(OfferedVersion, pending.Version);
        Assert.Equal(Origin.AddMinutes(30), pending.DueUtc);
    }

    [Fact]
    public async Task Players_are_told_the_whole_window_the_moment_it_is_scheduled()
    {
        await ArrangeAsync(delayMinutes: 30);
        await SweepAsync();

        var opening = Assert.Single(Said);

        Assert.Contains(OfferedVersion, opening);
        Assert.Contains("30 minutes", opening);
    }

    /// <summary>
    /// The countdown, at the points the delay implies, each said once however
    /// many times the loop ticks in between.
    /// </summary>
    [Fact]
    public async Task The_countdown_runs_down_and_repeats_nothing()
    {
        await ArrangeAsync(delayMinutes: 30);
        await SweepAsync();

        // Deliberately more sweeps than there are points, and several landing
        // between them: the ladder decides how often players hear from us, not
        // how often the loop happens to run.
        foreach (var elapsed in new[] { 2, 5, 7, 10, 12, 15, 17, 20, 22, 25, 26, 27, 28, 29, 29 })
        {
            _time.SetUtcNow(Origin.AddMinutes(elapsed));
            await SweepAsync();
        }

        // Opening at 30, every five minutes down to five, then one a minute —
        // and nothing twice, despite sixteen sweeps across the window.
        Assert.Equal(
            [
                "30 minutes", "25 minutes", "20 minutes", "15 minutes", "10 minutes",
                "5 minutes", "4 minutes", "3 minutes", "2 minutes", "1 minute",
            ],
            Said.Select(Spoken));
    }

    // A tick that arrives late — a busy box, a paused VM — must say the right
    // thing once, not fire every point it slept through in a burst.
    [Fact]
    public async Task A_late_sweep_says_where_we_are_rather_than_every_point_it_missed()
    {
        await ArrangeAsync(delayMinutes: 30);
        await SweepAsync();

        _time.SetUtcNow(Origin.AddMinutes(29.5));
        await SweepAsync();

        Assert.Equal(2, Said.Count);
        Assert.Contains("1 minute", Said[1]);
    }

    [Fact]
    public async Task The_build_is_applied_when_the_window_closes()
    {
        await ArrangeAsync(delayMinutes: 30);
        await SweepUntilDownloadedAsync();

        _time.SetUtcNow(Origin.AddMinutes(31));

        await ApplyWithTheClockRunningAsync(afterProcessCount: 1);

        Assert.Null(_updater.PendingFor("survival"));

        // Stopped and started again, and the new binaries are on disk.
        Assert.Equal(2, _factory.Created.Count);
        Assert.Equal(
            "the 1.26.44.3 executable",
            await File.ReadAllTextAsync(
                Path.Combine(_supervisor.WorkingDirectory, "bedrock_server.exe")));
    }

    [Fact]
    public async Task Nothing_is_scheduled_for_an_instance_that_only_wants_telling()
    {
        await ArrangeAsync(mode: BdsUpdateMode.Notify);
        await SweepAsync();

        Assert.Null(_updater.PendingFor("survival"));
        Assert.Empty(Said);
    }

    [Fact]
    public async Task Nothing_is_scheduled_for_a_pinned_instance()
    {
        await ArrangeAsync(pin: "1.26.40.8");
        await SweepAsync();

        Assert.Null(_updater.PendingFor("survival"));
    }

    // A pin outranks the mode, including mid-countdown. Leaving a scheduled
    // update running under one would make the two settings contradict each
    // other with the quieter one winning.
    [Fact]
    public async Task A_pin_applied_during_the_countdown_calls_it_off()
    {
        await ArrangeAsync(delayMinutes: 30);
        await SweepUntilPendingAsync();

        _supervisor.Definition.ServerUpdate.PinnedVersion = "1.26.40.8";
        await SweepAsync();

        Assert.Null(_updater.PendingFor("survival"));
        Assert.Contains(Said, s => s.Contains("called off"));
    }

    [Fact]
    public async Task Cancelling_stops_it_and_it_is_not_scheduled_again()
    {
        await ArrangeAsync(delayMinutes: 30);
        await SweepAsync();

        Assert.True(_updater.Cancel("survival"));
        Assert.Null(_updater.PendingFor("survival"));

        // The build is still newer, so without the decline this would simply be
        // scheduled again on the next tick and the button would look broken.
        _time.SetUtcNow(Origin.AddMinutes(5));
        await SweepAsync();

        Assert.Null(_updater.PendingFor("survival"));
        Assert.Contains(Said, s => s.Contains("called off"));
    }

    // The bug behind all three of these: a decline was only ever cleared by a
    // newer build appearing, so an operator who cancelled and then went to
    // change the delay found the schedule would not come back — and the gesture
    // they reached for first, turning the mode off and on again, was the one
    // thing that could not undo a Cancel.
    [Fact]
    public async Task Saving_the_settings_after_a_cancel_schedules_it_again()
    {
        await ArrangeAsync(delayMinutes: 30);
        await SweepAsync();

        Assert.True(_updater.Cancel("survival"));
        Assert.Null(_updater.PendingFor("survival"));

        var settings = _supervisor.Definition.ServerUpdate;

        await _registry.SetServerUpdateAsync("survival", new BdsUpdateSettings
        {
            Mode = settings.Mode,
            Channel = settings.Channel,
            PinnedVersion = settings.PinnedVersion,
            AutoApplyDelayMinutes = 45,
        });

        _time.SetUtcNow(Origin.AddMinutes(5));
        await SweepAsync();

        var pending = _updater.PendingFor("survival");

        Assert.NotNull(pending);
        Assert.Equal(OfferedVersion, pending.Version);

        // The new delay, measured from when it was scheduled again rather than
        // from when it was first seen.
        Assert.Equal(Origin.AddMinutes(50), pending.DueUtc);
    }

    [Fact]
    public async Task The_mode_off_and_on_again_schedules_it_again()
    {
        await ArrangeAsync(delayMinutes: 30);
        await SweepAsync();

        Assert.True(_updater.Cancel("survival"));

        await SetModeAsync(BdsUpdateMode.Notify);
        await SweepAsync();
        Assert.Null(_updater.PendingFor("survival"));

        await SetModeAsync(BdsUpdateMode.Automatic);
        await SweepUntilPendingAsync();
    }

    // Withdrawing because the schedule stopped applying is not a judgement
    // about the build, so it must not decline it. Set on the definition
    // directly, so the settings-saved event is not what re-arms this one.
    [Fact]
    public async Task Turning_the_schedule_off_mid_countdown_does_not_decline_the_build()
    {
        await ArrangeAsync(delayMinutes: 30);
        await SweepUntilPendingAsync();

        _supervisor.Definition.ServerUpdate.Mode = BdsUpdateMode.Notify;
        await SweepAsync();
        Assert.Null(_updater.PendingFor("survival"));

        _supervisor.Definition.ServerUpdate.Mode = BdsUpdateMode.Automatic;
        await SweepUntilPendingAsync();
    }

    private Task SetModeAsync(BdsUpdateMode mode)
    {
        var settings = _supervisor.Definition.ServerUpdate;

        return _registry.SetServerUpdateAsync("survival", new BdsUpdateSettings
        {
            Mode = mode,
            Channel = settings.Channel,
            PinnedVersion = settings.PinnedVersion,
            AutoApplyDelayMinutes = settings.AutoApplyDelayMinutes,
        });
    }

    [Fact]
    public async Task Apply_now_brings_the_window_forward()
    {
        await ArrangeAsync(delayMinutes: 30);
        await SweepUntilDownloadedAsync();

        Assert.True(_updater.ApplyNow("survival"));

        await ApplyWithTheClockRunningAsync(afterProcessCount: 1);

        Assert.Null(_updater.PendingFor("survival"));
        Assert.Equal(2, _factory.Created.Count);
    }

    // Zero is a supported answer meaning "as soon as it is seen". There is no
    // window, so there is no countdown — the stop's own short warning is all
    // anybody gets.
    [Fact]
    public async Task A_zero_delay_applies_without_a_countdown()
    {
        await ArrangeAsync(delayMinutes: 0);
        await SweepUntilDownloadedAsync();

        Assert.Empty(Said);

        await ApplyWithTheClockRunningAsync(afterProcessCount: 1);

        Assert.Equal(2, _factory.Created.Count);
    }

    /// <summary>
    /// An update announced to players and then silently never happening is the
    /// shape of failure nobody investigates, because nothing says it went wrong.
    /// </summary>
    [Fact]
    public async Task A_download_that_fails_is_recorded_rather_than_forgotten()
    {
        _handler.FailDownloads = true;

        await ArrangeAsync(delayMinutes: 30);
        await SweepAsync();

        for (var i = 0; i < 30 && _updater.PendingFor("survival")?.Failure is null; i++)
        {
            await SweepAsync();
        }

        var pending = _updater.PendingFor("survival");

        Assert.NotNull(pending);
        Assert.NotNull(pending.Failure);

        // And it is not applied when the window closes.
        _time.SetUtcNow(Origin.AddMinutes(31));
        await SweepAsync();

        Assert.Single(_factory.Created);
    }

    // The zip is carried across a countdown that can run for hours, and the
    // Settings page can delete it in the meantime.
    [Fact]
    public async Task A_package_deleted_during_the_countdown_fails_loudly_rather_than_applying()
    {
        await ArrangeAsync(delayMinutes: 30);
        var pending = await SweepUntilDownloadedAsync();

        File.Delete(Path.Combine(_paths.ServerPackages, pending.PackageFileName!));

        _time.SetUtcNow(Origin.AddMinutes(31));
        await SweepAsync();

        Assert.Contains(
            "no longer in the package store", _updater.PendingFor("survival")?.Failure ?? "");
        Assert.Single(_factory.Created);
    }

    /// <summary>
    /// A rung that falls while the server is down must not be booked as spoken
    /// (`T142`).
    /// </summary>
    /// <remarks>
    /// Found by reading rather than by a failing run, and it hides well: nobody
    /// is connected during the outage, so the missed rung has no audience at the
    /// time, and every *later* rung still fires on its own due value. What bites
    /// is the last rung falling inside an outage — the server comes back with
    /// under a minute left and anyone who rejoins gets no final warning at all.
    /// The 2026-09-03 run came within about ninety seconds of hitting it by
    /// accident.
    ///
    /// The fix is ordering: `_lastSpoken` is recorded only once `SayAsync`
    /// reports it actually spoke. Nothing else needed changing, because the rung
    /// is recomputed from the remaining time on every sweep — so a server that
    /// comes back mid-countdown announces where the clock *is*, not the rung it
    /// slept through.
    /// </remarks>
    [Fact]
    public async Task A_rung_that_falls_while_the_server_is_down_is_not_marked_spoken()
    {
        await ArrangeAsync(delayMinutes: 30);
        await SweepAsync();

        Assert.Equal(["30 minutes"], Said.Select(Spoken));

        // Down across the 25-minute rung, which therefore cannot be said.
        await _supervisor.StopAsync(
            GracefulStopRequest.Immediate(StopReason.Manual, TimeSpan.FromSeconds(45)));

        _time.SetUtcNow(Origin.AddMinutes(5));
        await SweepAsync();

        Assert.Equal(["30 minutes"], Said.Select(Spoken));

        // Back up, still inside the same rung's window.
        _factory.ProcessesExitOnStop = true;
        await _supervisor.StartAsync();
        _factory.Latest.Emit("[2026-08-17 12:06:00:000 INFO] Version: 1.26.40.8");
        _factory.Latest.Ready();
        Assert.True(await _supervisor.WaitForReadyAsync(TimeSpan.FromSeconds(5)));

        _time.SetUtcNow(Origin.AddMinutes(6));
        await SweepAsync();

        // The rung it slept through is spoken now that there is somebody to tell.
        // Before the fix this asserted ["30 minutes"] alone: 25 had been booked as
        // delivered while nobody could hear it, so it was suppressed for good and
        // the next thing players heard was the 20-minute rung.
        Assert.Equal(["30 minutes", "25 minutes"], Said.Select(Spoken));
    }

    // A stopped server has nobody to tell, and that is not an error — the
    // countdown is for players, and the update still happens on time.
    [Fact]
    public async Task A_stopped_server_is_updated_without_anyone_being_told()
    {
        await ArrangeAsync(delayMinutes: 30, running: false);

        // No console means no reported version, so nothing is offered at all.
        await SweepAsync();
        Assert.Null(_updater.PendingFor("survival"));
        Assert.Empty(Said);
    }

    /// <summary>
    /// Runs an apply to completion: drives the fake clock through the stop's own
    /// short countdown, then reports the new build started.
    /// </summary>
    /// <remarks>
    /// Everything here shares one <see cref="FakeTimeProvider"/>, including the
    /// supervisor — so the ten-second warning the update ends with is a
    /// <c>Task.Delay</c> on a clock that only moves when a test moves it. Without
    /// this the apply simply never finishes.
    /// </remarks>
    private async Task ApplyWithTheClockRunningAsync(int afterProcessCount)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        var sweep = _updater.SweepAsync();

        while (DateTime.UtcNow < deadline && !sweep.IsCompleted)
        {
            if (_factory.Created.Count > afterProcessCount && !_factory.Latest.HasExited)
            {
                _factory.Latest.Ready();
            }

            _time.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(10);
        }

        await sweep;
    }

    /// <summary>Mojang's feed, and a zip at the end of the download URL.</summary>
    private sealed class FeedHandler : HttpMessageHandler
    {
        private const string Feed =
            """{"result":{"links":[{"downloadType":"serverBedrockWindows","downloadUrl":"""
            + "\"https://example.invalid/bin-win/bedrock-server-" + OfferedVersion + ".zip\"}]}}";

        public bool FailDownloads { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            if (url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                if (FailDownloads)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(ServerZip()),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Feed),
            });
        }

        private static byte[] ServerZip()
        {
            using var buffer = new MemoryStream();

            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(
                    archive.CreateEntry("bedrock_server.exe").Open(), new UTF8Encoding(false));

                writer.Write($"the {OfferedVersion} executable");
            }

            return buffer.ToArray();
        }
    }

    private sealed class StubClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
