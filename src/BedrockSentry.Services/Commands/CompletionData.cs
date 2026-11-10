using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using BedrockSentry.Core.Addons;
using BedrockSentry.Core.Commands;
using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Addons;
using BedrockSentry.Services.Instances;

namespace BedrockSentry.Services.Commands;

/// <summary>Everything completion knows for one instance, and where each part came from.</summary>
/// <param name="Commands">The commands in use and why: the server's own, and the add-ons' after them.</param>
/// <param name="AddonCommands">The add-ons' commands, which <see cref="CommandsSource.Grammar"/> does not hold.</param>
/// <param name="AddonsLoaded">The running server started after the add-ons were switched on.</param>
/// <param name="Tag">Changes whenever anything above does; the browser's cache key.</param>
public sealed record CompletionView(
    CommandsSource Commands,
    VanillaSource VanillaSource,
    VanillaData? Vanilla,
    AddonSource Addons,
    IReadOnlyList<GrammarCommand> AddonCommands,
    bool AddonsLoaded,
    string Tag);

/// <summary>Puts together what completion offers one instance (<c>D105</c>).</summary>
/// <remarks>
/// <para>Reads only what the coordinator has stored and what the instance's files say;
/// it never runs a server or fetches anything. Something missing is reported on the
/// Commands view and nudges the coordinator, which decides whether to run.</para>
///
/// <para><strong>The commands are the closest version's.</strong> This server's own
/// version when a run has read it; otherwise the newest version read here that is not
/// newer than this server's; otherwise the list that ships with BedrockSentry. A newer
/// version's list would offer commands the server does not have yet, and an older one
/// misses only what was added since, so older is the safer side to be wrong on.</para>
/// </remarks>
public sealed class CompletionData(
    ICompletionRuns coordinator,
    CompletionStore store,
    InstanceAddons addons)
{
    private readonly ConcurrentDictionary<string, AddonIds> _declarations = new(StringComparer.Ordinal);

    /// <summary>What completion offers <paramref name="supervisor"/> now.</summary>
    public CompletionView For(InstanceSupervisor supervisor)
    {
        ArgumentNullException.ThrowIfNull(supervisor);

        var build = coordinator.BuildOf(supervisor);
        var commands = CommandsFor(build);
        var (vanillaSource, vanilla) = VanillaFor(build);

        var state = addons.Read(supervisor);
        var run = AddonRun.For(state);
        var ids = Declarations(state);
        var (addonSource, addonCommands, loaded) = AddonsFor(supervisor, build, state, run, ids);

        var tag = Tag(
            commands.Grammar.BdsVersion, commands.Grammar.CapturedUtc.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            commands.State.ToString(), vanilla?.Version, vanilla?.FetchedUtc.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            addonSource.State.ToString(), addonSource.ReadUtc?.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            run?.Signature, string.Join(',', ids.Blocks.Concat(ids.Items).Concat(ids.Entities).Concat(ids.Functions)), loaded.ToString());

        return new CompletionView(commands, vanillaSource, vanilla, addonSource, addonCommands, loaded, tag);
    }

    private CommandsSource CommandsFor(InstanceBuild build)
    {
        if (build.Version is not { } version)
        {
            var newest = NewestNotAfter(null);
            return new CommandsSource(CommandsSourceState.VersionUnknown, null, newest ?? ShippedGrammar.Grammar, newest is null);
        }

        if (store.ReadGrammar(version) is { } exact)
        {
            return new CommandsSource(CommandsSourceState.Exact, version, exact, Shipped: false);
        }

        var nearest = NewestNotAfter(version);
        var grammar = nearest ?? ShippedGrammar.Grammar;
        var shipped = nearest is null;

        switch (coordinator.VersionState(version))
        {
            case CompletionJobState.Reading:
                return new CommandsSource(CommandsSourceState.Reading, version, grammar, shipped);
            case CompletionJobState.Waiting:
                return new CommandsSource(CommandsSourceState.Waiting, version, grammar, shipped);
        }

        if (build.Package is null)
        {
            return new CommandsSource(CommandsSourceState.NoPackage, version, grammar, shipped);
        }

        if (store.ReadGrammarFailure(version) is { } failure)
        {
            return new CommandsSource(CommandsSourceState.Failed, version, grammar, shipped, failure.Reason);
        }

        // Nothing has run for it yet: the coordinator's next pass will.
        coordinator.Nudge();
        return new CommandsSource(CommandsSourceState.Waiting, version, grammar, shipped);
    }

    // The newest version read here that is not after `version`; any version when null.
    private CommandGrammar? NewestNotAfter(string? version)
    {
        var limit = version is not null && Version.TryParse(version, out var parsed) ? parsed : null;

        return store.GrammarVersions()
            .Select(v => (Text: v, Parsed: Version.TryParse(v, out var p) ? p : null))
            .Where(v => v.Parsed is not null && (limit is null || v.Parsed <= limit))
            .OrderByDescending(v => v.Parsed)
            .Select(v => store.ReadGrammar(v.Text))
            .FirstOrDefault(g => g is not null);
    }

    private (VanillaSource Source, VanillaData? Data) VanillaFor(InstanceBuild build)
    {
        var state = store.ReadVanillaState();
        var wanted = build.Version is { } v ? VanillaDataVersion.Wanted(v, build.Preview) : null;
        var match = build.Version is { } version && state.Published.Count > 0
            ? VanillaDataVersion.Match(version, build.Preview, state.Published)
            : null;

        // The release to use: the match when it has been fetched, otherwise the newest
        // fetched one that is not after what the server wants.
        var data = match?.Chosen is { } chosen ? store.ReadVanilla(chosen) : null;
        data ??= store.NewestVanillaNotAfter(wanted);

        if (data is null)
        {
            return state.Failure is { } failed
                ? (Source(VanillaSourceState.Failed, wanted, null, state, failed.Reason), null)
                : (Source(coordinator.FetchesVanillaData ? VanillaSourceState.Waiting : VanillaSourceState.CheckingOff, wanted, null, state), null);
        }

        var exact = wanted is not null && string.Equals(data.Version, wanted, StringComparison.Ordinal);
        var kind = state.Failure is not null && !exact ? VanillaSourceState.Failed
            : exact ? VanillaSourceState.Exact
            : VanillaSourceState.Behind;

        return (Source(kind, wanted, data, state, state.Failure?.Reason), data);
    }

    private static VanillaSource Source(
        VanillaSourceState kind, string? wanted, VanillaData? data, VanillaState state, string? reason = null) =>
        new(kind, wanted, data?.Version, data?.FetchedUtc, state.CheckedUtc,
            data?.Of("Block").Count ?? 0, data?.Of("Item").Count ?? 0, data?.Of("EntityType").Count ?? 0, reason);

    private (AddonSource Source, IReadOnlyList<GrammarCommand> Commands, bool Loaded) AddonsFor(
        InstanceSupervisor supervisor, InstanceBuild build, InstanceAddonState state, AddonRun? run, AddonIds ids)
    {
        var names = run?.Names ?? Registered(state).Select(p => p.Manifest.Name).ToList();

        if (run is null && ids.Count == 0)
        {
            return (new AddonSource(AddonSourceState.None, [], 0, null, ids), [], false);
        }

        var loaded = LoadedSince(supervisor, state);
        var settled = supervisor.Status.State is InstanceState.Running or InstanceState.Starting
            ? (loaded ? AddonSourceState.Loaded : AddonSourceState.NextRestart)
            : AddonSourceState.NextStart;

        if (run is null)
        {
            return (new AddonSource(settled, names, 0, null, ids), [], loaded);
        }

        switch (coordinator.AddonState(supervisor.InstanceId))
        {
            case CompletionJobState.Reading:
                return (new AddonSource(AddonSourceState.Reading, names, 0, null, ids), [], loaded);
            case CompletionJobState.Waiting:
                return (new AddonSource(AddonSourceState.Waiting, names, 0, null, ids), [], loaded);
        }

        if (build is not { Version: { } version })
        {
            return (new AddonSource(AddonSourceState.Failed, names, 0, null, ids,
                "this server has not reported its Bedrock version yet"), [], loaded);
        }

        if (build.Package is null)
        {
            return (new AddonSource(AddonSourceState.Failed, names, 0, null, ids,
                $"Bedrock {version}'s package is not in the package store, so there is nothing to run them in"), [], loaded);
        }

        var stored = store.ReadAddons(supervisor.InstanceId);
        if (stored is null || stored.Signature != run.Signature || stored.BdsVersion != version)
        {
            coordinator.Nudge();
            return (new AddonSource(AddonSourceState.Waiting, names, 0, null, ids), [], loaded);
        }

        if (stored.Failure is { } failure)
        {
            return (new AddonSource(AddonSourceState.Failed, names, 0, null, ids, failure.Reason), [], loaded);
        }

        return (new AddonSource(settled, names, stored.Commands.Count, stored.CapturedUtc, ids), stored.Commands, loaded);
    }

    // The add-ons count as loaded when the server's last start came after the world's
    // pack lists were last written: switching a pack on or off rewrites one of them.
    private static bool LoadedSince(InstanceSupervisor supervisor, InstanceAddonState state)
    {
        if (supervisor.Status.ReadyUtc is not { } ready || state.WorldDirectory is not { } world)
        {
            return false;
        }

        var changed = PackPlacementLayout.Placed
            .Select(p => Path.Combine(world, PackPlacementLayout.WorldListFileName(p)!))
            .Where(File.Exists)
            .Select(f => new DateTimeOffset(File.GetLastWriteTimeUtc(f), TimeSpan.Zero))
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();

        return ready >= changed;
    }

    private static IEnumerable<InstalledPack> Registered(InstanceAddonState state) =>
        state.Registrations.All()
            .Where(r => r.Placement is PackPlacement.Behavior)
            .Select(r => state.Installed.FirstOrDefault(p =>
                string.Equals(p.Manifest.Uuid, r.Entry.PackId, StringComparison.OrdinalIgnoreCase)
                && (r.Entry.Version.Version is not { } wanted || p.Manifest.Version.Version == wanted)))
            .Where(p => p is not null && !CompanionPack.Is(p.Manifest.Uuid))
            .Select(p => p!);

    // What the switched-on behaviour packs declare, read from their files once per set
    // of folders: a big add-on has thousands, and the console asks every few seconds.
    private AddonIds Declarations(InstanceAddonState state)
    {
        var packs = Registered(state).Where(p => p.Directory is not null).ToList();
        if (packs.Count == 0)
        {
            return AddonIds.None;
        }

        var key = string.Join('|', packs.Select(p => $"{p.Directory}@{p.Manifest.Version.Raw}"));
        return _declarations.GetOrAdd(key, _ => packs.Aggregate(AddonIds.None, (all, p) => all.With(Read(p))));

        static AddonIds Read(InstalledPack pack) =>
            AddonDeclarations.Read(pack.Files
                .Where(AddonDeclarations.Wants)
                .Select(f => (f, f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? TryRead(Path.Combine(pack.Directory!, f)) : null)));

        static string? TryRead(string path)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private static string Tag(params string?[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', parts))))[..16].ToLowerInvariant();
}
