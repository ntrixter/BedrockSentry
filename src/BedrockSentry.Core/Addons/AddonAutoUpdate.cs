namespace BedrockSentry.Core.Addons;

/// <summary>What auto-update does with one switched-on add-on at a start (<c>D112</c>).</summary>
public enum AddonAutoUpdateOutcome
{
    /// <summary>Nothing newer, held, not listed exactly once, or not readable: left alone, and said nowhere.</summary>
    Nothing,

    /// <summary>Moves to the library's newest.</summary>
    Move,

    /// <summary>Moves to the newest version that fits the server's build; a newer one needs a newer Bedrock.</summary>
    MoveBelowNewest,

    /// <summary>Every newer version needs a newer Bedrock than the server runs.</summary>
    NothingFits,

    /// <summary>
    /// A newer version is in the library, and this server has a copy of the pack the library
    /// does not hold. An update deletes the old copy, so it is kept and the pack stays.
    /// </summary>
    CopyNotInLibrary,
}

/// <summary>An <see cref="AddonAutoUpdateOutcome"/> and the versions it names.</summary>
/// <param name="To">The version moved to, for the two moves.</param>
/// <param name="Newest">The library's newest, passed over or blocked, for the other three.</param>
/// <param name="NewestNeeds">The Bedrock build <paramref name="Newest"/> asks for, when that is why it was passed over.</param>
public readonly record struct AddonAutoUpdateStep(
    AddonAutoUpdateOutcome Outcome, PackVersion? To, PackVersion? Newest, PackVersion? NewestNeeds)
{
    public static AddonAutoUpdateStep Nothing { get; } = new(AddonAutoUpdateOutcome.Nothing, null, null, null);
}

/// <summary>One version the library holds, with the Bedrock floor its manifest names.</summary>
public readonly record struct AddonCandidate(PackVersion Version, PackVersion? MinEngine);

/// <summary>
/// Whether a switched-on add-on moves to a newer version in the library at a start
/// (<c>D112</c>). The companion has its own rule, <see cref="CompanionUpgrade"/>.
/// </summary>
/// <remarks>
/// <para>It never moves a pack down, never switches one on, and leaves a world that lists
/// it twice alone, as <see cref="CompanionUpgrade"/> does. A held pack stays where its
/// administrator put it, as a pinned Bedrock build outranks the server's update mode.</para>
///
/// <para>A version whose <c>min_engine_version</c> is above the server's build is passed
/// over for the newest one that fits, since the server would refuse it (<c>D80</c>). An
/// unknown build checks nothing: a server that has never started has reported none, and
/// the Issues view says when a floor is not met.</para>
/// </remarks>
public static class AddonAutoUpdate
{
    /// <param name="listed">The pack's versions in the world's lists, one per entry.</param>
    /// <param name="library">Every version of it the library holds.</param>
    /// <param name="serverBuild">The Bedrock build the server last reported, or null.</param>
    /// <param name="held">Whether the administrator holds this pack at the version they chose.</param>
    /// <param name="libraryHoldsEveryCopy">
    /// Whether every version of it this server has is one the library holds, so the copy an
    /// update deletes can be put back (<c>T277</c>).
    /// </param>
    public static AddonAutoUpdateStep StepFor(
        IReadOnlyList<PackVersion?> listed,
        IReadOnlyList<AddonCandidate> library,
        PackVersion? serverBuild,
        bool held,
        bool libraryHoldsEveryCopy)
    {
        ArgumentNullException.ThrowIfNull(listed);
        ArgumentNullException.ThrowIfNull(library);

        if (held || listed.Count != 1 || listed[0] is not { } current)
        {
            return AddonAutoUpdateStep.Nothing;
        }

        var newer = library.Where(c => c.Version > current).OrderByDescending(c => c.Version).ToList();
        if (newer.Count == 0)
        {
            return AddonAutoUpdateStep.Nothing;
        }

        var top = newer[0];

        if (!libraryHoldsEveryCopy)
        {
            return new(AddonAutoUpdateOutcome.CopyNotInLibrary, null, top.Version, null);
        }

        var fits = newer.Where(c => Fits(c, serverBuild)).Select(c => (PackVersion?)c.Version).FirstOrDefault();

        return fits switch
        {
            null => new(AddonAutoUpdateOutcome.NothingFits, null, top.Version, top.MinEngine),
            { } to when to == top.Version => new(AddonAutoUpdateOutcome.Move, to, null, null),
            { } to => new(AddonAutoUpdateOutcome.MoveBelowNewest, to, top.Version, top.MinEngine),
        };
    }

    private static bool Fits(AddonCandidate candidate, PackVersion? serverBuild) =>
        candidate.MinEngine is not { } floor || serverBuild is not { } build || floor <= build;
}
