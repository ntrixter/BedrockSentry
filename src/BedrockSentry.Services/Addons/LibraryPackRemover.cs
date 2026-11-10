using BedrockSentry.Core.Addons;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Addons;

/// <summary>Where every server stands with every pack, read once.</summary>
/// <param name="Holders">The servers holding each uuid, keyed ignoring case.</param>
/// <param name="Unreadable">
/// Servers whose add-ons could not be read, by name: which packs they hold is not
/// known (<see cref="InstanceAddonState.WorldPacksKnown"/>).
/// </param>
public sealed record LibrarySurvey(
    IReadOnlyDictionary<string, IReadOnlyList<PackHolder>> Holders,
    IReadOnlyList<string> Unreadable)
{
    /// <summary>The servers holding one pack, or none.</summary>
    public IReadOnlyList<PackHolder> HoldersOf(string uuid) =>
        Holders.TryGetValue(uuid, out var holders) ? holders : [];
}

/// <summary>How a removal the page asked for ended.</summary>
public enum LibraryRemovalOutcomeKind
{
    /// <summary>Off the library, and off every server it was confirmed for.</summary>
    Removed,

    /// <summary>Still on the library; the sentence says why and what changed anyway.</summary>
    NotRemoved,

    /// <summary>
    /// The servers changed since the page asked, so the new plan goes back to the
    /// administrator rather than being carried out on a confirmation given for a
    /// different one.
    /// </summary>
    AskAgain,
}

/// <summary>What <see cref="LibraryPackRemover.RemoveAsync"/> did.</summary>
/// <param name="Plan">The fresh plan, for <see cref="LibraryRemovalOutcomeKind.AskAgain"/>.</param>
public sealed record LibraryRemovalOutcome(
    LibraryRemovalOutcomeKind Kind, string Sentence, LibraryRemovalPlan? Plan = null);

