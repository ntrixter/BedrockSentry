using System.Text.Json;
using BedrockSentry.Core;
using BedrockSentry.Core.Broker;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Updates;
using BedrockSentry.Services.Updates;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Broker;

/// <summary>The broker's two update operations, as the dispatcher calls them.</summary>
public interface IBrokerUpdates
{
    /// <summary><see cref="BrokerOperation.StageUpdate"/>: fetch, verify and unpack the build the feed offers.</summary>
    Task<BrokerResponse> StageAsync(string identity, CancellationToken ct);

    /// <summary><see cref="BrokerOperation.ApplyUpdate"/>: launch the updater for the build staged last.</summary>
    BrokerResponse Apply(string identity);
}

/// <summary>Where the broker reads the feed and puts a build: the network and the install tree.</summary>
/// <remarks>A seam so every rule in <see cref="BrokerUpdates"/> runs in the Linux suite.</remarks>
public interface IBrokerUpdateSource
{
    /// <summary>The manifest the feed offers now, for the channel the main service chose.</summary>
    Task<ManifestFetch> FetchAsync(CancellationToken ct);

    /// <summary>Downloads, verifies and unpacks into a version folder made in this call; null on any failure.</summary>
    Task<string?> StageFreshAsync(UpdateManifest manifest, CancellationToken ct);
}

/// <summary>Starts the updater detached, so it outlives the broker it stops.</summary>
public interface IHelperLauncher
{
    /// <returns>The process id.</returns>
    int Start(string executable, string arguments, string workingDirectory);
}

/// <summary>
/// Self-update as the privilege broker runs it (milestone 18.5, `D46`).
/// </summary>
/// <remarks>
/// <para>The main service sends a build identity and nothing else. Everything the
/// update acts on is found here: the manifest from a feed the service cannot redirect,
/// the archive checked against that manifest's SHA-256, the epoch against this build's,
/// a version folder unpacked in this call, the updater copied from the running build
/// into the install tree, and every field of its plan.</para>
/// <para>No write lands in a folder the service's account can write. The download, the
/// helper, the plan, the log and the result are all under <c>update</c> in the install
/// root, which only an administrator and LocalSystem can change; a junction planted by
/// the service would otherwise turn a LocalSystem write into one of its choosing.</para>
/// </remarks>
public sealed class BrokerUpdates : IBrokerUpdates
{
    /// <summary>The folder under the install root that holds the broker's update files.</summary>
    public const string FolderName = "update";

    private readonly IBrokerUpdateSource _source;
    private readonly IHelperLauncher _launcher;
    private readonly InstallLayout _layout;
    private readonly Func<UpdateOptions> _options;
    private readonly string _mainService;
    private readonly string _brokerService;
    private readonly int _runningEpoch;
    private readonly ILogger _log;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _staging = new(1, 1);

    private string? _staged;

    /// <param name="options">The main service's update settings, read for the channel, the health port and the timeouts only.</param>
    /// <param name="runningEpoch">This build's install epoch (<c>D114</c>); a build above it needs a reinstall.</param>
    public BrokerUpdates(
        IBrokerUpdateSource source,
        IHelperLauncher launcher,
        InstallLayout layout,
        Func<UpdateOptions> options,
        string mainService,
        string brokerService,
        int runningEpoch,
        ILogger<BrokerUpdates> log)
    {
        _source = source;
        _launcher = launcher;
        _layout = layout;
        _options = options;
        _mainService = mainService;
        _brokerService = brokerService;
        _runningEpoch = runningEpoch;
        _log = log;
    }

    /// <summary>
    /// The feed the broker reads: the install policy's, which only an administrator can
    /// write, or the built-in one. Never the main service's configuration.
    /// </summary>
    public static string FeedFor(IInstallPolicy? policy) =>
        policy?.UpdateFeedUrlTemplate ?? new UpdateOptions().FeedUrlTemplate;

    /// <summary>The update folder for an install rooted at <paramref name="installRoot"/>.</summary>
    public static string FolderIn(string installRoot) => Path.Combine(installRoot, FolderName);

    /// <summary>Where the broker's updater leaves its outcome; the main service reads it at its next start.</summary>
    public static string ResultPathIn(string installRoot) => Path.Combine(FolderIn(installRoot), "result.json");

    private string Folder => FolderIn(_layout.InstallRoot);

    public async Task<BrokerResponse> StageAsync(string identity, CancellationToken ct)
    {
        lock (_gate)
        {
            _staged = null;
        }

        // One stage at a time: two would share the build's staging folder.
        if (!await _staging.WaitAsync(0, ct))
        {
            return BrokerResponse.Refusal("Another update is being staged.");
        }

        try
        {
            return await StageOneAsync(identity, ct);
        }
        finally
        {
            _staging.Release();
        }
    }

