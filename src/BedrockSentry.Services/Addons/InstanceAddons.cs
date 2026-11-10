using System.Text.Json;
using BedrockSentry.Core.Addons;
using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Backups;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Addons;

/// <summary>Everything one instance's Add-ons tab needs, read from disk.</summary>
/// <param name="WorldDirectory">
/// The world this instance loads, or null when <c>level-name</c> could not be
/// read — in which case nothing below describes a world and the tab says so
/// rather than describing an empty one.
/// </param>
/// <param name="WorldNotCreated">
/// True when <c>level-name</c> names a world Bedrock has not created yet. That
/// world has no add-ons at all, which is known; a <c>level-name</c> that could not
/// be read leaves them unknown (<c>D83</c>).
/// </param>
public sealed record InstanceAddonState(
    IReadOnlyList<InstalledPack> Installed,
    WorldPackLists Registrations,
    PackSettingsDocument Settings,
    PackSettingsDocumentState SettingsState,
    IReadOnlyDictionary<string, IReadOnlyList<PackSchema>> Schemas,
    bool? ScriptsPermitted,
    string? WorldDirectory,
    string? Reason,
    bool WorldNotCreated = false)
{
    /// <summary>
    /// Whether this reading says which packs the world uses: its lists were read,
    /// or it has no world yet.
    /// </summary>
    public bool WorldPacksKnown =>
        WorldDirectory is null ? WorldNotCreated : Registrations.Readable;

    /// <summary>Can this tab act at all?</summary>
    /// <remarks>
    /// <strong>Both lists or neither.</strong> Which file a change rewrites is
    /// decided by the pack somebody presses, so acting while one of the two
    /// could not be read would make that press the difference between a safe
    /// write and one that replaces a file nobody has seen.
    /// </remarks>
    public bool CanAct => WorldDirectory is not null && Registrations.Readable;

    /// <summary>One pack's declared controls, or null when it is not here.</summary>
    /// <remarks>
    /// <para>Null rather than an empty schema, because <c>PackSettingsWrite</c>
    /// treats the two differently: a pack that is not installed cannot have its
    /// values judged, and a stale entry for one is measured to be ignored by the
    /// server.</para>
    ///
    /// <para><strong>Answered for the version the world switched on.</strong>
    /// Many folders can share a uuid, so the settings file's header-uuid key
    /// does not pick a folder by itself; the registration's version does. With
    /// nothing registered, any installed version answers — every one of them
    /// declares the same controls far more often than not, and refusing would
    /// leave a gear that never renders.</para>
    /// </remarks>
    public PackSettingsSchema? SchemaFor(string packId)
    {
        if (!Schemas.TryGetValue(packId, out var versions) || versions.Count == 0)
        {
            return null;
        }

        // Compared as VERSIONS rather than as strings: comparison pads a short
        // one and ToString does not, so "1.1" and [1,1,0] are one version and
        // two spellings of it.
        //
        // FirstOrDefault is deliberately not used here. PackSchema is a struct,
        // so a miss returns default rather than null and `is { }` matches it —
        // which handed back an empty schema for a registration naming a version
        // this instance does not have.
        if (Registrations.EntryFor(packId) is { Version.Version: { } asked })
        {
            foreach (var candidate in versions)
            {
                if (candidate.Version == asked)
                {
                    return candidate.Schema;
                }
            }
        }

        // Nothing registered, so no version has been chosen. Every folder of one
        // pack declares the same controls far more often than not, and refusing
        // here would leave a gear that never renders for a pack nobody has
        // switched on yet — which is exactly when somebody looks at it.
        return versions[0].Schema;
    }

    /// <summary>
    /// May this world's settings file be replaced at all?
    /// </summary>
    /// <remarks>
    /// <strong>Only when it was read or is absent.</strong> A write replaces the
    /// whole file, so writing over one that could not be read or parsed discards
    /// overrides nobody has seen — every other pack's included.
    /// </remarks>
    public bool SettingsAreWritable =>
        SettingsState is PackSettingsDocumentState.Read or PackSettingsDocumentState.Absent;
}

/// <summary>One installed version of a pack, and the controls it declares.</summary>
/// <param name="Version">
/// Null when the manifest's version could not be read, which is not the same as
/// a version nothing registered — the two are told apart by the caller.
/// </param>
public readonly record struct PackSchema(PackVersion? Version, PackSettingsSchema Schema);

/// <summary>How writing a pack's settings went.</summary>
/// <param name="Problems">
/// Empty on success. The whole document is checked, so a problem here may name
/// a pack other than the one being changed.
/// </param>
/// <param name="Queued">
/// Kept to be written once the server's process has exited (<c>D85</c>), and not
/// <paramref name="Saved"/>: a waiting change reported as written is the lie a third state is for.
/// </param>
public sealed record SettingsSaveResult(
    bool Saved, string? Reason, IReadOnlyList<SettingsWriteProblem> Problems, bool Queued = false)
{
    /// <summary>It worked.</summary>
    public static readonly SettingsSaveResult Ok = new(true, null, []);

    /// <summary>It was queued until the server stops (<c>D85</c>).</summary>
    public static readonly SettingsSaveResult QueuedUntilStopped = new(false, null, [], Queued: true);

    /// <summary>It was refused for a reason that is not about a value.</summary>
    public static SettingsSaveResult Refused(string reason) => new(false, reason, []);
}

