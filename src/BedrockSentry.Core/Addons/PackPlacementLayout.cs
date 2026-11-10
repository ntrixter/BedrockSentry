namespace BedrockSentry.Core.Addons;

/// <summary>Which of a server's two pack locations a folder was found in.</summary>
/// <remarks>
/// <para><strong>Bedrock reads both, which is measured rather than assumed.</strong>
/// A pack this wrapper placed under the server root came back in BDS's own
/// <c>Pack Stack</c> (<c>T256</c>); packs placed only in a world's own
/// <c>behavior_packs</c>, registered in that world's list, load and work on a
/// real server with nothing in the root (<c>T271</c>). Reading one and calling
/// it <em>the server's packs</em> reported working add-ons as missing.</para>
///
/// <para><strong>Which one BDS prefers when a uuid is in both is unmeasured</strong>,
/// so nothing here picks: the diagnosis reports the pair and says it cannot tell.
/// A precedence invented here would be a rule the server never agreed to.</para>
/// </remarks>
public enum PackHome
{
    /// <summary>
    /// The world's own pack folder. Per world, travels in a world backup
    /// (<c>D97</c>), and goes when the world goes.
    /// </summary>
    World,

    /// <summary>
    /// The server root's pack folder, shared by every world on that server. What
    /// a Bedrock package ships its own packs in.
    /// </summary>
    ServerRoot,

    /// <summary>
    /// The server root's <c>development_behavior_packs</c>, which BDS also loads
    /// registered packs from (<c>T279</c>, measured on 1.26.51.1). BDS creates it
    /// empty, so anything in it was put there by a person; this wrapper reads it
    /// and never writes or deletes in it.
    /// </summary>
    Development,
}

/// <summary>
/// Where a pack of each kind lives, and which file switches it on.
/// </summary>
/// <remarks>
/// <para><strong>The two answers are one rule, and stating them separately is
/// what made a resource pack silent.</strong> The copy read the placement and
/// wrote into <c>resource_packs</c>; the registration did not ask at all and
/// hardcoded the behaviour list. Bedrock then loaded nothing, logged nothing —
/// not even the <em>"was not found and was ignored"</em> line a genuinely
/// missing behaviour pack earns — and the panel said the pack was on.
/// <c>PlacementLayoutTests</c> pins that every placed kind answers both
/// questions and that no two kinds share an answer.</para>
///
/// <para><strong><see cref="PackPlacement.Unknown"/> answers null at every
/// arm.</strong> A pack whose manifest declares no module kind this understands
/// cannot be steered anywhere, and picking a folder for it is a write to
/// somebody's server that looks like it worked. Each caller states its own
/// refusal rather than sharing one sentence, because what they are refusing
/// differs — a copy has nowhere to go, an enable has no list to write.</para>
/// </remarks>
public static class PackPlacementLayout
{
    /// <summary>The kinds of pack that have somewhere to go.</summary>
    /// <remarks>
    /// Iterated wherever both lists have to be read or walked, so adding a third
    /// kind is one entry here rather than a search for pairs of literals.
    /// </remarks>
    public static IReadOnlyList<PackPlacement> Placed { get; } =
        [PackPlacement.Behavior, PackPlacement.Resource];

    /// <summary>Every place a pack of a given kind can be, nearest first.</summary>
    /// <remarks>
    /// Iterated wherever packs are read, so another location is one entry here
    /// rather than a search for pairs of literals. The world comes first because
    /// it is where this wrapper writes.
    /// </remarks>
    public static IReadOnlyList<PackHome> Homes { get; } =
        [PackHome.World, PackHome.ServerRoot, PackHome.Development];

    /// <summary>The places Switch off may delete a pack's folder from.</summary>
    /// <remarks>
    /// <para><strong>Not the same question as <see cref="Homes"/>.</strong> These
    /// are the places this wrapper has written to: the world, and the server root
    /// earlier builds used (<c>T265</c>). Nothing here ever wrote into
    /// <see cref="PackHome.Development"/>, so a pack there is somebody's working
    /// copy, and switching it off takes away its registration and nothing
    /// else (<c>T279</c>).</para>
    /// </remarks>
    public static IReadOnlyList<PackHome> Swept { get; } =
        [PackHome.World, PackHome.ServerRoot];

    /// <summary>Where this wrapper puts a pack it copies out of the library.</summary>
    /// <remarks>
    /// <para><strong>The world, not the server root.</strong> The registration
    /// that switches a pack on is per world, so putting the pack beside it keeps
    /// the two together: a world backup carries its add-ons (<c>D97</c>; before
    /// it, only a stopped server's did), removing the world removes them, and an
    /// administrator's own packs stay clear of the 71 folders a Bedrock package
    /// ships in the root.</para>
    ///
    /// <para>Earlier builds wrote to the server root. Nothing migrates, and
    /// nothing needs to: both homes are read and both are swept, so a pack an
    /// older build placed is still listed, still switched off by its own button,
    /// and still deleted from where it actually is (<c>T265</c> — correcting a
    /// write does not correct the writes already made).</para>
    /// </remarks>
    public static PackHome Writes => PackHome.World;

