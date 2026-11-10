using BedrockSentry.Core.Addons;
using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Addons;

/// <summary>
/// Moves an instance's switched-on add-ons to the newest version in the library just
/// before the server launches, when the instance asks for it (<c>D112</c>; the rule is
/// <see cref="AddonAutoUpdate"/>).
/// </summary>
/// <remarks>
/// <para>It takes the Add-ons tab's own update path, which leaves the world's pack settings
/// file, the world's (<c>D81</c>), as it is. It runs with the process down
/// (<see cref="IBeforeLaunch"/>), so the lists it writes are not rewritten by a running
/// server (<c>D85</c>), and a running server picks a new version up at its next start.</para>
///
/// <para>The companion and its resources are left to <see cref="CompanionPackUpgrade"/>,
/// which moves them whatever this setting says. Only a pack the library holds moves: one
/// that is only on the server has nothing to move to.</para>
/// </remarks>
public sealed class AddonAutoUpdater(
    InstanceAddons addons,
    AddonLibrary library,
    Func<string, PackVersion?> serverBuild,
    TimeProvider time,
    ILogger<AddonAutoUpdater> log) : IBeforeLaunch
{
    public AddonAutoUpdater(
        InstanceAddons addons, AddonLibrary library, ServerVersionLog versions, TimeProvider time, ILogger<AddonAutoUpdater> log)
        : this(addons, library, id => PackVersion.Parse(versions.Read(id)?.Version).Version, time, log)
    {
    }

    /// <inheritdoc />
    public async Task BeforeLaunchAsync(InstanceSupervisor supervisor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(supervisor);

        var settings = supervisor.Definition.Addons;
        if (!settings.AutoUpdate)
        {
            return;
        }

        var state = addons.Read(supervisor);
        if (!state.CanAct)
        {
            return;
        }

        var build = serverBuild(supervisor.InstanceId);
        var shelf = library.List();

        foreach (var placement in PackPlacementLayout.Placed)
        {
            // Listed once per uuid in this list, in the list's order; each move rewrites
            // the list, so the state is read again before the next.
            var uuids = state.Registrations.For(placement).Entries
                .Select(e => e.PackId)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var uuid in uuids)
            {
                if (CompanionPack.Is(uuid) || CompanionPack.IsResource(uuid)
                    || shelf.FirstOrDefault(p => string.Equals(p.Uuid, uuid, StringComparison.OrdinalIgnoreCase))
                        is not { Versions.Count: > 0 } shelved)
                {
                    continue;
                }

                var list = state.Registrations.For(placement);
                var listed = list.Entries
                    .Where(e => string.Equals(e.PackId, uuid, StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.Version.Version)
                    .ToList();

                var shelvedVersions = shelved.Versions.Select(v => v.Version).ToHashSet();
                var step = AddonAutoUpdate.StepFor(
                    listed,
                    [.. shelved.Versions.Select(v => new AddonCandidate(v.Version, v.Manifest.MinEngineVersion.Version))],
                    build,
                    AddonHolds.IsHeld(settings.Holds, uuid),
                    LibraryHoldsEveryCopy(state, uuid, shelvedVersions));

                if (step.Outcome == AddonAutoUpdateOutcome.Nothing)
                {
                    continue;
                }

                var from = listed[0]!.Value;
                string? refused = null;

                if (step.To is { } to)
                {
                    var plan = AddonChangePlanner.Update(
                        list, placement, uuid, to, (u, v) => library.Find(u, v) is not null);

                    refused = plan.Change is { } change
                        ? await addons.ApplyAsync(supervisor, state, change, cancellationToken).ConfigureAwait(false)
                        : AddonText.Refusal(plan.Refusal);
                }

                Say(supervisor, AddonAutoUpdateText.Describe(shelved.Name, from, step, build, refused));

                if (step.To is not null && refused is null)
                {
                    state = addons.Read(supervisor);
                    if (!state.CanAct)
                    {
                        return;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Whether every version of a pack this server has is one the library holds. An update
    /// deletes the old copy, and Switch off keeps one the library cannot put back (<c>T277</c>),
    /// so auto-update does too.
    /// </summary>
    private static bool LibraryHoldsEveryCopy(InstanceAddonState state, string uuid, IReadOnlySet<PackVersion> shelved) =>
        state.Installed
            .Where(p => string.Equals(p.Manifest.Uuid, uuid, StringComparison.OrdinalIgnoreCase))
            .All(p => p.Manifest.Version.Version is { } v && shelved.Contains(v));

    private void Say(InstanceSupervisor supervisor, string notice)
    {
        log.LogInformation("{Instance}: {Notice}", supervisor.InstanceId, notice);
        supervisor.Console.Append(ConsoleOrigin.Notice, notice, time.GetUtcNow());
    }
}

/// <summary>What auto-update says on the console (<c>D112</c>), one line per add-on.</summary>
public static class AddonAutoUpdateText
{
    /// <param name="refused">Why a move was not made, or null when it was or none was tried.</param>
    public static string Describe(
        string name, PackVersion from, AddonAutoUpdateStep step, PackVersion? build, string? refused)
    {
        if (step.To is { } to && refused is not null)
        {
            return $"Add-on {name} stays at {from} for this start, not {to}: {refused}";
        }

        return step.Outcome switch
        {
            AddonAutoUpdateOutcome.Move => $"Add-on {name} updated from {from} to {step.To} for this start.",
            AddonAutoUpdateOutcome.MoveBelowNewest =>
                $"Add-on {name} updated from {from} to {step.To} for this start. {step.Newest} needs Bedrock {step.NewestNeeds} and this server runs {build}.",
            AddonAutoUpdateOutcome.NothingFits =>
                $"Add-on {name} stays at {from}: {step.Newest} needs Bedrock {step.NewestNeeds} and this server runs {build}.",
            AddonAutoUpdateOutcome.CopyNotInLibrary =>
                $"Add-on {name} stays at {from}: this server's copy is not in the add-on library, so updating to {step.Newest} would delete it. Remove it on the Add-ons tab to let it update.",
            AddonAutoUpdateOutcome.Nothing => "",

            _ => throw new ArgumentOutOfRangeException(nameof(step), step.Outcome, "No words are written for this outcome."),
        };
    }
}
