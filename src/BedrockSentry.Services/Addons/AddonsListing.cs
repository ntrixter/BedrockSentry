using BedrockSentry.Core.Addons;

namespace BedrockSentry.Services.Addons;

/// <summary>Where one add-on stands on this server.</summary>
public enum AddonRowState
{
    /// <summary>The world switches it on, in the list for its kind.</summary>
    On,

    /// <summary>
    /// The world switches it on in the other kind's list, where Bedrock loads
    /// nothing and reports nothing. It keeps its Switch off button: switching it
    /// off and on again moves it.
    /// </summary>
    WrongList,

    /// <summary>Its files are on this server and the world does not switch it on.</summary>
    NotSwitchedOn,

    /// <summary>Only the library holds it.</summary>
    InLibrary,
}

/// <summary>What the details under a row say.</summary>
/// <param name="Where">Where this server's copies are, or the library.</param>
/// <param name="Uuid">The header uuid, which is the add-on's identity (<c>D79</c>).</param>
/// <param name="Scripts">Whether it runs scripts, and on which modules.</param>
/// <param name="Experimental">Whether its scripts need Beta APIs, as its manifest says.</param>
/// <param name="Needs">Its Bedrock floor and the packs it depends on.</param>
/// <param name="Authors">Who wrote it, or null when nothing says.</param>
/// <param name="Description">What it does, in its own words, or null.</param>
public sealed record AddonDetails(
    string Where,
    string Uuid,
    string Scripts,
    ExperimentalNeed Experimental,
    string Needs,
    string? Authors,
    string? Description);

/// <summary>One add-on on the tab: one uuid, whichever folders it has.</summary>
/// <param name="Version">The version shown: the one the world asks for, or the newest here.</param>
/// <param name="Parsed">The version Switch on would enable, or null when it cannot be read.</param>
/// <param name="Note">
/// The line under the name, only when something about the add-on is unusual: a
/// place other than this world, the wrong list, other versions, an unreadable
/// version. A pack switched on in this world has none, because being on is being
/// in the world.
/// </param>
/// <param name="Hidden">
/// Left out of the list by <see cref="UntouchedServerPacks.Hides"/>, and shown only
/// when somebody asks.
/// </param>
/// <param name="IconFile">The pack's <c>pack_icon.png</c>, or null when it has none.</param>
/// <param name="Choices">
/// The versions the row's picker offers, newest first (<c>D112</c>), or empty when it
/// offers none. <see cref="AddonVersionChoices.For"/> is the rule.
/// </param>
/// <param name="CanRemove">
/// Whether the row offers Remove, which deletes its folders from this server
/// (<c>T363</c>). Only for a pack this server has, does not switch on and that somebody
/// put here: Switch off keeps a folder the library cannot put back (<c>T277</c>), and
/// without this nothing on the panel deletes it. Never for the companion, which the
/// wrapper puts back, a pack <see cref="UntouchedServerPacks.Hides"/> leaves out, or a
/// pack only in the development folder, which nothing here writes or deletes in.
/// </param>
/// <param name="CanCopyToLibrary">
/// Whether the row offers Copy to library (<c>D78</c> (a)): this server has a version of
/// it the library lacks. <see cref="LibraryCopyIn.Offered"/> is the rule.
/// </param>
public sealed record AddonRow(
    string Uuid,
    string Name,
    PackPlacement Placement,
    string Version,
    PackVersion? Parsed,
    AddonRowState State,
    string? Note,
    bool IsCompanion,
    bool CanEnable,
    bool CanDisable,
    bool Hidden,
    AddonDetails Details,
    string? IconFile,
    bool CanRemove = false,
    IReadOnlyList<PackVersion>? Choices = null,
    bool CanCopyToLibrary = false)
{
    /// <summary>Whether the row's Version cell is a picker: more than one version to choose from.</summary>
    public bool CanChooseVersion => Choices is { Count: > 1 };

    /// <summary>What a button on this row names: the add-on and its kind.</summary>
    /// <remarks>
    /// The kind as well (<c>T309</c>): Bedrock names both of its chemistry packs
    /// <em>Chemistry</em>, and two buttons each labelled <em>Details for
    /// Chemistry</em> are one button to anybody listening to the page. A pack of no
    /// kind the tab knows is named alone, since "pack" tells nothing apart.
    /// </remarks>
    public string SpokenName => Placement is PackPlacement.Unknown
        ? Name
        : $"{Name}, {PackPlacementLayout.Describe(Placement)}";

    /// <summary>The Type column.</summary>
    public string TypeLabel => Placement switch
    {
        PackPlacement.Behavior => "Behaviour",
        PackPlacement.Resource => "Resource",
        _ => "Unknown",
    };

    /// <summary>The Status column's words.</summary>
    public string StateLabel => State switch
    {
        AddonRowState.On => "On",
        AddonRowState.WrongList => "Wrong list",
        AddonRowState.NotSwitchedOn => "Not switched on",
        AddonRowState.InLibrary => "In the library",

        _ => throw new InvalidOperationException($"No words are written for {State}."),
    };

    /// <summary>
    /// The Status column's badge. Amber for a pack somebody put here that is not
    /// loading; a pack a Bedrock package ships, shown on request, is off on
    /// purpose and stays neutral.
    /// </summary>
    public string BadgeClass => State switch
    {
        AddonRowState.On => "state-badge state-running",
        AddonRowState.WrongList => "state-badge state-faulted",
        AddonRowState.NotSwitchedOn => Hidden ? "state-badge" : "state-badge state-warning",
        AddonRowState.InLibrary => "state-badge",

        _ => throw new InvalidOperationException($"No badge is chosen for {State}."),
    };

    /// <summary>Whether the world switches it on, rightly or not.</summary>
    public bool IsSwitchedOn => State is AddonRowState.On or AddonRowState.WrongList;
}

