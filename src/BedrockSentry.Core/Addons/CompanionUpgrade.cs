namespace BedrockSentry.Core.Addons;

/// <summary>Whether a world's companion pack moves to the version BedrockSentry ships (<c>D106</c>).</summary>
/// <remarks>
/// <para>The companion is BedrockSentry's own, and the commands and relays the wrapper
/// expects arrive with its version, so a world follows the newest version in the
/// library at each start. Every other pack stays where its administrator put it.</para>
///
/// <para>It never switches the companion on (switched off is the administrator's
/// choice), never moves it down, and leaves a world that lists it twice alone: which
/// entry to move is not something to guess.</para>
/// </remarks>
public static class CompanionUpgrade
{
    /// <summary>The version to move to, or null to leave the world as it is.</summary>
    /// <param name="listed">The companion's versions in the world's behaviour list, one per entry.</param>
    /// <param name="newest">The newest companion in the library, or null when it has none.</param>
    public static PackVersion? MoveTo(IReadOnlyList<PackVersion?> listed, PackVersion? newest)
    {
        ArgumentNullException.ThrowIfNull(listed);

        if (listed.Count != 1 || listed[0] is not { } inWorld || newest is not { } shipped)
        {
            return null;
        }

        return inWorld.CompareTo(shipped) < 0 ? shipped : null;
    }
}

/// <summary>What happens to the companion's resource pack to bring it into line with the companion.</summary>
public enum CompanionResourcesAction
{
    /// <summary>It is where it should be, or which entry to change is not something to guess.</summary>
    None,

    /// <summary>The companion is on and the resources are not: switch them on.</summary>
    Enable,

    /// <summary>Both are on and the resources are older than the library's newest: move them.</summary>
    Update,

    /// <summary>The companion is off and the resources are on: switch them off.</summary>
    Disable,
}

/// <summary>A <see cref="CompanionResourcesAction"/> and, for Enable and Update, the version.</summary>
public readonly record struct CompanionResourcesStep(CompanionResourcesAction Action, PackVersion? Version)
{
    public static CompanionResourcesStep Nothing { get; } = new(CompanionResourcesAction.None, null);
}

/// <summary>
/// Whether the companion's resource pack follows the companion (<c>D109</c>): on while it
/// is on, at the library's newest version, and off once it is off.
/// </summary>
/// <remarks>
/// The companion does not name its resources as a dependency, so a server with only the
/// companion still reports; the wrapper keeps the two together instead. A world that lists
/// the resources twice is left alone, as <see cref="CompanionUpgrade"/> leaves the companion.
/// </remarks>
public static class CompanionResources
{
    /// <param name="companionOn">Whether the world's behaviour list names the companion.</param>
    /// <param name="listed">The resources' versions in the world's resource list, one per entry.</param>
    /// <param name="newest">The newest resources in the library, or null when it has none.</param>
    public static CompanionResourcesStep StepFor(bool companionOn, IReadOnlyList<PackVersion?> listed, PackVersion? newest)
    {
        ArgumentNullException.ThrowIfNull(listed);

        if (!companionOn)
        {
            return listed.Count == 1 ? new(CompanionResourcesAction.Disable, null) : CompanionResourcesStep.Nothing;
        }

        if (newest is not { } shipped)
        {
            return CompanionResourcesStep.Nothing;
        }

        return listed.Count switch
        {
            0 => new(CompanionResourcesAction.Enable, shipped),
            1 when listed[0] is { } inWorld && inWorld.CompareTo(shipped) < 0 => new(CompanionResourcesAction.Update, shipped),
            _ => CompanionResourcesStep.Nothing,
        };
    }
}
