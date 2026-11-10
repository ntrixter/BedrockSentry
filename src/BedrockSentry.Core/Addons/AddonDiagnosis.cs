namespace BedrockSentry.Core.Addons;

/// <summary>Why an add-on that looks enabled does nothing.</summary>
/// <remarks>
/// <para><strong>Bedrock reports almost none of this, and the one thing it does
/// report is overloaded.</strong> Measured on BDS 1.26.51.1: <em>"was not found
/// and was ignored"</em> is the identical sentence for a pack that is genuinely
/// absent, a pack whose registered version no longer matches, and a pack
/// rejected for its engine floor. So a panel relaying that line tells an
/// administrator their pack is missing while it sits in the folder — which is
/// why the cause is decided here rather than read off the server.</para>
/// </remarks>
public enum AddonFindingKind
{
    /// <summary>
    /// The world asks for a version that is not the one installed. Measured: the
    /// registration matches on uuid <strong>and version</strong>, so the pack
    /// vanishes and the server calls it missing.
    /// </summary>
    RegisteredVersionNotInstalled,

    /// <summary>The world asks for a pack whose folder is not there at all.</summary>
    RegisteredPackNotInstalled,

    /// <summary>The folder is there and nothing asks for it, so it never loads.</summary>
    InstalledButNotRegistered,

    /// <summary>
    /// One uuid has folders in two places, in an arrangement whose winner is
    /// unmeasured: different versions, a resource pack, more than two copies, or
    /// the development folder. This reports the pair rather than picking: an edit
    /// to the copy the server does not load is an afternoon lost to a pack that
    /// will not change.
    /// </summary>
    InstalledInTwoPlaces,

    /// <summary>
    /// One behaviour pack at one version has a folder in this world and one in
    /// the server's own folder. Measured on 1.26.45.1 and 1.26.51.1, with the
    /// copies placed both ways round (<c>T271</c> clause 6): Bedrock loads the
    /// world's copy, so an edit to the other one changes nothing.
    /// </summary>
    WorldCopyLoadsOverServerCopy,

    /// <summary>
    /// A resource pack registered in the behaviour list, or the other way round.
    /// <strong>The quietest failure in this set</strong>: the uuid appears
    /// nowhere in the server's output — no <c>Pack Stack</c> entry and not even
    /// the <em>"was not found and was ignored"</em> warning a genuinely missing
    /// behaviour pack earns — so there is no signal at all outside this finding.
    /// </summary>
    RegisteredInTheWrongList,

    /// <summary>
    /// Its <c>min_engine_version</c> is above the server's build. Measured:
    /// enforced hard — the pack is skipped with an error naming the highest
    /// value accepted, then reported as not found.
    /// </summary>
    EngineTooOld,

    /// <summary>A pack it depends on is not enabled here.</summary>
    MissingPackDependency,

    /// <summary>
    /// It depends on a beta script module and the world does not have Beta APIs
    /// on. That lives in <c>level.dat</c>, so nothing the properties tab shows
    /// reveals it. A pack on stable modules alone never raises it.
    /// </summary>
    ScriptsNeedBetaApis,

    /// <summary>
    /// The server's <c>permissions.json</c> does not list
    /// <c>@minecraft/server</c>, so its scripts never run and
    /// <strong>nothing is logged anywhere</strong>. Measured to ship correct on
    /// a stock tree, so this is something that was changed rather than a default.
    /// </summary>
    ScriptsNotPermitted,

    /// <summary>
    /// Another pack earlier in the list defines something this one also defines.
    /// Measured: the entry at index 0 wins, so the earlier one is in effect.
    /// </summary>
    ShadowedByEarlierPack,

    /// <summary>
    /// The world's pack list carries this uuid more than once. <c>D79</c>
    /// forbids it, and nothing was refusing it — a world hand-edited into that
    /// state loads, and the only visible trace was the shelf naming its
    /// instance twice.
    /// </summary>
    RegisteredTwice,

    /// <summary>
    /// Something this needed to check could not be read. Never folded into a
    /// clean result: an unreadable world file is not a world that is fine.
    /// </summary>
    CouldNotCheck,
}

/// <summary>How much a finding matters.</summary>
public enum AddonFindingSeverity
{
    /// <summary>This add-on is doing nothing.</summary>
    Blocking,

    /// <summary>It may be doing less than intended, or something else may be.</summary>
    Warning,

    /// <summary>It could not be established either way.</summary>
    Unknown,
}

