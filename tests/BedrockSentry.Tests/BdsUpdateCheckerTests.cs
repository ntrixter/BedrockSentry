using System.Net;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Configuration;
using BedrockSentry.Services.DependencyInjection;
using BedrockSentry.Services.Instances;
using BedrockSentry.Services.Packages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace BedrockSentry.Tests;

/// <summary>
/// docs/design.md § 11. The schedule is settable from the Settings page, so the test
/// that matters is that a change to it is picked up by the running loop — a
/// setting that needs the Windows Service restarted to take effect is a setting
/// that will be reported as broken.
/// </summary>
public sealed class BdsUpdateCheckerTests : IDisposable
{
    private const string Feed = """
        {"result":{"links":[
          {"downloadType":"serverBedrockWindows",
           "downloadUrl":"https://example.invalid/bin-win/bedrock-server-1.26.44.3.zip"}
        ]}}
        """;

    /// <summary>A feed that parses and names no build this wrapper recognises.</summary>
    private const string EmptyFeed = """
        {"result":{"links":[]}}
        """;

    private readonly string _root =
        Directory.CreateTempSubdirectory("bedrocksentry-checker-").FullName;

    private readonly CountingHandler _handler = new(Feed);
    private readonly MutableOptions _options = new();
    private readonly FakeTimeProvider _time =
        new(new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.Zero));

    private readonly BdsUpdateChecker _checker;
    private readonly BdsUpdateService _service;

    public BdsUpdateCheckerTests()
    {
        var paths = new SentryPaths(_root);
        Directory.CreateDirectory(paths.ServerPackages);

        var service = new BdsUpdateService(
            new StubClientFactory(_handler),
            new ServerPackageStore(paths, NullLogger<ServerPackageStore>.Instance),
            _options,
            _time,
            NullLogger<BdsUpdateService>.Instance,
            new ServerVersionLog(paths, NullLogger<ServerVersionLog>.Instance));

        _service = service;

        _checker = new BdsUpdateChecker(
            service, _options, _time, NullLogger<BdsUpdateChecker>.Instance);
    }

    public void Dispose()
    {
        _checker.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// Lets the loop reach its next sleep before the clock is touched.
    /// </summary>
    /// <remarks>
    /// Real time, not fake. A request being counted says the HTTP call started,
    /// not that the loop has finished the iteration and armed its next timer,
    /// and a clock jump taken in that window is one the loop never sees — it
    /// arms afterwards, against the already-advanced now.
    /// </remarks>
    private static Task SettleAsync() => Task.Delay(250);

    /// <summary>
    /// Jumps the fake clock forward in whole steps until something happens.
    /// </summary>
    /// <remarks>
    /// A single jump would be enough if the loop were guaranteed to be asleep
    /// when it lands, and it is not — so each step is larger than the interval
    /// under test and the loop gets another one if it missed the last. Steps
    /// rather than one huge jump because the assertions care about how many
    /// checks happened, not just that one did.
    /// </remarks>
    private async Task<bool> AdvanceUntilAsync(TimeSpan step, Func<bool> condition, int maxSteps = 20)
    {
        for (var taken = 0; taken < maxSteps && !condition(); taken++)
        {
            _time.Advance(step);

            for (var settle = 0; settle < 25 && !condition(); settle++)
            {
                await Task.Delay(10);
            }
        }

        return condition();
    }

    /// <summary>Jumps the clock once, for assertions that nothing should happen.</summary>
    private async Task AdvanceAsync(TimeSpan total)
    {
        await SettleAsync();
        _time.Advance(total);
        await SettleAsync();
    }

    private Task<bool> RequestsReach(int count) =>
        WaitUntil(() => _handler.Requests >= count);

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
    /// The registration that made the panel look like it reset itself.
    /// </summary>
    /// <remarks>
    /// <c>AddHttpClient&lt;BdsUpdateService&gt;()</c> registers a typed client as
    /// <em>transient</em>, and this service caches the feed in its own fields.
    /// The background checker was therefore refreshing an object no page could
    /// ever see, and every page load started from "nothing has been seen yet"
    /// with no update button — until you pressed Check now, whose result then
    /// vanished the moment you navigated away.
    ///
    /// Asserted on the real registration rather than on a hand-built container,
    /// because the defect was in the registration.
    /// </remarks>
    [Fact]
    public void The_update_service_is_one_object_for_the_whole_install()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddBedrockSentryServices(new SentryPaths(_root))
            .BuildServiceProvider();

        var first = services.GetRequiredService<BdsUpdateService>();
        var second = services.GetRequiredService<BdsUpdateService>();

        Assert.Same(first, second);

        // And scoped resolution — which is what a Blazor circuit does — must
        // reach that same one, not a per-circuit copy.
        using var scope = services.CreateScope();
        Assert.Same(first, scope.ServiceProvider.GetRequiredService<BdsUpdateService>());
    }

    [Fact]
    public async Task It_checks_once_at_startup_so_the_first_page_load_has_something_to_say()
    {
        await _checker.StartAsync(CancellationToken.None);

        Assert.True(await RequestsReach(1));

        await _checker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task It_checks_again_when_the_interval_elapses()
    {
        _options.Set(o => o.CheckSchedule = new RestartSchedule
        {
            Mode = RestartScheduleMode.EveryNHours,
            IntervalHours = 6,
        });

        await _checker.StartAsync(CancellationToken.None);
        Assert.True(await RequestsReach(1));
        await SettleAsync();

        Assert.True(
            await AdvanceUntilAsync(TimeSpan.FromHours(7), () => _handler.Requests >= 2),
            "the six-hour interval never came round");

        await _checker.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// The point of re-reading the schedule every time round the loop. Started
    /// on a daily schedule, changed to hourly, and the change has to be picked
    /// up without the service being restarted.
    /// </summary>
    [Fact]
    public async Task A_schedule_changed_while_it_runs_takes_effect_without_a_restart()
    {
        _options.Set(o => o.CheckSchedule = new RestartSchedule
        {
            Mode = RestartScheduleMode.DailyAt,
            TimeOfDay = new TimeOnly(4, 0),
        });

        await _checker.StartAsync(CancellationToken.None);
        Assert.True(await RequestsReach(1));

        // An hour on a daily schedule due at 04:00: nothing more is due.
        await AdvanceAsync(TimeSpan.FromHours(1));
        Assert.Equal(1, _handler.Requests);

        _options.Set(o => o.CheckSchedule = new RestartSchedule
        {
            Mode = RestartScheduleMode.EveryNHours,
            IntervalHours = 1,
        });

        // An interval counts from the last check, not from when the setting
        // changed, so this has to pass 09:00 + N hours rather than 10:00 + 1.
        Assert.True(
            await AdvanceUntilAsync(TimeSpan.FromHours(4), () => _handler.Requests >= 2),
            "the schedule change was never picked up");

        await _checker.StopAsync(CancellationToken.None);
    }

    // Mode None still checks at startup and still answers the button. Only the
    // timer between them goes away — which is why the page does not call it
    // "never".
    [Fact]
    public async Task No_timed_schedule_still_checks_at_startup_and_then_stops()
    {
        _options.Set(o => o.CheckSchedule = new RestartSchedule { Mode = RestartScheduleMode.None });

        await _checker.StartAsync(CancellationToken.None);
        Assert.True(await RequestsReach(1));

        await AdvanceAsync(TimeSpan.FromDays(2));

        Assert.Equal(1, _handler.Requests);

        await _checker.StopAsync(CancellationToken.None);
    }

    // The install-wide off switch is not the schedule: it means do not contact
    // Mojang at all, and RefreshAsync refuses before any request is made.
    [Fact]
    public async Task Turning_checking_off_makes_no_requests_at_all()
    {
        _options.Set(o => o.CheckForUpdates = false);

        await _checker.StartAsync(CancellationToken.None);
        await AdvanceAsync(TimeSpan.FromDays(1));

        Assert.Equal(0, _handler.Requests);

        await _checker.StopAsync(CancellationToken.None);
    }

    /// <summary>An <see cref="IOptionsMonitor{T}"/> a test can move underneath the loop.</summary>
    /// <summary>
    /// `T152`. A page on screen when the scheduled check lands has to be told.
    /// </summary>
    /// <remarks>
    /// Driven through the real checker rather than by calling RefreshAsync here,
    /// because the whole defect was that the background path and the page were
    /// not connected — a test that calls the service directly proves the event
    /// fires and not that the thing which was broken now works.
    ///
    /// <b>Check now</b> was never evidence either way: Blazor re-renders after
    /// any handler returns, so the button worked for a reason outside this class.
    /// </remarks>
    [Fact]
    public async Task A_scheduled_check_tells_whoever_is_listening()
    {
        var told = 0;
        _service.Refreshed += () => Interlocked.Increment(ref told);

        await _checker.StartAsync(CancellationToken.None);

        Assert.True(await WaitUntil(() => Volatile.Read(ref told) >= 1));

        // And the listener can see the result, not just that something happened.
        Assert.NotNull(_service.LastCheckedUtc);
        Assert.NotEmpty(_service.Latest);

        await _checker.StopAsync(CancellationToken.None);
    }

    /// <remarks>
    /// The one exit that changes nothing must stay quiet, or a page would redraw
    /// on every tick of a schedule that is contacting nobody.
    /// </remarks>
    [Fact]
    public async Task A_check_that_never_happens_tells_nobody()
    {
        _options.Set(o => o.CheckForUpdates = false);

        var told = 0;
        _service.Refreshed += () => Interlocked.Increment(ref told);

        await _service.RefreshAsync();

        Assert.Equal(0, Volatile.Read(ref told));
        Assert.Equal(0, _handler.Requests);
    }

    /// <remarks>
    /// A failure is a thing the page renders — LastFailure is on screen — so it
    /// is as much a change as an offer is. Both of the other two exits below the
    /// gate are here: the request failing, and a feed that parses to nothing.
    /// </remarks>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, Feed)]
    [InlineData(HttpStatusCode.OK, EmptyFeed)]
    public async Task A_check_that_goes_wrong_still_tells_whoever_is_listening(
        HttpStatusCode status, string body)
    {
        _handler.Status = status;
        _handler.Body = body;

        var told = 0;
        _service.Refreshed += () => Interlocked.Increment(ref told);

        await _service.RefreshAsync();

        Assert.Equal(1, Volatile.Read(ref told));
        Assert.NotNull(_service.LastFailure);
    }

    /// <summary>
    /// RefreshAsync promises not to throw, and that promise now covers somebody
    /// else's code.
    /// </summary>
    /// <remarks>
    /// The listener is a Blazor circuit that may have gone away mid-check. Before
    /// the guard, a page navigating off at the wrong moment would have faulted the
    /// background checker — a worse bug than the stale panel the event fixes.
    /// </remarks>
    [Fact]
    public async Task A_listener_that_throws_does_not_break_the_check()
    {
        _service.Refreshed += () => throw new ObjectDisposedException("circuit");

        var after = 0;
        _service.Refreshed += () => Interlocked.Increment(ref after);

        var links = await _service.RefreshAsync();

        Assert.NotEmpty(links);
        Assert.NotNull(_service.LastCheckedUtc);

        // The throw must not swallow the listeners behind it either.
        Assert.Equal(1, Volatile.Read(ref after));
    }

    private sealed class MutableOptions : IOptionsMonitor<BdsUpdateOptions>
    {
        private BdsUpdateOptions _value = new() { LinksUrl = "https://example.invalid/feed" };

        public BdsUpdateOptions CurrentValue => _value;

        public BdsUpdateOptions Get(string? name) => _value;

        public IDisposable? OnChange(Action<BdsUpdateOptions, string?> listener) => null;

        public void Set(Action<BdsUpdateOptions> mutate)
        {
            var next = new BdsUpdateOptions
            {
                LinksUrl = _value.LinksUrl,
                CheckForUpdates = _value.CheckForUpdates,
                CheckInterval = _value.CheckInterval,
                CheckSchedule = _value.CheckSchedule,
            };

            mutate(next);
            _value = next;
        }
    }

    /// <remarks>
    /// The service takes a factory rather than a client because it is a
    /// singleton that must not hold a handler — and its DNS answers — for the
    /// months a Windows Service runs. Each call gets a fresh client over the
    /// one handler, so the request count is still the whole story.
    /// </remarks>
    private sealed class StubClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false);
    }

    private sealed class CountingHandler(string body) : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        /// <summary>What the feed answers with, so a test can make it fail.</summary>
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        /// <summary>The body, so a test can return a feed with no usable build.</summary>
        public string Body { get; set; } = body;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);

            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body),
            });
        }
    }
}
