using System.Text.Json;
using BedrockSentry.Core;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Updates;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BedrockSentry.Services.Updates;

/// <summary>What came of asking the channel on demand (<c>D60</c>).</summary>
public enum ManualCheckOutcome
{
    /// <summary>The feed was read. <c>Available</c> now reflects it.</summary>
    Checked,

    /// <summary>Refused: another manual check was too recent.</summary>
    TooSoon,

    /// <summary>Refused: the server asked to be left alone and has not been.</summary>
    Throttled,

    /// <summary>Refused: self-update is not active on this install at all.</summary>
    Inactive,

    /// <summary>The feed was asked and gave nothing usable.</summary>
    Failed,
}

/// <summary>
/// The answer to a manual check, with the sentence for the page.
/// </summary>
/// <remarks>
/// The sentence is built here rather than in the panel on purpose. A rule stated
/// in a <c>.razor</c> file is one the test project cannot see — the defect class
/// this codebase keeps producing — and this one has four branches and a duration
/// to format.
/// </remarks>
/// <param name="RetryAfter">
/// How long until asking again would be allowed. <c>Zero</c> unless the outcome
/// is a refusal that will expire.
/// </param>
public sealed record ManualCheckResult(ManualCheckOutcome Outcome, TimeSpan RetryAfter, string Detail)
{
    /// <summary>Whether the feed was actually reached.</summary>
    public bool Asked => Outcome is ManualCheckOutcome.Checked;
}

/// <summary>
/// Polls the release channel and, when configured to, hands a new build to the
/// updater helper.
/// </summary>
/// <remarks>
/// This process never stops itself. It stages the new build, writes a plan, launches
/// the detached helper, and returns — the helper issues the stop through the service
/// control manager, so the SCM sees an ordinary controlled shutdown rather than a
/// service that exited on its own and needs recovering.
/// </remarks>
public sealed class UpdateService : BackgroundService
{
    /// <summary>
    /// The fastest this will ever poll the channel feed, whatever configuration
    /// says.
    /// </summary>
    /// <remarks>
    /// This is a request to somebody else's server on a loop. A zero or negative
    /// interval — a typo, or a hand-edited "00:00:00" — would make the delay
    /// complete instantly and turn the check into an unthrottled poll, which is a
    /// self-inflicted rate limit and an endless log.
    /// </remarks>
    private static readonly TimeSpan MinimumCheckInterval = TimeSpan.FromMinutes(5);

    /// <summary>How long to sleep before re-reading the interval.</summary>
    private static readonly TimeSpan CheckSlice = TimeSpan.FromMinutes(1);

    /// <summary>The shortest gap between two checks somebody asked for.</summary>
    /// <remarks>
    /// Enforced here rather than by greying out the button, because the panel
    /// binds every interface and a disabled attribute is a hint to one browser.
    /// Here it is one rule a test can reach, and it holds against a held key, a
    /// second tab and a hand-written POST alike.
    ///
    /// Short on purpose: the thing being protected is the frequency of a 438-byte
    /// request to somebody else's origin, and an administrator who has just cut a
    /// release should not be made to wait. The layer that matters is
    /// <see cref="_throttledUntilUtc"/>, which does what the server asks.
    /// </remarks>
    private static readonly TimeSpan ManualCheckCooldown = TimeSpan.FromSeconds(60);

    private readonly UpdateClient _client;
    private readonly IWrapperUpdateInstaller _installer;
    private readonly UpdateLedgerStore _ledger;
    private readonly InstallLayout _layout;
    private readonly IOptionsMonitor<UpdateOptions> _options;
    private readonly ILogger<UpdateService> _logger;
    private readonly TimeProvider _time;

    /// <summary>
    /// Takes the servers down before the helper is launched (milestone 13.5).
    /// </summary>
    /// <remarks>
    /// Optional so the self-update mechanism can still be constructed and tested
    /// without the whole instance layer behind it — it predates instances, and
    /// an install with no servers configured has nothing for it to do.
    /// </remarks>
    private readonly IManagedServerShutdown? _shutdown;