/// <summary>One reason one add-on is not working.</summary>
/// <param name="Kind">Which reason.</param>
/// <param name="Severity">Whether it stops the pack outright.</param>
/// <param name="Subject">The pack this is about, by uuid.</param>
/// <param name="Detail">
/// The specifics — the version asked for, the uuid missing, the pack shadowing
/// this one. Composed by the caller rather than here, because the sentence needs
/// names this rule does not carry.
/// </param>
public sealed record AddonFinding(
    AddonFindingKind Kind,
    AddonFindingSeverity Severity,
    string Subject,
    string? Detail);

/// <summary>A pack as it sits in one of an instance's two pack locations.</summary>
/// <param name="Manifest">Its manifest, already read.</param>
/// <param name="Files">
/// Its files, relative to the pack folder and separated by <c>/</c>. Used only
/// to find two packs defining the same thing, which is the one conflict that
/// can be seen without loading the world.
/// </param>
/// <param name="Home">
/// Which of the two locations it was found in. Carried rather than inferred:
/// the same uuid can be in both, and a caller that wanted to delete one copy
/// had no way to say which.
/// </param>
/// <param name="Directory">
/// The pack's folder, or null when the pack was not read from disk. The tab
/// finds a pack's icon there, among <paramref name="Files"/>.
/// </param>
public sealed record InstalledPack(
    PackManifest Manifest, IReadOnlyList<string> Files, PackHome Home, string? Directory = null);

/// <summary>Everything the diagnosis reads.</summary>
/// <param name="Installed">
/// Every pack folder this instance has, in both locations. A pack in the world
/// and a pack in the server root are separate entries even when they share a
/// uuid, because they are separate folders on disk.
/// </param>
/// <param name="Registrations">
/// What the world's two pack lists ask for, each in its own order. Both are
/// read, because a pack registered in the list for the other kind loads nowhere
/// and is reported by nothing.
/// </param>
/// <param name="ServerBuild">
/// The instance's Bedrock version, or null when it is not known — which is
/// <strong>not</strong> the same as a build that is new enough.
/// </param>
/// <param name="ScriptsPermitted">
/// Whether the server's <c>permissions.json</c> lists <c>@minecraft/server</c>,
/// or null when it could not be read.
/// </param>
/// <param name="BetaApisEnabled">
/// Whether the world has the Beta APIs experiment, or null when it could not be
/// read — which is the ordinary case, since it lives in <c>level.dat</c>.
/// </param>
public sealed record AddonDiagnosisContext(
    IReadOnlyList<InstalledPack> Installed,
    WorldPackLists Registrations,
    PackVersion? ServerBuild,
    bool? ScriptsPermitted,
    bool? BetaApisEnabled,
    IReadOnlySet<string>? LibraryUuids = null);

/// <summary>
/// Why the add-ons on one instance are or are not doing anything.
/// </summary>
/// <remarks>
/// <para>Pure, so every silent failure is an ordinary unit test rather than a
/// world somebody has to break. The inputs are the things that decide — what is
/// installed, what is registered, the build, and two flags that may be
/// unreadable — and each of those is answered by the caller reading one file.</para>
///
/// <para><strong>Three states on the two flags, not two.</strong>
/// <c>level.dat</c> is a binary the wrapper does not parse and
/// <c>permissions.json</c> may be ACL-denied, so <c>null</c> means <em>could not
/// be read</em> and produces a finding of its own. Folding it into <em>fine</em>
/// would report a pack as healthy on evidence nobody has.</para>
/// </remarks>
public static class AddonDiagnosis
{
    /// <summary>Files every pack has, which two packs sharing prove nothing.</summary>
    private static readonly HashSet<string> Ubiquitous = new(StringComparer.OrdinalIgnoreCase)
    {
        "manifest.json", "pack_icon.png", "texts/en_US.lang", "texts/languages.json",
    };

    /// <summary>Everything wrong with this instance's add-ons, worst first.</summary>
    public static IReadOnlyList<AddonFinding> Diagnose(AddonDiagnosisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<AddonFinding>();

        // ONE UUID, MANY FOLDERS. A stock BDS server root ships a folder per
        // Bedrock version of its own packs, all sharing one uuid — measured at
        // 71 behavior_packs folders over 16 distinct uuids, vanilla's alone
        // appearing 54 times. So this is a lookup to a LIST, and a dictionary
        // keyed by uuid throws on the ordinary case rather than the odd one.
        //
        // EVERY uuid, GUID or not, because the world's list is read that way
        // too (`WorldPackEntry.IsUnderstood`). Keeping only GUIDs here made a
        // registered pack whose uuid is not one both On in its row and missing
        // in a finding (`T300`); a GUID is what a library folder needs, and
        // nothing here names a folder.
        var byUuid = context.Installed
            .GroupBy(p => p.Manifest.Uuid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<InstalledPack>)[.. g],
                StringComparer.OrdinalIgnoreCase);

