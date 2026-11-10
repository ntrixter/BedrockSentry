using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using BedrockSentry.Core.Broker;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Players;
using BedrockSentry.Core.Structures;
using BedrockSentry.Core.Sync;
using BedrockSentry.Services.Broker;
using BedrockSentry.Services.Configuration;
using BedrockSentry.Services.Packages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using BedrockSentry.Services.Addons;

namespace BedrockSentry.Services.Instances;

/// <summary>
/// The set of managed instances, built once from configuration.
///
/// Instance is a first-class concept, not a singleton: everything here is keyed
/// by instance id from the outset, even though the management UI does not arrive
/// until milestone 16. Retrofitting a collection onto code that assumed one
/// server is the expensive version of this.
/// </summary>
public sealed class InstanceRegistry : IAsyncDisposable
{
    // Concurrent because instances can now be created while the status page and
    // the restart scheduler are reading the collection.
    private readonly ConcurrentDictionary<string, InstanceSupervisor> _supervisors =
        new(StringComparer.OrdinalIgnoreCase);

    // Keyed rather than a bag: removing an instance has to close its transcript
    // before the folder holding it can be deleted, and a bag cannot give one back.
    private readonly ConcurrentDictionary<string, ConsoleLogWriter> _writers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<InstanceRegistry> _log;
    private readonly ISentryPaths _paths;
    private readonly IBedrockProcessFactory _processFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ServerPackageStore _packages;
    private readonly IInstancePlayerData _playerData;

    /// <summary>
    /// Handed to every supervisor so the LAN discovery pair stays with the
    /// instance that configured it, whatever order they start in (<c>D24</c>).
    /// </summary>
    private readonly LanDiscoveryPortGuard? _discoveryGuard;

    /// <summary>Handed to every supervisor so console lines become sessions (<c>D25</c>).</summary>
    private readonly IPlayerStore? _players;

    /// <summary>Handed to every supervisor so a version change is dated when it happens.</summary>
    private readonly ServerVersionLog? _versions;

    /// <summary>Switches the companion pack on for a new server before its first start (<c>D93</c>).</summary>
    private readonly InstanceAddons? _addons;
    private readonly IFirewallService? _firewall;

    /// <summary>What to say, or ask the broker, about a folder outside the data root (<c>D120</c>).</summary>
    private readonly OutsideFolders? _outside;

    /// <summary>Serialises instance creation against itself.</summary>
    private readonly SemaphoreSlim _createGate = new(1, 1);

    /// <summary>How a directory tree is removed. Replaced only by tests.</summary>
    /// <remarks>
    /// A seam, and it exists for one reason. The part-way-failure arm of
    /// <see cref="TryDeleteDirectory"/> is the arm that reported data loss as no loss
    /// (<c>T176</c>), and reproducing it for real needs a file lock that Windows
    /// enforces and Unix does not — so without this the arm would stay uncovered on
    /// the platform this code is written on, which is exactly how it survived. The
    /// real locking behaviour is still Windows-only (<c>T176</c>); what this covers is
    /// that a failed delete is *reported* as a partial one all the way to the record.
    /// Production never replaces it.
    /// </remarks>
    internal Action<string> DeleteDirectoryTree { get; set; } =
        static directory => Directory.Delete(directory, recursive: true);

    public InstanceRegistry(
        IOptionsMonitor<InstanceOptions> options,
        ISentryPaths paths,
        IBedrockProcessFactory processFactory,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        ServerPackageStore packages,
        IInstancePlayerData playerData,
        LanDiscoveryPortGuard? discoveryGuard = null,
        IPlayerStore? players = null,
        ServerVersionLog? versions = null,
        InstanceAddons? addons = null,
        IFirewallService? firewall = null,
        OutsideFolders? outside = null)
    {
        _outside = outside;
        _addons = addons;
        _firewall = firewall;
        _discoveryGuard = discoveryGuard;
        _players = players;
        _versions = versions;
        _log = loggerFactory.CreateLogger<InstanceRegistry>();
        _paths = paths;
        _processFactory = processFactory;
        _timeProvider = timeProvider;
        _loggerFactory = loggerFactory;
        _packages = packages;
        _playerData = playerData;
        Options = options.CurrentValue;

        Root = string.IsNullOrWhiteSpace(Options.Root)
            ? paths.Instances
            : Path.GetFullPath(Options.Root);

        if (_discoveryGuard is { } guard)
        {
            // Live, not captured: instances are created and retired at runtime,
            // and the guard holds the discovery pair only while some configured
            // instance actually owns it. Reads each instance's own file, the
            // same source of truth the supervisor's start uses.
            guard.OwnerExists = () => _supervisors.Values.Any(s =>
                guard.OwnsDiscoveryPair(PortInspector.ModelOf(s.WorkingDirectory)));

            // Whether that owner is up. The guard needs the two apart to know what a
            // failed bind means: the owner running is holding the pair itself and
            // wants no help, while the owner stopped means something else has it and
            // the owner is locked out of its own ports.
            guard.OwnerRunning = () => _supervisors.Values.Any(s =>
                guard.OwnsDiscoveryPair(PortInspector.ModelOf(s.WorkingDirectory))
                && s.Status.State is not (InstanceState.Stopped or InstanceState.Faulted));
        }

        foreach (var (id, definition) in Options.Servers)
        {
            if (definition.Retired)
            {
                // D14: retired instances keep their player history but are never
                // launched again, and get no supervisor to launch them with.
                _log.LogInformation("Instance {InstanceId} is retired; skipping", id);
                continue;
            }

            var workingDirectory = InstanceLayout.ResolveWorkingDirectory(Root, id, definition);

            // Created eagerly so an administrator has somewhere obvious to put
            // bedrock_server.exe, and so the path appears in the log before the
            // first failed start rather than after it.
            try
            {
                Directory.CreateDirectory(workingDirectory);
            }
            catch (Exception ex)
            {
                _log.LogError(
                    ex, "Could not create the working directory for {InstanceId} at {Directory}",
                    id, workingDirectory);
            }

            Register(id, definition, workingDirectory);
            WarnIfUnderAUserProfile(id, workingDirectory);

            _log.LogInformation(
                "Instance {InstanceId} ({Name}) will run from {Directory}",
                id, definition.Name, workingDirectory);
        }

        if (_supervisors.Count == 0)
        {
            _log.LogInformation(
                "No instances are configured. Add them under '{Section}' in the config file; "
                + "the default root is {Root}", InstanceOptions.SectionName, Root);
        }

        // Config reloads on change, but the instance set is captured once — adding
        // a server mid-run would mean creating supervisors, directories and log
        // writers underneath running code. Saying so beats an edit that appears to
        // do nothing until the next restart.
        options.OnChange(updated =>
        {
            // Compared against what is actually loaded, not against the startup
            // snapshot: creating an instance from the UI writes the config file
            // too, and warning that our own change needs a restart would be both
            // wrong and alarming.
            var configured = updated.Servers
                .Where(s => !s.Value.Retired)
                .Select(s => s.Key);

            if (configured.Except(_supervisors.Keys, StringComparer.OrdinalIgnoreCase).Any()
                || _supervisors.Keys.Except(configured, StringComparer.OrdinalIgnoreCase).Any())
            {
                _log.LogWarning(
                    "The configured instance list was edited by hand. Restart the BedrockSentry "
                    + "service for it to take effect.");
            }
        });
    }

    /// <summary>
    /// Flags an instance configured to run from inside somebody's user profile.
    ///
    /// It is allowed — the working directory override exists precisely so a world
    /// can live wherever the administrator wants — but a user profile is the one
    /// destination that is quietly wrong. The service runs as its own account, not
    /// as the person who put the files there; profile folders are routinely
    /// redirected into OneDrive, where files become cloud placeholders that are
    /// not actually on disk; and Downloads in particular is a folder people empty
    /// without thinking, which for a Minecraft server means deleting the world.
    ///
    /// A warning rather than a refusal: it is the administrator's machine, and a
    /// wrapper that declines to run a server because it dislikes the path would
    /// be worse than one that says so and carries on. Since <c>D43</c> the
    /// container itself and each account's own root <em>are</em> refused, so what
    /// reaches this warning is the reasonable case — a folder somebody chose inside
    /// their own documents.
    /// </summary>
    /// <remarks>
    /// The profile half reads <see cref="ReservedSystemDirectories.AccountsFolder"/>,
    /// the same lookup the refusal uses, rather than rebuilding <c>%SystemDrive%\Users</c>
    /// here. It used to do the latter, and matched it as a prefix — so
    /// <c>C:\UsersData</c> warned. The OneDrive half is still deliberately a
    /// heuristic: there is no folder to resolve, only a name that turns up in a
    /// redirected path, and this produces a log line rather than a decision.
    /// </remarks>
    private void WarnIfUnderAUserProfile(string instanceId, string workingDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var underProfile = ReservedSystemDirectories.IsAtOrUnder(
            workingDirectory, ReservedSystemDirectories.AccountsFolder);

        var underOneDrive = workingDirectory.Contains("OneDrive", StringComparison.OrdinalIgnoreCase);

        if (!underProfile && !underOneDrive)
        {
            return;
        }

        if (SuggestsInstanceRoot(workingDirectory, Root, ReservedSystemDirectories.AccountsFolder))
        {
            _log.LogWarning(
                "Instance {InstanceId} is configured to run from {Directory}, which is inside a user "
                + "profile. The service runs as its own account and does not own that folder, and "
                + "profile folders are often synced to OneDrive or cleared out by hand — either of "
                + "which loses the world. Prefer the instance root at {Root}.",
                instanceId, workingDirectory, Root);
            return;
        }

        _log.LogWarning(
            "Instance {InstanceId} is configured to run from {Directory}, which is inside a user "
            + "profile. The service runs as its own account and does not own that folder, and "
            + "profile folders are often synced to OneDrive or cleared out by hand — either of "
            + "which loses the world. Prefer a folder outside every user profile and OneDrive.",
            instanceId, workingDirectory);
    }