/// <summary>Reads and changes one instance's add-ons.</summary>
/// <remarks>
/// <para><strong>A pack can be in either of two places and Bedrock reads
/// both</strong> — the server root's <c>behavior_packs</c>, which is what a
/// Bedrock package ships its own packs in, and the world's own. The
/// registrations are always the world's. So this reads four directories and a
/// change writes a folder and a list.</para>
///
/// <para>This read the server root alone, on a measurement that proved Bedrock
/// loads a pack from there (<c>T256</c>) and was written up as proving it loads
/// packs from <em>only</em> there. The conditions did not travel with the claim:
/// the world scanned had no pack folder of its own, so that reading could not
/// have seen the other location, and an administrator's own packs were reported
/// as add-ons the server was asking for and did not have (<c>T271</c>).</para>
///
/// <para><strong>Beta APIs is never reported as on.</strong> It lives in
/// <c>level.dat</c>, which is a binary this does not parse, so it is always
/// unknown here, and the diagnosis says so for a pack that depends on a beta
/// script module or cannot be placed. A pack on stable modules alone does not
/// need it and hears nothing. Guessing it on would clear a finding nobody has
/// evidence for.</para>
/// </remarks>
public sealed class InstanceAddons(AddonLibrary library, ILogger<InstanceAddons> logger)
{
    /// <summary>Raised with the instance's id after a change to its packs is written.</summary>
    /// <remarks>
    /// Console completion listens, so an add-on switched on completes before the server
    /// has loaded it (<c>D105</c>). The class is a singleton for the life of the service:
    /// whatever subscribes must unsubscribe.
    /// </remarks>
    public event Action<string>? Changed;

    private void RaiseChanged(string instanceId) =>
        EventRaising.RaiseIndependently(
            Changed, instanceId, ex => logger.LogWarning(ex, "A listener threw while being told {Instance}'s add-ons changed", instanceId));

    private readonly Func<string, CancellationToken, Task<string?>> _turnOnScriptOutput =
        ScriptOutput.TurnOnAsync;

    /// <summary>Reads <c>server.properties</c> to find the world.</summary>
    /// <remarks>
    /// A seam, because a file the service may not read cannot be arranged by a
    /// suite running as root (<c>T290</c> clause 4).
    /// </remarks>
    internal Func<string, PropertiesFile> ReadProperties { get; init; } = ServerProperties.ReadFile;

    /// <param name="turnOnScriptOutput">
    /// Sets the script output key. A seam, because a <c>server.properties</c>
    /// that cannot be written cannot be arranged by a suite running as root. On
    /// Windows a file another process holds open refuses the write as access
    /// denied (<c>T292</c> clause 5).
    /// </param>
    internal InstanceAddons(
        AddonLibrary library,
        ILogger<InstanceAddons> logger,
        Func<string, CancellationToken, Task<string?>> turnOnScriptOutput)
        : this(library, logger)
    {
        _turnOnScriptOutput = turnOnScriptOutput;
    }

    /// <summary>
    /// The settings file, which is the world's and covers <strong>both</strong>
    /// kinds of pack — keyed by header uuid, so unlike the registrations it is
    /// one file rather than a pair.
    /// </summary>
    private const string SettingsFileName = WorldAddonFiles.SettingsFileName;

    /// <summary>Read one instance's add-on state.</summary>
    public InstanceAddonState Read(InstanceSupervisor supervisor)
    {
        ArgumentNullException.ThrowIfNull(supervisor);

        var root = supervisor.WorkingDirectory;
        var installed = new List<InstalledPack>();
        var schemas = new Dictionary<string, List<PackSchema>>(StringComparer.OrdinalIgnoreCase);

        // BEFORE the scan, because half of what there is to scan is inside it.
        // Bedrock loads packs from the world's own folders as well as the
        // server root, so a reader that resolved the world afterwards could only
        // ever see one of the two.
        var lookup = FindWorld(root);
        var world = lookup.Directory;

        foreach (var home in PackPlacementLayout.Homes)
        {
            foreach (var placement in PackPlacementLayout.Placed)
            {
                if (PackPlacementLayout.DirectoryFor(root, world, home, placement)
                    is not { } directory)
                {
                    continue;
                }

                List<(InstalledPack Pack, PackSettingsSchema Schema)> found;

                try
                {
                    found = [.. PacksIn(directory, home)];
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Never an empty list in place of an unreadable folder (T392 clause
                    // 3): a junction RedirectionGuard refuses throws here, from the
                    // enumeration, and the tab says why instead of failing whole.
                    logger.LogWarning(ex, "Could not list the packs under {Folder}.", directory);

                    return new InstanceAddonState(
                        installed, WorldPackLists.Empty,
                        new PackSettingsDocument(null, []), PackSettingsDocumentState.Absent,
                        new Dictionary<string, IReadOnlyList<PackSchema>>(StringComparer.OrdinalIgnoreCase),
                        ScriptsPermitted(root), null, Unlisted(root, directory, ex));
                }

                foreach (var (pack, schema) in found)
                {
                    installed.Add(pack);

                    // A uuid maps to a LIST. A stock server root holds one folder
                    // per Bedrock version of its own packs, all sharing a uuid, so
                    // a map keyed by uuid alone silently kept whichever folder
                    // happened to be enumerated last. The world's own copy of a
                    // uuid joins that list for the same reason.
                    if (!schemas.TryGetValue(pack.Manifest.Uuid, out var versions))
                    {
                        schemas[pack.Manifest.Uuid] = versions = [];
                    }

                    versions.Add(new PackSchema(pack.Manifest.Version.Version, schema));
                }
            }
        }

        var byUuid = schemas.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<PackSchema>)kv.Value,
            StringComparer.OrdinalIgnoreCase);