        // Gathered from BOTH lists before the walk rather than as it proceeds.
        // Whether a dependency is satisfied is a fact about the world, so it
        // must not depend on which list the needed pack is in or how far down
        // one it sits — and an add-on's behaviour half very often depends on its
        // resource half, which is in the other file.
        var registeredUuids = context.Registrations.All()
            .Where(r => r.Entry.IsUnderstood)
            .Select(r => r.Entry.PackId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var placement in PackPlacementLayout.Placed)
        {
            // Both are per LIST. Order is precedence within one stack, and a
            // behaviour pack cannot shadow a resource pack however either is
            // arranged, so one uuid in each file is two registrations rather
            // than a duplicate.
            var seenRegistrations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Walked in file order, because that order IS the precedence: the
            // entry at index 0 wins, measured, so a pack is shadowed only by one
            // ahead of it and the walk decides which is which.
            var loaded = new List<InstalledPack>();

            foreach (var entry in context.Registrations.For(placement).Entries)
            {
                if (!entry.IsUnderstood)
                {
                    continue;
                }

                // D79 forbids two versions of one uuid being enabled at once,
                // and nothing was refusing it: a world hand-edited into that
                // state loaded, and the shelf named its instance twice with no
                // finding anywhere saying why.
                if (!seenRegistrations.Add(entry.PackId!))
                {
                    findings.Add(new AddonFinding(
                        AddonFindingKind.RegisteredTwice, AddonFindingSeverity.Blocking,
                        entry.PackId!, $"listed again as {Describe(entry.Version)}"));
                    continue;
                }

                if (!byUuid.TryGetValue(entry.PackId!, out var versions))
                {
                    // What the list says is all there is: no folder means no manifest
                    // and so no name. Saying which file and version is what lets
                    // somebody find the add-on or remove the line (`T416`).
                    findings.Add(new AddonFinding(
                        AddonFindingKind.RegisteredPackNotInstalled, AddonFindingSeverity.Blocking,
                        entry.PackId!,
                        AddonText.NotInstalledDetail(PackPlacementLayout.WorldListFileName(placement), Describe(entry.Version))));
                    continue;
                }

                // The M7 trap, and the reason it earns its own arm: the server
                // reports this exactly as it reports a pack that is not there.
                // Matched on uuid AND version, which is what BDS matches on —
                // with many versions of one uuid on disk, picking any one of
                // them and comparing would report the wrong answer for all the
                // others.
                if (versions.FirstOrDefault(
                        p => p.Manifest.Version.Version == entry.Version.Version) is not { } installed)
                {
                    findings.Add(new AddonFinding(
                        AddonFindingKind.RegisteredVersionNotInstalled, AddonFindingSeverity.Blocking,
                        entry.PackId!,
                        $"registered as {Describe(entry.Version)}, installed as "
                        + string.Join(", ", versions.Select(p => Describe(p.Manifest.Version)))));
                    continue;
                }

                // The one state the server itself reports NOTHING about, so it
                // is worth an arm rather than a nearby sentence: a pack in the
                // other kind's list is not loaded, not named in the Pack Stack,
                // and not warned about either. Everything below is about a pack
                // that loads, so none of it applies.
                if (installed.Manifest.Placement != PackPlacement.Unknown
                    && installed.Manifest.Placement != placement)
                {
                    findings.Add(new AddonFinding(
                        AddonFindingKind.RegisteredInTheWrongList, AddonFindingSeverity.Blocking,
                        entry.PackId!,
                        $"it is a {PackPlacementLayout.Describe(installed.Manifest.Placement)} and "
                        + $"is listed in {PackPlacementLayout.WorldListFileName(placement)}. Switch "
                        + "it off and on again to move it."));
                    continue;
                }

                findings.AddRange(Compatibility(installed, context));
                findings.AddRange(Dependencies(installed, byUuid, registeredUuids, context));
                findings.AddRange(Scripts(installed, context));
                findings.AddRange(Shadowing(installed, loaded));

                loaded.Add(installed);
            }
        }

        // One finding per uuid, not per folder: a stock server root holds 54
        // behaviour folders and 55 resource folders of one pack each, so a
        // finding per folder is a page nobody reads. Both homes are in here, so
        // the grouping is what keeps a world's own copy from adding a second.
        //
        // AND NOT AT ALL for a pack nobody here put there: a stock root's own
        // packs are off on purpose, so a warning for each of them — 27 on a
        // stock root — is an inventory rather than a set of findings, and it
        // buries the ones that matter (`D90`). The same rule decides which rows the tab lists, so the
        // findings and the table cannot disagree about which packs exist.
        var library = context.LibraryUuids ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var needed = UntouchedServerPacks.NeededBy(context.Installed, registeredUuids.Contains);