/// <summary>
/// Takes a pack off the add-on library, and off the stopped servers that have it once
/// that is confirmed (<c>D83</c>).
/// </summary>
/// <remarks>
/// Here rather than in the library, which does not know the servers it is shared by,
/// and rather than in the page, which the suite cannot reach.
/// </remarks>
public sealed class LibraryPackRemover(
    AddonLibrary library,
    InstanceAddons perInstance,
    InstanceRegistry registry,
    ILogger<LibraryPackRemover> logger)
{
    /// <summary>Reads every server once and says which packs each holds.</summary>
    /// <remarks>
    /// Once per server rather than once per pack: it walks every world, and a shelf of
    /// ten packs would otherwise do it ten times over. Registrations in both lists
    /// count, a world keeping its resource packs in a file of their own, and a uuid a
    /// list names twice counts once (<c>T260</c>).
    /// </remarks>
    public LibrarySurvey Survey()
    {
        var holders = new Dictionary<string, List<PackHolder>>(StringComparer.OrdinalIgnoreCase);
        var unreadable = new List<string>();

        foreach (var supervisor in registry.All)
        {
            var name = supervisor.Status.Name;
            var state = perInstance.Read(supervisor);

            if (!state.WorldPacksKnown)
            {
                unreadable.Add(name);
                continue;
            }

            var on = state.Registrations.All()
                .Select(r => r.Entry)
                .Where(e => e.IsUnderstood)
                .Select(e => e.PackId!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var copies = state.Installed
                .Where(p => p.Home is PackHome.World or PackHome.ServerRoot)
                .Select(p => p.Manifest.Uuid)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var stopped = LibraryRemoval.IsStopped(supervisor.Status.State);

            foreach (var uuid in on.Union(copies, StringComparer.OrdinalIgnoreCase))
            {
                if (!holders.TryGetValue(uuid, out var list))
                {
                    holders[uuid] = list = [];
                }

                list.Add(new PackHolder(
                    supervisor.InstanceId, name, stopped, on.Contains(uuid), copies.Contains(uuid)));
            }
        }

        return new LibrarySurvey(
            holders.ToDictionary(
                kv => kv.Key, kv => (IReadOnlyList<PackHolder>)kv.Value, StringComparer.OrdinalIgnoreCase),
            unreadable);
    }

    /// <summary>What removing one pack would do, as of <paramref name="survey"/>.</summary>
    public static LibraryRemovalPlan Plan(LibraryPack pack, LibrarySurvey survey)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(survey);

        return LibraryRemoval.Plan(pack.Uuid, pack.Name, survey.HoldersOf(pack.Uuid), survey.Unreadable);
    }

    /// <summary>
    /// Carries out the removal the page showed, and the administrator confirmed when
    /// it asked for confirmation.
    /// </summary>
    /// <param name="pack">The pack, every version of which goes.</param>
    /// <param name="shown">The plan the page showed.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <remarks>
    /// <para><strong>Asked again first.</strong> A server may have started, or been
    /// given the pack, since the page asked; a confirmation covers the plan it was
    /// given for, so a different plan goes back to the administrator.</para>
    ///
    /// <para><strong>The servers first, the library last.</strong> A server that
    /// cannot be changed stops the removal with the library copy still there, so
    /// nothing is lost that cannot be put back from it.</para>
    /// </remarks>
    public async Task<LibraryRemovalOutcome> RemoveAsync(
        LibraryPack pack, LibraryRemovalPlan shown, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(shown);

        var plan = Plan(pack, Survey());

        if (plan.Verdict == LibraryRemovalVerdict.Refuse)
        {
            return new LibraryRemovalOutcome(LibraryRemovalOutcomeKind.NotRemoved, plan.Sentence!);
        }

        if (!Same(plan, shown))
        {
            return new LibraryRemovalOutcome(
                LibraryRemovalOutcomeKind.AskAgain,
                plan.Sentence ?? $"Nothing has {pack.Name} any more.",
                plan);
        }

        var done = new List<string>();

        foreach (var instanceId in plan.RemoveFrom)
        {
            var supervisor = registry.Get(instanceId);

            if (await perInstance.RemovePackAsync(supervisor, pack.Uuid, cancellationToken)
                    .ConfigureAwait(false) is { } failed)
            {
                return new LibraryRemovalOutcome(
                    LibraryRemovalOutcomeKind.NotRemoved,
                    $"{pack.Name} is still in the library. {failed}"
                    + (done.Count > 0 ? $" It was already taken off {string.Join(", ", done)}." : ""));
            }

            done.Add(supervisor.Status.Name);
        }

        var kept = pack.Versions
            .Where(v => !library.Remove(pack.Uuid, v.Version))
            .Select(v => v.Version.ToString())
            .ToList();

        if (kept.Count > 0)
        {
            return new LibraryRemovalOutcome(
                LibraryRemovalOutcomeKind.NotRemoved,
                $"{pack.Name} {string.Join(", ", kept)} could not be deleted from the library; the service "
                + "log has the reason."
                + (done.Count > 0 ? $" It was taken off {string.Join(", ", done)}." : ""));
        }

        logger.LogInformation(
            "Removed {Pack} from the library and from {Count} server(s).", pack.Uuid, done.Count);

        return new LibraryRemovalOutcome(
            LibraryRemovalOutcomeKind.Removed,
            done.Count == 0
                ? $"Removed {pack.Name} from the library."
                : $"Removed {pack.Name} from the library and from {string.Join(", ", done)}.");
    }

    /// <summary>One plan compared with another by what they would do and say.</summary>
    private static bool Same(LibraryRemovalPlan a, LibraryRemovalPlan b) =>
        a.Verdict == b.Verdict
        && string.Equals(a.Sentence, b.Sentence, StringComparison.Ordinal)
        && a.RemoveFrom.SequenceEqual(b.RemoveFrom, StringComparer.OrdinalIgnoreCase);
}