    /// <summary>
    /// Whether the profile warning may point at <paramref name="root"/>: only when the
    /// server is not already in it and the root is not in a profile or OneDrive itself
    /// (<c>T397</c>).
    /// </summary>
    internal static bool SuggestsInstanceRoot(string workingDirectory, string root, string? accountsFolder) =>
        !ReservedSystemDirectories.IsAtOrUnder(workingDirectory, root)
        && !ReservedSystemDirectories.IsAtOrUnder(root, accountsFolder)
        && !root.Contains("OneDrive", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What every supervisor asks before it launches (<c>T149</c>). Set after
    /// construction rather than taken by the constructor, because the thing
    /// that answers it — <see cref="PortInspector"/> — takes <em>this</em>
    /// registry, and the registry builds the supervisors. Same shape as the
    /// discovery guard's callbacks. Supervisors read it live, so an instance
    /// created after it is set is covered too.
    /// </summary>
    public IStartPreflight? StartPreflight { get; set; }

    /// <summary>
    /// What every supervisor asks before it launches, about processes on its
    /// world rather than on its port (<c>D56</c>). Set after construction for
    /// the same reason as <see cref="StartPreflight"/>, and read live, so an
    /// instance created later is covered too.
    /// </summary>
    public IOrphanInspector? Orphans { get; set; }

    /// <summary>
    /// Work every launch does first, with the process down (<see cref="IBeforeLaunch"/>).
    /// Wired like the two above; null does nothing.
    /// </summary>
    public IBeforeLaunch? BeforeLaunch { get; set; }

    /// <summary>
    /// The operating system's own directories, which no server may live in
    /// (<c>D43</c>). Settable so a test can point the guard at a pretend OS tree
    /// under its own temp folder — the real rules name paths a test must not create
    /// files in. Same shape as the two seams above.
    /// </summary>
    internal ImmutableArray<ReservedSystemDirectory> SystemDirectories { get; set; } =
        ReservedSystemDirectories.All;

    /// <summary>
    /// Builds the supervisor and its transcript writer, and adds them to the
    /// collection. Shared by startup and by runtime creation so the two cannot
    /// drift into producing differently-configured instances.
    /// </summary>
    private InstanceSupervisor Register(string id, InstanceDefinition definition, string workingDirectory)
    {
        var supervisor = new InstanceSupervisor(
            id,
            definition,
            workingDirectory,
            _processFactory,
            _timeProvider,
            _loggerFactory.CreateLogger<InstanceSupervisor>(),
            Options.ConsoleLog.BufferedLines,
            _discoveryGuard,
            _players,
            _versions,
            () => StartPreflight,
            () => Orphans,
            () => BeforeLaunch);

        _supervisors[id] = supervisor;
        supervisor.AreaCommand += (from, command) => AreaCommand?.Invoke(from, command);
        supervisor.TickShow += (from, command) => TickShow?.Invoke(from, command);
        supervisor.RestoreCommand += (from, command) => RestoreCommand?.Invoke(from, command);
        supervisor.CompanionHello += from => CompanionHello?.Invoke(from);
        supervisor.StatusChanged += status => StatusChanged?.Invoke(supervisor, status);

        if (Options.ConsoleLog.Enabled)
        {
            _writers[id] = new ConsoleLogWriter(
                id,
                supervisor.Console,
                TranscriptDirectory(id),
                Options.ConsoleLog,
                _loggerFactory.CreateLogger<ConsoleLogWriter>());
        }

        return supervisor;
    }

    /// <summary>
    /// Takes over a Bedrock server that already exists on disk (<c>docs/design.md</c> § 4.4).
    /// </summary>
    /// <remarks>
    /// Registration, not installation: nothing is extracted, no port is written,
    /// and the folder's own <c>server.properties</c>, allowlist, permissions and
    /// worlds are left exactly as found — the wrapper writes only
    /// <c>server-name</c>, which the form pre-fills from the file so writing it
    /// back is usually the identity. Auto-start is the caller's explicit choice
    /// and the UI defaults it off, because the server being adopted may well be
    /// running under whatever ran it yesterday, and the wrapper starting a
    /// second copy uninvited would fight it for the port.
    /// </remarks>
    /// <summary>Whether this service can write a file in <paramref name="folder"/>: one probe, written and removed.</summary>
    internal static bool Writable(string folder)
    {
        var probe = Path.Combine(folder, $".bedrocksentry-probe-{Guid.NewGuid():n}");

        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public async Task<InstanceSupervisor> AdoptAsync(
        string name,
        string directory,
        bool autoStart = false,
        CancellationToken cancellationToken = default)
    {
        RefuseAnUnsupportedPathForm(directory);

        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory))
        {
            throw new InvalidOperationException(
                "Adopting a server needs the full path to its folder.");
        }

        var full = Path.GetFullPath(directory);

        RefuseAManagedOrReservedDirectory(full);

        // Through the same rule the panel and the launch guard read, so the
        // three cannot come to disagree about which file makes a folder a server.
        //
        // `T252`. A Directory.Exists check stood here and answered this question
        // first, which put the same swallow one folder up from the one `T246` was
        // filed for: it reports false for a folder this service may not traverse,
        // so a denied folder was refused with "does not exist" and somebody was
        // sent to look for a folder sitting where they left it. PresenceOf
        // separates the two from the exception the read itself throws, so the
        // check is gone rather than duplicated.
        var defaults = new InstanceDefinition();
        var executable = InstanceLayout.ExecutablePath(full, defaults);

        var presence = InstanceLayout.PresenceOf(full, defaults);

        // D120: a folder outside the data root the service may not read, or may read
        // and not write, is one an administrator has not handed over yet; say how. It
        // holds a server, so the broker would refuse it too, and adoption writes
        // server.properties before anything else could ask (T391 clause 9).
        if (_outside is { } outside && outside.IsOutside(full)
            && (presence.State is ServerPresenceState.Unreadable or ServerPresenceState.Unknown
                || (presence.CanLaunch && !Writable(full))))
        {
            throw new InvalidOperationException(outside.Refusal(full));
        }

        if (!presence.CanLaunch)
        {
            // The one thing an instance cannot run without. Checked here rather
            // than discovered at first start, where it reads as a crash — and
            // said differently when the file is there and refused, because
            // "it does not look like a Bedrock server folder" is then false and
            // sends somebody to re-download a server they already have (`T246`).
            throw new InvalidOperationException(
                MissingServerText.AdoptionRefusal(
                    full, Path.GetFileName(executable), presence));
        }

        return await CreateAsync(
                new NewInstanceRequest(
                    name,
                    PackageFileName: null,
                    Port: null,
                    AutoStart: autoStart,
                    WorkingDirectory: full,
                    AllowExistingFiles: true),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses a directory the wrapper already manages or owns.
    /// </summary>
    /// <remarks>
    /// Two instances over one folder means two supervisors writing one console
    /// transcript and fighting over one process — and adopting the wrapper's own
    /// config or backups directory as a "server" is nobody's intent, however the
    /// path was pasted. Nesting is refused in both directions: a server inside
    /// another server's folder is backed up by both and restored by either.
    /// </remarks>
    private void RefuseAManagedOrReservedDirectory(string full, string? exceptInstanceId = null)
    {
        foreach (var (id, definition) in Options.Servers)
        {
            if (string.Equals(id, exceptInstanceId, StringComparison.OrdinalIgnoreCase))
            {
                // The instance being moved is allowed to overlap itself —
                // otherwise no move could ever name a folder near its own.
                continue;
            }

            var existing = InstanceLayout.ResolveWorkingDirectory(Root, id, definition);

            if (PathsOverlap(existing, full))
            {
                throw new InvalidOperationException(
                    $"'{full}' overlaps '{existing}', which is already managed as "
                    + $"{definition.Name} ({id})"
                    + (definition.Retired ? ", currently retired — restore it instead." : "."));
            }
        }

        string[] reserved =
        [
            _paths.Config, _paths.Certificates, _paths.DataProtectionKeys, _paths.Data,
            _paths.Backups, _paths.Update, _paths.Logs, _paths.ServerPackages,
        ];

        foreach (var owned in reserved)
        {
            if (PathsOverlap(owned, full))
            {
                throw new InvalidOperationException(
                    $"'{full}' is inside BedrockSentry's own data at '{owned}' — a server "
                    + "cannot live there.");
            }
        }

        if (string.Equals(
                Path.TrimEndingDirectorySeparator(full),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.DataRoot)),
                PathComparison))
        {
            throw new InvalidOperationException(
                $"'{full}' is BedrockSentry's data root itself, not a server folder.");
        }

        // D43 and D52. Asked here rather than at each door because every door —
        // create, adopt and re-home — already comes through this one, so all three are
        // covered by construction rather than by remembering.
        if (WhyNoServerMayLiveThere(full) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }
    }

    /// <summary>
    /// Why <paramref name="full"/> is a folder the operating system owns, a volume
    /// root, or somewhere this service cannot reach — or null (<c>D43</c>,
    /// <c>D52</c>).
    /// </summary>
    /// <remarks>
    /// <para>Separate from <see cref="RefuseAManagedOrReservedDirectory"/> so
    /// <see cref="SetRootAsync"/> can ask the same question without also inheriting
    /// the overlap rules, which the instances root is expected to overlap.</para>
    ///
    /// <para>There is no carve-out for the wrapper's own instances root: it is allowed
    /// because the ProgramData rule is folder-itself, and a moved root for the same
    /// reason. An explicit exception would be dead code today and would let
    /// <c>C:\Windows\instances\x</c> through tomorrow.</para>
    /// </remarks>
    private string? WhyNoServerMayLiveThere(string full) =>
        ReservedSystemDirectories.RefuseAnUnsupportedPathForm(full)
        ?? ReservedSystemDirectories.Refuse(full, SystemDirectories)
        ?? ReservedSystemDirectories.RefuseAVolumeRoot(full)
        ?? ReservedSystemDirectories.RefuseAVolumeFolder(full)
        ?? RefuseADriveThisMachineDoesNotHave(full);

    /// <summary>
    /// One refusal from <see cref="SetRootAsync"/>, said as what it means for the
    /// instances root rather than for one server.
    /// </summary>
    /// <remarks>
    /// Stated once because it was stated twice and applied once: two of the three
    /// refusal paths in that method carried the prefix and the raw-form one did not
    /// (<c>T183</c> clause 5).
    /// </remarks>
    private static string NotSomewhereNewServersMayGo(string refusal) =>
        "New servers would be created inside a folder no server may use. " + refusal;

    /// <summary>
    /// Why <paramref name="requested"/> would be refused as a server's folder, or
    /// null — the same answer <see cref="AdoptAsync"/> gives, available before the
    /// operator presses the button (<c>D43</c>, <c>D52</c>).
    /// </summary>
    /// <remarks>
    /// <para>Exists for the Add-an-existing-server form, whose pre-flight said
    /// <em>"That folder does not exist, or the path is not a full path"</em> for a
    /// mapped drive — technically true, reads as a lie, and the operator retypes the
    /// path three times and concludes the wrapper is broken. This is where they
    /// actually meet <c>D52</c>, and neither decision mentioned it.</para>
    ///
    /// <para>Both callers want the <em>same</em> answer here, which is what makes
    /// sharing the method right rather than the mistake <c>T151</c> clause 6 was:
    /// a pre-flight that says something other than the refusal is a pre-flight
    /// that lies. The test pins the two to one string.</para>
    ///
    /// <para>It deliberately answers nothing about overlap with another instance or
    /// with the wrapper's own data. Those need the instance list and belong to the
    /// attempt, not to a keystroke.</para>
    /// </remarks>
    /// <summary>
    /// The adoption form's sentence for a folder it could not read, the same one
    /// <see cref="AdoptAsync"/> gives (<c>T391</c> clause 9).
    /// </summary>
    public string? AdoptionRefusalFor(BdsFolderReport report, string folder, string executable) =>
        _outside is { } outside
            ? outside.AdoptionRefusal(report, folder, executable)
            : report.RefusalSentence(folder, executable);

