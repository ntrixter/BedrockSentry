using System.Net;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Updates;
using BedrockSentry.Services.Configuration;
using BedrockSentry.Services.Instances;
using BedrockSentry.Services.Updates;
using Microsoft.Extensions.Logging.Abstractions;

namespace BedrockSentry.Tests;

/// <summary>
/// docs/design.md § 12.3, milestone 13.5. Two rules there are load-bearing, and both
/// are the kind that look like details until the day they cost an outage.
/// </summary>
public sealed class WrapperUpdateDrainTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("bedrocksentry-wrapupd-").FullName;

    private readonly RecordingShutdown _shutdown = new();
    private readonly FailingDownloads _handler = new();

    private readonly UpdateService _service;

    public WrapperUpdateDrainTests()
    {
        var paths = new SentryPaths(_root);
        Directory.CreateDirectory(paths.Update);
        Directory.CreateDirectory(paths.Logs);

        var layout = new InstallLayout(_root);

        _service = new UpdateService(
            new UpdateClient(new HttpClient(_handler), layout, NullLogger<UpdateClient>.Instance),
            new UpdateLedgerStore(paths, NullLogger<UpdateLedgerStore>.Instance),
            layout,
            paths,
            new StaticOptionsMonitor<UpdateOptions>(new UpdateOptions()),
            NullLogger<UpdateService>.Instance,
            _shutdown);
    }

    public void Dispose()
    {
        _service.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static UpdateManifest Manifest() => new()
    {
        Identity = "0.3.0-build.99+abcdef0",
        Version = "0.3.0-build.99",
        CommitSha = "abcdef0000000000000000000000000000000000",
        Channel = "build",
        Sha256 = new string('a', 64),
        DownloadUrl = "https://example.invalid/bedrocksentry.zip",
    };

    /// <summary>
    /// The one that would make this feature worse than not having it: an update
    /// that decides not to happen must not have cost an outage first. Here the
    /// download fails, which is the first of several checks that return early.
    /// </summary>
    [Fact]
    public async Task An_update_that_does_not_happen_stops_no_servers()
    {
        var applied = await _service.ApplyAsync(Manifest(), new UpdateOptions());

        Assert.False(applied);
        Assert.Equal(0, _shutdown.Calls);
    }

    /// <summary>
    /// Applying warns players and drains, which takes minutes by design. Awaiting
    /// that on the request would hold the browser open for the whole countdown —
    /// and hand the drain a token that cancels the moment somebody navigates away.
    /// </summary>
    [Fact]
    public void Requesting_an_apply_returns_immediately()
    {
        Assert.True(_service.RequestApply(Manifest(), new UpdateOptions()));
    }

    // Two clicks used to mean two staged updates racing each other into the same
    // helper launch.
    [Fact]
    public void A_second_request_while_one_is_in_flight_is_refused()
    {
        _handler.HangForever = true;

        Assert.True(_service.RequestApply(Manifest(), new UpdateOptions()));
        Assert.True(_service.IsApplying);
        Assert.False(_service.RequestApply(Manifest(), new UpdateOptions()));

        _handler.Release();
    }

    private sealed class RecordingShutdown : IManagedServerShutdown
    {
        public int Calls { get; private set; }

        public IReadOnlyList<string> Restored { get; private set; } = [];

        public IReadOnlyList<string> Stops { get; set; } = [];

        public Task<ManagedShutdownResult> DrainAsync(
            StopReason reason, TimeSpan warning, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ManagedShutdownResult(Stops, []));
        }

        public Task RestoreAsync(
            IReadOnlyList<string> instanceIds, CancellationToken cancellationToken = default)
        {
            Restored = instanceIds;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingDownloads : HttpMessageHandler
    {
        private readonly SemaphoreSlim _held = new(0, 1);

        public bool HangForever { get; set; }

        public void Release() => _held.Release();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (HangForever)
            {
                await _held.WaitAsync(cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