    /// <summary>
    /// The NAME of the folder that holds packs of this kind, or null when this
    /// kind has none.
    /// </summary>
    /// <remarks>
    /// A name, not a path: the same name is used under a server root and under a
    /// world. Combine it with <see cref="DirectoryFor"/> rather than with a root
    /// directly — <c>Path.Combine(root, ServerFolder(x))</c> is the reader that
    /// saw one of the two locations and reported the other's packs as missing.
    /// </remarks>
    public static string? ServerFolder(PackPlacement placement) => placement switch
    {
        PackPlacement.Behavior => "behavior_packs",
        PackPlacement.Resource => "resource_packs",
        _ => null,
    };

    /// <summary>
    /// The file in a world that switches packs of this kind on, or null when
    /// this kind has none.
    /// </summary>
    /// <remarks>
    /// A world may hold both files, one, or neither, and a uuid in one says
    /// nothing about the other — so the two are read and written independently
    /// rather than as one list with a flag.
    /// </remarks>
    public static string? WorldListFileName(PackPlacement placement) => placement switch
    {
        PackPlacement.Behavior => "world_behavior_packs.json",
        PackPlacement.Resource => "world_resource_packs.json",
        _ => null,
    };

    /// <summary>What to call this kind of pack in a sentence.</summary>
    public static string Describe(PackPlacement placement) => placement switch
    {
        PackPlacement.Behavior => "behaviour pack",
        PackPlacement.Resource => "resource pack",
        _ => "pack",
    };

    /// <summary>What to call a group of them, for a heading.</summary>
    /// <remarks>
    /// Here rather than in the markup that renders it. The panel had its own
    /// two-arm version, which is one noun spelled in two places — and the arm
    /// it lacked was <see cref="PackPlacement.Unknown"/>, where it answered
    /// <em>resource packs</em> for a pack whose kind nothing declared.
    /// </remarks>
    public static string DescribeMany(PackPlacement placement) => placement switch
    {
        PackPlacement.Behavior => "Behaviour packs",
        PackPlacement.Resource => "Resource packs",
        _ => "Packs of an unrecognised kind",
    };

    /// <summary>
    /// The directory holding packs of this kind in this home, or null when there
    /// is no such directory to look in.
    /// </summary>
    /// <param name="instanceRoot">The server's own directory.</param>
    /// <param name="worldDirectory">
    /// This server's world, or null when it could not be resolved. A null world
    /// yields null for <see cref="PackHome.World"/> rather than a path under the
    /// root: guessing would read one home twice and report every pack in it
    /// twice over.
    /// </param>
    public static string? DirectoryFor(
        string instanceRoot, string? worldDirectory, PackHome home, PackPlacement placement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceRoot);

        if (ServerFolder(placement) is not { } folder)
        {
            return null;
        }

        return home switch
        {
            PackHome.World => worldDirectory is null
                ? null
                : Path.Combine(worldDirectory, folder),

            PackHome.ServerRoot => Path.Combine(instanceRoot, folder),

            // development_resource_packs was not measured (T279), so it is not
            // read: a resource pack there still reads as one the server does not
            // have, which is the louder of the two ways an unmeasured claim fails.
            PackHome.Development => placement == PackPlacement.Behavior
                ? Path.Combine(instanceRoot, "development_" + folder)
                : null,

            _ => throw new NotSupportedException(
                $"No directory for {home}. Add an arm here rather than letting a caller " +
                "read or delete packs in a place nobody wrote a path for."),
        };
    }

    /// <summary>Where a pack's folders are, in the order <see cref="Homes"/> lists them.</summary>
    /// <remarks>
    /// One statement for the row and for the finding about a pack in two places
    /// (<c>T299</c>), so the two cannot name the places differently.
    /// </remarks>
    public static string DescribeHomes(IEnumerable<PackHome> homes)
    {
        ArgumentNullException.ThrowIfNull(homes);

        var order = Homes.ToList();

        return string.Join(" and ", homes.Distinct().OrderBy(order.IndexOf).Select(DescribeHome));
    }

    /// <summary>Where a pack is, for a sentence on the page.</summary>
    /// <remarks>
    /// Said in terms an administrator can act on — which folder to open — rather
    /// than as a path, because the path is long, differs per instance, and the
    /// two are told apart by which of the two words applies.
    /// </remarks>
    public static string DescribeHome(PackHome home) => home switch
    {
        PackHome.World => "in this world",
        PackHome.ServerRoot => "in the server's own folder",
        PackHome.Development => "in the server's development folder",

        _ => throw new NotSupportedException(
            $"No description for {home}. Add an arm here rather than letting the page " +
            "name a place nobody wrote words for."),
    };
}
