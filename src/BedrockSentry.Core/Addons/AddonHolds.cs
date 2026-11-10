using BedrockSentry.Core.Instances;

namespace BedrockSentry.Core.Addons;

/// <summary>
/// Which add-ons an instance holds at a version its administrator chose (<c>D112</c>), so
/// auto-update leaves them where they are.
/// </summary>
/// <remarks>
/// Choosing a version older than the library's newest holds the pack there; choosing the
/// newest, or switching the pack off, lets it go. A hold is set whether or not auto-update
/// is on, so turning it on later does not undo a choice made before.
/// </remarks>
public static class AddonHolds
{
    /// <summary>The version a pack is held at, or null when it is not held.</summary>
    public static PackVersion? HeldAt(IReadOnlyList<AddonHold> holds, string uuid)
    {
        ArgumentNullException.ThrowIfNull(holds);

        return holds.FirstOrDefault(h => string.Equals(h.Uuid, uuid, StringComparison.OrdinalIgnoreCase)) is { } hold
            ? PackVersion.Parse(hold.Version).Version
            : null;
    }

    /// <summary>Whether a pack is held at all, read or not.</summary>
    public static bool IsHeld(IReadOnlyList<AddonHold> holds, string uuid)
    {
        ArgumentNullException.ThrowIfNull(holds);

        return holds.Any(h => string.Equals(h.Uuid, uuid, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The holds after a pack is switched on or moved to <paramref name="chosen"/>.</summary>
    /// <param name="newest">The newest version the library holds of it, or null when it holds none.</param>
    /// <returns>The new list, or null when it is unchanged.</returns>
    public static IReadOnlyList<AddonHold>? AfterChoosing(
        IReadOnlyList<AddonHold> holds, string uuid, PackVersion chosen, PackVersion? newest)
    {
        ArgumentNullException.ThrowIfNull(holds);
        ArgumentException.ThrowIfNullOrWhiteSpace(uuid);

        var hold = newest is { } top && chosen < top;
        var others = holds.Where(h => !string.Equals(h.Uuid, uuid, StringComparison.OrdinalIgnoreCase)).ToList();

        if (!hold)
        {
            return others.Count == holds.Count ? null : others;
        }

        if (HeldAt(holds, uuid) == chosen)
        {
            return null;
        }

        others.Add(new AddonHold { Uuid = uuid, Version = chosen.ToString() });
        return others;
    }

    /// <summary>The holds after a pack is switched off, or null when it is unchanged.</summary>
    public static IReadOnlyList<AddonHold>? AfterSwitchingOff(IReadOnlyList<AddonHold> holds, string uuid)
    {
        ArgumentNullException.ThrowIfNull(holds);

        return IsHeld(holds, uuid)
            ? holds.Where(h => !string.Equals(h.Uuid, uuid, StringComparison.OrdinalIgnoreCase)).ToList()
            : null;
    }
}
