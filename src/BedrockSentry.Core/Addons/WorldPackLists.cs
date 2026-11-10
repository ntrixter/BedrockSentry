namespace BedrockSentry.Core.Addons;

/// <summary>Where one registration was found, and what it says.</summary>
/// <param name="Placement">The list it is in, which is not always the list its
/// pack belongs in — see <see cref="WorldPackLists.Find"/>.</param>
public readonly record struct WorldPackRegistration(PackPlacement Placement, WorldPackEntry Entry);

/// <summary>Both of a world's pack lists, read together.</summary>
/// <remarks>
/// <para><strong>A world has one list per kind of pack and they are
/// independent.</strong> It may hold both files, one, or neither; a uuid in one
/// says nothing about the other; and each is its own load order, so a behaviour
/// pack cannot shadow a resource pack however the two are arranged. Treating
/// them as one list is what registered every pack in the behaviour file.</para>
///
/// <para><strong>Reading is all-or-nothing on purpose.</strong> A tab that acted
/// while one of the two could not be read would write a fresh list over a file
/// nobody has seen, and which file that is depends on which pack somebody
/// happens to press — so the refusal is the pair's rather than each list's.</para>
/// </remarks>
public sealed record WorldPackLists(WorldPackListRead Behavior, WorldPackListRead Resource)
{
    private static readonly WorldPackListRead NoFile =
        new(WorldPackListState.Absent, WorldPackList.Empty, null);

    /// <summary>A world with neither file, which is what a fresh one has.</summary>
    public static WorldPackLists Empty { get; } = new(NoFile, NoFile);

    /// <summary>
    /// How this kind's list read, or null when this kind has no list at all.
    /// </summary>
    public WorldPackListRead? Of(PackPlacement placement) => placement switch
    {
        PackPlacement.Behavior => Behavior,
        PackPlacement.Resource => Resource,
        _ => null,
    };

    /// <summary>
    /// This kind's list. Empty for a kind that has none, which is the only
    /// honest answer to "what is switched on" for a pack that cannot be.
    /// </summary>
    public WorldPackList For(PackPlacement placement) => Of(placement)?.List ?? WorldPackList.Empty;

    /// <summary>Every registration in both lists, behaviour first.</summary>
    public IEnumerable<WorldPackRegistration> All()
    {
        foreach (var placement in PackPlacementLayout.Placed)
        {
            foreach (var entry in For(placement).Entries)
            {
                yield return new WorldPackRegistration(placement, entry);
            }
        }
    }

    /// <summary>
    /// Find a uuid in whichever list holds it.
    /// </summary>
    /// <remarks>
    /// <strong>Deliberately not the list the pack belongs in.</strong> A world
    /// written before the placement was consulted has its resource packs in the
    /// behaviour list, and a switch-off that looked only in the right list would
    /// refuse — leaving a registration the panel reports as on and offers no way
    /// to remove. What it is asked here is where the entry <em>is</em>.
    /// </remarks>
    public WorldPackRegistration? Find(string packId)
    {
        foreach (var placement in PackPlacementLayout.Placed)
        {
            if (For(placement).EntryFor(packId) is { } entry)
            {
                return new WorldPackRegistration(placement, entry);
            }
        }

        return null;
    }

    /// <summary>The entry for a uuid, from either list.</summary>
    public WorldPackEntry? EntryFor(string packId) => Find(packId)?.Entry;

    /// <summary>Were both lists read well enough to write over?</summary>
    public bool Readable => PackPlacementLayout.Placed.All(p =>
        Of(p) is { State: WorldPackListState.Read or WorldPackListState.Absent });

    /// <summary>Why one of them could not be read, or null when both were.</summary>
    public string? Reason => PackPlacementLayout.Placed
        .Select(Of)
        .FirstOrDefault(r => r is not null
            && r.Value.State is not (WorldPackListState.Read or WorldPackListState.Absent))
        ?.Reason;
}
