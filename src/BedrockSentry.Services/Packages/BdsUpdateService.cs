using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Instances;
using BedrockSentry.Core.Packages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BedrockSentry.Services.Packages;

/// <summary>What one instance should do about the build Mojang is offering.</summary>
/// <param name="Available">
/// The offer itself, or null when the feed said nothing usable — a request that
/// failed, a channel with no entry, or a file name carrying no version.
/// </param>
/// <param name="Installed">
/// The version this server reported on its last startup, or null when it has
/// not run under the wrapper yet. Read from the console rather than from the
/// package it was installed from, because a hand-updated server stops matching
/// its package the moment somebody unzips over it.
/// </param>
/// <param name="Newer">
/// True only when both versions are known and the offer is genuinely ahead.
/// Not knowing is never treated as being behind.
/// </param>
/// <param name="Detail">One sentence for the page, always populated.</param>
public sealed record BdsUpdateStatus(
    BdsDownload? Available,
    Version? Installed,
    bool Newer,
    string Detail)
{
    /// <summary>Whether there is something an administrator could act on.</summary>
    public bool Actionable => Newer && Available is not null;
}

/// <summary>
/// Asks Mojang what it is offering, and brings a build into the package store
/// (<c>docs/design.md</c> § 11).
/// </summary>
/// <remarks>
/// Checking and downloading only. Applying a build to an instance — stopping
/// it, keeping its world and configuration, swapping the binaries, and putting
/// it back — is a separate step with its own failure modes, and mixing the two
/// would mean a network hiccup could leave a server half-updated.
///
/// The downloaded file lands in the package store under the name Mojang gave
/// it, which is the same folder an administrator can drop a zip into by hand
/// (<c>D19</c>). One store, one install path, and a downloaded build is not a
/// special case of anything.
/// </remarks>
/// <remarks>
/// <b>A singleton, and it has to be.</b> Everything this class knows — what
/// Mojang was last seen offering, when the feed was last read, why it last
/// failed — lives in its own fields, and the background checker and every page
/// have to be looking at the same ones. Registered the obvious way, as
/// <c>AddHttpClient&lt;BdsUpdateService&gt;()</c>, a typed client is
/// <em>transient</em>: the checker would refresh a copy nobody could see, and
/// every page load would start from "nothing has been seen yet" — which is
/// exactly how it was reported, as an update panel that reset itself whenever
/// you left it.
///
/// So the client comes from <see cref="IHttpClientFactory"/> per request rather
/// than being held for the lifetime of the service. A Windows Service runs for
/// months; a captured <see cref="HttpClient"/> holds its handler, and its DNS
/// answers, for just as long.
/// </remarks>
public sealed class BdsUpdateService
{
    /// <summary>The named client, so its traffic stays separable in diagnosis.</summary>
    public const string HttpClientName = "bds-updates";

    private readonly IHttpClientFactory _clients;
    private readonly ServerPackageStore _packages;
    private readonly IOptionsMonitor<BdsUpdateOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<BdsUpdateService> _log;
    private readonly ServerVersionLog _versions;

    private readonly SemaphoreSlim _fetchGate = new(1, 1);

    private IReadOnlyList<BdsDownload> _latest = [];
    private DateTimeOffset _fetchedUtc;
    private string? _lastFailure;

    public BdsUpdateService(
        IHttpClientFactory clients,
        ServerPackageStore packages,
        IOptionsMonitor<BdsUpdateOptions> options,
        TimeProvider time,
        ILogger<BdsUpdateService> log,
        ServerVersionLog versions)
    {
        _clients = clients;
        _packages = packages;
        _options = options;
        _time = time;
        _log = log;
        _versions = versions;
    }

    /// <summary>
    /// Raised after a check that may have changed what this service knows.
    /// </summary>
    /// <remarks>
    /// Exists because a scheduled check and an open page were two different
    /// things. <c>BdsUpdateChecker</c> re-reads the feed on its own timer, into
    /// the fields below, and nothing told a Settings page already on screen —
    /// its <em>Last checked</em> sat still across a scheduled check and moved
    /// only on reload (<c>T152</c>). <b>Check now</b> appeared to work for a
    /// reason that had nothing to do with this class: Blazor re-renders after
    /// any <c>@onclick</c> handler returns.
    ///
    /// Carries no payload on purpose. A subscriber's job is to re-read
    /// <see cref="Latest"/>, <see cref="LastCheckedUtc"/> and
    /// <see cref="LastFailure"/> together; handing it one of the three would
    /// invite it to render a mixture of old and new.
    ///
    /// <b>This object is a singleton for the life of the service</b>, so a
    /// subscriber that does not unsubscribe is kept alive for months. Every
    /// handler belongs to something with a <c>Dispose</c>.
    /// </remarks>
    public event Action? Refreshed;