/// <summary>Every add-on the tab can show, in the order it shows them.</summary>
/// <remarks>
/// <para><strong>ONE WALK.</strong> The rows, the hidden count and the names the
/// findings use all come from one pass over the same predicate, so the sentence
/// under the list cannot name a number the list disagrees with (<c>T126</c>) and a
/// finding cannot call a pack something its row does not (<c>T299</c>).</para>
///
/// <para><strong>The companion first, then by name.</strong> It is the pack this
/// wrapper ships and the one an administrator looks for; the rest are in an
/// order a person can scan.</para>
/// </remarks>
public sealed class AddonsListing
{
    /// <summary>The file name Bedrock shows a pack's icon from.</summary>
    public const string IconFileName = "pack_icon.png";

    private readonly Dictionary<string, AddonRow> _byUuid;

    private AddonsListing(IReadOnlyList<AddonRow> rows)
    {
        Rows = rows;
        _byUuid = new Dictionary<string, AddonRow>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            _byUuid.TryAdd(row.Uuid, row);
        }
    }

    /// <summary>A listing with nothing in it, before anything has been read.</summary>
    public static AddonsListing Empty { get; } = new([]);

    /// <summary>Every row, hidden ones included.</summary>
    public IReadOnlyList<AddonRow> Rows { get; }

    /// <summary>The first group: what the world switches on.</summary>
    public IReadOnlyList<AddonRow> SwitchedOn => [.. Rows.Where(r => !r.Hidden && r.IsSwitchedOn)];

    /// <summary>The second group: here or in the library, and not switched on.</summary>
    public IReadOnlyList<AddonRow> NotSwitchedOn => [.. Rows.Where(r => !r.Hidden && !r.IsSwitchedOn)];

    /// <summary>What <see cref="UntouchedServerPacks.Hides"/> left out.</summary>
    public IReadOnlyList<AddonRow> Hidden => [.. Rows.Where(r => r.Hidden)];

    /// <summary>What the list says about itself: an empty state, or the note about what it left out.</summary>
    public ListingText Text => UntouchedServerPacks.Describe(SwitchedOn.Count + NotSwitchedOn.Count, Hidden.Count);

    /// <summary>The second group's heading, which names the library only when every row in it is there alone.</summary>
    public string NotSwitchedOnHeading =>
        NotSwitchedOn.All(r => r.State == AddonRowState.InLibrary)
            ? "In the library, not switched on"
            : "Not switched on";

    /// <summary>The row for a uuid, hidden or not, or null.</summary>
    public AddonRow? Find(string uuid) => _byUuid.GetValueOrDefault(uuid);

    /// <summary>What a finding, the order list or a Needs field calls a pack (<c>T299</c>).</summary>
    public string SubjectOf(string uuid) => AddonText.Subject(uuid, Find(uuid)?.Name);

    /// <summary>Build the listing from what the tab read.</summary>
    /// <param name="exists">Whether a file exists. A seam, so the rules are tested without a disk.</param>
    public static AddonsListing Build(
        InstanceAddonState state, IReadOnlyList<LibraryPack> library, Func<string, bool>? exists = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(library);

        exists ??= File.Exists;

        var inLibrary = library.Select(p => p.Uuid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shelves = library
            .GroupBy(p => p.Uuid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<PackVersion>)[.. g.SelectMany(p => p.Versions).Select(v => v.Version)],
                StringComparer.OrdinalIgnoreCase);

        // Names and kinds first, so a Needs field can name a pack whose row comes later.
        var known = new Dictionary<string, (string Name, PackPlacement Placement)>(StringComparer.OrdinalIgnoreCase);

        foreach (var pack in state.Installed)
        {
            known.TryAdd(pack.Manifest.Uuid, (pack.Manifest.Name, pack.Manifest.Placement));
        }

        foreach (var pack in library)
        {
            known.TryAdd(pack.Uuid, (pack.Name, pack.Newest.Manifest.Placement));
        }

        string NameOf(string uuid) => known.TryGetValue(uuid, out var pack)
            ? AddonText.Needed(uuid, pack.Name, pack.Placement, withUuid: false)
            : AddonText.Needed(uuid, null, PackPlacement.Unknown, withUuid: false);

        // T284. A pack a switched-on pack depends on is listed, whoever shipped it.
        var needed = UntouchedServerPacks.NeededBy(
            state.Installed, uuid => state.Registrations.Find(uuid) is not null);

        var rows = new List<AddonRow>();

        // ONE ROW PER UUID, not per folder. A stock server root ships a folder per
        // Bedrock version of its own packs — measured at 71 folders over 16 uuids —
        // so a row per folder is a table of 54 identical vanilla lines.
        foreach (var group in state.Installed.GroupBy(p => p.Manifest.Uuid, StringComparer.OrdinalIgnoreCase))
        {
            var registration = state.Registrations.Find(group.Key);
            var entry = registration?.Entry;
            var registered = registration is not null;

            // The version shown is the one the world asked for when it asked for
            // one, because that is the folder BDS will load; otherwise the newest,
            // which is what switching it on would enable.
            var pack = group.FirstOrDefault(
                    p => entry is not null && p.Manifest.Version.Version == entry.Version.Version)
                ?? group.OrderByDescending(p => p.Manifest.Version.Version).First();

            // A pack listed in the OTHER kind's file is not on, whatever the list
            // says. Read off the SAME pack whose version this row displays, which
            // is also the one the diagnosis judges.
            var listedIn = registration?.Placement;
            var misplaced = listedIn is { } list
                && pack.Manifest.Placement != PackPlacement.Unknown
                && pack.Manifest.Placement != list;

            var homes = group.Select(p => p.Home).Distinct().ToList();
            var companion = CompanionPack.Is(group.Key);
            var canEnable = !registered && pack.Manifest.Version.Version is not null;

            // D112. Switch on enables the newest version offered, which is the library's
            // newest when the library holds every copy here.
            var choices = AddonVersionChoices.For(
                [.. group.Select(p => p.Manifest.Version.Version)],
                shelves.TryGetValue(group.Key, out var shelvedVersions) ? shelvedVersions : [],
                registered ? entry!.Version.Version : null,
                registered);
            var enables = !registered && !companion && choices.Count > 0 ? choices[0] : pack.Manifest.Version.Version;

            var hidden = UntouchedServerPacks.Hides(
                homes, inLibrary.Contains(group.Key), registered, needed.Contains(group.Key));

            var notes = new List<string>();

            if (misplaced)
            {
                notes.Add(listedIn == PackPlacement.Behavior
                    ? "Switched on in the behaviour list"
                    : "Switched on in the resource list");
            }

            // Being on is being in the world, so only somewhere else is worth a line.
            if (homes is not [PackHome.World])
            {
                notes.Add(Capitalised(PackPlacementLayout.DescribeHomes(homes)));
            }

            // Versions, not folders: one version in two places is the note above,
            // and counting it again here would call a copy another version.
            var others = group.Select(p => p.Manifest.Version.Version).Distinct().Count() - 1;

            if (others > 0)
            {
                notes.Add(others == 1 ? "1 other version installed" : $"{others} other versions installed");
            }

            if (pack.Manifest.Version.State != PackVersionState.Read)
            {
                notes.Add("Its version could not be read");
            }

            if (companion && canEnable)
            {
                // D93: one button, and it says what else it does.
                notes.Add(ScriptOutput.CompanionSwitchOnNote);
            }

            rows.Add(new AddonRow(
                group.Key,
                pack.Manifest.Name,
                pack.Manifest.Placement,
                VersionText(pack.Manifest.Version),
                enables,
                misplaced
                    ? AddonRowState.WrongList
                    : registered ? AddonRowState.On : AddonRowState.NotSwitchedOn,
                notes.Count == 0 ? null : string.Join(" · ", notes),
                companion,
                CanEnable: canEnable,
                CanDisable: registered,

                // D90. Built rather than skipped, so the count and the rows come off
                // one walk and the row is there to be shown the moment any of the
                // four conditions stops holding.
                Hidden: hidden,
                Details(
                    pack.Manifest,
                    AddonDetailText.Where(homes, FolderKind(pack.Directory) ?? pack.Manifest.Placement),
                    companion,
                    NameOf),
                pack.Directory is { } folder
                    && pack.Files.Any(f => string.Equals(f, IconFileName, StringComparison.OrdinalIgnoreCase))
                    ? Path.Combine(folder, IconFileName)
                    : null,
                CanRemove: !registered
                    && !hidden
                    && !companion
                    && !CompanionPack.IsResource(group.Key)
                    && homes.Any(h => h is PackHome.World or PackHome.ServerRoot),
                Choices: companion ? [] : choices,
                CanCopyToLibrary: LibraryCopyIn.Offered(
                    group.Key, group, v => shelvedVersions?.Contains(v) == true, hidden)));
        }

        // What the library has and this server does not. Offered here because the
        // shelf is install-wide and choosing from it is this tab's job. Never
        // hidden: a library pack with no folder here has no home to be in the wrong
        // one, and this row is the only way to switch it on.
        var here = state.Installed.Select(p => p.Manifest.Uuid).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var pack in library.Where(p => !here.Contains(p.Uuid)))
        {
            var newest = pack.Newest;
            var companion = CompanionPack.Is(pack.Uuid);
            var icon = Path.Combine(newest.Directory, IconFileName);

            rows.Add(new AddonRow(
                pack.Uuid,
                pack.Name,
                newest.Manifest.Placement,
                newest.Version.ToString(),
                newest.Version,
                AddonRowState.InLibrary,
                companion ? ScriptOutput.CompanionSwitchOnNote : null,
                companion,
                CanEnable: true,
                CanDisable: false,
                Hidden: false,
                Details(newest.Manifest, AddonDetailText.InTheLibrary, companion, NameOf),
                exists(icon) ? icon : null,
                Choices: companion ? [] : [.. pack.Versions.Select(v => v.Version).OrderDescending()]));
        }

        return new AddonsListing([
            .. rows
                .OrderBy(r => r.IsCompanion ? 0 : 1)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Uuid, StringComparer.OrdinalIgnoreCase),
        ]);
    }

    private static AddonDetails Details(
        PackManifest manifest, string where, bool companion, Func<string, string> nameOf) =>
        new(
            where,
            manifest.Uuid,
            AddonDetailText.Scripts(manifest),
            AddonDetailText.Experimental(manifest),
            AddonDetailText.Needs(manifest, nameOf),

            // The companion's manifest names no author, and its version is frozen
            // on every shelf that holds it (CompanionPackSeeder leaves a held
            // version alone), so the panel says who ships it.
            AddonDetailText.Authors(manifest) ?? (companion ? CompanionPack.Author : null),
            AddonDetailText.Description(manifest));

    /// <summary>
    /// Which kind's folder a pack was found in, from the folder's own name, or null
    /// when it is not one of the two. Where names the folder a pack is in, and a
    /// resource pack sitting in <c>behavior_packs</c> is in the behaviour folder,
    /// whatever its manifest says it is.
    /// </summary>
    private static PackPlacement? FolderKind(string? directory)
    {
        var parent = Path.GetFileName(Path.GetDirectoryName(directory?.TrimEnd('/', '\\')));

        return PackPlacementLayout.Placed
            .Where(p => string.Equals(PackPlacementLayout.ServerFolder(p), parent, StringComparison.OrdinalIgnoreCase))
            .Select(p => (PackPlacement?)p)
            .FirstOrDefault();
    }

    /// <summary>A version as the Version column shows it.</summary>
    private static string VersionText(PackVersionRead version) => version.State switch
    {
        PackVersionState.Read => version.Version!.Value.ToString(),
        PackVersionState.Malformed => version.Raw ?? "none",
        _ => "none",
    };

    private static string Capitalised(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}

