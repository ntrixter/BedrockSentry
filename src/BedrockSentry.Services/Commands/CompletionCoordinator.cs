using System.Collections.Immutable;
using BedrockSentry.Core.Commands;
using BedrockSentry.Core.Packages;
using BedrockSentry.Services.Addons;
using BedrockSentry.Services.Instances;
using BedrockSentry.Services.Packages;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Commands;

/// <summary>Which build an instance is on, as completion needs it.</summary>
/// <param name="Version">The build, <c>1.26.52.3</c>, or null when it cannot be told.</param>
/// <param name="Preview">A Preview build, whose Mojang data is named <c>-preview.N</c>.</param>
/// <param name="Package">The package in the store with that version, or null when there is none.</param>
public sealed record InstanceBuild(string? Version, bool Preview, ServerPackage? Package);

/// <summary>Where a run for one version or one instance stands.</summary>
public enum CompletionJobState
{
    /// <summary>Nothing queued or running.</summary>
    Idle,

    /// <summary>Queued behind another run.</summary>
    Waiting,

    /// <summary>Running now.</summary>
    Reading,
}

/// <summary>What the Commands view asks of the runs: where each stands, and to run soon.</summary>
/// <remarks>
/// <see cref="CompletionCoordinator"/> is the one implementation; the seam lets a test put
/// a run in any state without starting one.
/// </remarks>
public interface ICompletionRuns
{
    /// <summary>The build an instance is on, and the package that holds it.</summary>
    InstanceBuild BuildOf(InstanceSupervisor supervisor);

    /// <summary>Where the run for one version stands.</summary>
    CompletionJobState VersionState(string version);

    /// <summary>Where the run for one instance's add-ons stands.</summary>
    CompletionJobState AddonState(string instanceId);

    /// <summary>Asks for a pass soon.</summary>
    void Nudge(bool retry = false, bool fetchVanilla = false);

    /// <summary>Whether Mojang's data is fetched at all: only while the BDS update check is on.</summary>
    bool FetchesVanillaData { get; }
}

/// <summary>Keeps completion's commands, add-on commands and Mojang's data current (<c>D105</c>).</summary>
/// <remarks>
/// <para><strong>One run at a time</strong>, each a throwaway server, so the machine
/// carries at most one server nobody plays on. A pass works out what is missing for the
/// instances as they are now and runs it: a version's commands, then each instance's
/// add-on commands.</para>
///
/// <para><strong>What wakes it.</strong> A BDS update check and an install fetch
/// Mojang's data and retry what failed, because those are when a version changes; an
/// add-on change re-reads that instance's add-ons; the console asking for something
/// missing wakes it without a retry. A pass also runs every ten minutes for an instance
/// whose version changed by hand, and a minute after the service starts so the
/// servers start first.</para>
///
/// <para><strong>Mojang's data is fetched only when the BDS update check is on.</strong>
/// The check is the switch an administrator has for this machine talking to anyone
/// about Bedrock, and the operator asked for the data to follow the update check rather
/// than be fetched on the fly.</para>
/// </remarks>
public sealed class CompletionCoordinator : BackgroundService, ICompletionRuns
{
    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private readonly InstanceRegistry _instances;
    private readonly ServerPackageStore _packages;
    private readonly BdsUpdateService _updates;
    private readonly BdsUpdateInstaller _installer;
    private readonly InstanceAddons _addons;
    private readonly CompletionCapture _capture;
    private readonly VanillaDataClient _vanilla;
    private readonly CompletionStore _store;
    private readonly ThrowawayServer _server;
    private readonly TimeProvider _time;
    private readonly ILogger<CompletionCoordinator> _log;

    private readonly SemaphoreSlim _wake = new(0);
    private int _retry;
    private int _fetch;
    private volatile string? _running;
    private ImmutableHashSet<string> _waiting = [];