    /// <summary>
    /// Consulted so a wrapper update never pulls a server out from under a
    /// Bedrock update in flight. Optional for the same reason the shutdown is.
    /// </summary>
    private readonly Packages.BdsAutoUpdater? _bdsUpdates;

    /// <summary>
    /// So an update in progress can tell the host is going down and stand down.
    /// </summary>
    private readonly IHostApplicationLifetime? _lifetime;

    /// <summary>
    /// Consulted so the drain never stops a server part-way through a claimed job,
    /// such as a sync (<see cref="InstanceClaim"/>). Optional for the same
    /// reason the shutdown is.
    /// </summary>
    private readonly InstanceRegistry? _registry;

    /// <summary>The apply in flight, so a second request does not start another.</summary>
    private Task? _applying;

    private readonly Lock _applyGate = new();

    /// <summary>
    /// The manual check in flight, so a second request joins it instead of
    /// starting a second fetch.
    /// </summary>
    /// <remarks>
    /// Joining rather than refusing is what makes several tabs, a double-click
    /// and a held key all correct: every caller gets the answer of the one
    /// request that was made.
    /// </remarks>
    private Task<ManualCheckResult>? _checking;

    private readonly Lock _checkGate = new();

    /// <summary>When the last manual check was let through.</summary>
    private DateTimeOffset _lastManualCheckUtc;

    /// <summary>
    /// Until when the channel host has asked not to be contacted.
    /// </summary>
    /// <remarks>
    /// Honoured by the scheduled check and the manual one alike. This is the
    /// layer that makes the unverified question of GitHub's secondary limits
    /// stop mattering: whatever they are, being told is enough.
    /// </remarks>
    private DateTimeOffset _throttledUntilUtc;

    /// <summary>Undoes the channel-change watch in <see cref="Dispose"/>.</summary>
    private readonly IDisposable? _optionsWatch;

    /// <summary>The channel last seen, so a change can be told from a save.</summary>
    private UpdateChannel _lastSeenChannel;

    public UpdateService(
        UpdateClient client,
        UpdateLedgerStore ledger,
        InstallLayout layout,
        ISentryPaths paths,
        IOptionsMonitor<UpdateOptions> options,
        ILogger<UpdateService> logger,
        IManagedServerShutdown? shutdown = null,
        Packages.BdsAutoUpdater? bdsUpdates = null,
        IHostApplicationLifetime? lifetime = null,
        TimeProvider? time = null,
        InstanceRegistry? registry = null,
        IWrapperUpdateInstaller? installer = null)
    {
        _installer = installer ?? new LocalWrapperUpdateInstaller(client, layout, paths, logger);
        _shutdown = shutdown;
        _bdsUpdates = bdsUpdates;
        _lifetime = lifetime;
        _registry = registry;
        _client = client;
        _ledger = ledger;
        _layout = layout;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;

        _lastSeenChannel = options.CurrentValue.Channel;

        // D60 part two: changing the channel asks the question by itself. Watched
        // here rather than wired to the Save button for three reasons — a
        // hand-edited config file gets it too, the rule sits where a test can
        // reach it, and the panel needs no new control.
        //
        // Seeded above so the first callback is not mistaken for a change: this
        // fires for every write to the section, including saving the interval.
        _optionsWatch = options.OnChange(OnOptionsChanged);
    }

    /// <summary>The newest build seen on the channel, if it is not the running one.</summary>
    public UpdateManifest? Available { get; private set; }

    /// <summary>When the channel feed was last read successfully, or null if never.</summary>
    /// <remarks>
    /// Exists because a manual check that finds nothing is otherwise invisible:
    /// the panel's "no newer build has been seen" sentence is identical before and
    /// after, so there would be no way to tell the button from a no-op. The
    /// Bedrock panel has carried this row since milestone 13.
    /// </remarks>
    public DateTimeOffset? LastCheckedUtc { get; private set; }

