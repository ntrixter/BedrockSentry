using System.Net;
using System.Net.Http.Headers;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Services.Configuration;
using BedrockSentry.Services.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BedrockSentry.Tests;

/// <summary>
/// `T163`, `D60`. Asking the channel on demand, and the four layers that stop a
/// button from becoming a way to hammer somebody else's server.
/// </summary>
/// <remarks>
/// The feed is a release <em>asset</em> rather than an API endpoint, so the
/// documented 60-per-hour REST ceiling does not govern it — and GitHub's
/// secondary limits could not be read from this environment at all. So the design
/// does not depend on knowing them: the layer that matters is honouring a throttle
/// when one arrives, and the rest is keeping the frequency sane without it.
/// </remarks>
public sealed class ManualUpdateCheckTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("bedrocksentry-manualcheck-").FullName;

    private readonly FeedHandler _handler = new();
    private readonly FakeTimeProvider _time =
        new(new DateTimeOffset(2026, 9, 10, 14, 0, 0, TimeSpan.Zero));

    private readonly ChangeableOptionsMonitor<UpdateOptions> _options =
        new(new UpdateOptions { Mode = UpdateMode.Notify });

    private readonly UpdateService _service;

    public ManualUpdateCheckTests()
    {
        var paths = new SentryPaths(_root);
        Directory.CreateDirectory(paths.Update);
        Directory.CreateDirectory(paths.Logs);

        // A versioned install: the running directory is named "current" and has a
        // sibling "versions". Without both, self-update is inactive and every
        // check is refused — which is its own test below.
        var current = Path.Combine(_root, "current");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(Path.Combine(_root, "versions"));

        var layout = new InstallLayout(current);

        _service = new UpdateService(
            new UpdateClient(new HttpClient(_handler), layout, NullLogger<UpdateClient>.Instance),
            new UpdateLedgerStore(paths, NullLogger<UpdateLedgerStore>.Instance),
            layout,
            paths,
            _options,
            NullLogger<UpdateService>.Instance,
            time: _time);
    }

    public void Dispose()
    {
        _service.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>Past the cooldown, without waiting out a real minute.</summary>
    private void LetTheCooldownLapse() => _time.Advance(TimeSpan.FromSeconds(61));

    [Fact]
    public async Task A_manual_check_reads_the_channel_and_says_so()
    {
        var result = await _service.CheckNowAsync();

        Assert.Equal(ManualCheckOutcome.Checked, result.Outcome);
        Assert.True(result.Asked);
        Assert.Equal(1, _handler.Requests);
        Assert.NotNull(_service.Available);

        // The row that makes the button's effect visible when nothing is newer.
        Assert.Equal(_time.GetUtcNow(), _service.LastCheckedUtc);
    }

    /// <summary>
    /// Several tabs, a double-click and a held key are all this.
    /// </summary>
    /// <remarks>
    /// Joining rather than refusing is the point: every caller gets the answer of
    /// the one request that was actually made, so nothing has to explain itself.
    /// </remarks>
    [Fact]
    public async Task Two_requests_at_once_make_one_request_and_share_its_answer()
    {
        _handler.Hold();

        var first = _service.CheckNowAsync();

        // Past the cooldown deliberately, so the cooldown cannot be what stops the
        // second request. Without that the test passes with single-flight deleted —
        // the second call is simply refused as too soon, and the request count
        // reads the same either way.
        _time.Advance(TimeSpan.FromSeconds(61));

        var second = _service.CheckNowAsync();

        Assert.True(_service.IsChecking);

        _handler.Release();

        var a = await first;
        var b = await second;

        Assert.Equal(1, _handler.Requests);
        Assert.Equal(ManualCheckOutcome.Checked, a.Outcome);
        Assert.Same(a, b);
    }

    /// <remarks>
    /// Enforced in the service rather than by greying out the button, because the
    /// panel binds every interface: a disabled attribute is a hint to one browser,
    /// and this has to hold against a hand-written POST too.
    /// </remarks>
    [Fact]
    public async Task A_second_check_straight_afterwards_is_refused_without_a_request()
    {
        await _service.CheckNowAsync();

        _time.Advance(TimeSpan.FromSeconds(5));

        var again = await _service.CheckNowAsync();

        Assert.Equal(ManualCheckOutcome.TooSoon, again.Outcome);
        Assert.Equal(1, _handler.Requests);
        Assert.True(again.RetryAfter > TimeSpan.Zero);

        // And it says how long, because a refusal with no number reads as a fault.
        Assert.Contains("seconds", again.Detail);
    }

    [Fact]
    public async Task Once_the_cooldown_has_passed_it_checks_again()
    {
        await _service.CheckNowAsync();
        LetTheCooldownLapse();

        var again = await _service.CheckNowAsync();

        Assert.Equal(ManualCheckOutcome.Checked, again.Outcome);
        Assert.Equal(2, _handler.Requests);
    }

    /// <summary>
    /// What <c>UpdateMode.Disabled</c> has always documented and never did.
    /// </summary>
    /// <remarks>
    /// Never checking on a schedule is a different statement from refusing to
    /// answer a person, and the person has usually just changed the channel or
    /// watched a release go out.
    /// </remarks>
    [Fact]
    public async Task Checking_turned_off_still_answers_a_person()
    {
        _options.Set(new UpdateOptions { Mode = UpdateMode.Disabled });

        var result = await _service.CheckNowAsync();

        Assert.Equal(ManualCheckOutcome.Checked, result.Outcome);
        Assert.NotNull(_service.Available);
        Assert.False(_service.IsApplying);
    }

    /// <summary>
    /// The guard `T151` clause 6 needed and did not have.
    /// </summary>
    /// <remarks>
    /// That regression came from a fix whose stated purpose was to stop two
    /// callers drifting: they stopped drifting and started sharing an answer one
    /// of them did not want. So this pins the two callers of <c>LookAsync</c> to
    /// <em>different</em> answers — the scheduled path may apply, the manual one
    /// never does — rather than trusting a comment to say so. Pinning the
    /// difference is the only kind of guard that works here, because agreeing is
    /// the failure mode.
    /// </remarks>
    [Fact]
    public async Task A_manual_check_never_applies_even_on_automatic()
    {
        _options.Set(new UpdateOptions { Mode = UpdateMode.Automatic });

        var result = await _service.CheckNowAsync();

        Assert.Equal(ManualCheckOutcome.Checked, result.Outcome);
        Assert.NotNull(_service.Available);

        // One request: the feed, and nothing else.
        Assert.Equal(1, _handler.Requests);
        Assert.False(_service.IsApplying);

        // And "one request" means something, because applying is visibly a second
        // one: it goes on to fetch the zip. Without this line the assertion above
        // could pass for a service that had no apply path at all.
        await _service.ApplyAsync(_service.Available!, _options.CurrentValue);

        Assert.Equal(2, _handler.Requests);
    }

    /// <remarks>
    /// The layer that makes the unverified question of GitHub's secondary limits
    /// stop mattering: whatever they are, being told is enough.
    /// </remarks>
    [Fact]
    public async Task A_server_that_asks_to_be_left_alone_is_left_alone()
    {
        _handler.Status = HttpStatusCode.TooManyRequests;
        _handler.RetryAfter = TimeSpan.FromMinutes(10);

        var refused = await _service.CheckNowAsync();

        Assert.Equal(ManualCheckOutcome.Throttled, refused.Outcome);
        Assert.Equal(1, _handler.Requests);

        _handler.Status = HttpStatusCode.OK;
        _handler.RetryAfter = null;
        LetTheCooldownLapse();

        var blocked = await _service.CheckNowAsync();

        Assert.Equal(ManualCheckOutcome.Throttled, blocked.Outcome);
        Assert.Equal(1, _handler.Requests);
        Assert.Contains("minutes", blocked.Detail);

        // And it lifts by itself, rather than needing a service restart.
        _time.Advance(TimeSpan.FromMinutes(11));

        var allowed = await _service.CheckNowAsync();

        Assert.Equal(ManualCheckOutcome.Checked, allowed.Outcome);
        Assert.Equal(2, _handler.Requests);
    }

    /// <summary>
    /// A 404 is not a throttle, and this is the shape every install is in today.
    /// </summary>
    /// <remarks>
    /// A private repository serves no release asset anonymously (<c>D13</c>), so a
    /// 404 is the commonest answer this feed gives. Inferring a throttle from a
    /// status code alone would stop checking everywhere, for an hour at a time,
    /// on the most ordinary failure there is.
    /// </remarks>
    [Fact]
    public async Task A_not_found_is_not_treated_as_a_throttle()
    {
        _handler.Status = HttpStatusCode.NotFound;

        var failed = await _service.CheckNowAsync();

        Assert.Equal(ManualCheckOutcome.Failed, failed.Outcome);

        _handler.Status = HttpStatusCode.OK;
        LetTheCooldownLapse();

        var again = await _service.CheckNowAsync();

        Assert.Equal(ManualCheckOutcome.Checked, again.Outcome);
        Assert.Equal(2, _handler.Requests);
    }

    /// <summary>
    /// A failed read is not evidence the build went away.
    /// </summary>
    /// <remarks>
    /// <c>Available</c> used to be cleared on any unsuccessful fetch, so a single
    /// network blip took <b>Install now and restart</b> off the page until the next
    /// successful check — up to six hours later. Changed deliberately while `T163`
    /// was being built, and named here so it is a decision rather than a side
    /// effect.
    /// </remarks>
    [Fact]
    public async Task A_failed_check_does_not_forget_a_build_it_already_found()
    {
        await _service.CheckNowAsync();
        Assert.NotNull(_service.Available);

        var found = _service.LastCheckedUtc;

        _handler.Status = HttpStatusCode.ServiceUnavailable;
        LetTheCooldownLapse();

        await _service.CheckNowAsync();

        Assert.NotNull(_service.Available);

        // But the timestamp does not move, because nothing was read.
        Assert.Equal(found, _service.LastCheckedUtc);
    }

    /// <remarks>
    /// `D60` part two. Watched in the service rather than wired to the Save button
    /// so a hand-edited config file gets it too, and so the rule sits where this
    /// test can reach it.
    /// </remarks>
    [Fact]
    public async Task Changing_the_channel_asks_the_new_channel()
    {
        _options.Set(new UpdateOptions { Mode = UpdateMode.Notify, Channel = UpdateChannel.Build });

        Assert.True(await WaitUntil(() => _handler.Requests >= 1));
        Assert.Equal(1, _handler.Requests);
    }

    /// <remarks>
    /// The monitor fires for every write to the section, so saving the check
    /// interval must not reach for the network. Seeding the last-seen channel in
    /// the constructor is what makes this true.
    /// </remarks>
    [Fact]
    public async Task Saving_something_other_than_the_channel_asks_nothing()
    {
        _options.Set(new UpdateOptions
        {
            Mode = UpdateMode.Notify,
            CheckInterval = TimeSpan.FromHours(12),
        });

        await Task.Delay(200);

        Assert.Equal(0, _handler.Requests);
    }

    /// <summary>
    /// An install that cannot self-update says so rather than failing.
    /// </summary>
    /// <remarks>
    /// The page greys the button out from <c>SelfUpdateIsActive</c>, the way the
    /// Bedrock panel greys out <b>Check now</b> when checking is off; this is the
    /// same rule behind the button, for anything that gets past it.
    /// </remarks>
    [Fact]
    public async Task An_install_that_cannot_self_update_refuses_without_asking()
    {
        var plain = new InstallLayout(_root);
        var paths = new SentryPaths(_root);

        using var service = new UpdateService(
            new UpdateClient(new HttpClient(_handler), plain, NullLogger<UpdateClient>.Instance),
            new UpdateLedgerStore(paths, NullLogger<UpdateLedgerStore>.Instance),
            plain,
            paths,
            _options,
            NullLogger<UpdateService>.Instance,
            time: _time);

        Assert.False(service.SelfUpdateIsActive);

        var refused = await service.CheckNowAsync();

        Assert.Equal(ManualCheckOutcome.Inactive, refused.Outcome);
        Assert.Equal(0, _handler.Requests);
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(10);
        }

        return condition();
    }

    /// <summary>Answers with a manifest, a status, or a throttle, on demand.</summary>
    private sealed class FeedHandler : HttpMessageHandler
    {
        private const string Manifest = """
            {
              "identity": "0.3.0-build.99+abcdef0",
              "version": "0.3.0-build.99",
              "commitSha": "abcdef0000000000000000000000000000000000",
              "channel": "build",
              "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "downloadUrl": "https://example.invalid/bedrocksentry.zip"
            }
            """;

        private int _requests;
        private TaskCompletionSource? _held;

        public int Requests => Volatile.Read(ref _requests);

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public TimeSpan? RetryAfter { get; set; }

        /// <summary>Makes the next request block, so two can overlap.</summary>
        public void Hold() => _held = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _held?.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);

            if (_held is { } held)
            {
                await held.Task.WaitAsync(cancellationToken);
            }

            var response = new HttpResponseMessage(Status)
            {
                Content = new StringContent(Manifest),
            };

            if (RetryAfter is { } wait)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
            }

            return response;
        }
    }
}
