using BedrockSentry.Core.Addons;
using BedrockSentry.Core.Commands;
using BedrockSentry.Core.Packages;
using BedrockSentry.Services.Addons;

namespace BedrockSentry.Services.Commands;

/// <summary>The add-ons a run for one instance loads, and the signature that says when to run again.</summary>
/// <param name="Signature">
/// Every pack it copies, as <c>uuid@version</c>, sorted: a pack switched on, off or to
/// another version changes it, and nothing else does.
/// </param>
/// <param name="Names">The script packs' names, for the Commands view.</param>
/// <param name="Packs">The packs to copy, in the order the world lists them, dependencies after.</param>
/// <param name="LevelDat">The world's <c>level.dat</c>, when it has one.</param>
public sealed record AddonRun(string Signature, IReadOnlyList<string> Names, IReadOnlyList<ThrowawayPack> Packs, string? LevelDat)
{
    /// <summary>The run an instance's add-ons need, or null when none of them can add a command.</summary>
    /// <remarks>
    /// <para>Only a script pack can register a command, so an instance whose switched-on
    /// packs have no script module needs no run: their blocks, items and entities are
    /// read from their files (<see cref="AddonDeclarations"/>). The companion pack is
    /// read too: it registers <c>listcord</c> from 1.1.0 (<c>D106</c>), and its version is
    /// part of the signature, so a new companion is read again.</para>
    ///
    /// <para><strong>A pack goes with every pack it depends on.</strong> BDS 1.26.52.3
    /// ignores a pack whose dependency is missing, scripts and all, so copying a script
    /// pack alone reads nothing from it.</para>
    /// </remarks>
    public static AddonRun? For(InstanceAddonState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var registered = state.Registrations.All()
            .Select(r => (Registration: r, Pack: Installed(state, r.Entry)))
            .Where(r => r.Pack is not null)
            .ToList();

        var scripts = registered
            .Where(r => r.Registration.Placement is PackPlacement.Behavior
                && r.Pack!.Manifest.HasScriptModule)
            .Select(r => r.Pack!)
            .ToList();

        if (scripts.Count == 0)
        {
            return null;
        }

        var chosen = new List<InstalledPack>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<InstalledPack>(scripts);
        while (pending.TryDequeue(out var pack))
        {
            if (!seen.Add(pack.Manifest.Uuid))
            {
                continue;
            }

            chosen.Add(pack);
            foreach (var dependency in pack.Manifest.PackDependencies)
            {
                if (Dependency(state, dependency) is { } found)
                {
                    pending.Enqueue(found);
                }
            }
        }

        // In the world's own order, so the throwaway loads them with the precedence the
        // instance gives them; a dependency the world does not list goes last.
        var order = registered.Select(r => r.Pack!.Manifest.Uuid).ToList();
        var ordered = chosen
            .OrderBy(p => order.FindIndex(u => string.Equals(u, p.Manifest.Uuid, StringComparison.OrdinalIgnoreCase)) is var i && i >= 0 ? i : int.MaxValue)
            .ToList();

        var packs = ordered
            .Where(p => p.Directory is not null && p.Manifest.Version.Version is not null && p.Manifest.Placement is not PackPlacement.Unknown)
            .Select(p => new ThrowawayPack(p.Directory!, p.Manifest.Uuid, p.Manifest.Version.Version!.Value, p.Manifest.Placement))
            .ToList();

        var signature = string.Join(',', packs
            .Select(p => $"{p.Uuid.ToLowerInvariant()}@{p.Version}")
            .Order(StringComparer.Ordinal));

        var levelDat = state.WorldDirectory is { } world && File.Exists(Path.Combine(world, "level.dat"))
            ? Path.Combine(world, "level.dat")
            : null;

        return new AddonRun(signature, [.. scripts.Select(p => p.Manifest.Name)], packs, levelDat);
    }

    // The folder the world switched on: the registered version, the world's own copy first.
    private static InstalledPack? Installed(InstanceAddonState state, WorldPackEntry entry)
    {
        if (entry.PackId is not { } uuid)
        {
            return null;
        }

        return state.Installed
            .Where(p => string.Equals(p.Manifest.Uuid, uuid, StringComparison.OrdinalIgnoreCase)
                && (entry.Version.Version is not { } wanted || p.Manifest.Version.Version == wanted))
            .OrderBy(p => p.Home == PackHome.World ? 0 : 1)
            .FirstOrDefault();
    }