    /// <summary>
    /// The last check that could not read the channel, or null once a check has
    /// read it since (<c>T196</c>).
    /// </summary>
    /// <remarks>
    /// <see cref="LastCheckedUtc"/> alone has two states and a feed has three:
    /// never asked, asked and unreadable, and read. Without this a feed that has
    /// failed every six hours since install reads exactly like one nobody has
    /// asked, because the banner a manual press shows does not survive a reload
    /// and the scheduled check shows nothing at all.
    /// </remarks>
    public UpdateCheckFailure? LastFailedCheck { get; private set; }

    /// <summary>
    /// Whether this install can self-update at all.
    /// </summary>
    /// <remarks>
    /// Exposed so the page can grey out a check it would refuse, rather than
    /// offering a button that reports a failure having done nothing wrong — the
    /// choice <c>BdsUpdateService.CheckingIsEnabled</c> already makes for the
    /// Bedrock button. A non-versioned install has nowhere to put a new build, so
    /// there is nothing a check could lead to.
    /// </remarks>
    public bool SelfUpdateIsActive => _layout.IsVersionedInstall && _installer.Unavailable is null;

    /// <summary>Why <see cref="SelfUpdateIsActive"/> is false, in the words the page shows; null when it is true.</summary>
    public string? InactiveReason =>
        !_layout.IsVersionedInstall
            ? "Self-update is inactive on this install, because it is not running from a "
              + "versioned install directory. There is nowhere to put a new build, so "
              + "there is nothing a check could lead to."
            : _installer.Unavailable;

    /// <summary>Why the last update attempt failed, for the settings page.</summary>
    public string? LastFailureReason => _ledger.Load().LastFailureReason;

    /// <summary>The update ledger as it stands, for the pages that say whether updates are paused.</summary>
    public UpdateLedger Ledger => _ledger.Load();

    /// <summary>Resumes automatic updates that repeated failures paused (<c>D101</c>).</summary>
    public void ResumeAutomaticUpdates()
    {
        _ledger.ResumeAutomaticUpdates();
        RaiseChecked();
    }

    /// <summary>
    /// Raised after the channel has been read, from either path.
    /// </summary>
    /// <remarks>
    /// The button's own render happens anyway — Blazor re-renders after any
    /// handler returns — so this exists for the check <em>nobody clicked</em>: the
    /// one a channel change starts. Without it that check lands silently and the
    /// panel keeps the previous answer until something unrelated redraws, which is
    /// `T152` and `T164` exactly, and shipping it knowingly would be worse than
    /// having found it.
    ///
    /// Carries no payload for the reason <c>BdsUpdateService.Refreshed</c> does: a
    /// subscriber should re-read <see cref="Available"/> and
    /// <see cref="LastCheckedUtc"/> together rather than render a mixture. This
    /// object is a singleton, so every subscriber needs a <c>Dispose</c>.
    /// </remarks>
    public event Action? Checked;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Whatever the helper did while this process was not running.
        _ledger.ConsumeResult(_options.CurrentValue, _installer.ResultPath);

        // An in-process update's result from before the broker was switched on.
        _ledger.ConsumeResult(_options.CurrentValue);

        if (!_layout.IsVersionedInstall)
        {
            _logger.LogInformation(
                "Self-update is inactive: this is not a versioned install (running from {Directory}).",
                _layout.RunningDirectory);

            return;
        }

        if (_installer.Unavailable is { } unavailable)
        {
            _logger.LogWarning("Self-update is inactive: {Reason}", unavailable);
            return;
        }

        _installer.PrepareHelper();