    public CompletionCoordinator(
        InstanceRegistry instances,
        ServerPackageStore packages,
        BdsUpdateService updates,
        BdsUpdateInstaller installer,
        InstanceAddons addons,
        CompletionCapture capture,
        VanillaDataClient vanilla,
        CompletionStore store,
        ThrowawayServer server,
        TimeProvider time,
        ILogger<CompletionCoordinator> log)
    {
        _instances = instances;
        _packages = packages;
        _updates = updates;
        _installer = installer;
        _addons = addons;
        _capture = capture;
        _vanilla = vanilla;
        _store = store;
        _server = server;
        _time = time;
        _log = log;
    }

    /// <summary>Raised after a run or a fetch has changed what completion has.</summary>
    public event Action? Changed;

    /// <summary>Asks for a pass soon.</summary>
    /// <param name="retry">Run again what failed last time.</param>
    /// <param name="fetchVanilla">Ask the registry for Mojang's data.</param>
    public void Nudge(bool retry = false, bool fetchVanilla = false)
    {
        if (retry)
        {
            Interlocked.Exchange(ref _retry, 1);
        }

        if (fetchVanilla)
        {
            Interlocked.Exchange(ref _fetch, 1);
        }

        _wake.Release();
    }

    /// <summary>Where the run for one version stands.</summary>
    public CompletionJobState VersionState(string version) => StateOf(VersionKey(version));

    /// <summary>Where the run for one instance's add-ons stands.</summary>
    public CompletionJobState AddonState(string instanceId) => StateOf(AddonKey(instanceId));

    /// <inheritdoc/>
    public bool FetchesVanillaData => _updates.CheckingIsEnabled;

    /// <summary>The build an instance is on, and the package that holds it.</summary>
    public InstanceBuild BuildOf(InstanceSupervisor supervisor)
    {
        var (build, announced) = _updates.BuildOf(supervisor);
        var package = build is null ? null : _packages.List().FirstOrDefault(p => p.Version == build);

        // A Preview build announces itself as 1.26.60-beta21, which is not a version
        // (D53); the package it came from says Preview when this wrapper downloaded it.
        var preview = package?.Channel == BdsChannel.Preview
            || (announced is { Length: > 0 } && InstalledBuild.Parse(announced) is null);

        return new InstanceBuild(build?.ToString(), preview, package);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _updates.Refreshed += OnRefreshed;
        _installer.Installed += OnInstalled;
        _addons.Changed += OnAddonsChanged;

        try
        {
            await Task.Delay(StartDelay, _time, stoppingToken).ConfigureAwait(false);
            await _server.SweepAsync().ConfigureAwait(false);

            // The first pass retries what failed before the service last stopped.
            Interlocked.Exchange(ref _retry, 1);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PassAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "A completion pass failed; the next one tries again");
                }

                await _wake.WaitAsync(Interval, stoppingToken).ConfigureAwait(false);