        if (world is null)
        {
            return new InstanceAddonState(
                installed, WorldPackLists.Empty,
                new PackSettingsDocument(null, []), PackSettingsDocumentState.Absent,
                byUuid, ScriptsPermitted(root), null, lookup.Reason, lookup.NotCreated);
        }

        // BOTH lists. A world keeps its behaviour and resource registrations in
        // separate files and may hold either, both or neither, so reading one
        // and calling it the world's packs reports every resource pack as off.
        var registrations = new WorldPackLists(
            ReadRegistrations(Path.Combine(
                world, PackPlacementLayout.WorldListFileName(PackPlacement.Behavior)!)),
            ReadRegistrations(Path.Combine(
                world, PackPlacementLayout.WorldListFileName(PackPlacement.Resource)!)));

        var settings = ReadSettings(Path.Combine(world, SettingsFileName));

        return new InstanceAddonState(
            installed,
            registrations,
            settings.Document,
            settings.State,
            byUuid,
            ScriptsPermitted(root),
            world,
            registrations.Reason);
    }

    /// <summary>Why a pack folder could not be listed: the link on its way when there is one.</summary>
    internal static string Unlisted(string root, string directory, Exception ex) =>
        ServerTree.LinkOnPath(root, directory) is { } link
            ? ServerTree.Refusal(link)
            : $"'{directory}' could not be listed, so this server's add-ons are not shown ({ex.Message}).";

    /// <summary>Everything wrong with this instance's add-ons.</summary>
    public IReadOnlyList<AddonFinding> Diagnose(InstanceAddonState state, PackVersion? serverBuild) =>
        AddonDiagnosis.Diagnose(new AddonDiagnosisContext(
            state.Installed,
            state.Registrations,
            serverBuild,
            state.ScriptsPermitted,

            // Always unknown: level.dat is a binary this does not read. The
            // diagnosis has an arm for that and it is the honest one.
            BetaApisEnabled: null,

            // What the shared library has seen, which is what separates an
            // administrator's own pack in the server root from one a Bedrock
            // package put there (`D90`).
            LibraryUuids: library.List()
                .Select(p => p.Uuid)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)));

    /// <summary>Write one pack's settings into the world's settings file.</summary>
    /// <remarks>
    /// <para><strong>The whole document is checked before any of it is
    /// written.</strong> One rejected value discards every pack's overrides in
    /// the world, so a change that is itself perfect is still refused when
    /// anything else in the file is wrong — see <c>PackSettingsWrite</c>.</para>
    ///
    /// <para><strong>A file that could not be read is never replaced.</strong> A
    /// write replaces the whole file, so writing over one nobody could parse
    /// discards overrides nobody has seen.</para>
    /// </remarks>
    public async Task<SettingsSaveResult> SaveSettingsAsync(
        InstanceSupervisor supervisor,
        InstanceAddonState state,
        string packId,
        IReadOnlyDictionary<string, PackSettingValue> values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        ArgumentNullException.ThrowIfNull(values);

        if (state.WorldDirectory is null)
        {
            return SettingsSaveResult.Refused(state.Reason ?? "This server's world could not be found.");
        }

        var plan = PackSettingsSave.Plan(
            state.Settings, state.SettingsState, packId, values, state.SchemaFor);

        if (!plan.CanWrite)
        {
            return plan.Refusal == SettingsSaveRefusal.FileNotRead
                ? SettingsSaveResult.Refused(
                    "This world's add-on settings file could not be read, so it will not be "
                    + "replaced. Writing over it would discard settings nothing here has seen.")
                : new SettingsSaveResult(false, null, plan.Problems);
        }

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(state.WorldDirectory, SettingsFileName),
                plan.Json!,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                ex, "Could not write add-on settings on {Instance}.", supervisor.InstanceId);

            return SettingsSaveResult.Refused(ex.Message);
        }

        logger.LogInformation(
            "Wrote settings for {Pack} on {Instance}.", packId, supervisor.InstanceId);

        return SettingsSaveResult.Ok;
    }

    /// <summary>Carry out a planned change: the files and the registration.</summary>
    /// <remarks>
    /// <strong>Both writes or neither, in that order.</strong> A registration
    /// naming a version whose files are not there is reported by the server as a
    /// missing pack, so the files move first and the registration is written
    /// only once they have.
    /// </remarks>
    /// <summary>
    /// Plans switching a pack on: copied from the library when it holds that
    /// version, or registered where it is when the library does not and this
    /// server already has its files (<c>T277</c>).
    /// </summary>
    public AddonChangePlan PlanEnable(
        InstanceAddonState state, WorldPackList list, PackPlacement placement, string uuid, PackVersion version)
    {
        ArgumentNullException.ThrowIfNull(state);

        var copies = state.Installed
            .Where(p => string.Equals(p.Manifest.Uuid, uuid, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Manifest.Version.Version)
            .ToList();

        return AddonChangePlanner.Enable(
            list,
            placement,
            uuid,
            version,
            (u, v) => library.Find(u, v) is not null,
            (u, v) => copies.Contains(v),

            // D112: another version picked while a copy of this one is here. The tab
            // offers that only when the library holds every copy (AddonVersionChoices).
            replaceCopies: AddonVersionChoices.ReplacesACopy(copies, v => library.Find(uuid, v) is not null, version));
    }

    /// <summary>
    /// Switches the companion pack on for a server that has never started
    /// (<c>D93</c>): makes the world folder <c>level-name</c> names, which BDS
    /// generates the world into and keeps (the spike under <c>D93</c>), then takes
    /// the Add-ons tab's own path, script output first.
    /// </summary>
    /// <returns>Null when the pack is on; otherwise why not.</returns>
    /// <remarks>
    /// A world from a world file can arrive with the pack already switched on (<c>T334</c>).
    /// It stays at the version the world names, and script output is turned on, since the
    /// new server's <c>server.properties</c> is the package's.
    /// </remarks>
    public async Task<string?> PreSeedCompanionAsync(
        InstanceSupervisor supervisor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(supervisor);

        var properties = ServerProperties.PathFor(supervisor.WorkingDirectory);

        if (!File.Exists(properties)
            || !ServerProperties.Read(properties).TryGetValue("level-name", out var level)
            || level.Length == 0)
        {
            return $"{ServerProperties.FileName} names no level-name, so the world the pack goes in is not known.";
        }

        if (library.List().FirstOrDefault(p => CompanionPack.Is(p.Uuid)) is not { Versions.Count: > 0 } shelved)
        {
            return "The companion pack is not in the add-on library.";
        }

        var version = shelved.Newest.Version;

        Directory.CreateDirectory(Path.Combine(supervisor.WorkingDirectory, "worlds", level));

        var state = Read(supervisor);
        var plan = PlanEnable(
            state, state.Registrations.For(PackPlacement.Behavior), PackPlacement.Behavior,
            CompanionPack.Uuid, version);

        var refused = plan.Change is { } change
            ? await ApplyAsync(supervisor, state, change, cancellationToken).ConfigureAwait(false)
            : plan.Refusal is AddonChangeRefusal.AlreadyAtThatVersion or AddonChangeRefusal.AlreadyAtAnotherVersion
                ? await TurnOnScriptOutputAsync(supervisor, cancellationToken).ConfigureAwait(false)
                : AddonText.Refusal(plan.Refusal);

        return refused ?? await FollowCompanionAsync(supervisor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Brings the companion's resource pack into line with the companion (<c>D109</c>,
    /// the rule is <see cref="CompanionResources"/>): on at the library's newest version
    /// while the companion is on, off once it is off. Called after anything that switches
    /// the companion on or off or moves it.
    /// </summary>
    /// <returns>Null when nothing needed doing or it was done; otherwise why not.</returns>
    public async Task<string?> FollowCompanionAsync(
        InstanceSupervisor supervisor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(supervisor);

        var state = Read(supervisor);
        if (!state.CanAct)
        {
            return null;
        }

        var companionOn = state.Registrations.For(PackPlacement.Behavior).Entries
            .Any(e => e.PackId is { } id && CompanionPack.Is(id));
        var list = state.Registrations.For(PackPlacement.Resource);
        var listed = list.Entries
            .Where(e => e.PackId is { } id && CompanionPack.IsResource(id))
            .Select(e => e.Version.Version)
            .ToList();
        var newest = library.List().FirstOrDefault(p => CompanionPack.IsResource(p.Uuid)) is { Versions.Count: > 0 } shelved
            ? shelved.Newest.Version
            : (PackVersion?)null;

        var step = CompanionResources.StepFor(companionOn, listed, newest);
        AddonChangePlan? plan = step switch
        {
            { Action: CompanionResourcesAction.Enable, Version: { } version } =>
                PlanEnable(state, list, PackPlacement.Resource, CompanionPack.ResourceUuid, version),
            { Action: CompanionResourcesAction.Update, Version: { } version } =>
                AddonChangePlanner.Update(
                    list, PackPlacement.Resource, CompanionPack.ResourceUuid, version, (u, v) => library.Find(u, v) is not null),
            { Action: CompanionResourcesAction.Disable } =>
                AddonChangePlanner.Disable(list, PackPlacement.Resource, CompanionPack.ResourceUuid),
            _ => null,
        };

        if (plan is not { } planned)
        {
            return null;
        }

        var refused = planned.Change is { } change
            ? await ApplyAsync(supervisor, state, change, cancellationToken).ConfigureAwait(false)
            : AddonText.Refusal(planned.Refusal);

        return refused is null
            ? null
            : $"The companion's resources, which draw its outlines, were not {(companionOn ? "switched on" : "switched off")}: {refused.TrimEnd('.')}.";
    }

    public async Task<string?> ApplyAsync(
        InstanceSupervisor supervisor,
        InstanceAddonState state,
        AddonChange change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(change);

        // A job that claims the server, such as a sync, may be replacing this world. A
        // restart does not, and its stop is when a queued change is written (`T417`).
        if (supervisor.Status.ClaimedFor is { } purpose && !ClaimText.OnlyRestarts(purpose))
        {
            return ClaimText.Refusal(supervisor.Definition.Name, purpose);
        }

        if (state.WorldDirectory is null)
        {
            return state.Reason ?? "This server's world could not be found.";
        }

        if (!change.IsComplete)
        {
            // Unreachable through the planner, and refused here rather than
            // trusted: half an update leaves the world naming a version that is
            // not installed, which the server reports as a missing pack.
            return "That change is incomplete and was not applied.";
        }

        // D93, before any other write: the companion pack reports through
        // script output, so switching it on or moving it to another version sets
        // the key first, and a server.properties that is missing, unreadable or
        // cannot be written refuses the whole change. Switching it off leaves the
        // key, which other script packs read.
        if (change.Type is AddonChangeType.Enable or AddonChangeType.Update
            && change.Files.Any(f =>
                f.Kind is AddonFileActionKind.CopyFromLibrary or AddonFileActionKind.AlreadyInPlace
                && CompanionPack.Is(f.Uuid))
            && await TurnOnScriptOutputAsync(supervisor, cancellationToken).ConfigureAwait(false)
                is { } refused)
        {
            return refused;
        }

        try
        {
            // The change says which list it rewrote, so the file name is read
            // off the change rather than assumed. Assuming it wrote every
            // resource pack's registration into the behaviour file, where
            // Bedrock does not look and does not complain.
            if (PackPlacementLayout.WorldListFileName(change.Placement) is not { } listFile)
            {
                return "That add-on is neither a behaviour pack nor a resource pack, so there is "
                    + "no list that would switch it on.";
            }

            foreach (var action in change.Files)
            {
                await ActAsync(
                        supervisor,
                        state.WorldDirectory,
                        action,
                        keepWhatTheLibraryCannotReplace: change.Type == AddonChangeType.Disable,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await File.WriteAllTextAsync(
                Path.Combine(state.WorldDirectory, listFile),
                change.WorldList.Write(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not change add-ons on {Instance}.", supervisor.InstanceId);
            return ex.Message;
        }

        logger.LogInformation(
            "{Summary} on {Instance}.", change.Summary, supervisor.InstanceId);

        RaiseChanged(supervisor.InstanceId);
        return null;
    }

    /// <summary>
    /// Put this server's copies of an add-on onto the shared shelf, one per version the
    /// shelf lacks (<c>D78</c> (a)).
    /// </summary>
    /// <returns>What became of each copy, or a refusal when none was tried.</returns>
    /// <remarks>
    /// <para><strong>Reads the server and writes only the library</strong>, so a running
    /// server is no reason to refuse: nothing it has open is changed, and its copy stays
    /// switched on or off as it was. A claimed one is refused, because the job holding it
    /// may be replacing the folders this would read.</para>
    ///
    /// <para><strong>Which copies</strong> is <see cref="LibraryCopyIn.Copies"/>, asked of
    /// the server as it is now rather than as the tab last drew it.</para>
    /// </remarks>
    public async Task<LibraryCopyResult> CopyToLibraryAsync(
        InstanceSupervisor supervisor, string uuid, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentException.ThrowIfNullOrWhiteSpace(uuid);

        var server = supervisor.Status.Name;

        if (supervisor.Status.ClaimedFor is { } purpose)
        {
            return LibraryCopyResult.Refused(ClaimText.Refusal(server, purpose));
        }

        var shelf = library.List()
            .Where(p => string.Equals(p.Uuid, uuid, StringComparison.OrdinalIgnoreCase))
            .SelectMany(p => p.Versions)
            .Select(v => v.Version)
            .ToHashSet();

        var copies = LibraryCopyIn.Copies(
            Read(supervisor).Installed.Where(
                p => string.Equals(p.Manifest.Uuid, uuid, StringComparison.OrdinalIgnoreCase)),
            shelf.Contains);

        if (copies.Count == 0)
        {
            return LibraryCopyResult.Refused(
                $"The add-on library already holds every version of it {server} has, so nothing was copied.");
        }

        var results = new List<ImportedPack>();

        foreach (var copy in copies)
        {
            results.Add(await library.CopyFromFolderAsync(copy.Directory!, cancellationToken).ConfigureAwait(false));
        }

        return new LibraryCopyResult(results, null);
    }

    /// <summary>
    /// Takes one pack off a stopped server entirely (<c>D83</c>): its registrations in
    /// both world lists, its entries in the history files and the settings file, and
    /// its copies in the world's and the server root's pack folders.
    /// </summary>
    /// <returns>Null when it is gone; otherwise why not, or what was left.</returns>
    /// <remarks>
    /// <para><strong>Only on a stopped server that nothing has claimed</strong>, asked
    /// again here rather than trusted from whoever planned the removal: a server started
    /// since would rewrite its world's files when it stops (<c>D85</c>), and a claimed one
    /// is stopped by a job that may be replacing its world.</para>
    ///
    /// <para><strong>Two callers</strong>: the library's Remove, for every stopped server
    /// that has the pack, and the Add-ons tab's Remove, for a pack this server has and does
    /// not switch on (<c>T363</c>). Switch off keeps a folder the library cannot put back
    /// (<c>T277</c>), so this is the only way the panel deletes one.</para>
    ///
    /// <para><strong>Every file is planned before any is written</strong>, so a list,
    /// history or settings file this cannot read refuses the whole removal: a removal
    /// that half-applies leaves a world naming a pack it no longer has, which is worse
    /// than one that does not happen. A write that fails puts back the files already
    /// written.</para>
    ///
    /// <para><strong>The copies go last</strong>, once nothing names the pack. A
    /// folder delete is not atomic (<c>T186</c>), and one that stops partway leaves a
    /// broken folder that is at least no longer switched on. The development folders
    /// are the administrator's and are not touched (<c>T279</c>).</para>
    /// </remarks>
    public async Task<string?> RemovePackAsync(
        InstanceSupervisor supervisor, string uuid, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentException.ThrowIfNullOrWhiteSpace(uuid);

        var server = supervisor.Status.Name;

        // A job that claims the server, such as a sync, stops it and may be replacing
        // its world, so stopped is not enough.
        if (supervisor.Status.ClaimedFor is { } purpose)
        {
            return ClaimText.Refusal(server, purpose);
        }

        if (!LibraryRemoval.IsStopped(supervisor.Status.State))
        {
            return $"{server} is running, so nothing was changed on it.";
        }

        var state = Read(supervisor);
        var writes = new List<(string Path, string Json)>();

        if (!state.WorldPacksKnown)
        {
            return $"{state.Reason ?? $"{server}'s add-ons could not be read."} Nothing was changed on {server}.";
        }

        if (state.WorldDirectory is { } world)
        {
            foreach (var placement in PackPlacementLayout.Placed)
            {
                var change = state.Registrations.For(placement).Disable(uuid);

                if (change.Kind == WorldPackChangeKind.Disabled)
                {
                    writes.Add((
                        Path.Combine(world, PackPlacementLayout.WorldListFileName(placement)!),
                        change.List.Write()));
                }
            }

            foreach (var name in PackHistoryFile.FileNames)
            {
                var path = Path.Combine(world, name);

                if (!File.Exists(path))
                {
                    continue;
                }

                string text;

                try
                {
                    text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return $"{server}'s {name} could not be read ({ex.Message}), so nothing was changed on it.";
                }

                var edit = PackHistoryFile.Without(text, uuid);

                if (!edit.Understood)
                {
                    return $"{server}'s {name} is not in a shape this can rewrite ({edit.Reason}), "
                        + "so nothing was changed on it.";
                }

                if (edit.Changed)
                {
                    writes.Add((path, edit.Json!));
                }
            }

            if (!state.SettingsAreWritable)
            {
                return $"{server}'s {SettingsFileName} could not be read, so nothing was changed on it.";
            }

            if (state.Settings.For(uuid) is not null)
            {
                writes.Add((
                    Path.Combine(world, SettingsFileName),
                    state.Settings.Without(uuid).Write(
                        state.Settings.FormatVersion ?? PackSettingsDocument.KnownFormatVersion)));
            }
        }

        if (await WriteAllOrNoneAsync(writes, cancellationToken).ConfigureAwait(false) is { } failed)
        {
            logger.LogWarning("Could not take {Pack} off {Instance}: {Why}", uuid, supervisor.InstanceId, failed);
            return $"{failed} Nothing was changed on {server}.";
        }

        foreach (var copy in state.Installed
                     .Where(p => p.Home is PackHome.World or PackHome.ServerRoot
                                 && p.Directory is not null
                                 && string.Equals(p.Manifest.Uuid, uuid, StringComparison.OrdinalIgnoreCase))
                     .Select(p => p.Directory!))
        {
            try
            {
                Directory.Delete(copy, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not delete {Folder} on {Instance}.", copy, supervisor.InstanceId);

                return $"It is switched off on {server} and its entries are cleared, and its folder "
                    + $"{copy} could not be deleted ({ex.Message}); delete it by hand.";
            }
        }

        logger.LogInformation("Took {Pack} off {Instance}.", uuid, supervisor.InstanceId);
        RaiseChanged(supervisor.InstanceId);
        return null;
    }

    /// <summary>
    /// Writes every file or, on a failure, puts back the ones already written.
    /// </summary>
    /// <returns>Null when all were written; otherwise the failure.</returns>
    private async Task<string?> WriteAllOrNoneAsync(
        IReadOnlyList<(string Path, string Json)> writes, CancellationToken cancellationToken)
    {
        var done = new List<(string Path, string? Before)>();

        try
        {
            foreach (var (path, json) in writes)
            {
                var before = File.Exists(path)
                    ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
                    : null;

                await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
                done.Add((path, before));
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var (path, before) in done)
            {
                try
                {
                    if (before is null)
                    {
                        File.Delete(path);
                    }
                    else
                    {
                        await File.WriteAllTextAsync(path, before, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception undo) when (undo is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(undo, "Could not put {File} back.", path);
                }
            }

            return ex.Message;
        }
    }

    /// <summary>Set the script output key for the companion pack; null when it is on.</summary>
    /// <remarks>
    /// A key already <c>true</c> is left unwritten, so switching the pack on
    /// changes <c>server.properties</c> only when it has to, and the console
    /// carries the shared notice only when it did.
    /// </remarks>
    private async Task<string?> TurnOnScriptOutputAsync(
        InstanceSupervisor supervisor, CancellationToken cancellationToken)
    {
        var file = ServerProperties.ReadFile(ServerProperties.PathFor(supervisor.WorkingDirectory));

        if (ScriptOutput.StateOf(scriptPackSwitchedOn: true, file) == ScriptOutputState.Visible)
        {
            return null;
        }

        string? refused;

        try
        {
            refused = await _turnOnScriptOutput(supervisor.WorkingDirectory, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                ex, "Could not turn on script output on {Instance}.", supervisor.InstanceId);

            return $"{ServerProperties.FileName} could not be written: {ex.Message} "
                + "Nothing has been switched on.";
        }

        if (refused is not null)
        {
            return refused;
        }

        logger.LogInformation(
            "Set {Key}=true on {Instance} for the companion pack.", ScriptOutput.Key, supervisor.InstanceId);

        supervisor.Console.Append(Core.Instances.ConsoleOrigin.Notice, ServerProperties.UpdatedNotice);
        return null;
    }

    /// <param name="keepWhatTheLibraryCannotReplace">
    /// True when switching a pack off (<c>T277</c>): a folder is deleted only when
    /// the library holds the same uuid and version, so Switch on can put it back.
    /// Any other folder was placed by hand, and deleting it deleted the only copy;
    /// it stays, and the list write alone switches it off. An update replaces the
    /// folder with the library's version, so it still removes the old one.
    /// </param>
    private async Task ActAsync(
        InstanceSupervisor supervisor,
        string worldDirectory,
        AddonFileAction action,
        bool keepWhatTheLibraryCannotReplace,
        CancellationToken cancellationToken)
    {
        if (action.Kind == AddonFileActionKind.AlreadyInPlace)
        {
            // Registered by the list write; the files are where they are.
            return;
        }

        if (action.Kind == AddonFileActionKind.RemoveFromServer)
        {
            // T280: a uuid that is not a GUID names no folder the library put
            // there, since the library refuses such a pack, and asking for its
            // folder name threw past the page and ended the circuit.
            if (!AddonLibraryLayout.IsUsableIdentity(action.Uuid))
            {
                return;
            }

            var uuid = AddonLibraryLayout.FolderName(action.Uuid);

            // Every folder it could be in, not the one its manifest claims. The
            // manifest is gone once the folder is, and a pack put in the wrong
            // place by an older build is exactly the one somebody is trying to
            // get rid of.
            //
            // BOTH HOMES THIS WRAPPER HAS WRITTEN TO, and that is what makes the
            // move to the world need no migration: a pack an earlier build copied
            // into the server root is deleted from where it actually is rather
            // than from where this build would have put it (`T265`). Never the
            // development folder, which nothing here wrote (`T279`).
            foreach (var home in PackPlacementLayout.Swept)
            {
                foreach (var placement in PackPlacementLayout.Placed)
                {
                    if (PackPlacementLayout.DirectoryFor(
                            supervisor.WorkingDirectory, worldDirectory, home, placement)
                        is not { } directory)
                    {
                        continue;
                    }

                    var path = Path.Combine(directory, uuid);

                    if (!Directory.Exists(path))
                    {
                        continue;
                    }

                    if (keepWhatTheLibraryCannotReplace && !LibraryCanReplace(path))
                    {
                        logger.LogInformation(
                            "Kept {Path} on {Instance}: the add-on library holds no copy of it.",
                            path, supervisor.InstanceId);
                        continue;
                    }

                    Directory.Delete(path, recursive: true);
                }
            }

            return;
        }

        if (action.Version is { } version
            && library.Find(action.Uuid, version) is { } shelved)
        {
            await library
                .CopyToInstanceAsync(
                    shelved, supervisor.WorkingDirectory, worldDirectory, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether the library holds the pack in <paramref name="folder"/>, by the uuid
    /// and version its own manifest names. A folder whose manifest cannot be read is
    /// not one the library can replace.
    /// </summary>
    private bool LibraryCanReplace(string folder)
    {
        try
        {
            var manifest = Path.Combine(folder, AddonLibraryLayout.ManifestFileName);

            return File.Exists(manifest)
                && PackManifestReader.Read(PackFolder.ReadText(manifest)).Manifest is { } read
                && read.Version.Version is { } version
                && library.Find(read.Uuid, version) is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private IEnumerable<(InstalledPack Pack, PackSettingsSchema Schema)> PacksIn(
        string folder, PackHome home)
    {
        if (!Directory.Exists(folder))
        {
            yield break;
        }

        foreach (var pack in Directory.EnumerateDirectories(folder))
        {
            (InstalledPack Pack, PackSettingsSchema Schema)? read;

            try
            {
                var manifest = Path.Combine(pack, AddonLibraryLayout.ManifestFileName);

                // One read of the file for both, so a pack's controls and its
                // identity can never come from different bytes.
                var text = File.Exists(manifest) ? PackFolder.ReadText(manifest) : null;

                read = text is not null
                    && PackManifestReader.Read(text).Manifest is { } parsed
                        ? (new InstalledPack(
                               Named(parsed, pack), Files(pack), home, pack), PackSettingsReader.Read(text))
                        : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not read a pack under {Folder}.", folder);
                read = null;
            }

            if (read is { } found)
            {
                yield return found;
            }
        }
    }

    /// <summary>Give a pack something a person can read as its name.</summary>
    /// <remarks>
    /// The library path already does this; this one did not, so a stock
    /// instance's tab printed <c>pack.name</c> fifteen times over. The language
    /// overlay is applied first and the folder name is the fallback, because
    /// most of the packs that carry a key ship no language file to resolve it.
    /// </remarks>
    private static PackManifest Named(PackManifest manifest, string packDirectory)
    {
        var language = PackFolder.ReadLanguage(packDirectory);

        return manifest.WithDisplayText(language) with
        {
            Name = PackDisplayName.For(
                manifest.Name, language, Path.GetFileName(packDirectory.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))),
        };
    }

    private static IReadOnlyList<string> Files(string pack)
    {
        try
        {
            return
            [
                .. Directory.EnumerateFiles(pack, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(pack, f).Replace(Path.DirectorySeparatorChar, '/'))
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The pack still reads; only the conflict check loses its input,
            // and an empty list reports no conflict rather than a false one.
            return [];
        }
    }

    /// <summary>The world this instance loads, or why it cannot be named.</summary>
    /// <remarks>
    /// <para><strong>Four ways to have no world, each said as itself</strong>
    /// (<c>T290</c> clause 4). This returned a bare null for all of them and the tab
    /// said the world could not be found, so a <c>server.properties</c> the service
    /// was denied read as a world with the wrong name. The file is read through
    /// <see cref="ServerProperties.ReadFile(string)"/>, whose three states are the
    /// first two of these.</para>
    ///
    /// <para><see cref="Directory.Exists(string)"/> answers false for a folder it
    /// may not look at as well as for one that is not there, so the last sentence
    /// says the world could not be found in its folder rather than that it is
    /// absent.</para>
    /// </remarks>
    private WorldLookup FindWorld(string root)
    {
        var properties = ReadProperties(ServerProperties.PathFor(root));

        switch (properties.State)
        {
            case PropertiesReadState.Missing:
                return new WorldLookup(null,
                    $"This server has no {ServerProperties.FileName}, so which world it loads is not "
                    + "known and nothing here can say which add-ons it is using.");

            case PropertiesReadState.Unreadable:
                return new WorldLookup(null,
                    $"{ServerProperties.FileName} could not be read, so which world this server loads "
                    + "is not known and nothing here can say which add-ons it is using"
                    + (properties.Why is { Length: > 0 } why ? $": {why.TrimEnd('.')}." : "."));
        }

        // level-name must match the folder exactly, including case, so it is
        // used as given rather than matched loosely.
        if (!properties.Values.TryGetValue("level-name", out var name) || name.Length == 0)
        {
            return new WorldLookup(null,
                $"{ServerProperties.FileName} names no world, because level-name is not set, so nothing "
                + "here can say which add-ons this server is using.");
        }

        var world = Path.Combine(root, "worlds", name);

        return Directory.Exists(world)
            ? new WorldLookup(world, null)
            : new WorldLookup(null,
                $"This server's world, {name}, could not be found in its worlds folder, so nothing "
                + "here can say which add-ons it is using. Bedrock creates it the first time the "
                + $"server starts; if it has started, check that level-name in {ServerProperties.FileName} "
                + "names a world that exists.",
                NotCreated: true);
    }

    /// <summary>A world folder, or the sentence saying why there is none.</summary>
    private readonly record struct WorldLookup(string? Directory, string? Reason, bool NotCreated = false);

    /// <summary>
    /// Does this server let packs run scripts, or could that not be read?
    /// </summary>
    /// <remarks>
    /// Measured to ship correct on a stock 1.26.51.1 tree, so a false here is
    /// something that was changed rather than a default — which is worth saying,
    /// because it makes the finding rarer and more surprising rather than noise.
    /// </remarks>
    private bool? ScriptsPermitted(string root)
    {
        var file = Path.Combine(root, "config", "default", "permissions.json");

        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(file));

            return document.RootElement.TryGetProperty("allowed_modules", out var modules)
                && modules.ValueKind == JsonValueKind.Array
                && modules.EnumerateArray().Any(m =>
                    m.ValueKind == JsonValueKind.String
                    && string.Equals(m.GetString(), "@minecraft/server", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "Could not read script permissions for {Root}.", root);
            return null;
        }
    }

    /// <summary>
    /// Read the world's pack list, telling a file that is not there apart from
    /// one that will not open.
    /// </summary>
    /// <remarks>
    /// The distinction is the whole reason this is here rather than a helper
    /// returning a nullable string: absent yields an empty list a write may
    /// safely replace, and unreadable must never do that — the file is there
    /// and holds registrations nobody can see.
    /// </remarks>
    private WorldPackListRead ReadRegistrations(string path)
    {
        string text;

        try
        {
            if (!File.Exists(path))
            {
                return new WorldPackListRead(WorldPackListState.Absent, WorldPackList.Empty, null);
            }

            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read a world's pack list at {Path}.", path);
            return new WorldPackListRead(
                WorldPackListState.Unreadable, WorldPackList.Empty, ex.Message);
        }

        return WorldPackList.Read(text);
    }

    private (PackSettingsDocumentState State, PackSettingsDocument Document) ReadSettings(string path)
    {
        string text;

        try
        {
            if (!File.Exists(path))
            {
                return (PackSettingsDocumentState.Absent, new PackSettingsDocument(null, []));
            }

            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read a world's pack settings at {Path}.", path);
            return (PackSettingsDocumentState.Unreadable, new PackSettingsDocument(null, []));
        }

        var read = PackSettingsDocument.Read(text);
        return (read.State, read.Document);
    }
}