    /// <summary>When the feed was last read successfully, or null if never.</summary>
    public DateTimeOffset? LastCheckedUtc => _fetchedUtc == default ? null : _fetchedUtc;

    /// <summary>Why the last check failed, or null when it did not.</summary>
    public string? LastFailure => _lastFailure;

    /// <summary>What Mojang was last seen offering.</summary>
    public IReadOnlyList<BdsDownload> Latest => _latest;

    /// <summary>
    /// Whether the install will contact Mojang at all.
    /// </summary>
    /// <remarks>
    /// Exposed so a page can grey out <b>Check now</b> rather than offer a
    /// button that quietly does nothing and then reports "Checked." — which is
    /// what it did, because <see cref="RefreshAsync"/> returns the cached list
    /// and leaves no failure behind to notice.
    /// </remarks>
    public bool CheckingIsEnabled => _options.CurrentValue.CheckForUpdates;

    private HttpClient NewClient()
    {
        var http = _clients.CreateClient(HttpClientName);

        // Mojang's endpoint is somebody else's, and a wedged request must not
        // hold a page render or a scheduler tick.
        http.Timeout = TimeSpan.FromSeconds(30);
        return http;
    }

    /// <summary>
    /// Reads the feed. Never throws: a third party being unreachable is a thing
    /// to report on a page, not a reason for a background service to fault.
    /// </summary>
    public async Task<IReadOnlyList<BdsDownload>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue;

        if (!options.CheckForUpdates)
        {
            return _latest;
        }

        await _fetchGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var http = NewClient();

            var json = await http.GetStringAsync(options.LinksUrl, cancellationToken).ConfigureAwait(false);
            var links = BdsDownloadLinks.Parse(json);

            if (links.Count == 0)
            {
                // Parsed but empty means the shape changed under us, which is a
                // different problem from the request failing and is worth
                // saying differently.
                _lastFailure =
                    "Mojang's download feed was reached but contained no Bedrock server build "
                    + "this wrapper recognises. The feed's format may have changed.";
                _log.LogWarning("{Failure}", _lastFailure);
                return _latest;
            }

            _latest = links;
            _fetchedUtc = _time.GetUtcNow();
            _lastFailure = null;

            _log.LogInformation(
                "Mojang is offering {Builds}",
                string.Join(", ", links.Select(l => $"{l.Channel} {l.Version?.ToString() ?? l.FileName}")));