        // Let the service finish coming up before reaching for the network.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // An update check must never be able to take the wrapper down.
                _logger.LogError(ex, "The update check failed.");
            }

            // Floored, and slept in slices. Floored because CheckInterval is
            // hand-editable and a zero turns this into an unthrottled poll of
            // somebody else's server — and that Delay sits outside the try/catch
            // whose comment promises a check can never take the wrapper down.
            // Sliced because the interval is settable from the Settings page now,
            // and a single Delay would make a correction wait out the mistake it
            // was correcting: set 168 hours by accident and the fix does not
            // apply for a week.
            var due = _options.CurrentValue.CheckInterval;

            if (due < MinimumCheckInterval)
            {
                due = MinimumCheckInterval;
            }

            var waited = TimeSpan.Zero;

            while (waited < due && !stoppingToken.IsCancellationRequested)
            {
                var slice = due - waited < CheckSlice ? due - waited : CheckSlice;
                await Task.Delay(slice, stoppingToken);
                waited += slice;

                var current = _options.CurrentValue.CheckInterval;
                due = current < MinimumCheckInterval ? MinimumCheckInterval : current;
            }
        }
    }

    internal async Task CheckAsync(CancellationToken ct)
    {
        var options = _options.CurrentValue;

        if (options.Mode == UpdateMode.Disabled)
        {
            return;
        }

        if (!await LookAsync(options, ct) || Available is not { } manifest)
        {
            return;
        }

        if (options.Mode == UpdateMode.Notify)
        {
            return;
        }

        if (manifest.NeedsReinstall())
        {
            // Not a failure: recording one would count towards the three that
            // pause automatic updates, for a build that was never going to apply.
            _logger.LogInformation(
                "Not applying {Identity}: it needs a reinstall rather than an update.", manifest.Identity);
            return;
        }

        var ledger = _ledger.Load();

        if (!ledger.CanAttempt(manifest.Identity, options.MaxConsecutiveFailures, DateTimeOffset.UtcNow, out var reason))
        {
            _logger.LogWarning("Not applying {Identity}: {Reason}", manifest.Identity, reason);
            return;
        }

        await ApplyAsync(manifest, options, ct);
    }

    /// <summary>
    /// Reads the channel and records what it said. Applies nothing.
    /// </summary>
    /// <returns>True when the feed was read, whatever it turned out to hold.</returns>
    /// <remarks>
    /// <b>The one place the feed is read</b>, shared by the scheduled check and the
    /// manual one — and the two are deliberately pinned to <em>different</em>
    /// answers rather than made to agree. The scheduled path may go on to apply;
    /// this one never does, whatever <c>Mode</c> says. That distinction is a test,
    /// because <c>T151</c> clause 6 was a regression introduced by a fix whose
    /// stated purpose was to stop two callers drifting: they stopped drifting and
    /// started sharing an answer one of them did not want. Sharing an
    /// implementation is good; sharing an answer is only good when both callers
    /// want that answer.
    /// </remarks>
    private async Task<bool> LookAsync(UpdateOptions options, CancellationToken ct)
    {
        if (Throttled(out _))
        {
            _logger.LogDebug("Skipping the update check: the channel host asked to be left alone.");
            return false;
        }

        var fetch = await _client.FetchManifestAsync(options, ct, _installer.FeedUrlTemplate);

        if (fetch.RetryAfter is { } wait)
        {
            _throttledUntilUtc = _time.GetUtcNow() + wait;
        }

        if (fetch.Manifest is not { } manifest)
        {
            // A failed read is not evidence the build went away, so Available is
            // deliberately left alone here. It used to be cleared, which meant a
            // single network blip took the Install now button off the page until
            // the next successful check — up to six hours later. LastCheckedUtc
            // only moves on a read that worked, for the same reason. What does
            // move is the record that this check failed, so the page can tell it
            // from one that never happened (`T196`).
            LastFailedCheck = new UpdateCheckFailure(
                _time.GetUtcNow(), fetch.Failure ?? "it gave nothing usable");
            RaiseChecked();
            return false;
        }

        // Read, so the page can say so even when the answer was "nothing newer" —
        // which is otherwise indistinguishable from the check not having happened.
        LastCheckedUtc = _time.GetUtcNow();
        LastFailedCheck = null;

        if (!manifest.IsNewerThanRunning())
        {
            Available = null;
            RaiseChecked();
            return true;
        }

        Available = manifest;

        _logger.LogInformation(
            "Update available on {Channel}: {Identity} (running {Running}).",
            options.Channel,
            manifest.Identity,
            BuildInfo.Identity);

        RaiseChecked();
        return true;
    }

    private void RaiseChecked() =>
        EventRaising.RaiseIndependently(
            Checked,
            ex => _logger.LogWarning(ex, "A listener threw while being told the update channel was read"));

    private bool Throttled(out TimeSpan remaining)
    {
        remaining = _throttledUntilUtc - _time.GetUtcNow();
        return remaining > TimeSpan.Zero;
    }

    /// <summary>
    /// Asks the channel now, because somebody asked (<c>D60</c>, <c>T163</c>).
    /// </summary>
    /// <remarks>
    /// <b>Never applies</b>, whatever <c>Mode</c> is set to. With
    /// <see cref="UpdateMode.Disabled"/> it still reads the feed, which is what
    /// that mode's own documentation has always promised and the code did not do:
    /// never checking on a schedule is a different statement from refusing to
    /// answer a person.
    ///
    /// The scheduled loop is untouched by this — it holds its own elapsed time and
    /// re-reads only <c>CheckInterval</c> — so a manual check neither resets,
    /// extends nor skips the next one.
    /// </remarks>
    public Task<ManualCheckResult> CheckNowAsync()
    {
        lock (_checkGate)
        {
            if (_checking is { IsCompleted: false } running)
            {
                return running;
            }

            if (!SelfUpdateIsActive)
            {
                return Task.FromResult(new ManualCheckResult(
                    ManualCheckOutcome.Inactive, TimeSpan.Zero, InactiveReason!));
            }

            if (Throttled(out var standoff))
            {
                return Task.FromResult(new ManualCheckResult(
                    ManualCheckOutcome.Throttled,
                    standoff,
                    $"The update server asked not to be contacted for another {Describe(standoff)}. "
                    + "Nothing was sent."));
            }

            var now = _time.GetUtcNow();
            var since = now - _lastManualCheckUtc;

            if (_lastManualCheckUtc != default && since < ManualCheckCooldown)
            {
                var left = ManualCheckCooldown - since;

                return Task.FromResult(new ManualCheckResult(
                    ManualCheckOutcome.TooSoon,
                    left,
                    $"Checked a moment ago. Try again in {Describe(left)}."));
            }

            _lastManualCheckUtc = now;

            // CancellationToken.None, for the reason RequestApply uses it: the
            // caller is a browser, and a second tab joining this task must not be
            // cancelled by the first one navigating away.
            _checking = RunManualCheckAsync();

            return _checking;
        }
    }

    private async Task<ManualCheckResult> RunManualCheckAsync()
    {
        var options = _options.CurrentValue;

        try
        {
            if (await LookAsync(options, CancellationToken.None).ConfigureAwait(false))
            {
                return new ManualCheckResult(ManualCheckOutcome.Checked, TimeSpan.Zero, "Checked.");
            }

            if (Throttled(out var standoff))
            {
                return new ManualCheckResult(
                    ManualCheckOutcome.Throttled,
                    standoff,
                    $"The update server asked not to be contacted again for {Describe(standoff)}.");
            }

            // The reason LookAsync recorded, so the banner says what the Last checked
            // row under it says (`T196`). The log stays the fallback for a record a
            // concurrent read has already cleared.
            var channel = options.Channel.ToString().ToLowerInvariant();

            return new ManualCheckResult(
                ManualCheckOutcome.Failed,
                TimeSpan.Zero,
                LastFailedCheck is { } failure
                    ? $"Could not read the {channel} channel: {failure.Reason}."
                    : $"Could not read the {channel} channel. The service log has the reason.");
        }
        catch (Exception ex)
        {
            // Nothing above this can throw today — the client catches its own — but
            // this task is handed to callers that may all have gone away, so an
            // escaping exception would be an unobserved one.
            _logger.LogError(ex, "The manual update check failed.");

            return new ManualCheckResult(
                ManualCheckOutcome.Failed, TimeSpan.Zero, "The check failed. The service log has the reason.");
        }
    }

    /// <summary>Whole seconds or whole minutes, because this goes in a sentence.</summary>
    private static string Describe(TimeSpan left)
    {
        if (left < TimeSpan.FromMinutes(1))
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(left.TotalSeconds));
            return seconds == 1 ? "1 second" : $"{seconds} seconds";
        }

        var minutes = Math.Max(1, (int)Math.Ceiling(left.TotalMinutes));
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }

    /// <remarks>
    /// Fires for every write to the section, so the channel is compared rather
    /// than assumed — saving the check interval must not ask Mojang's neighbour
    /// anything. The check goes through the same cooldown as the button, so
    /// flipping the channel back and forth is not a way around it.
    /// </remarks>
    private void OnOptionsChanged(UpdateOptions options, string? name)
    {
        if (options.Channel == _lastSeenChannel)
        {
            return;
        }

        var from = _lastSeenChannel;
        _lastSeenChannel = options.Channel;

        if (!SelfUpdateIsActive)
        {
            return;
        }

        _logger.LogInformation(
            "Update channel changed from {From} to {To}; checking the new channel.", from, options.Channel);

        _ = CheckNowAsync();
    }

    /// <summary>True while an update is being staged, drained for, or handed off.</summary>
    public bool IsApplying => _applying is { IsCompleted: false };

    /// <summary>True while a manual check is in flight.</summary>
    public bool IsChecking => _checking is { IsCompleted: false };

    /// <remarks>
    /// The options registration outlives this object otherwise: the monitor holds
    /// the callback, and the callback holds this.
    /// </remarks>
    public override void Dispose()
    {
        _optionsWatch?.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Starts an update and returns straight away.
    /// </summary>
    /// <remarks>
    /// What the "Install now" button posts to. Applying is no longer a quick
    /// hand-off — it warns players and drains every server first, which is
    /// minutes by design — so awaiting it on the request would hold the browser
    /// open for the whole countdown and then hand the drain a token that
    /// cancels the moment somebody navigates away.
    ///
    /// One at a time. Two clicks used to mean two staged updates racing each
    /// other into the same helper launch.
    /// </remarks>
    public bool RequestApply(UpdateManifest manifest, UpdateOptions options)
    {
        lock (_applyGate)
        {
            if (_applying is { IsCompleted: false })
            {
                return false;
            }

            _applying = Task.Run(async () =>
            {
                try
                {
                    await ApplyAsync(manifest, options, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Nothing is awaiting this task, so an escaping exception
                    // would be an unobserved one — logged here or lost entirely.
                    _logger.LogError(ex, "Applying update {Identity} failed.", manifest.Identity);
                }
            });

            return true;
        }
    }

    /// <summary>Why the drain has to wait, or null when nothing is part-way through a server.</summary>
    /// <remarks>
    /// <para>A Bedrock update in flight: stopping a server mid-update is read as a
    /// build that will not start, rolling back one that was fine.</para>
    ///
    /// <para>A claim (<see cref="InstanceClaim"/>), such as a sync: the drain's stop
    /// is the wrapper's own and goes through any claim, so the claims are asked about
    /// here. A sync stopped between its world being replaced and its Sync Areas being
    /// put back leaves the world replaced and the areas unplaced.</para>
    /// </remarks>
    internal string? DrainMustWait()
    {
        if (_bdsUpdates?.IsApplyingAnywhere == true)
        {
            return $"{string.Join(", ", _bdsUpdates.Applying)} is taking a Bedrock build.";
        }

        var claimed = _registry?.All
            .Select(s => s.Status)
            .Where(s => s.ClaimedFor is not null)
            .Select(s => ClaimText.Refusal(s.Name, s.ClaimedFor!))
            .ToList();

        return claimed is { Count: > 0 } ? string.Join(" ", claimed) : null;
    }

    /// <summary>Stages the build and hands off to the helper.</summary>
    public async Task<bool> ApplyAsync(UpdateManifest manifest, UpdateOptions options, CancellationToken ct = default)
    {
        // First, before the download: every caller arrives here, the Install now
        // POST included, and a build that needs a reinstall must stop no server
        // and stage nothing (`D114`).
        if (manifest.NeedsReinstall())
        {
            _logger.LogWarning(
                "Not applying {Identity}: it needs a reinstall rather than an update.", manifest.Identity);
            return false;
        }

        var staging = await _installer.StageAsync(manifest, options, ct);

        if (staging.State == UpdateStagingState.NotDownloaded)
        {
            _ledger.RecordFailure(manifest.Identity, staging.Reason, options);
            return false;
        }

        if (staging.State != UpdateStagingState.Staged)
        {
            return false;
        }

        _logger.LogWarning(
            "Applying update {Identity}. The service will stop and restart; if the new build does not " +
            "report healthy within {Timeout}s it will be rolled back automatically.",
            manifest.Identity,
            staging.HealthTimeoutSeconds);

        // Here, and not a line earlier. Everything above can decide not to
        // update — a download that would not verify, a 'current' link that does
        // not resolve, a helper that was never staged — and every one of those
        // returns false. Draining before those checks would take every server on
        // the box down for an update that then did not happen, which is the one
        // way this feature could be worse than not having it.
        //
        // And here rather than leaving it to the service stop, which is the
        // point of milestone 13.5: that stop has the service control manager
        // waiting on it and gets the short countdown of docs/design.md § 5.2. This one
        // has nothing waiting on it at all.
        // Checked here, immediately before the drain, rather than at the top:
        // staging takes a while and a Bedrock update or a sync can start during it.
        if (DrainMustWait() is { } why)
        {
            _logger.LogInformation("Deferring the wrapper update: {Why}", why);
            return false;
        }

        var drained = ManagedShutdownResult.Nothing;

        if (_shutdown is not null)
        {
            // ApplicationStopping, and pointedly not the caller's token. The
            // caller's is an HTTP request's — the operator pressed "Install now"
            // — and a browser that gives up must not cut the countdown short.
            // The host going down is the one thing that legitimately says the
            // courtesy is over, and DrainAsync reads cancellation as "stop
            // waiting" rather than "stop draining": the servers still get their
            // graceful stop, they just get it now.
            drained = await _shutdown
                .DrainAsync(
                    StopReason.WrapperUpdate,
                    options.PlayerWarning,
                    _lifetime?.ApplicationStopping ?? CancellationToken.None)
                .ConfigureAwait(false);

            if (drained.Drained > 0 || drained.Failed.Count > 0)
            {
                _logger.LogInformation(
                    "Drained {Drained} instance(s) before the update ({Failed} did not stop cleanly)",
                    drained.Drained, drained.Failed.Count);
            }
        }

        // Checked after the drain, because that is where the time goes and the
        // host can start stopping during it. Launching a detached helper now
        // would have it swap versions and issue a service stop in the middle of
        // an operating-system shutdown — strictly worse than not updating. The
        // staged build and the plan are both on disk, so the next start picks
        // this up.
        if (_lifetime?.ApplicationStopping.IsCancellationRequested == true)
        {
            _logger.LogWarning(
                "The host began shutting down mid-update; not launching the helper. {Identity} is "
                + "staged and will be applied after the next start.",
                manifest.Identity);

            return false;
        }

        int pid;

        try
        {
            pid = await _installer.LaunchAsync(manifest.Identity, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The one moment this can strand a machine. Every server is down by
            // now, and the helper — which is what restarts the service and
            // therefore the servers — never ran. Staging only asks
            // whether the file exists, and the broker copies its helper only
            // now, so a copy that is quarantined, truncated
            // by an interrupted staging, or denied execute by policy passes the
            // pre-flight check and fails here.
            //
            // So put back exactly what the drain took down. The update has not
            // happened and will be retried; an outage that outlives the attempt
            // that caused it is a different and much worse problem.
            _logger.LogError(
                ex, "The updater helper could not be launched; restarting the servers that were stopped");

            _ledger.RecordFailure(manifest.Identity, $"The updater helper would not start: {ex.Message}", options);

            if (_shutdown is not null && drained.Drained > 0)
            {
                await _shutdown.RestoreAsync(drained.Stopped, CancellationToken.None).ConfigureAwait(false);
            }

            return false;
        }

        _logger.LogInformation("Updater helper started as process {Pid}.", pid);

        return true;
    }

}