        foreach (var group in context.Installed
            .GroupBy(p => p.Manifest.Uuid, StringComparer.OrdinalIgnoreCase)
            .Where(g => !registeredUuids.Contains(g.Key))
            .Where(g => !UntouchedServerPacks.Hides(
                [.. g.Select(p => p.Home).Distinct()],
                library.Contains(g.Key),
                registered: false,
                needed: needed.Contains(g.Key))))
        {
            // No detail: the name was all it carried, and the page names every
            // finding's pack now (`T299`).
            findings.Add(new AddonFinding(
                AddonFindingKind.InstalledButNotRegistered, AddonFindingSeverity.Warning,
                group.Key, null));
        }

        // ONE UUID IN TWO PLACES. Which copy Bedrock loads is measured for one
        // arrangement only, and that one is named: a behaviour pack at one
        // version in this world and in the server's own folder loads the world's
        // copy (`T271` clause 6). Any other arrangement reports the pair rather
        // than picking, because a wrong guess sends somebody to edit the copy the
        // server does not load.
        foreach (var group in context.Installed
            .GroupBy(p => p.Manifest.Uuid, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(p => p.Home).Distinct().Count() > 1))
        {
            findings.Add(WorldCopyWinsAsMeasured(group.ToList())
                ? new AddonFinding(
                    AddonFindingKind.WorldCopyLoadsOverServerCopy, AddonFindingSeverity.Warning,
                    group.Key, null)

                // The places rather than the name, which the page now shows beside
                // every finding (`T299`), in the words the row uses for them.
                : new AddonFinding(
                    AddonFindingKind.InstalledInTwoPlaces, AddonFindingSeverity.Warning,
                    group.Key, PackPlacementLayout.DescribeHomes(group.Select(p => p.Home))));
        }