                // Nudges that arrived together are one pass.
                while (_wake.Wait(0))
                {
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The service is stopping; a run in progress has already been killed.
        }
        finally
        {
            _updates.Refreshed -= OnRefreshed;
            _installer.Installed -= OnInstalled;
            _addons.Changed -= OnAddonsChanged;
        }
    }

    /// <summary>One pass: fetch if asked, then every run that is missing.</summary>
    internal async Task PassAsync(CancellationToken cancellationToken)
    {
        var retry = Interlocked.Exchange(ref _retry, 0) == 1;
        var fetch = Interlocked.Exchange(ref _fetch, 0) == 1;

        var instances = _instances.All
            .Select(s => (Supervisor: s, Build: BuildOf(s)))
            .ToList();

        if (fetch && FetchesVanillaData)
        {
            await FetchVanillaAsync(instances.Select(i => i.Build), cancellationToken).ConfigureAwait(false);
        }

        var plan = Plan(
            [.. instances.Select(i => new PlannedInstance(
                i.Supervisor.InstanceId, i.Supervisor.Definition.Executable, i.Build, AddonRun.For(_addons.Read(i.Supervisor))))],
            version => _store.ReadGrammar(version) is not null,
            version => _store.ReadGrammarFailure(version) is not null,
            _store.ReadAddons,
            retry);

        foreach (var instanceId in plan.Forget)
        {
            _store.ForgetAddons(instanceId);
            RaiseChanged();
        }

        var jobs = plan.Jobs
            .Select(job => (job.Key, Run: job.Run is null
                ? (Func<Task>)(() => ReadVersionAsync(job.Build, job.Executable, cancellationToken))
                : () => ReadAddonsAsync(job.InstanceId!, job.Build, job.Executable, job.Run, cancellationToken)))
            .ToList();

        _waiting = [.. jobs.Select(j => j.Key)];
        try
        {
            foreach (var (key, job) in jobs)
            {
                _running = key;
                _waiting = _waiting.Remove(key);
                RaiseChanged();

                await job().ConfigureAwait(false);
                RaiseChanged();
            }
        }
        finally
        {
            _running = null;
            _waiting = [];
        }
    }

    /// <summary>One instance as a pass sees it.</summary>
    internal sealed record PlannedInstance(string InstanceId, string Executable, InstanceBuild Build, AddonRun? Run);

    /// <summary>One run a pass makes: a version's when <see cref="Run"/> is null, else an instance's add-ons'.</summary>
    internal sealed record PlannedJob(string Key, InstanceBuild Build, string Executable, string? InstanceId, AddonRun? Run);

    /// <summary>What a pass runs, and which instances' stored add-on runs it drops.</summary>
    /// <remarks>
    /// <para>A version's run comes before any instance's add-on run, because the add-on run
    /// is told apart from the version's own commands. One run per version, whichever
    /// instance it is found on; an instance whose build or package is unknown has nothing
    /// to run.</para>
    ///
    /// <para><strong>A failure is retried only when asked</strong>: at the update check,
    /// an install, an add-on change and the first pass after a start. Retrying on the
    /// ten-minute pass would start a server that cannot start six times an hour.</para>
    ///
    /// <para>An add-on run is current when it ran with the same packs on the same build.
    /// An instance with no script pack left has its stored run dropped, so commands from a
    /// pack switched off stop completing.</para>
    /// </remarks>
    internal static (IReadOnlyList<PlannedJob> Jobs, IReadOnlyList<string> Forget) Plan(
        IReadOnlyList<PlannedInstance> instances,
        Func<string, bool> versionRead,
        Func<string, bool> versionFailed,
        Func<string, AddonCapture?> storedAddons,
        bool retry)
    {
        var jobs = new List<PlannedJob>();
        var forget = new List<string>();

        foreach (var group in instances.Where(i => i.Build is { Version: not null, Package: not null }).GroupBy(i => i.Build.Version!))
        {
            var first = group.First();
            if (!versionRead(group.Key) && (retry || !versionFailed(group.Key)))
            {
                jobs.Add(new PlannedJob(VersionKey(group.Key), first.Build, first.Executable, null, null));
            }
        }

        foreach (var instance in instances)
        {
            var stored = storedAddons(instance.InstanceId);

            if (instance.Run is null)
            {
                if (stored is not null)
                {
                    forget.Add(instance.InstanceId);
                }

                continue;
            }

            if (instance.Build is not { Version: { } version, Package: not null })
            {
                continue;
            }

            var current = stored is not null && stored.Signature == instance.Run.Signature && stored.BdsVersion == version;
            if (current && (stored!.Failure is null || !retry))
            {
                continue;
            }

            jobs.Add(new PlannedJob(AddonKey(instance.InstanceId), instance.Build, instance.Executable, instance.InstanceId, instance.Run));
        }

        return (jobs, forget);
    }

    private async Task ReadVersionAsync(InstanceBuild build, string executable, CancellationToken cancellationToken)
    {
        var (grammar, transcript, reason) = await _capture.VersionAsync(build.Package!, executable, cancellationToken).ConfigureAwait(false);

        if (grammar is not null && transcript is not null)
        {
            _store.WriteGrammar(grammar, transcript);
            _log.LogInformation(
                "Read Bedrock {Version}'s commands from a throwaway copy in {Seconds:0} s: {Commands} commands",
                build.Version, grammar.Took.TotalSeconds, grammar.Commands.Count);
            return;
        }

        _store.WriteGrammarFailure(build.Version!, new CaptureFailure(reason ?? "no reason was recorded", _time.GetUtcNow()));
        _log.LogWarning("Could not read Bedrock {Version}'s commands: {Reason}", build.Version, reason);
    }

    private async Task ReadAddonsAsync(
        string instanceId, InstanceBuild build, string executable, AddonRun run, CancellationToken cancellationToken)
    {
        var version = build.Version!;
        var vanilla = _store.ReadGrammar(version) ?? (version == ShippedGrammar.Version ? ShippedGrammar.Grammar : null);
        var now = _time.GetUtcNow();

        if (vanilla is null)
        {
            _store.WriteAddons(instanceId, new AddonCapture(run.Signature, version, now, run.Names, [],
                new CaptureFailure($"Bedrock {version}'s own commands have not been read, so its add-on commands cannot be told apart from them", now)), null);
            return;
        }

        var (commands, transcript, _, reason) = await _capture
            .AddonsAsync(build.Package!, executable, run, vanilla, cancellationToken)
            .ConfigureAwait(false);

        _store.WriteAddons(
            instanceId,
            commands is not null
                ? new AddonCapture(run.Signature, version, _time.GetUtcNow(), run.Names, commands)
                : new AddonCapture(run.Signature, version, _time.GetUtcNow(), run.Names, [], new CaptureFailure(reason ?? "no reason was recorded", _time.GetUtcNow())),
            transcript);
    }

    private async Task FetchVanillaAsync(IEnumerable<InstanceBuild> builds, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var before = _store.ReadVanillaState();

        try
        {
            var releases = await _vanilla.ListAsync(cancellationToken).ConfigureAwait(false);
            var published = releases.Select(r => r.Version).ToList();

            foreach (var build in builds.Where(b => b.Version is not null).DistinctBy(b => (b.Version, b.Preview)))
            {
                if (VanillaDataVersion.Match(build.Version!, build.Preview, published) is not { Chosen: { } chosen }
                    || _store.ReadVanilla(chosen) is not null)
                {
                    continue;
                }

                var lists = await _vanilla
                    .FetchAsync(releases.First(r => r.Version == chosen), cancellationToken)
                    .ConfigureAwait(false);
                _store.WriteVanilla(new VanillaData(chosen, _time.GetUtcNow(), lists));
                _log.LogInformation("Fetched Mojang's vanilla data {Release}", chosen);
            }

            _store.WriteVanillaState(new VanillaState(now, published, null));
        }
        catch (VanillaDataException ex)
        {
            _store.WriteVanillaState(before with { CheckedUtc = now, Failure = new CaptureFailure(ex.Message, now) });
            _log.LogWarning("Could not fetch Mojang's vanilla data: {Reason}", ex.Message);
        }

        RaiseChanged();
    }

    private CompletionJobState StateOf(string key) =>
        _running == key ? CompletionJobState.Reading
        : _waiting.Contains(key) ? CompletionJobState.Waiting
        : CompletionJobState.Idle;

    private static string VersionKey(string version) => "version:" + version;

    private static string AddonKey(string instanceId) => "addons:" + instanceId;

    private void OnRefreshed() => Nudge(retry: true, fetchVanilla: true);

    private void OnInstalled(string instanceId) => Nudge(retry: true, fetchVanilla: true);

    private void OnAddonsChanged(string instanceId) => Nudge(retry: true);

    private void RaiseChanged() =>
        Core.Diagnostics.EventRaising.RaiseIndependently(
            Changed, ex => _log.LogWarning(ex, "A listener threw while being told completion changed"));
}