    private static InstalledPack? Dependency(InstanceAddonState state, PackDependency dependency) =>
        dependency.Kind is not PackDependencyKind.OnPack
            ? null
            : state.Installed
                .Where(p => string.Equals(p.Manifest.Uuid, dependency.Target, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Manifest.Version.Version)
                .FirstOrDefault();
}

/// <summary>The two runs completion makes: one per server version, one per instance with add-ons.</summary>
/// <remarks>
/// What each sends is in <see cref="GrammarCapture"/>; this sends it, in the order a
/// reply decides: the first page says how many pages there are, and the pages say which
/// names to ask about.
/// </remarks>
public sealed class CompletionCapture(ThrowawayServer server, TimeProvider time)
{
    /// <summary>A version's commands, option lists and gamerules, read from a run of its package.</summary>
    public async Task<(CommandGrammar? Grammar, IReadOnlyList<ConsoleExchange>? Transcript, string? Reason)> VersionAsync(
        ServerPackage package, string executable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);

        var result = await server
            .RunAsync(new ThrowawayRequest(package, executable, [], null, VersionPlanAsync), cancellationToken)
            .ConfigureAwait(false);

        if (result.Transcript is not { } transcript)
        {
            return (null, null, result.Reason);
        }

        var grammar = GrammarCapture.Version(transcript, KeyOf(package), time.GetUtcNow(), result.Took);
        return grammar.Commands.Count == 0
            ? (null, transcript, "the server listed no commands")
            : (grammar, transcript, null);
    }

    /// <summary>The commands an instance's add-ons add to its version's own.</summary>
    public async Task<(IReadOnlyList<GrammarCommand>? Commands, IReadOnlyList<ConsoleExchange>? Transcript, TimeSpan Took, string? Reason)> AddonsAsync(
        ServerPackage package, string executable, AddonRun run, CommandGrammar vanilla, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(vanilla);

        var known = new HashSet<string>(
            vanilla.Commands.SelectMany(c => c.Aliases.Prepend(c.Name)), StringComparer.OrdinalIgnoreCase);

        var result = await server
            .RunAsync(
                new ThrowawayRequest(package, executable, run.Packs, run.LevelDat, (session, ct) => AddonPlanAsync(session, known, ct)),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Transcript is not { } transcript)
        {
            return (null, null, result.Took, result.Reason);
        }

        return (GrammarBuilder.AddedTo(GrammarCapture.Commands(transcript), vanilla), transcript, result.Took, null);
    }

    /// <summary>The name a package's commands are kept under: its version.</summary>
    public static string KeyOf(ServerPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return package.Version?.ToString() ?? package.VersionText ?? package.FileName;
    }

    private static async Task<IReadOnlyList<ConsoleExchange>> VersionPlanAsync(ThrowawaySession session, CancellationToken cancellationToken)
    {
        var transcript = new List<ConsoleExchange>(await PagesAsync(session, cancellationToken).ConfigureAwait(false));

        var names = GrammarCapture.ListedNames(transcript);
        transcript.AddRange(await session.ExchangeAsync([.. names.Select(GrammarCapture.HelpCommand)], cancellationToken).ConfigureAwait(false));
        transcript.AddRange(await session.ExchangeAsync([GrammarCapture.GameruleCommand], cancellationToken).ConfigureAwait(false));
        transcript.AddRange(await session.ExchangeAsync(GrammarCapture.ProbeCommands(), cancellationToken).ConfigureAwait(false));

        return transcript;
    }

    private static async Task<IReadOnlyList<ConsoleExchange>> AddonPlanAsync(
        ThrowawaySession session, IReadOnlySet<string> known, CancellationToken cancellationToken)
    {
        var transcript = new List<ConsoleExchange>(await PagesAsync(session, cancellationToken).ConfigureAwait(false));

        var names = GrammarCapture.ListedNames(transcript).Where(n => !known.Contains(n));
        transcript.AddRange(await session.ExchangeAsync([.. names.Select(GrammarCapture.HelpCommand)], cancellationToken).ConfigureAwait(false));

        return transcript;
    }

    private static async Task<IReadOnlyList<ConsoleExchange>> PagesAsync(ThrowawaySession session, CancellationToken cancellationToken)
    {
        var first = await session.ExchangeAsync([GrammarCapture.PageCommand(1)], cancellationToken).ConfigureAwait(false);

        var header = first.SelectMany(e => e.Messages).Select(HelpPages.Header).FirstOrDefault(h => h is not null)
            ?? throw new ThrowawayFailure("help 1 printed no page header");

        var rest = await session
            .ExchangeAsync([.. Enumerable.Range(2, Math.Max(0, header.Of - 1)).Select(GrammarCapture.PageCommand)], cancellationToken)
            .ConfigureAwait(false);

        return [.. first, .. rest];
    }
}