            return _latest;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _lastFailure = $"Could not reach Mojang's download feed: {ex.Message}";
            _log.LogWarning(ex, "Could not reach Mojang's download feed");
            return _latest;
        }
        finally
        {
            _fetchGate.Release();

            // Here, rather than at each return: all three exits below the gate
            // changed something a page renders — the offer, the timestamp, or
            // the failure sentence. The one exit that changes nothing is the
            // disabled-checking return above, which never reaches the gate.
            //
            // After the release, not before: a handler is free to call back in.
            RaiseRefreshed();
        }
    }

    /// <remarks>
    /// <see cref="RefreshAsync"/> promises not to throw, and a subscriber is
    /// somebody else's code — typically a Blazor circuit that may have gone away
    /// mid-check. Raising the event bare would let a page navigating off fault
    /// the background checker, which is a worse bug than the one the event fixes.
    /// </remarks>
    private void RaiseRefreshed() =>
        EventRaising.RaiseIndependently(
            Refreshed,
            ex => _log.LogWarning(
                ex, "A listener threw while being told Mojang's download feed had been re-read"));

    /// <summary>What this instance should be told, given what was last seen.</summary>
    public BdsUpdateStatus StatusFor(InstanceSupervisor supervisor)
    {
        var settings = supervisor.Definition.ServerUpdate;

        // What the running server announced, and failing that what the log has —
        // either a version some earlier run announced, or one the wrapper installed
        // onto a stopped instance that has not run since.
        //
        // The fallback is what makes this survive a service restart. Without it the
        // announced version lived only in memory, so every restart left instances
        // the wrapper held a first-seen date for reading "start this server once so
        // it reports its own version" — and, worse, made a downgrade undetectable,
        // since nothing was left to compare an older build against (`T116`, `T64`).
        var installed =
            ParseInstalled(supervisor.Status.ServerVersion)
            ?? ParseInstalled(_versions.Read(supervisor.InstanceId)?.Version);

        if (settings.Mode is BdsUpdateMode.Off)
        {
            return new BdsUpdateStatus(null, installed, false, "Update checks are turned off for this server.");
        }

        if (settings.IsPinned)
        {
            return new BdsUpdateStatus(
                null, installed, false,
                $"Pinned to {settings.PinnedVersion.Trim()}. Clear the pin to follow the "
                + $"{settings.Channel} channel again.");
        }

        var offer = BdsDownloadLinks.For(_latest, settings.Channel);

        if (offer is null)
        {
            return new BdsUpdateStatus(
                null, installed, false,
                _lastFailure ?? $"Nothing has been seen on the {settings.Channel} channel yet.");
        }

        if (installed is null)
        {
            // The version comes off the console at startup, so a server that has
            // never run under this wrapper has not told us anything. Guessing it
            // is out of date would offer an update somebody may already have.
            return new BdsUpdateStatus(
                offer, null, false,
                $"Mojang is offering {offer.Version?.ToString() ?? offer.FileName}. "
                + "Start this server once so it reports its own version, and this page can "
                + "tell you whether that is newer.");
        }

        if (offer.Version is null)
        {
            return new BdsUpdateStatus(
                offer, installed, false,
                $"Mojang is offering {offer.FileName}, whose name carries no version this "
                + "wrapper can compare. Install it by hand if you want it.");
        }

        if (offer.Version <= installed)
        {
            return new BdsUpdateStatus(
                offer, installed, false, $"Up to date on {installed}.");
        }

        // "running" only when it is. A stopped server described as running the
        // build it is about to be moved off reads as a bug in the panel, and on a
        // stopped update it was one.
        var running = supervisor.Status.State is InstanceState.Running or InstanceState.Starting;

        return new BdsUpdateStatus(
            offer, installed, true,
            $"{offer.Version} is available. This server is "
            + $"{(running ? "running" : "on")} {installed}.");
    }

    /// <summary>A package in the store by file name, or null if it has gone.</summary>
    /// <remarks>
    /// Re-read at the point of use rather than a <see cref="ServerPackage"/>
    /// held from earlier: an automatic update carries a file name across a
    /// countdown that can run for hours, and somebody deleting that zip from the
    /// Settings page in the meantime is a thing the applier has to notice rather
    /// than fail on.
    /// </remarks>
    public ServerPackage? PackageFor(string fileName) => _packages.Find(fileName);

    /// <summary>
    /// Brings a build into the package store, or returns the copy already
    /// there.
    /// </summary>
    /// <remarks>
    /// Downloaded through a temp file and renamed into place, so an interrupted
    /// download cannot leave a half-written zip that lists as an installable
    /// package. There is no checksum to verify against — Mojang's feed publishes
    /// none — which is worth stating rather than leaving as an apparent
    /// oversight: the transport is HTTPS to Mojang's own host, and that is the
    /// whole of the guarantee.
    /// </remarks>
    public async Task<ServerPackage> DownloadAsync(
        BdsDownload download, CancellationToken cancellationToken = default)
    {
        if (_packages.Find(download.FileName) is { } already)
        {
            _log.LogInformation("{FileName} is already in the package store", download.FileName);
            return already;
        }

        Directory.CreateDirectory(_packages.Directory);
        var destination = Path.Combine(_packages.Directory, download.FileName);
        var temp = $"{destination}.{Guid.NewGuid():N}.part";

        _log.LogInformation("Downloading {Url}", download.Url);

        try
        {
            // No timeout on this one: a server zip is tens of megabytes and the
            // thirty seconds that stops a feed read wedging a page render would
            // abort a perfectly healthy download on a slow line.
            using var http = _clients.CreateClient(HttpClientName);
            http.Timeout = Timeout.InfiniteTimeSpan;

            using (var response = await http
                .GetAsync(download.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                await using var source = await response.Content
                    .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var file = File.Create(temp);

                await source.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, destination, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        _log.LogInformation("Downloaded {FileName} into the package store", download.FileName);

        return _packages.Find(download.FileName)
            ?? throw new InvalidOperationException(
                $"{download.FileName} was downloaded but the package store does not list it.");
    }

    private static Version? ParseInstalled(string? reported)
    {
        if (string.IsNullOrWhiteSpace(reported))
        {
            return null;
        }

        // "1.26.36.1" as the console reports it. Version.TryParse wants two to
        // four parts, which is what Bedrock uses.
        return Version.TryParse(reported.Trim(), out var version) ? version : null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