    private async Task<BrokerResponse> StageOneAsync(string identity, CancellationToken ct)
    {
        var fetch = await _source.FetchAsync(ct);

        if (fetch.Manifest is not { } manifest)
        {
            return new BrokerResponse(false, fetch.Failure ?? "The update feed could not be read.");
        }

        if (!string.Equals(manifest.Identity, identity, StringComparison.Ordinal))
        {
            return BrokerResponse.Refusal(
                $"The feed offers {manifest.Identity}, not {identity}. Check for updates again.");
        }

        if (manifest.NeedsReinstallFrom(_runningEpoch))
        {
            return BrokerResponse.Refusal(
                $"{identity} needs a reinstall (install epoch {manifest.InstallEpoch}, this build {_runningEpoch}): "
                + "run uninstall.ps1, then install.ps1 with its zip.");
        }

        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var download) || download.Scheme != Uri.UriSchemeHttps)
        {
            return BrokerResponse.Refusal($"The feed's download address for {identity} is not an https address.");
        }

        var current = _layout.ResolveCurrentVersionDirectory();
        var target = _layout.VersionDirectory(identity);

        // Without the running build known, a fresh unpack could delete it.
        if (current is null)
        {
            return new BrokerResponse(false, "'current' does not resolve to a version directory.");
        }

        if (SamePath(current, target))
        {
            return BrokerResponse.Refusal($"{identity} is the build running now.");
        }

        if (await _source.StageFreshAsync(manifest, ct) is not { } staged || !SamePath(staged, target))
        {
            return new BrokerResponse(false, $"{identity} could not be downloaded, verified and unpacked.");
        }

        lock (_gate)
        {
            _staged = identity;
        }

        _log.LogInformation("Staged {Identity} at {Directory}.", identity, staged);
        return new BrokerResponse(true, $"{identity} is staged.");
    }

    public BrokerResponse Apply(string identity)
    {
        lock (_gate)
        {
            if (!string.Equals(identity, _staged, StringComparison.Ordinal))
            {
                return BrokerResponse.Refusal($"{identity} is not the build this broker staged last.");
            }

            _staged = null;
        }

        var previous = _layout.ResolveCurrentVersionDirectory();

        if (previous is null)
        {
            return new BrokerResponse(false, "'current' does not resolve to a version directory, so there is nothing to roll back to.");
        }

        var target = _layout.VersionDirectory(identity);

        if (!Directory.Exists(target))
        {
            return new BrokerResponse(false, $"{identity} is no longer staged.");
        }

        var helper = CopyHelper(previous);

        if (helper is null)
        {
            return new BrokerResponse(false, $"The running build has no updater under '{Path.Combine(previous, "updater")}'.");
        }

        var plan = Plan(previous, target, identity);
        var planPath = Path.Combine(Folder, "plan.json");

        File.WriteAllText(planPath, JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }));

        var pid = _launcher.Start(helper, $"\"{planPath}\"", Path.GetDirectoryName(helper)!);

        _log.LogInformation("Started the updater for {Identity} as process {ProcessId}.", identity, pid);
        return new BrokerResponse(true, $"The updater is applying {identity}.", ProcessId: pid);
    }

    /// <summary>Every field from what the broker resolved; the main service supplies none of them.</summary>
    internal UpdatePlan Plan(string previous, string target, string identity)
    {
        var options = _options();

        return new UpdatePlan
        {
            ServiceName = _mainService,
            BrokerServiceName = _brokerService,
            InstallRoot = _layout.InstallRoot,
            PreviousVersionDirectory = previous,
            TargetVersionDirectory = target,
            TargetIdentity = identity,
            PreviousIdentity = BuildInfo.Identity,

            // Loopback by construction: only the port is read, as a number.
            HealthUrl = $"http://127.0.0.1:{(options.HealthPort is > 0 and <= 65535 ? options.HealthPort : new UpdateOptions().HealthPort)}{UpdateOptions.HealthPath}",
            HealthTimeoutSeconds = HealthTimeoutSeconds(options),
            StopTimeoutSeconds = Seconds(options.StopTimeout, 30, 900),
            LogPath = Path.Combine(Folder, "update.log"),
            ResultPath = ResultPathIn(_layout.InstallRoot),
        };
    }

    /// <summary>
    /// A fresh copy of the running build's updater under the install root's update
    /// folder: outside every version folder, so it can flip the junction, and in a
    /// folder the service cannot write, so what LocalSystem runs is what shipped.
    /// </summary>
    private string? CopyHelper(string previous)
    {
        var source = Path.Combine(previous, "updater");
        var executable = Path.Combine(source, "BedrockSentry.Updater.exe");

        if (!File.Exists(executable))
        {
            return null;
        }

        var destination = Path.Combine(Folder, "helper");

        if (Directory.Exists(destination))
        {
            Directory.Delete(destination, recursive: true);
        }

        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        return Path.Combine(destination, "BedrockSentry.Updater.exe");
    }

    /// <summary>The health wait the plan carries, bounded so a configuration cannot make it zero or endless.</summary>
    public static int HealthTimeoutSeconds(UpdateOptions options) => Seconds(options.HealthTimeout, 30, 600);

    private static int Seconds(TimeSpan value, int least, int most) =>
        (int)Math.Clamp(value.TotalSeconds, least, most);

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// <see cref="IBrokerUpdateSource"/> through <see cref="UpdateClient"/>, with the feed
/// from the install policy and only the channel from the main service's configuration.
/// </summary>
public sealed class BrokerUpdateSource(
    UpdateClient client, IInstallPolicy policy, Func<UpdateOptions> options, string downloadDirectory) : IBrokerUpdateSource
{
    public Task<ManifestFetch> FetchAsync(CancellationToken ct)
    {
        var asked = new UpdateOptions { Channel = options().Channel, FeedUrlTemplate = BrokerUpdates.FeedFor(policy) };

        return client.FetchManifestAsync(asked, ct);
    }

    public Task<string?> StageFreshAsync(UpdateManifest manifest, CancellationToken ct) =>
        client.StageAsync(manifest, ct, fresh: true, downloadDirectory);
}

/// <summary><see cref="IHelperLauncher"/> through <see cref="DetachedProcess"/>.</summary>
public sealed class DetachedHelperLauncher : IHelperLauncher
{
    public int Start(string executable, string arguments, string workingDirectory) =>
        DetachedProcess.Start(executable, arguments, workingDirectory);
}