/// <summary>The versions an Add-ons tab row offers (<c>D112</c>).</summary>
/// <remarks>
/// <para><strong>The library's versions only when it holds every copy this server
/// has.</strong> Moving to another version deletes the copy here, and Switch off keeps
/// one the library cannot put back (<c>T277</c>), so a server with a copy the library
/// lacks is offered only what it has; Remove (<c>T363</c>) clears the way.</para>
///
/// <para>A pack switched on with a copy the library lacks is offered nothing: an update
/// copies from the library and deletes the old copy, which is that one.</para>
/// </remarks>
public static class AddonVersionChoices
{
    /// <param name="installed">This server's copies' versions, null where one could not be read.</param>
    /// <param name="shelved">The library's versions of it.</param>
    /// <param name="current">The version the world switches on, when it does.</param>
    /// <param name="registered">Whether the world switches it on.</param>
    /// <returns>Newest first, without repeats.</returns>
    public static IReadOnlyList<PackVersion> For(
        IReadOnlyList<PackVersion?> installed, IReadOnlyList<PackVersion> shelved, PackVersion? current, bool registered)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(shelved);

        var libraryHoldsEvery = installed.All(v => v is { } version && shelved.Contains(version));

        IEnumerable<PackVersion> offered = libraryHoldsEvery
            ? shelved
            : registered
                ? current is { } on ? [on] : []
                : installed.OfType<PackVersion>();

        return [.. offered.Distinct().OrderDescending()];
    }

    /// <summary>
    /// Whether switching on <paramref name="version"/> first deletes a copy of another version
    /// here: only when there is one and the library holds every copy, so nothing is lost.
    /// </summary>
    public static bool ReplacesACopy(
        IReadOnlyList<PackVersion?> installed, Func<PackVersion, bool> shelved, PackVersion version)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(shelved);

        return installed.Any(v => v != version) && installed.All(v => v is { } copy && shelved(copy));
    }
}