    public string? WhyThisFolderCannotHoldAServer(string? requested)
    {
        if (ReservedSystemDirectories.RefuseAnUnsupportedPathForm(requested) is { } form)
        {
            return form;
        }

        if (string.IsNullOrWhiteSpace(requested) || !Path.IsPathRooted(requested))
        {
            // Half-typed. The form has its own sentence for that and this is called
            // on every keystroke.
            return null;
        }

        try
        {
            return WhyNoServerMayLiveThere(Path.GetFullPath(requested));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// The raw-input half of <c>D52</c>, which has to run before each door's own
    /// "needs a full path" sentence.
    /// </summary>
    /// <remarks>
    /// Every door calls this as well as <see cref="RefuseAManagedOrReservedDirectory"/>,
    /// which asks the same question again about the normalised path. Not duplication,
    /// and the reason is stated once here rather than at each call. By the time the
    /// second ask happens <c>Path.GetFullPath</c> has run, and off Windows that mangles
    /// a UNC string into something unrecognisable. The door-level call is also what
    /// puts the useful sentence ahead of <c>'\\fileserver\x' is not a full path</c> —
    /// which is true on Linux, false on Windows, and no help on either.
    /// </remarks>
    private static void RefuseAnUnsupportedPathForm(string? requested)
    {
        if (ReservedSystemDirectories.RefuseAnUnsupportedPathForm(requested) is { } form)
        {
            throw new InvalidOperationException(form);
        }
    }

    /// <summary>
    /// Why <paramref name="full"/> is on a drive this service cannot reach, or null
    /// (<c>D52</c>).
    /// </summary>
    /// <remarks>
    /// <para><strong><c>NoRootDirectory</c> is the operative answer and
    /// <c>Network</c> is the backstop, which is the opposite way round from how this
    /// reads.</strong> Drive mappings live in the logon session's device namespace.
    /// This service runs as a service account, whose session has no <c>Z:</c>, so Windows
    /// answers <c>DRIVE_NO_ROOT_DIR</c> and the service genuinely cannot tell "the
    /// drive you mapped in Explorer" from "a letter nobody ever assigned". It does not
    /// need to: both are equally unusable from here, and the message says both.
    /// <c>Network</c> still happens, for a mapping made under SYSTEM itself.</para>
    ///
    /// <para><see cref="DriveInfo.DriveType"/> rather than <c>Directory.Exists</c> on
    /// the root: the latter blocks for the connect timeout on an unreachable share,
    /// and the panel is this process.</para>
    /// </remarks>
    private static string? RefuseADriveThisMachineDoesNotHave(string full)
    {
        if (!OperatingSystem.IsWindows() || Path.GetPathRoot(full) is not { Length: > 0 } root)
        {
            return null;
        }

        DriveType type;
        try
        {
            type = new DriveInfo(root).DriveType;
        }
        catch (ArgumentException)
        {
            return null;
        }

        // The read is here because it is the Windows call; the sentences are in Core
        // because a rule only reachable through a real mapped drive is a rule tested
        // on a real mapped drive or nowhere (T183 clause 2).
        return type switch
        {
            DriveType.Network => ReservedSystemDirectories.UnreachableDrive(full, root, networkDrive: true),
            DriveType.NoRootDirectory => ReservedSystemDirectories.UnreachableDrive(full, root, networkDrive: false),
            _ => null,
        };
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Equal, or one inside the other.</summary>
    /// <remarks>
    /// <see cref="Path.TrimEndingDirectorySeparator(string)"/> deliberately never
    /// trims a bare volume root — <c>"D:\"</c> stays <c>"D:\"</c> — so appending a
    /// separator unconditionally would compare against a double separator that
    /// can never match, and an instance adopted at a drive root would never be
    /// found to overlap anything else on that volume. Adding the separator only
    /// when it is not already there handles the root the same as everything else.
    /// </remarks>
    internal static bool PathsOverlap(string a, string b)
    {
        var left = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
        var right = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));

        return string.Equals(left, right, PathComparison)
            || Contains(left, right)
            || Contains(right, left);

        bool Contains(string outer, string inner)
        {
            var prefix = Path.EndsInDirectorySeparator(outer)
                ? outer
                : outer + Path.DirectorySeparatorChar;
            return inner.StartsWith(prefix, PathComparison);
        }
    }

    /// <summary>
    /// Re-homes one instance's folder (<c>docs/design.md</c> § 4.3): pins the new path
    /// and rebuilds the supervisor against it. The files move by hand.
    /// </summary>
    /// <returns>
    /// True when the new folder already holds the server's executable — the
    /// caller uses it to say either "done" or "now move the files before
    /// starting".
    /// </returns>
    /// <remarks>
    /// Refused unless the instance is fully stopped — not merely "not running":
    /// a server in backoff is one the watchdog is about to start, and a start
    /// racing a folder move would launch from whichever path won. The service
    /// itself never stops; only this one instance has to. The supervisor is
    /// released and re-registered rather than mutated, because its working
    /// directory threads through the process factory, the transcript path and
    /// the discovery guard at construction — the same rebuild retiring and
    /// restoring already exercise.
    ///
    /// Order matters the same way it does everywhere else in this class: config
    /// first, then the live objects, so a failure leaves the wrapper still
    /// matching what the file says. And the old folder is never touched — the
    /// administrator moves the contents themselves, which is also what makes
    /// "point it at a copy you already made" and "re-home to a folder you moved
    /// yesterday" the same operation.
    /// </remarks>
    public async Task<bool> SetWorkingDirectoryAsync(
        string instanceId, string newDirectory, CancellationToken cancellationToken = default)
    {
        RefuseAnUnsupportedPathForm(newDirectory);

        if (string.IsNullOrWhiteSpace(newDirectory) || !Path.IsPathRooted(newDirectory))
        {
            throw new InvalidOperationException(
                "The server's folder needs a full path, like D:\\minecraft\\survival.");
        }

        var full = Path.GetFullPath(newDirectory);

        await _createGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var supervisor = Get(instanceId);

            if (string.Equals(
                    Path.TrimEndingDirectorySeparator(full),
                    Path.TrimEndingDirectorySeparator(supervisor.WorkingDirectory),
                    PathComparison))
            {
                return File.Exists(Path.Combine(full, supervisor.Definition.Executable));
            }

            // Before the state is read: a sync stops the server part-way through,
            // and a stopped server inside a claimed job is not one that can move.
            using var claim = await supervisor.ClaimAsync(ClaimText.Move, cancellationToken).ConfigureAwait(false);

            if (supervisor.Status.State is not (InstanceState.Stopped or InstanceState.Faulted))
            {
                throw new InvalidOperationException(
                    "Stop the server first. Its folder cannot move while it is running — "
                    + "or waiting to restart, which is a start about to happen.");
            }

            RefuseAManagedOrReservedDirectory(full, exceptInstanceId: instanceId);

            var definition = supervisor.Definition;
            var previous = supervisor.WorkingDirectory;

            try
            {
                Directory.CreateDirectory(full);
            }
            catch (UnauthorizedAccessException ex) when (_outside is { } outside && outside.IsOutside(full))
            {
                await MoveHandOverAsync(outside, instanceId, definition.WorkingDirectory, full, ex, cancellationToken).ConfigureAwait(false);
            }

            await JsonConfigWriter
                .MergeAsync(
                    ConfigPath,
                    new Dictionary<string, JsonNode?>
                    {
                        [$"{InstanceOptions.SectionName}:Servers:{instanceId}:WorkingDirectory"] =
                            JsonValue.Create(full),
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            definition.WorkingDirectory = full;

            await ReleaseAsync(supervisor, claim).ConfigureAwait(false);
            Register(instanceId, definition, full);
            WarnIfUnderAUserProfile(instanceId, full);

            _log.LogInformation(
                "Instance {InstanceId} re-homed from {Previous} to {Directory}",
                instanceId, previous, full);

            return File.Exists(Path.Combine(full, definition.Executable));
        }
        finally
        {
            _createGate.Release();
        }
    }

    /// <summary>
    /// Moves where <em>new</em> instances go, without moving anything that exists.
    /// </summary>
    /// <remarks>
    /// The order is load-bearing in two ways. First, every configured instance
    /// with no <c>WorkingDirectory</c> of its own is pinned to the absolute path
    /// it resolves to today, in the same config merge as the new root — an
    /// instance's own definition is not mutated to match until that merge
    /// actually succeeds, so a failure (locked file, full disk) leaves nothing
    /// pinned in memory that the file does not also hold; a retry starts clean
    /// rather than skipping instances a failed attempt already marked pinned.
    /// Second, this refuses outright when the config file on disk lists a
    /// different instance set than this process loaded — the same hand-edit
    /// drift the constructor's <c>OnChange</c> handler warns about — because
    /// pinning only what is in memory would leave a hand-added instance
    /// unpinned in the very file whose <c>Root</c> is about to change under it.
    /// Existing servers stay exactly where they are either way; only servers
    /// created after this land under the new root.
    /// </remarks>
    public async Task SetRootAsync(string newRoot, CancellationToken cancellationToken = default)
    {
        // The raw-form half has to run before the rooted check, for the reason the
        // three doors give — but its sentence needs the same prefix as everything
        // else this method refuses. It did not have it (T183 clause 5): a UNC root
        // was the one refusal on this page that never said what it was about, while
        // C:\Windows\srvroot and Z:\roots both did. The ordering was deliberate and
        // the lost prefix was a side effect nobody chose.
        if (ReservedSystemDirectories.RefuseAnUnsupportedPathForm(newRoot) is { } form)
        {
            throw new InvalidOperationException(NotSomewhereNewServersMayGo(form));
        }

        if (string.IsNullOrWhiteSpace(newRoot) || !Path.IsPathRooted(newRoot))
        {
            throw new InvalidOperationException(
                "The instances folder needs a full path, like D:\\minecraft\\servers.");
        }

        var full = Path.GetFullPath(newRoot);

        // D43 and D52 here as well as at the three doors, so the operator learns when
        // they set the root rather than at the next create. Without it a root inside
        // C:\Windows is accepted happily and every create after it fails, with the
        // reason a page away from the setting that caused it.
        if (WhyNoServerMayLiveThere(full) is { } refusal)
        {
            throw new InvalidOperationException(NotSomewhereNewServersMayGo(refusal));
        }

        await _createGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.Equals(
                    Path.TrimEndingDirectorySeparator(full),
                    Path.TrimEndingDirectorySeparator(Root),
                    PathComparison))
            {
                return;
            }

            var configuredIds = await ReadConfiguredServerIdsAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!configuredIds.SetEquals(Options.Servers.Keys))
            {
                throw new InvalidOperationException(
                    "The configured instance list was edited by hand since the service "
                    + "started. Restart the service before changing where new instances go, "
                    + "so every existing instance is pinned correctly.");
            }

            // Prove the location works before anything is written: created if
            // missing, and a probe file written and removed. Failing now is a
            // message on the page; failing later is a create button that stopped
            // working with the reason three screens away.
            Directory.CreateDirectory(full);
            var probe = Path.Combine(full, $".bedrocksentry-probe-{Guid.NewGuid():n}");
            await File.WriteAllTextAsync(probe, "probe", cancellationToken).ConfigureAwait(false);
            File.Delete(probe);

            var values = new Dictionary<string, JsonNode?>
            {
                [$"{InstanceOptions.SectionName}:Root"] = JsonValue.Create(full),
            };

            var pins = new List<(InstanceDefinition Definition, string Path)>();

            foreach (var (id, definition) in Options.Servers)
            {
                if (!string.IsNullOrWhiteSpace(definition.WorkingDirectory))
                {
                    continue;
                }

                var pinned = InstanceLayout.ResolveWorkingDirectory(Root, id, definition);
                pins.Add((definition, pinned));
                values[$"{InstanceOptions.SectionName}:Servers:{id}:WorkingDirectory"] =
                    JsonValue.Create(pinned);
            }

            await JsonConfigWriter.MergeAsync(ConfigPath, values, cancellationToken)
                .ConfigureAwait(false);

            // Only now that the file agrees does memory follow it.
            foreach (var (definition, pinned) in pins)
            {
                definition.WorkingDirectory = pinned;
            }

            var previous = Root;
            Root = full;

            _log.LogInformation(
                "New instances now go under {Root} (was {Previous}); existing instances were "
                + "pinned where they are", full, previous);
        }
        finally
        {
            _createGate.Release();
        }
    }

    /// <summary>
    /// The instance ids the config file on disk actually lists, read fresh
    /// rather than from the boot-time snapshot in <see cref="Options"/>.
    /// </summary>
    private async Task<HashSet<string>> ReadConfiguredServerIdsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ConfigPath))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        await using var stream = File.OpenRead(ConfigPath);
        var root = await JsonNode.ParseAsync(
                stream,
                documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        JsonNode? node = root;
        foreach (var segment in $"{InstanceOptions.SectionName}:Servers".Split(':'))
        {
            node = node?[segment];
        }

        return node is JsonObject servers
            ? new HashSet<string>(servers.Select(kv => kv.Key), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The resolved default root. Individual instances may sit elsewhere.</summary>
    public string Root { get; private set; }

    public InstanceOptions Options { get; }

    /// <summary>Every live instance, retired ones excluded.</summary>
    public IReadOnlyCollection<InstanceSupervisor> All => _supervisors.Values.ToList();

    public bool TryGet(string instanceId, out InstanceSupervisor supervisor) =>
        _supervisors.TryGetValue(instanceId, out supervisor!);

    public InstanceSupervisor Get(string instanceId) =>
        _supervisors.TryGetValue(instanceId, out var supervisor)
            ? supervisor
            : throw new KeyNotFoundException($"No instance named '{instanceId}' is configured.");

    /// <summary>A snapshot of every instance, ordered by display name for the UI.</summary>
    public IReadOnlyList<InstanceStatus> Statuses() =>
        _supervisors.Values
            .Select(s => s.Status)
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>
    /// Creates an instance: makes its directory, optionally installs a server
    /// into it, writes it into the config file, and registers it live.
    /// </summary>
    /// <remarks>
    /// Registering it live is the point. Writing the config file alone would
    /// leave a "new instance" button whose result only appears after a service
    /// restart, which is barely better than editing the file by hand.
    /// </remarks>
    public async Task<InstanceSupervisor> CreateAsync(
        NewInstanceRequest request, CancellationToken cancellationToken = default)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            throw new ArgumentException("An instance needs a name.", nameof(request));
        }

        // Before anything is made. The parser does not read the address, so a line
        // break is refused on its own: it would put a second line into server.properties.
        if (!string.IsNullOrWhiteSpace(request.UdpPorts)
            && (request.UdpPorts.Trim().Any(char.IsControl)
                || ServerUdpPorts.Parse(request.UdpPorts) is not { IsPinned: true }))
        {
            throw new ArgumentException(
                $"\"{request.UdpPorts.Trim()}\" is not a {ServerUdpPorts.Key} value. It is a port range such "
                + "as 19132-19141, optionally after an address and the published range: "
                + "203.0.113.7:19132-19141:19132-19141.",
                nameof(request));
        }

        // D120: set once the server is written to the configuration ahead of the rest,
        // for the broker to grant its folder, so a later failure takes it out again.
        string? writtenEarly = null;

        await _createGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var id = InstanceId.MakeUnique(
                InstanceId.FromName(name),
                candidate => _supervisors.ContainsKey(candidate)
                             || Options.Servers.ContainsKey(candidate));

            var definition = new InstanceDefinition
            {
                Name = name,
                AutoStart = request.AutoStart,
                WorkingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
                    ? null
                    : request.WorkingDirectory,

                // `D40`. Through the create path rather than written afterwards, so
                // the note reaches the config file in the same write as everything
                // else — a second write would be a window in which the copy exists
                // and does not know what it is.
                RestoredFrom = request.RestoredFrom,
            };

            // The default folder is named after the instance, which two instances
            // sharing a display name would both resolve to — landing the second
            // one on top of the first's world. When the id had to be
            // disambiguated, the folder follows the id instead, and is recorded
            // explicitly so a restart resolves it the same way rather than going
            // back to the name.
            if (definition.WorkingDirectory is null && id != InstanceId.FromName(name))
            {
                definition.WorkingDirectory =
                    Path.Combine(Root, InstanceLayout.SanitiseFolderName(id, id));
            }

            RefuseAnUnsupportedPathForm(definition.WorkingDirectory);

            if (definition.WorkingDirectory is { } chosen && !Path.IsPathRooted(chosen))
            {
                // A relative path would resolve against the service's own
                // working directory, which for a Windows Service is System32 —
                // the server would land there, silently, and the mistake would
                // surface as a missing world.
                throw new InvalidOperationException(
                    $"'{chosen}' is not a full path. A server's folder needs one, "
                    + "like D:\\minecraft\\survival.");
            }

            var workingDirectory = InstanceLayout.ResolveWorkingDirectory(Root, id, definition);

            // The same rule adoption enforces, for the same reasons: never two
            // instances over one folder, never a server inside the wrapper's
            // own data — whether the folder was chosen explicitly or derived
            // from the instance's name. An unchecked default folder can just as
            // easily land on ground something else already manages.
            RefuseAManagedOrReservedDirectory(workingDirectory);

            try
            {
                Directory.CreateDirectory(workingDirectory);
            }
            catch (UnauthorizedAccessException ex) when (_outside is { } outside && outside.IsOutside(workingDirectory))
            {
                await HandOverAsync(outside, id, definition, workingDirectory, ex, cancellationToken).ConfigureAwait(false);
                writtenEarly = id;
            }

            // Before the config is written: if unpacking fails, nothing has been
            // recorded, and the administrator can fix the cause and try again
            // rather than being left with a half-made instance.
            if (!request.AllowExistingFiles
                && Directory.EnumerateFileSystemEntries(workingDirectory).Any())
            {
                // Installing — or simply registering a definition — over
                // whatever is already there would quietly mix two servers into
                // one folder, package or not. A folder that already holds a
                // server is what adoption is for; a folder that holds anything
                // else is not a place to install one.
                throw new InvalidOperationException(
                    $"'{workingDirectory}' is not empty. To manage a server that already "
                    + "lives there, add it as an existing server instead of installing "
                    + "into it; to install fresh, pick an empty folder.");
            }

            // `T315`. A new server starts with no build history. A record already under
            // this id belongs to a removed server, and both the package record below and
            // the first start's announcement merge into what is there, which dated a new
            // server's first start as an update from the removed one's build. Here as
            // well as at removal, because a record left by a removal on an earlier build
            // is still on disk.
            _versions?.Forget(id);

            if (request.PackageFileName is { } packageName)
            {
                var package = _packages.Find(packageName)
                    ?? throw new InvalidOperationException(
                        $"'{packageName}' is not in the package store.");

                await _packages.ExtractAsync(package, workingDirectory, cancellationToken)
                    .ConfigureAwait(false);

                // D53: which package this server was built from, before it has run.
                if (package.Version is { } installed)
                {
                    _versions?.RecordPackage(id, installed, reportedNow: null, _timeProvider.GetUtcNow());
                }
            }

            // D24. Every instance keeps Bedrock's own default for LAN visibility,
            // which is on. D21 used to turn it off from the second server onward;
            // that made those servers unjoinable rather than merely undiscoverable,
            // and LanDiscoveryPortGuard now settles the port contention it was
            // guarding against without taking anything away from the server.
            await ApplyServerPropertiesAsync(
                    workingDirectory, name, request.Port, request.World, request.Transport,
                    freshInstall: request.PackageFileName is not null, request.UdpPorts, cancellationToken)
                .ConfigureAwait(false);

            await WriteToConfigAsync(id, definition, cancellationToken).ConfigureAwait(false);
            writtenEarly = null;

            Options.Servers[id] = definition;
            var supervisor = Register(id, definition, workingDirectory);
            WarnIfUnderAUserProfile(id, workingDirectory);

            _log.LogInformation(
                "Created instance {InstanceId} ({Name}) at {Directory}", id, name, workingDirectory);

            // D93: before the first start, because the world is generated into
            // the seeded folder then. Only onto a server this call installed.
            if (request.CompanionPack && request.PackageFileName is not null && _addons is not null)
            {
                await SwitchOnCompanionAsync(supervisor, cancellationToken).ConfigureAwait(false);
            }

            if (request.StartNow && supervisor.IsProvisioned)
            {
                await supervisor.StartAsync(cancellationToken).ConfigureAwait(false);
            }

            return supervisor;
        }
        catch when (writtenEarly is { } early)
        {
            await JsonConfigWriter
                .RemoveAsync(ConfigPath, [$"{InstanceOptions.SectionName}:Servers:{early}"], CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            _createGate.Release();
        }
    }

    /// <summary>
    /// Switches the companion pack on for a server that has never started (<c>D93</c>).
    /// When it could not be, the server's console says why.
    /// </summary>
    /// <returns>Null when the pack is on; otherwise why not.</returns>
    /// <remarks>
    /// Whatever goes wrong, the server exists, so only a cancellation is thrown. A world
    /// file's world is placed before this is called (<c>T334</c>), since the pack goes into
    /// the world <c>level-name</c> names.
    /// </remarks>
    public async Task<string?> SwitchOnCompanionAsync(
        InstanceSupervisor supervisor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(supervisor);

        string? notOn;

        try
        {
            notOn = _addons is { } addons
                ? await addons.PreSeedCompanionAsync(supervisor, cancellationToken).ConfigureAwait(false)
                : "Add-ons are not available in this wrapper.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            notOn = ex.Message;
        }

        if (notOn is not null)
        {
            supervisor.Console.Append(
                ConsoleOrigin.Notice,
                $"the companion pack was not switched on: {notOn} It can be switched on from the Add-ons tab.",
                _timeProvider.GetUtcNow());
            _log.LogWarning(
                "The companion pack was not switched on for {InstanceId}: {Reason}", supervisor.InstanceId, notOn);
        }

        return notOn;
    }

    /// <summary>
    /// Names the server, and — when the wrapper is the one that put the file
    /// there — moves it off the default port, sets its transport and answers the
    /// settings the create form collected.
    /// </summary>
    /// <remarks>
    /// <para>The port matters more than it looks. Every BDS package ships
    /// listening on 19132, so a second instance created from the same zip
    /// silently collides with the first — one of them starts, the other exits,
    /// and the wrapper reports a crash loop with no obvious cause. Bedrock also
    /// takes the next number for IPv6, which is written here and which the
    /// suggested port leaves room for (<see cref="NewServerPorts"/>).</para>
    ///
    /// <para><paramref name="freshInstall"/> is what separates the two halves,
    /// and every default below is behind it. A package was just extracted, so
    /// that file is the wrapper's to answer; a folder that already had a server
    /// in it made its own choices, or deliberately made none, and neither is
    /// ours to correct.</para>
    /// </remarks>
    private static async Task ApplyServerPropertiesAsync(
        string workingDirectory,
        string name,
        int? port,
        NewWorldSettings? world,
        ServerTransport transport,
        bool freshInstall,
        string? udpPorts,
        CancellationToken cancellationToken)
    {
        var path = ServerProperties.PathFor(workingDirectory);
        if (!File.Exists(path))
        {
            // Nothing installed yet — adopting a directory that gets its server
            // later. Writing a bare properties file here would be worse than
            // leaving it: the package's own defaults and comments are the better
            // starting point.
            return;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["server-name"] = name,
        };

        List<string> unset = [];

        if (port is { } p)
        {
            values["server-port"] = p.ToString();
            values["server-portv6"] = (p + 1).ToString();
        }

        if (freshInstall)
        {
            // D75. A 1.26.50-or-later package extracts a file that says
            // transport=nethernet, so without this line every server the wrapper
            // creates speaks a transport whose port model nothing here describes
            // correctly — the Status card names an IPv6 port it does not bind,
            // and the start pre-flight reads a UDP table that cannot hold the
            // answer. Written unconditionally rather than only when the file
            // disagrees: the value is the operator's answer from the form, and a
            // write that depends on what the package happened to ship is a rule
            // whose behaviour changes under it.
            //
            // Only on a fresh install, for the reason the telemetry default just
            // below spells out — an adopted folder's file is not the wrapper's to
            // configure, and rewriting this one in particular would change which
            // ports somebody's running server binds.
            values[ServerTransports.PropertyKey] = ServerTransports.Spell(transport);

            // ON1. The line the form showed, as the operator left it. D71 keeps the
            // wrapper from writing it afterwards, on its own schedule, into a file the
            // server owns; this is the file's first write and the operator's answer.
            if (transport is ServerTransport.NetherNet && !string.IsNullOrWhiteSpace(udpPorts))
            {
                values[ServerUdpPorts.Key] = udpPorts.Trim();
            }
            else
            {
                // A 1.26.61 package ships server-udp-ports=19132 set, beside
                // transport=nethernet. A RakNet server, or a NetherNet one whose Play
                // ports the form left empty, takes no window, so the stock line is
                // commented out rather than left naming a port the form never showed (T371).
                unset.Add(ServerUdpPorts.Key);
            }
        }

        if (world is not null)
        {
            // Chosen on the form, so these are deliberate and overwrite whatever
            // the package shipped. Only what the form actually collects — every
            // other setting in the file is left exactly as it was.
            foreach (var (key, value) in world.ToProperties())
            {
                values[key] = value;
            }
        }
        else if (freshInstall && !ServerProperties.Read(path).ContainsKey("emit-server-telemetry"))
        {
            // A package was just extracted with no world settings chosen (no
            // form to have chosen them from). BDS ships no line for this at all
            // and complains on every startup until one exists, so write the
            // answer it documents — and only when the file does not already
            // carry a value. Never for an adopted folder: freshInstall is false
            // there, because AdoptAsync's whole contract is writing nothing but
            // server-name, and a folder that already existed made its own
            // choice, or deliberately made none, which is not ours to correct.
            values["emit-server-telemetry"] = "false";
        }

        await ServerProperties.SetAsync(path, values, unset, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A new server's folder outside the data root that the service may not make
    /// (<c>D120</c>): the command that hands it over, or, where the install lets the panel
    /// ask, the broker's grant.
    /// </summary>
    /// <remarks>
    /// The broker grants the folder it reads from the configuration, so the server is
    /// written there first and taken out again if the grant is refused; the broker makes
    /// the folder and grants it to the service and the server's container.
    /// </remarks>
    private async Task HandOverAsync(
        OutsideFolders outside, string id, InstanceDefinition definition, string workingDirectory,
        UnauthorizedAccessException denied, CancellationToken cancellationToken)
    {
        if (!outside.PanelMayGrant)
        {
            throw new InvalidOperationException(outside.Refusal(workingDirectory), denied);
        }

        await WriteToConfigAsync(id, definition, cancellationToken).ConfigureAwait(false);

        if (await outside.GrantAsync(BrokerGrantTarget.InstanceWorkingDirectory, id, cancellationToken).ConfigureAwait(false)
            is { } refused)
        {
            await JsonConfigWriter
                .RemoveAsync(ConfigPath, [$"{InstanceOptions.SectionName}:Servers:{id}"], CancellationToken.None)
                .ConfigureAwait(false);
            throw new InvalidOperationException(outside.Refusal(workingDirectory, refused), denied);
        }
    }

    /// <summary>
    /// <see cref="HandOverAsync"/> for a server moving to a folder outside the data root:
    /// the new folder is written first for the broker to read, and the old one put back
    /// if the grant is refused.
    /// </summary>
    private async Task MoveHandOverAsync(
        OutsideFolders outside, string instanceId, string? previous, string full,
        UnauthorizedAccessException denied, CancellationToken cancellationToken)
    {
        if (!outside.PanelMayGrant)
        {
            throw new InvalidOperationException(outside.Refusal(full), denied);
        }

        var key = $"{InstanceOptions.SectionName}:Servers:{instanceId}:WorkingDirectory";

        await JsonConfigWriter
            .MergeAsync(ConfigPath, new Dictionary<string, JsonNode?> { [key] = JsonValue.Create(full) }, cancellationToken)
            .ConfigureAwait(false);

        if (await outside.GrantAsync(BrokerGrantTarget.InstanceWorkingDirectory, instanceId, cancellationToken).ConfigureAwait(false)
            is { } refused)
        {
            if (previous is null)
            {
                await JsonConfigWriter.RemoveAsync(ConfigPath, [key], CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await JsonConfigWriter
                    .MergeAsync(ConfigPath, new Dictionary<string, JsonNode?> { [key] = JsonValue.Create(previous) }, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            throw new InvalidOperationException(outside.Refusal(full, refused), denied);
        }
    }

    private async Task WriteToConfigAsync(
        string id, InstanceDefinition definition, CancellationToken cancellationToken)
    {
        var prefix = $"{InstanceOptions.SectionName}:Servers:{id}";

        var values = new Dictionary<string, JsonNode?>
        {
            [$"{prefix}:Name"] = JsonValue.Create(definition.Name),
            [$"{prefix}:AutoStart"] = JsonValue.Create(definition.AutoStart),
        };

        if (definition.WorkingDirectory is { } directory)
        {
            values[$"{prefix}:WorkingDirectory"] = JsonValue.Create(directory);
        }

        await JsonConfigWriter
            .MergeAsync(ConfigPath, values, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sets whether an instance starts with the service, in memory and in the
    /// config file.
    /// </summary>
    /// <remarks>
    /// Both, because either alone is a lie of a different kind. Memory only, and
    /// the setting silently reverts at the next service restart — which is the
    /// exact moment it was supposed to take effect. File only, and the toggle
    /// appears not to have worked until the service is restarted.
    /// </remarks>
    /// <summary>
    /// Replaces an instance's backup settings, in memory and in the config file
    /// — either alone is a lie of a different kind: memory-only vanishes on the
    /// next restart, file-only means the running wrapper backs up under rules
    /// the administrator already changed.
    /// </summary>
    public async Task SetBackupSettingsAsync(
        string instanceId, Core.Backups.BackupSettings settings, CancellationToken cancellationToken = default)
    {
        var supervisor = Get(instanceId);
        supervisor.Definition.Backup = settings;

        var prefix = $"{InstanceOptions.SectionName}:Servers:{instanceId}:Backup";

        await JsonConfigWriter
            .MergeAsync(
                ConfigPath,
                new Dictionary<string, JsonNode?>
                {
                    [$"{prefix}:CustomCommand:Enabled"] = JsonValue.Create(settings.CustomCommand.Enabled),
                    [$"{prefix}:CustomCommand:Command"] = JsonValue.Create(settings.CustomCommand.Command),
                    [$"{prefix}:Zip:Enabled"] = JsonValue.Create(settings.Zip.Enabled),
                    [$"{prefix}:Zip:Compression"] = JsonValue.Create(settings.Zip.Compression.ToString()),
                    [$"{prefix}:Zip:Retention:Mode"] = JsonValue.Create(settings.Zip.Retention.Mode.ToString()),
                    [$"{prefix}:Zip:Retention:Value"] = JsonValue.Create(settings.Zip.Retention.Value),
                    [$"{prefix}:Folder:Enabled"] = JsonValue.Create(settings.Folder.Enabled),
                    [$"{prefix}:Folder:Retention:Mode"] = JsonValue.Create(settings.Folder.Retention.Mode.ToString()),
                    [$"{prefix}:Folder:Retention:Value"] = JsonValue.Create(settings.Folder.Retention.Value),
                    [$"{prefix}:Acquisition:UseShadowCopy"] = JsonValue.Create(settings.Acquisition.UseShadowCopy),
                    [$"{prefix}:Schedule:Mode"] = JsonValue.Create(settings.Schedule.Mode.ToString()),
                    [$"{prefix}:Schedule:IntervalHours"] = JsonValue.Create(settings.Schedule.IntervalHours),
                    [$"{prefix}:Schedule:IntervalMinutes"] = JsonValue.Create(settings.Schedule.IntervalMinutes),
                    [$"{prefix}:Schedule:TimeOfDay"] =
                        JsonValue.Create(settings.Schedule.TimeOfDay.ToString("HH:mm:ss")),
                    [$"{prefix}:OnPlayerActivity:OnJoin"] =
                        JsonValue.Create(settings.OnPlayerActivity.OnJoin),
                    [$"{prefix}:OnPlayerActivity:OnLeave"] =
                        JsonValue.Create(settings.OnPlayerActivity.OnLeave),
                    [$"{prefix}:OnPlayerActivity:MinimumGapMinutes"] =
                        JsonValue.Create(settings.OnPlayerActivity.MinimumGapMinutes),
                    [$"{prefix}:OnShutdown:OnScheduledRestart"] =
                        JsonValue.Create(settings.OnShutdown.OnScheduledRestart),
                    [$"{prefix}:OnShutdown:OnWrapperUpdate"] =
                        JsonValue.Create(settings.OnShutdown.OnWrapperUpdate),
                    [$"{prefix}:OnShutdown:OnServerUpdate"] =
                        JsonValue.Create(settings.OnShutdown.OnServerUpdate),
                    [$"{prefix}:History:KeepDays"] =
                        JsonValue.Create(settings.History.KeepDays),
                },
                cancellationToken)
            .ConfigureAwait(false);

        _log.LogInformation("Instance {InstanceId} backup settings updated", instanceId);
    }

    /// <summary>
    /// Raised with the instance id after its Bedrock-update settings are saved.
    /// </summary>
    /// <remarks>
    /// Exists so <c>BdsAutoUpdater</c> can forget a build the operator declined
    /// without this class having to know that it exists — the updater already
    /// depends on the registry, and the reverse edge would be a cycle.
    /// </remarks>
    public event Action<string>? ServerUpdateSettingsChanged;

    /// <summary>
    /// Writes an instance's Bedrock update settings to memory and the config
    /// file together, the way every other per-instance setting is written.
    /// </summary>
    public async Task SetServerUpdateAsync(
        string instanceId, BdsUpdateSettings settings, CancellationToken cancellationToken = default)
    {
        var supervisor = Get(instanceId);
        supervisor.Definition.ServerUpdate = settings;

        var prefix = $"{InstanceOptions.SectionName}:Servers:{instanceId}:ServerUpdate";

        await JsonConfigWriter
            .MergeAsync(
                ConfigPath,
                new Dictionary<string, JsonNode?>
                {
                    [$"{prefix}:Mode"] = JsonValue.Create(settings.Mode.ToString()),
                    [$"{prefix}:Channel"] = JsonValue.Create(settings.Channel.ToString()),
                    [$"{prefix}:PinnedVersion"] = JsonValue.Create(settings.PinnedVersion),
                    [$"{prefix}:AutoApplyDelayMinutes"] = JsonValue.Create(settings.AutoApplyDelayMinutes),
                },
                cancellationToken)
            .ConfigureAwait(false);

        _log.LogInformation("Instance {InstanceId} server-update settings updated", instanceId);

        ServerUpdateSettingsChanged?.Invoke(instanceId);
    }

    public async Task SetAutoStartAsync(
        string instanceId, bool autoStart, CancellationToken cancellationToken = default)
    {
        var supervisor = Get(instanceId);
        supervisor.Definition.AutoStart = autoStart;

        await JsonConfigWriter
            .MergeAsync(
                ConfigPath,
                new Dictionary<string, JsonNode?>
                {
                    [$"{InstanceOptions.SectionName}:Servers:{instanceId}:AutoStart"] =
                        JsonValue.Create(autoStart),
                },
                cancellationToken)
            .ConfigureAwait(false);

        _log.LogInformation(
            "Instance {InstanceId} will {Behaviour} with the service",
            instanceId, autoStart ? "start" : "not start");
    }

    /// <summary>
    /// Changes an instance's display name.
    /// </summary>
    /// <remarks>
    /// The instance <em>id</em> is derived from the name once, at creation, and
    /// stays fixed — so a rename here moves nothing on disk and leaves every
    /// session row still attributable to the same server (<c>D14</c>). The
    /// <em>folder</em> is a separate story: an instance with no explicit
    /// <c>WorkingDirectory</c> of its own resolves it from the display name
    /// (<see cref="InstanceLayout.ResolveWorkingDirectory"/>), so a rename alone
    /// would leave config's notion of where this instance lives pointed at a
    /// name it no longer has — invisible until something re-derives the path,
    /// such as <see cref="SetRootAsync"/> pinning it under a new root. So a
    /// rename pins the folder the instance already runs from — first-time only,
    /// nothing pinned already is touched — before the name changes underneath
    /// it. Config is written first, and only a successful write updates the
    /// live definition, the same order every other setter here uses, so a
    /// failed attempt cannot poison a retry into skipping its own pin.
    /// The server's firewall rules are relabelled with it (<c>T411</c>): tagged under
    /// the old name before the write, renamed under the new one after it.
    /// </remarks>
    /// <returns>
    /// What happened to its firewall rules, or null when the name did not change or
    /// this service has no firewall access. A failure there does not undo the rename.
    /// </returns>
    public async Task<SystemCheckFix?> RenameAsync(
        string instanceId, string name, CancellationToken cancellationToken = default)
    {
        var supervisor = Get(instanceId);
        var definition = supervisor.Definition;
        var trimmed = name.Trim();

        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException("A server needs a name.");
        }

        var previous = definition.Name;
        var pin = string.IsNullOrWhiteSpace(definition.WorkingDirectory)
            ? supervisor.WorkingDirectory
            : null;

        var renamesRules = _firewall is not null && !string.Equals(previous, trimmed, StringComparison.Ordinal);

        if (renamesRules)
        {
            await RenameFirewallRulesAsync(instanceId, previous, cancellationToken).ConfigureAwait(false);
        }

        var prefix = $"{InstanceOptions.SectionName}:Servers:{instanceId}";
        var values = new Dictionary<string, JsonNode?>
        {
            [$"{prefix}:Name"] = JsonValue.Create(trimmed),
        };

        if (pin is not null)
        {
            values[$"{prefix}:WorkingDirectory"] = JsonValue.Create(pin);
        }

        await JsonConfigWriter
            .MergeAsync(ConfigPath, values, cancellationToken)
            .ConfigureAwait(false);

        definition.Name = trimmed;
        if (pin is not null)
        {
            definition.WorkingDirectory = pin;
        }

        // The supervisor's published status carries its own copy of the name and
        // nothing else rewrites it, so without this the Instances strip and the
        // Status table keep the old one until the service restarts (`T89`).
        supervisor.RenameTo(trimmed);

        _log.LogInformation(
            "Instance {InstanceId} renamed from {Previous} to {Name}",
            instanceId, previous, trimmed);

        return renamesRules
            ? await RenameFirewallRulesAsync(instanceId, trimmed, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <summary>The firewall half of a rename; a failure is reported, never thrown, so the rename stands.</summary>
    private async Task<SystemCheckFix> RenameFirewallRulesAsync(
        string instanceId, string name, CancellationToken cancellationToken)
    {
        try
        {
            var fix = await _firewall!.RenameServerRulesAsync(instanceId, name, cancellationToken).ConfigureAwait(false);

            if (!fix.Succeeded)
            {
                _log.LogWarning("The firewall rules of {InstanceId} were not renamed to {Name}: {Reason}", instanceId, name, fix.Message);
            }

            return fix;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "The firewall rules of {InstanceId} were not renamed to {Name}", instanceId, name);
            return new SystemCheckFix(false, ex.Message);
        }
    }

    /// <summary>Writes an instance's crash-watchdog settings.</summary>
    public async Task SetWatchdogAsync(
        string instanceId, WatchdogOptions watchdog, CancellationToken cancellationToken = default)
    {
        var supervisor = Get(instanceId);
        supervisor.Definition.Watchdog = watchdog;

        var prefix = $"{InstanceOptions.SectionName}:Servers:{instanceId}:Watchdog";

        await JsonConfigWriter
            .MergeAsync(
                ConfigPath,
                new Dictionary<string, JsonNode?>
                {
                    [$"{prefix}:MaxConsecutiveFailures"] =
                        JsonValue.Create(watchdog.MaxConsecutiveFailures),
                    [$"{prefix}:InitialBackoff"] = JsonValue.Create(watchdog.InitialBackoff.ToString()),
                    [$"{prefix}:MaxBackoff"] = JsonValue.Create(watchdog.MaxBackoff.ToString()),
                    [$"{prefix}:BackoffMultiplier"] = JsonValue.Create(watchdog.BackoffMultiplier),
                    [$"{prefix}:StableAfter"] = JsonValue.Create(watchdog.StableAfter.ToString()),
                },
                cancellationToken)
            .ConfigureAwait(false);

        _log.LogInformation("Instance {InstanceId} watchdog settings updated", instanceId);
    }

    /// <summary>Writes an instance's companion-pack toggles.</summary>
    /// <remarks>
    /// Config first, and only a written file changes the live definition, so a
    /// failed save cannot leave the panel showing a value that reverts at the
    /// next service start.
    /// </remarks>
    public async Task SetCompanionAsync(
        string instanceId, CompanionSettings companion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(companion);

        var supervisor = Get(instanceId);
        var prefix = $"{InstanceOptions.SectionName}:Servers:{instanceId}:Companion";

        await JsonConfigWriter
            .MergeAsync(
                ConfigPath,
                new Dictionary<string, JsonNode?>
                {
                    [$"{prefix}:RecordDeaths"] = JsonValue.Create(companion.RecordDeaths),
                    [$"{prefix}:RecordLastLocation"] = JsonValue.Create(companion.RecordLastLocation),
                    [$"{prefix}:EchoPositions"] = JsonValue.Create(companion.EchoPositions),
                },
                cancellationToken)
            .ConfigureAwait(false);

        supervisor.Definition.Companion = new CompanionSettings
        {
            RecordDeaths = companion.RecordDeaths,
            RecordLastLocation = companion.RecordLastLocation,
            EchoPositions = companion.EchoPositions,
        };

        _log.LogInformation("Instance {InstanceId} companion pack settings updated", instanceId);
    }

    /// <summary>Writes which of an instance's add-on issues are hidden (<c>D96</c>).</summary>
    /// <remarks>
    /// The whole list, as a JSON array, for the reason
    /// <see cref="SetRestartScheduleAsync"/> gives: merged key by key, a shorter
    /// list would leave the keys it dropped in force. The caller trims it to the
    /// issues the tab can see (<c>AddonIssues.Hide</c>), so this writes what it is
    /// given.
    /// </remarks>
    public async Task SetHiddenAddonIssuesAsync(
        string instanceId, IReadOnlyList<string> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var supervisor = Get(instanceId);
        var hidden = new JsonArray();

        foreach (var key in keys)
        {
            hidden.Add(JsonValue.Create(key));
        }

        await JsonConfigWriter
            .MergeAsync(
                ConfigPath,
                new Dictionary<string, JsonNode?>
                {
                    [$"{InstanceOptions.SectionName}:Servers:{instanceId}:Addons:HiddenIssues"] = hidden,
                },
                cancellationToken)
            .ConfigureAwait(false);

        var addons = supervisor.Definition.Addons.Copy();
        addons.HiddenIssues = [.. keys];
        supervisor.Definition.Addons = addons;
    }

    /// <summary>Writes whether an instance's add-ons follow the library at each start (<c>D112</c>).</summary>
    public async Task SetAddonAutoUpdateAsync(
        string instanceId, bool autoUpdate, CancellationToken cancellationToken = default)
    {
        var supervisor = Get(instanceId);

        await JsonConfigWriter
            .MergeAsync(
                ConfigPath,
                new Dictionary<string, JsonNode?>
                {
                    [$"{InstanceOptions.SectionName}:Servers:{instanceId}:Addons:AutoUpdate"] = JsonValue.Create(autoUpdate),
                },
                cancellationToken)
            .ConfigureAwait(false);

        var addons = supervisor.Definition.Addons.Copy();
        addons.AutoUpdate = autoUpdate;
        supervisor.Definition.Addons = addons;
    }

    /// <summary>Writes the add-ons an instance holds at a chosen version (<c>D112</c>).</summary>
    /// <remarks>
    /// The whole list, as a JSON array, for the reason <see cref="SetHiddenAddonIssuesAsync"/>
    /// gives. The caller works out the list (<c>AddonHolds.After</c>).
    /// </remarks>
    public async Task SetAddonHoldsAsync(
        string instanceId, IReadOnlyList<AddonHold> holds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(holds);

        var supervisor = Get(instanceId);
        var written = new JsonArray();

        foreach (var hold in holds)
        {
            written.Add(new JsonObject { ["Uuid"] = hold.Uuid, ["Version"] = hold.Version });
        }

        await JsonConfigWriter
            .MergeAsync(
                ConfigPath,
                new Dictionary<string, JsonNode?>
                {
                    [$"{InstanceOptions.SectionName}:Servers:{instanceId}:Addons:Holds"] = written,
                },
                cancellationToken)
            .ConfigureAwait(false);

        var addons = supervisor.Definition.Addons.Copy();
        addons.Holds = [.. holds.Select(h => new AddonHold { Uuid = h.Uuid, Version = h.Version })];
        supervisor.Definition.Addons = addons;
    }

    /// <summary>Writes the server an instance takes its world from and the areas it keeps (<c>D103</c>).</summary>
    /// <remarks>
    /// The Sync Areas are written whole, as a JSON array, for the reason
    /// <see cref="SetRestartScheduleAsync"/> gives: merged key by key, a shorter
    /// list would leave the areas it dropped in force. Config first, and only a
    /// written file changes the live definition.
    /// </remarks>
    public async Task SetSyncAsync(
        string instanceId, SyncSettings sync, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sync);

        var supervisor = Get(instanceId);
        var prefix = $"{InstanceOptions.SectionName}:Servers:{instanceId}:Sync";
        var copy = new SyncSettings
        {
            SourceInstanceId = sync.SourceInstanceId ?? string.Empty,
            KeptAreas =
            [
                .. sync.KeptAreas.Select(a => new KeptAreaSettings
                {
                    Name = (a.Name ?? string.Empty).Trim(),

                    // A dimension Bedrock has is written by its id; one it has not is kept as
                    // typed, so the Sync tab can name it as the problem it is.
                    Dimension = BedrockDimension.Find(a.Dimension)?.Id ?? (a.Dimension ?? string.Empty).Trim(),
                    Color = AreaColors.IsValid(a.Color) ? a.Color.ToLowerInvariant() : string.Empty,
                    ShowInGame = a.ShowInGame,
                    X1 = a.X1, Y1 = a.Y1, Z1 = a.Z1,
                    X2 = a.X2, Y2 = a.Y2, Z2 = a.Z2,
                }),
            ],
        };

        var areas = new JsonArray();

        foreach (var area in copy.KeptAreas)
        {
            areas.Add(new JsonObject
            {
                [nameof(KeptAreaSettings.Name)] = area.Name,
                [nameof(KeptAreaSettings.Dimension)] = area.Dimension,
                [nameof(KeptAreaSettings.Color)] = area.Color,
                [nameof(KeptAreaSettings.ShowInGame)] = area.ShowInGame,
                [nameof(KeptAreaSettings.X1)] = area.X1,
                [nameof(KeptAreaSettings.Y1)] = area.Y1,
                [nameof(KeptAreaSettings.Z1)] = area.Z1,
                [nameof(KeptAreaSettings.X2)] = area.X2,
                [nameof(KeptAreaSettings.Y2)] = area.Y2,
                [nameof(KeptAreaSettings.Z2)] = area.Z2,
            });
        }

        await JsonConfigWriter
            .MergeAsync(
                ConfigPath,
                new Dictionary<string, JsonNode?>
                {
                    [$"{prefix}:{nameof(SyncSettings.SourceInstanceId)}"] = JsonValue.Create(copy.SourceInstanceId),
                    [$"{prefix}:{nameof(SyncSettings.KeptAreas)}"] = areas,
                },
                cancellationToken)
            .ConfigureAwait(false);

        supervisor.Definition.Sync = copy;

        _log.LogInformation(
            "Instance {InstanceId} syncs from {Source} keeping {Count} area(s)",
            instanceId, copy.SourceInstanceId, copy.KeptAreas.Count);

        SyncSettingsChanged?.Invoke(instanceId);

        // A running server's outlines follow the panel at once (D106); a stopped one is
        // handed them at its next start.
        await supervisor.SendOutlinesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes how the panel shows the world's ticking areas in game (<c>D107</c>), whole, as
    /// <see cref="SetSyncAsync"/> writes the Sync Areas, and sends a running server its outlines.
    /// </summary>
    public async Task SetTickingAreasAsync(
        string instanceId, IReadOnlyList<TickingAreaSettings> settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var supervisor = Get(instanceId);
        var copy = settings
            .Where(s => (s.Name ?? string.Empty).Length > 0)
            .Select(s => new TickingAreaSettings
            {
                Name = s.Name,
                Dimension = BedrockDimension.Find(s.Dimension)?.Id ?? (s.Dimension ?? string.Empty).Trim(),
                ShowInGame = s.ShowInGame,
                Color = AreaColors.IsValid(s.Color) ? s.Color.ToLowerInvariant() : string.Empty,
            })
            .ToList();

        var array = new JsonArray();
        foreach (var setting in copy)
        {
            array.Add(new JsonObject
            {
                [nameof(TickingAreaSettings.Name)] = setting.Name,
                [nameof(TickingAreaSettings.Dimension)] = setting.Dimension,
                [nameof(TickingAreaSettings.ShowInGame)] = setting.ShowInGame,
                [nameof(TickingAreaSettings.Color)] = setting.Color,
            });
        }

        await JsonConfigWriter
            .MergeAsync(
                ConfigPath,
                new Dictionary<string, JsonNode?>
                {
                    [$"{InstanceOptions.SectionName}:Servers:{instanceId}:{nameof(InstanceDefinition.TickingAreas)}"] = array,
                },
                cancellationToken)
            .ConfigureAwait(false);

        supervisor.Definition.TickingAreas = copy;
        TickingAreasChanged?.Invoke(instanceId);
        await supervisor.SendOutlinesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A server's ticking-area display settings were written; an open Ticking Areas view reads them again.</summary>
    public event Action<string>? TickingAreasChanged;

    /// <summary>Every server's <see cref="InstanceSupervisor.TickShow"/>, raised on that server's console pump.</summary>
    public event Action<InstanceSupervisor, TickShowCommand>? TickShow;

    /// <summary>Every server's <see cref="InstanceSupervisor.RestoreCommand"/>, raised on that server's console pump.</summary>
    public event Action<InstanceSupervisor, AreaRestoreCommand>? RestoreCommand;

    /// <summary>Every server's <see cref="InstanceSupervisor.CompanionHello"/>, raised on that server's console pump.</summary>
    public event Action<InstanceSupervisor>? CompanionHello;

    /// <summary>Every supervisor's status changes, one subscription for all of them (<c>D85</c>).</summary>
    /// <remarks>Raised on the supervisor's own thread, as its event is; a listener must not block it.</remarks>
    public event Action<InstanceSupervisor, InstanceStatus>? StatusChanged;

    /// <summary>Raised with an instance's id as it is retired or removed and its supervisor let go.</summary>
    public event Action<string>? Released;

    /// <summary>Every server's <see cref="InstanceSupervisor.AreaCommand"/>, raised on that server's console pump.</summary>
    public event Action<InstanceSupervisor, SyncAreaCommand>? AreaCommand;

    /// <summary>
    /// A server's Sync Areas were written, by the Sync tab or by <c>syncarea</c> in game; an open
    /// Sync tab reads them again.
    /// </summary>
    public event Action<string>? SyncSettingsChanged;

    /// <summary>Writes an instance's scheduled-restart settings.</summary>
    /// <remarks>
    /// <see cref="RestartSchedule.Warnings"/> is written out in full rather than
    /// merged. It is a list, and the configuration binder <em>appends</em> to a
    /// collection rather than replacing it — so shortening a countdown by merging
    /// would leave the points that were removed still in force, with nothing to
    /// explain where they came from.
    /// </remarks>
    public async Task SetRestartScheduleAsync(
        string instanceId, RestartSchedule schedule, CancellationToken cancellationToken = default)
    {
        var supervisor = Get(instanceId);
        supervisor.Definition.RestartSchedule = schedule;

        var prefix = $"{InstanceOptions.SectionName}:Servers:{instanceId}:RestartSchedule";

        var warnings = new JsonArray();

        foreach (var point in schedule.WarningsOrDefault)
        {
            warnings.Add(JsonValue.Create(point.ToString()));
        }

        await JsonConfigWriter
            .MergeAsync(
                ConfigPath,
                new Dictionary<string, JsonNode?>
                {
                    [$"{prefix}:Mode"] = JsonValue.Create(schedule.Mode.ToString()),
                    [$"{prefix}:IntervalHours"] = JsonValue.Create(schedule.IntervalHours),
                    [$"{prefix}:IntervalMinutes"] = JsonValue.Create(schedule.IntervalMinutes),
                    [$"{prefix}:TimeOfDay"] = JsonValue.Create(schedule.TimeOfDay.ToString("HH:mm")),
                    [$"{prefix}:Warnings"] = warnings,
                },
                cancellationToken)
            .ConfigureAwait(false);

        _log.LogInformation(
            "Instance {InstanceId} restart schedule set to {Mode}", instanceId, schedule.Mode);
    }

    private string TranscriptDirectory(string instanceId) =>
        Path.Combine(_paths.InstanceLogs, InstanceLayout.SanitiseFolderName(instanceId, instanceId));

    /// <summary>
    /// Where this instance's archives live. Keyed by instance id, which is what
    /// keeps one server's removal from ever reaching another's — and the same
    /// layout <see cref="Backups.BackupHistory.DirectoryFor"/> writes to.
    /// </summary>
    private string BackupDirectory(string instanceId) =>
        Path.Combine(_paths.Backups, instanceId);

    /// <summary>
    /// Why this backup folder must not be deleted, or <c>null</c> to go ahead.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="InstanceDirectoryRemoval.Refuse"/>: that one
    /// ends by refusing any folder holding nothing that looks like a Bedrock
    /// server, which is right for a working directory and wrong here — a backup
    /// folder holds archives by definition, and a custom-command strategy may
    /// have written anything at all into it. What matters instead is that the
    /// path is the instance's own subfolder and not one of the wrapper's roots,
    /// which an empty or hand-edited instance id could otherwise collapse it to.
    /// </remarks>
    private string? RefuseBackupDirectory(string directory)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"'{directory}' is not a usable path";
        }

        foreach (var reserved in ProtectedPaths())
        {
            if (string.IsNullOrWhiteSpace(reserved))
            {
                continue;
            }

            var other = Path.TrimEndingDirectorySeparator(Path.GetFullPath(reserved));

            if (full.Equals(other, comparison))
            {
                return $"'{full}' is one of BedrockSentry's own folders";
            }
        }

        // Same reasoning as the working directory: a link deletes as a link, and
        // what that removed depends on details nobody has in mind at the time.
        if (File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint))
        {
            return $"'{full}' is a junction or symbolic link";
        }

        return null;
    }

    /// <summary>
    /// Stops managing an instance without touching anything it owns (<c>D14</c>).
    /// </summary>
    /// <remarks>
    /// The reversible half of the pair. Its config entry stays, marked retired,
    /// which is what keeps its player history attributable to a named server
    /// rather than to an id nothing explains. Undoing it is one edit.
    /// </remarks>
    public async Task RetireAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        await _createGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var supervisor = Get(instanceId);

            using (var claim = await supervisor.ClaimAsync(ClaimText.Retirement, cancellationToken).ConfigureAwait(false))
            {
                await ReleaseAsync(supervisor, claim).ConfigureAwait(false);
            }

            supervisor.Definition.Retired = true;

            await JsonConfigWriter
                .MergeAsync(
                    ConfigPath,
                    new Dictionary<string, JsonNode?>
                    {
                        [$"{InstanceOptions.SectionName}:Servers:{instanceId}:Retired"] = JsonValue.Create(true),
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            _log.LogInformation("Instance {InstanceId} retired", instanceId);
        }
        finally
        {
            _createGate.Release();
        }
    }

    /// <summary>
    /// Instances that are configured but retired, newest configuration order.
    /// </summary>
    /// <remarks>
    /// Retired instances get no supervisor, so they are absent from every live
    /// view — correct, since there is no server to manage, but it also means
    /// nothing would show them at all. Without this, "retire" is a one-way door
    /// with a reversible label on it.
    /// </remarks>
    public IReadOnlyList<RetiredInstance> Retired =>
    [
        .. Options.Servers
            .Where(entry => entry.Value.Retired && !_supervisors.ContainsKey(entry.Key))
            .Select(entry => new RetiredInstance(
                entry.Key,
                entry.Value.Name,
                InstanceLayout.ResolveWorkingDirectory(Root, entry.Key, entry.Value)))
            .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
    ];

    /// <summary>
    /// Brings a retired instance back under management, live, without a service
    /// restart.
    /// </summary>
    /// <remarks>
    /// Left stopped rather than started, even when its auto-start is on:
    /// auto-start means "come up with the service", and a restore is not a
    /// service start. Bringing a server back and having it immediately accept
    /// players is not something to do as a side effect.
    /// </remarks>
    public async Task<InstanceSupervisor> RestoreAsync(
        string instanceId, CancellationToken cancellationToken = default)
    {
        await _createGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_supervisors.ContainsKey(instanceId))
            {
                throw new InvalidOperationException(
                    $"'{instanceId}' is already being managed.");
            }

            if (!Options.Servers.TryGetValue(instanceId, out var definition))
            {
                throw new KeyNotFoundException($"There is no instance named '{instanceId}'.");
            }

            if (!definition.Retired)
            {
                throw new InvalidOperationException($"'{instanceId}' is not retired.");
            }

            definition.Retired = false;

            var workingDirectory = InstanceLayout.ResolveWorkingDirectory(Root, instanceId, definition);
            Directory.CreateDirectory(workingDirectory);

            // Removed rather than written as false. The config file is not
            // supposed to accumulate settings at their default — that is the
            // whole reason JsonConfigWriter exists — and taking the key out
            // leaves the file exactly as it was before the instance was retired,
            // which is what "reversible" ought to mean.
            await JsonConfigWriter
                .RemoveAsync(
                    ConfigPath,
                    [$"{InstanceOptions.SectionName}:Servers:{instanceId}:Retired"],
                    cancellationToken)
                .ConfigureAwait(false);

            var supervisor = Register(instanceId, definition, workingDirectory);
            WarnIfUnderAUserProfile(instanceId, workingDirectory);

            _log.LogInformation(
                "Instance {InstanceId} ({Name}) restored from retirement, stopped",
                instanceId, definition.Name);

            return supervisor;
        }
        finally
        {
            _createGate.Release();
        }
    }

    /// <summary>
    /// Removes an instance outright, doing as much of <paramref name="request"/>
    /// as is safe and reporting what it actually did.
    /// </summary>
    /// <remarks>
    /// Order matters. The config entry goes first: if the file delete is refused
    /// or fails halfway, the wrapper is no longer managing a server whose files
    /// are in an unknown state, which is the safer of the two ways to be
    /// interrupted. The reverse order can leave a registered instance with its
    /// world half-deleted, which the watchdog then tries to start.
    /// </remarks>
    public async Task<InstanceRemovalOutcome> RemoveAsync(
        string instanceId,
        RemoveInstanceRequest request,
        CancellationToken cancellationToken = default)
    {
        await _createGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var supervisor = Get(instanceId);
            var name = supervisor.Definition.Name;
            var workingDirectory = supervisor.WorkingDirectory;

            using (var claim = await supervisor.ClaimAsync(ClaimText.Removal, cancellationToken).ConfigureAwait(false))
            {
                await ReleaseAsync(supervisor, claim).ConfigureAwait(false);
            }

            // `T400`. Before the configuration forgets the server: the broker finds the
            // name the rules carry from that configuration.
            var firewallRules = request.RemoveFirewallRules
                ? await RemoveFirewallRulesAsync(instanceId, name, cancellationToken).ConfigureAwait(false)
                : null;

            Options.Servers.Remove(instanceId);

            await JsonConfigWriter
                .RemoveAsync(
                    ConfigPath,
                    [$"{InstanceOptions.SectionName}:Servers:{instanceId}"],
                    cancellationToken)
                .ConfigureAwait(false);

            // `T315`. Its build history goes with it whatever else is kept: nothing
            // reads the record of a server that is gone, and one left behind is
            // inherited by the next server given this id.
            _versions?.Forget(instanceId);

            var forgotten = request.ForgetPlayers
                ? await _playerData.ForgetAsync(instanceId, cancellationToken).ConfigureAwait(false)
                : 0;

            var files = DirectoryRemoval.NotAsked;
            string? filesFailedBecause = null;

            var transcripts = DirectoryRemoval.NotAsked;
            string? transcriptsFailedBecause = null;
            string? transcriptDirectory = null;

            if (request.DeleteFiles)
            {
                filesFailedBecause =
                    InstanceDirectoryRemoval.Refuse(workingDirectory, ProtectedPaths());

                if (filesFailedBecause is not null)
                {
                    // Refused before anything ran, so nothing was touched. This is the
                    // only path that can honestly say the files were kept (`T176`).
                    files = DirectoryRemoval.Refused;
                }
                else
                {
                    files = TryDeleteDirectory(workingDirectory, ref filesFailedBecause);

                    // The transcript follows the world. Keeping it would leave an
                    // orphaned folder under logs\instances for an instance nothing
                    // else remembers.
                    //
                    // T185: this used to throw the outcome away into a `ref ignored`,
                    // so a transcript tree that part-deleted was reported as a clean
                    // removal — T176's own thesis, on a third directory, left standing
                    // by T176's own fix. DeleteFiles promises this tree too, so its
                    // failure has to reach the operator.
                    //
                    // ITS OWN SLOT, NOT PROMOTED INTO `files` (`T190`). T185's fix
                    // reported a part-deleted transcript tree by overwriting `files`
                    // with PartlyDeleted and splicing a hand-written reason in, on the
                    // argument that the operator only needs "some of what you asked to
                    // delete is half-gone" and a fourth outcome line would bury the
                    // world's own.
                    //
                    // The second half of that is still true and this still does not add
                    // a bullet. The first half was not: the promotion is guarded by
                    // `files is not PartlyDeleted`, and by this point `files` came from
                    // TryDeleteDirectory, which returns Deleted or PartlyDeleted and
                    // nothing else. So whenever the promoted message rendered, the world
                    // directory had been deleted COMPLETELY — and the panel told the
                    // operator some of the world was already gone and to go inspect a
                    // folder that was not there. There was no execution path on which
                    // that sentence was true. T176's own thesis, in the mirror, inside
                    // the fix written for it: one field made to carry two meanings.
                    //
                    // `files` therefore says only what happened to the working
                    // directory, which is what its name claims, and the transcripts
                    // report themselves.
                    // Held in a local rather than recomputed at the return, because the
                    // panel has to be able to NAME it (`T192`). The notice used to
                    // report a transcript failure and then print the working directory
                    // — a different folder, and on that path a successfully deleted one
                    // — so a reader following the sentence went to the wrong place.
                    transcriptDirectory = TranscriptDirectory(instanceId);

                    transcripts =
                        TryDeleteDirectory(transcriptDirectory, ref transcriptsFailedBecause);
                }
            }

            var backups = DirectoryRemoval.NotAsked;
            string? backupsFailedBecause = null;

            if (request.DeleteBackups)
            {
                // Independent of the files above: the archives live outside the
                // instance directory, which is exactly why deleting the world
                // leaves them behind (D39). Resolved against the *current*
                // backups root, so archives taken before the root was moved are
                // not reached — the same "old ones stay where they were taken"
                // rule the Storage settings page states.
                var backupDirectory = BackupDirectory(instanceId);

                // Nothing was ever backed up. Not a refusal and not a deletion —
                // saying "deleted" for a folder that never existed would be the
                // same lie as saying it when the delete failed.
                if (Directory.Exists(backupDirectory))
                {
                    backupsFailedBecause = RefuseBackupDirectory(backupDirectory);

                    backups = backupsFailedBecause is not null
                        ? DirectoryRemoval.Refused
                        : TryDeleteDirectory(backupDirectory, ref backupsFailedBecause);
                }
            }

            // `T179`. Each slot is a self-contained phrase from one place, so the
            // line reads as one sentence in every combination - it used to take
            // either a verb or a raw exception sentence in the same slot, which put
            // a failure mid-clause with nothing marking it as one.
            //
            // Error rather than Warning when something was partly deleted, because
            // that is data loss and the only way anybody finds it later is by
            // severity (`T176`).
            //
            // THE TRANSCRIPTS ARE DELIBERATELY NOT IN THAT TEST (`T190`). They are log
            // files: a half-deleted transcript tree is untidy, not irreversible, and
            // the justification written on this very line is "that is data loss". A
            // branch added later has to be an instance of the stated reason, not merely
            // adjacent to it (`T188`) — and while the promotion existed this line went
            // to Error for a removal in which the world was deleted perfectly. Warning
            // still reaches the Windows event log, whose sink filters at Warning, so
            // nothing stops being findable.
            var partly = files == DirectoryRemoval.PartlyDeleted
                || backups == DirectoryRemoval.PartlyDeleted;

            // Its own slot rather than a conditional clause, for `T179`'s reason: every
            // slot is a self-contained phrase, so the line reads as one sentence in each
            // combination and a failure can never arrive mid-clause unmarked.
            _log.Log(
                partly ? LogLevel.Error : LogLevel.Warning,
                "Instance {InstanceId} ({Name}) removed; files were {FileOutcome}; "
                + "backup archives were {BackupOutcome}; console transcripts were "
                + "{TranscriptOutcome}",
                instanceId,
                name,
                DirectoryRemovalPhrase.For(files, filesFailedBecause),
                DirectoryRemovalPhrase.For(backups, backupsFailedBecause),
                DirectoryRemovalPhrase.For(transcripts, transcriptsFailedBecause));

            return new InstanceRemovalOutcome(
                instanceId, name, workingDirectory, files, filesFailedBecause, forgotten,
                backups, backupsFailedBecause, transcripts, transcriptsFailedBecause,
                transcriptDirectory, firewallRules);
        }
        finally
        {
            _createGate.Release();
        }
    }

    /// <summary>The firewall half of a removal; a failure is reported, never thrown, so the removal goes on.</summary>
    private async Task<SystemCheckFix> RemoveFirewallRulesAsync(
        string instanceId, string name, CancellationToken cancellationToken)
    {
        if (_firewall is null)
        {
            return new SystemCheckFix(false, "This service has no firewall access.");
        }

        try
        {
            var fix = await _firewall.RemoveServerRulesAsync(instanceId, name, cancellationToken).ConfigureAwait(false);

            if (!fix.Succeeded)
            {
                _log.LogWarning("The firewall rules of {InstanceId} ({Name}) were not removed: {Reason}", instanceId, name, fix.Message);
            }

            return fix;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "The firewall rules of {InstanceId} ({Name}) were not removed", instanceId, name);
            return new SystemCheckFix(false, ex.Message);
        }
    }

    /// <summary>Locations the wrapper owns and must never delete along with an instance.</summary>
    private IEnumerable<string> ProtectedPaths() =>
    [
        _paths.DataRoot,
        _paths.Config,
        _paths.Instances,
        _paths.Logs,
        _paths.ServerPackages,
        _paths.Backups,
        Root,
        AppContext.BaseDirectory,
    ];

    /// <summary>
    /// Stops the instance, closes its transcript, and takes it out of the
    /// collection. Shared by both routes so neither can forget a step — a
    /// supervisor left running after its config entry is gone would keep
    /// restarting a server nothing is managing.
    /// </summary>
    /// <param name="claim">
    /// The caller's claim on the server, taken before anything changed, so a sync or
    /// an update is refused rather than having the server disposed under it.
    /// </param>
    private async Task ReleaseAsync(InstanceSupervisor supervisor, InstanceClaim claim)
    {
        // Out of the collection first, so the watchdog and the restart scheduler
        // stop looking at an instance that is on its way out.
        _supervisors.TryRemove(supervisor.InstanceId, out _);
        Released?.Invoke(supervisor.InstanceId);

        // Stopped properly, not killed. DisposeAsync kills — it says so, and it
        // is right to — but a server killed mid-write leaves a world that may
        // not open again. That matters most in the case where the administrator
        // asked to *keep* the files: retiring or removing an instance must not
        // be the thing that corrupts the world it just handed back.
        if (supervisor.Status.State is not (InstanceState.Stopped or InstanceState.Faulted))
        {
            try
            {
                await claim.StopAsync(
                        new GracefulStopRequest(
                            StopReason.Manual,
                            Options.ServiceStopWarning,
                            Options.ServiceStopWarning > TimeSpan.Zero ? [Options.ServiceStopWarning] : [],
                            Options.ShutdownDrainBudget))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The removal still goes ahead: an instance that cannot be
                // stopped cleanly is a reason to be told, not a reason to be
                // stuck with it forever. Dispose below kills what is left.
                _log.LogWarning(
                    ex, "Instance {InstanceId} did not stop cleanly before being released",
                    supervisor.InstanceId);
            }
        }

        await supervisor.DisposeAsync().ConfigureAwait(false);

        // After the supervisor, so its final notices still reach the transcript.
        if (_writers.TryRemove(supervisor.InstanceId, out var writer))
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes a directory, reporting honestly when it got part-way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This used to return a <c>bool</c>, and <c>false</c> meant "kept". It
    /// does not</strong> (<c>T176</c>).
    /// <c>Directory.Delete(recursive: true)</c> deletes as it walks, so a
    /// <c>false</c> here never meant the folder was untouched; it meant an unknown
    /// amount of it was already gone. The panel rendered that as <em>"its files were
    /// kept"</em>, which on a working directory is world data lost reported as no
    /// loss.
    /// </para>
    /// <para>
    /// <strong>It does not stop at the first failure</strong> (<c>T186</c>) — this
    /// said it "throws on the first entry it cannot remove, having already removed
    /// everything it passed", and that was wrong in the half that matters. It records
    /// the first exception, keeps walking, keeps deleting, and rethrows at the end.
    /// The outcome recorded here is unaffected; what was wrong is what the panel then
    /// told the operator had survived.
    /// </para>
    /// <para>
    /// The comment below used to end "saying which beats 'delete failed'", and it
    /// was right about the message and wrong about the verb. Both are kept now: the
    /// reason, <em>and</em> the fact that the delete started.
    /// </para>
    /// </remarks>
    private DirectoryRemoval TryDeleteDirectory(string directory, ref string? failure)
    {
        // Nothing there is the goal state, so it counts as deleted rather than as a
        // failure - the backups caller checks existence first for its own reasons.
        if (!Directory.Exists(directory))
        {
            return DirectoryRemoval.Deleted;
        }

        try
        {
            DeleteDirectoryTree(directory);
            return DirectoryRemoval.Deleted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Usually a file still open — a world the server has not finished
            // releasing, or a text editor. Saying which beats "delete failed", and
            // saying that it had already started beats both.
            failure = ex.Message;
            return DirectoryRemoval.PartlyDeleted;
        }
        catch (Exception ex)
        {
            // T185, the secondary. This catch used to be narrow, and by the time it
            // runs the config entry has already been removed and persisted — so
            // anything it did not name escaped RemoveAsync and the removal produced
            // no log line and no notice at all, for a tree that is by then part
            // deleted. The two above are the expected causes; the point of this one
            // is that an unexpected cause must not be the only case that reports
            // nothing. Catching broadly is right exactly here and nowhere near as
            // defensible one frame up.
            failure = ex.Message;
            return DirectoryRemoval.PartlyDeleted;
        }
    }

    private string ConfigPath => Path.Combine(_paths.Config, "bedrocksentry.json");

    /// <summary>
    /// The first port from <paramref name="start"/> up where a RakNet server would
    /// clash with no instance's file: its port and the IPv6 port after it, against
    /// every instance's ports and play window (<see cref="NewServerPorts"/>).
    /// </summary>
    /// <remarks>
    /// Files only. The panel asks <see cref="PortInspector"/>, which adds the machine's
    /// sockets, for both the new server form and restore-as-new (<c>T404</c>).
    /// </remarks>
    public int SuggestPort(int start = ServerPorts.DefaultPort) =>
        NewServerPorts.Next(
            [.. PortInspector.FileClaims(_supervisors.Values)], ServerTransport.RakNet, pinWindow: false, start)
        ?? start;

    public async ValueTask DisposeAsync()
    {
        foreach (var supervisor in _supervisors.Values)
        {
            await supervisor.DisposeAsync().ConfigureAwait(false);
        }

        // After the supervisors, so the notices they write while shutting down
        // still reach the transcript.
        foreach (var writer in _writers.Values)
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }

        _supervisors.Clear();
        _writers.Clear();
    }
}
