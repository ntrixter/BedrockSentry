using BedrockSentry.Core.Addons;
using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Addons;

/// <summary>
/// Moves a world's companion pack to the version BedrockSentry ships, just before the
/// server launches (<c>D106</c>; the rule is <see cref="CompanionUpgrade"/>), and brings
/// its resources into line with it (<c>D109</c>, <see cref="CompanionResources"/>).
/// </summary>
/// <remarks>
/// <para>A world keeps the version it was given, and the panel has no control that
/// moves a pack to another version, so without this a server set up before a new
/// companion would never get its commands.</para>
///
/// <para>It takes the Add-ons tab's own update path, which turns script output on and
/// leaves the world's pack settings file, which is the world's (<c>D81</c>), as it is.
/// It runs with the process down (<see cref="IBeforeLaunch"/>), so the world list it
/// writes is not rewritten by a running server (<c>D85</c>).</para>
/// </remarks>
public sealed class CompanionPackUpgrade(
    InstanceAddons addons, AddonLibrary library, TimeProvider time, ILogger<CompanionPackUpgrade> log) : IBeforeLaunch
{
    /// <inheritdoc />
    public async Task BeforeLaunchAsync(InstanceSupervisor supervisor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(supervisor);

        var state = addons.Read(supervisor);
        if (!state.CanAct)
        {
            return;
        }

        var list = state.Registrations.For(PackPlacement.Behavior);
        var listed = list.Entries
            .Where(e => e.PackId is { } id && CompanionPack.Is(id))
            .Select(e => e.Version.Version)
            .ToList();
        var newest = library.List().FirstOrDefault(p => CompanionPack.Is(p.Uuid)) is { Versions.Count: > 0 } shelved
            ? shelved.Newest.Version
            : (PackVersion?)null;

        if (CompanionUpgrade.MoveTo(listed, newest) is not { } to)
        {
            await FollowAsync(supervisor, cancellationToken).ConfigureAwait(false);
            return;
        }

        var from = listed[0]!.Value;
        var plan = AddonChangePlanner.Update(
            list, PackPlacement.Behavior, CompanionPack.Uuid, to, (u, v) => library.Find(u, v) is not null);

        var refused = plan.Change is { } change
            ? await addons.ApplyAsync(supervisor, state, change, cancellationToken).ConfigureAwait(false)
            : AddonText.Refusal(plan.Refusal);

        var notice = refused is null
            ? $"Companion pack moved from {from} to {to} for this start."
            : $"Companion pack stays at {from} for this start, not {to}: {refused}";

        log.LogInformation("{Instance}: {Notice}", supervisor.InstanceId, notice);
        supervisor.Console.Append(ConsoleOrigin.Notice, notice, time.GetUtcNow());

        await FollowAsync(supervisor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The companion's resources follow it (<c>D109</c>), so a server set up before they
    /// shipped gets them at its next start; a refusal is said on the console.
    /// </summary>
    private async Task FollowAsync(InstanceSupervisor supervisor, CancellationToken cancellationToken)
    {
        if (await addons.FollowCompanionAsync(supervisor, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            log.LogInformation("{Instance}: {Notice}", supervisor.InstanceId, refused);
            supervisor.Console.Append(ConsoleOrigin.Notice, refused, time.GetUtcNow());
        }
    }
}

/// <summary>
/// Every piece of work a launch does first, in order. One that throws is logged and
/// the next still runs, as <see cref="IBeforeLaunch"/> asks of the launch itself.
/// </summary>
public sealed class BeforeLaunchSteps(IReadOnlyList<IBeforeLaunch> steps, ILogger<BeforeLaunchSteps> log) : IBeforeLaunch
{
    /// <inheritdoc />
    public async Task BeforeLaunchAsync(InstanceSupervisor supervisor, CancellationToken cancellationToken)
    {
        foreach (var step in steps)
        {
            try
            {
                await step.BeforeLaunchAsync(supervisor, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "{Step} failed before launching {InstanceId}", step.GetType().Name, supervisor.InstanceId);
            }
        }
    }
}