        return [.. findings.OrderBy(f => f.Severity)];
    }

    /// <summary>
    /// Whether two copies of one uuid are in the arrangement <c>T271</c> clause 6
    /// measured: one behaviour pack in this world and one in the server's own
    /// folder, both at one readable version.
    /// </summary>
    private static bool WorldCopyWinsAsMeasured(IReadOnlyList<InstalledPack> copies) =>
        copies is [var first, var second]
        && new[] { first.Home, second.Home }.Order().SequenceEqual([PackHome.World, PackHome.ServerRoot])
        && copies.All(p => p.Manifest.Placement == PackPlacement.Behavior)
        && first.Manifest.Version.Version is { } a
        && second.Manifest.Version.Version is { } b
        && a.CompareTo(b) == 0;

    private static IEnumerable<AddonFinding> Compatibility(
        InstalledPack pack, AddonDiagnosisContext context)
    {
        if (pack.Manifest.MinEngineVersion.Version is not { } floor)
        {
            yield break;
        }

        if (context.ServerBuild is not { } build)
        {
            yield return new AddonFinding(
                AddonFindingKind.CouldNotCheck, AddonFindingSeverity.Unknown,
                pack.Manifest.Uuid,
                $"it needs Bedrock {floor} or newer, and this server's build is not known");
            yield break;
        }

        if (floor > build)
        {
            yield return new AddonFinding(
                AddonFindingKind.EngineTooOld, AddonFindingSeverity.Blocking,
                pack.Manifest.Uuid, $"it needs Bedrock {floor} and this server is {build}");
        }
    }

    private static IEnumerable<AddonFinding> Dependencies(
        InstalledPack pack,
        IReadOnlyDictionary<string, IReadOnlyList<InstalledPack>> byUuid,
        IReadOnlySet<string> registered,
        AddonDiagnosisContext context)
    {
        foreach (var dependency in pack.Manifest.PackDependencies)
        {
            // Satisfied from what this instance has, which is the repair the
            // panel can actually offer.
            var here = byUuid.TryGetValue(dependency.Target, out var copies) && copies.Count > 0;
            if (here && registered.Contains(dependency.Target))
            {
                continue;
            }

            // Both packs by name (`T284`): with three switched on, "it needs the
            // pack 6baf8b62-…" named neither the one with the need nor the one
            // needed. The one needed by its kind as well (`T309`), since two packs
            // can share a name.
            var needed = here
                ? AddonText.Needed(dependency.Target, copies![0].Manifest.Name, copies[0].Manifest.Placement, withUuid: true)
                : AddonText.Needed(dependency.Target, null, PackPlacement.Unknown, withUuid: true);

            yield return new AddonFinding(
                AddonFindingKind.MissingPackDependency, AddonFindingSeverity.Blocking,
                pack.Manifest.Uuid,
                here
                    ? $"{pack.Manifest.Name} needs {needed}, which is on this server but not switched on"
                    : $"{pack.Manifest.Name} needs {needed}, which this server does not have");
        }

        // A script-module floor is a fact about the server's runtime rather than
        // about the library, so it is never reported as a missing pack — the
        // repair is a different one and naming the wrong repair reads as real.
        //
        // Not built: a stable module version above what this Bedrock build ships
        // is not detected, because nothing here maps a build to the module
        // versions it carries. A known build therefore says nothing either way.
        foreach (var module in pack.Manifest.ScriptModuleDependencies)
        {
            if (context.ServerBuild is null && module.Version.Version is not null)
            {
                yield return new AddonFinding(
                    AddonFindingKind.CouldNotCheck, AddonFindingSeverity.Unknown,
                    pack.Manifest.Uuid,
                    $"it needs the script API {module.Target} {module.Version.Version}, and which "
                    + "version this server ships is not known");
            }
        }
    }

    private static IEnumerable<AddonFinding> Scripts(
        InstalledPack pack, AddonDiagnosisContext context)
    {
        // Both checks are about scripts, so a pack without a script module is
        // not merely passing them — they do not apply to it, and reporting
        // either against it would be noise on every resource pack.
        if (!pack.Manifest.HasScriptModule)
        {
            yield break;
        }

        if (context.ScriptsPermitted is false)
        {
            yield return new AddonFinding(
                AddonFindingKind.ScriptsNotPermitted, AddonFindingSeverity.Blocking,
                pack.Manifest.Uuid, null);
        }
        else if (context.ScriptsPermitted is null)
        {
            yield return new AddonFinding(
                AddonFindingKind.CouldNotCheck, AddonFindingSeverity.Unknown,
                pack.Manifest.Uuid,
                "whether this server lets packs run scripts could not be read");
        }

        // The experiment matters only to a pack on a beta module. Stable modules
        // run without it, so asking about the world for such a pack is noise on
        // every script pack an administrator installs.
        switch (pack.Manifest.ScriptsRelease)
        {
            case ScriptModuleRelease.Stable:
                break;

            case ScriptModuleRelease.Prerelease when context.BetaApisEnabled is false:
                yield return new AddonFinding(
                    AddonFindingKind.ScriptsNeedBetaApis, AddonFindingSeverity.Blocking,
                    pack.Manifest.Uuid, null);
                break;

            case ScriptModuleRelease.Prerelease when context.BetaApisEnabled is null:
                yield return new AddonFinding(
                    AddonFindingKind.CouldNotCheck, AddonFindingSeverity.Unknown,
                    pack.Manifest.Uuid,
                    "it uses a beta script module, and whether this world has Beta APIs switched on could not be read");
                break;

            case ScriptModuleRelease.Unknown when context.BetaApisEnabled is not true:
                yield return new AddonFinding(
                    AddonFindingKind.CouldNotCheck, AddonFindingSeverity.Unknown,
                    pack.Manifest.Uuid,
                    "whether its scripts need this world's Beta APIs experiment cannot be told from its manifest");
                break;
        }
    }

    /// <summary>
    /// Can one pack's copy of this file stand in for another's? Not for the files
    /// every pack has, and not for a script: a script is scoped to its own pack,
    /// measured with two packs both carrying <c>scripts/main.js</c>, whose scripts
    /// both ran in both orders (<c>T278</c>).
    /// </summary>
    private static bool CanShadow(string file) =>
        !Ubiquitous.Contains(file)
        && !file.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<AddonFinding> Shadowing(
        InstalledPack pack, IReadOnlyList<InstalledPack> ahead)
    {
        var mine = pack.Files
            .Where(CanShadow)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var earlier in ahead)
        {
            var shared = earlier.Files.FirstOrDefault(f => CanShadow(f) && mine.Contains(f));

            if (shared is not null)
            {
                yield return new AddonFinding(
                    AddonFindingKind.ShadowedByEarlierPack, AddonFindingSeverity.Warning,
                    pack.Manifest.Uuid,
                    $"'{earlier.Manifest.Name}' is earlier in the list and also defines {shared}");
            }
        }
    }

    private static string Describe(PackVersionRead version) => version.State switch
    {
        PackVersionState.Read => version.Version!.Value.ToString(),
        PackVersionState.Malformed => $"'{version.Raw}' (unreadable)",
        _ => "no version",
    };
}
