using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Players;
using BedrockSentry.Services.Configuration;
using BedrockSentry.Services.Packages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
        ServerVersionLog? versions = null)
    {
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
                guard.OwnsDiscoveryPair(PortInspector.PortsOf(s.WorkingDirectory)));

            // Whether that owner is up. The guard needs the two apart to know what a
            // failed bind means: the owner running is holding the pair itself and
            // wants no help, while the owner stopped means something else has it and
            // the owner is locked out of its own ports.
            guard.OwnerRunning = () => _supervisors.Values.Any(s =>
                guard.OwnsDiscoveryPair(PortInspector.PortsOf(s.WorkingDirectory))
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
    /// destination that is quietly wrong. The service runs as LocalSystem, not as
    /// the person who put the files there; profile folders are routinely
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

        _log.LogWarning(
            "Instance {InstanceId} is configured to run from {Directory}, which is inside a user "
            + "profile. The service runs as LocalSystem and does not own that folder, and "
            + "profile folders are often synced to OneDrive or cleared out by hand — either of "
            + "which loses the world. Prefer the instance root at {Root}.",
            instanceId, workingDirectory, Root);
    }

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
            () => Orphans);

        _supervisors[id] = supervisor;

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

        if (!Directory.Exists(full))
        {
            throw new InvalidOperationException($"'{full}' does not exist.");
        }

        var executable = Path.Combine(full, new InstanceDefinition().Executable);

        if (!File.Exists(executable))
        {
            // The one thing an instance cannot run without. Checked here rather
            // than discovered at first start, where it reads as a crash.
            throw new InvalidOperationException(
                $"'{full}' has no {Path.GetFileName(executable)} — it does not look like a "
                + "Bedrock server folder. To create a new server, use New instance instead.");
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
    /// This service runs as LocalSystem, whose session has no <c>Z:</c>, so Windows
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

            if (supervisor.Status.State is not (InstanceState.Stopped or InstanceState.Faulted))
            {
                throw new InvalidOperationException(
                    "Stop the server first. Its folder cannot move while it is running — "
                    + "or waiting to restart, which is a start about to happen.");
            }

            RefuseAManagedOrReservedDirectory(full, exceptInstanceId: instanceId);
            Directory.CreateDirectory(full);

            var definition = supervisor.Definition;
            var previous = supervisor.WorkingDirectory;

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

            await ReleaseAsync(supervisor).ConfigureAwait(false);
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

            Directory.CreateDirectory(workingDirectory);

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

            if (request.PackageFileName is { } packageName)
            {
                var package = _packages.Find(packageName)
                    ?? throw new InvalidOperationException(
                        $"'{packageName}' is not in the package store.");

                await _packages.ExtractAsync(package, workingDirectory, cancellationToken)
                    .ConfigureAwait(false);
            }

            // D24. Every instance keeps Bedrock's own default for LAN visibility,
            // which is on. D21 used to turn it off from the second server onward;
            // that made those servers unjoinable rather than merely undiscoverable,
            // and LanDiscoveryPortGuard now settles the port contention it was
            // guarding against without taking anything away from the server.
            await ApplyServerPropertiesAsync(
                    workingDirectory, name, request.Port, request.World,
                    freshInstall: request.PackageFileName is not null, cancellationToken)
                .ConfigureAwait(false);

            await WriteToConfigAsync(id, definition, cancellationToken).ConfigureAwait(false);

            Options.Servers[id] = definition;
            var supervisor = Register(id, definition, workingDirectory);
            WarnIfUnderAUserProfile(id, workingDirectory);

            _log.LogInformation(
                "Created instance {InstanceId} ({Name}) at {Directory}", id, name, workingDirectory);

            if (request.StartNow && supervisor.IsProvisioned)
            {
                await supervisor.StartAsync(cancellationToken).ConfigureAwait(false);
            }

            return supervisor;
        }
        finally
        {
            _createGate.Release();
        }
    }

    /// <summary>
    /// Names the server and, if asked, moves it off the default port.
    /// </summary>
    /// <remarks>
    /// The port matters more than it looks. Every BDS package ships listening on
    /// 19132, so a second instance created from the same zip silently collides
    /// with the first — one of them starts, the other exits, and the wrapper
    /// reports a crash loop with no obvious cause. Bedrock also takes the next
    /// number for IPv6, which is why suggested ports are spaced two apart.
    /// </remarks>
    private static async Task ApplyServerPropertiesAsync(
        string workingDirectory,
        string name,
        int? port,
        NewWorldSettings? world,
        bool freshInstall,
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

        if (port is { } p)
        {
            values["server-port"] = p.ToString();
            values["server-portv6"] = (p + 1).ToString();
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

        await ServerProperties.SetAsync(path, values, cancellationToken).ConfigureAwait(false);
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
                    [$"{prefix}:Vss:Enabled"] = JsonValue.Create(settings.Vss.Enabled),
                    [$"{prefix}:Vss:Retention:Mode"] = JsonValue.Create(settings.Vss.Retention.Mode.ToString()),
                    [$"{prefix}:Vss:Retention:Value"] = JsonValue.Create(settings.Vss.Retention.Value),
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
    /// </remarks>
    public async Task RenameAsync(
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
            await ReleaseAsync(supervisor).ConfigureAwait(false);

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

            await ReleaseAsync(supervisor).ConfigureAwait(false);

            Options.Servers.Remove(instanceId);

            await JsonConfigWriter
                .RemoveAsync(
                    ConfigPath,
                    [$"{InstanceOptions.SectionName}:Servers:{instanceId}"],
                    cancellationToken)
                .ConfigureAwait(false);

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
                transcriptDirectory);
        }
        finally
        {
            _createGate.Release();
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
    private async Task ReleaseAsync(InstanceSupervisor supervisor)
    {
        // Out of the collection first, so the watchdog and the restart scheduler
        // stop looking at an instance that is on its way out.
        _supervisors.TryRemove(supervisor.InstanceId, out _);

        // Stopped properly, not killed. DisposeAsync kills — it says so, and it
        // is right to — but a server killed mid-write leaves a world that may
        // not open again. That matters most in the case where the administrator
        // asked to *keep* the files: retiring or removing an instance must not
        // be the thing that corrupts the world it just handed back.
        if (supervisor.Status.State is not (InstanceState.Stopped or InstanceState.Faulted))
        {
            try
            {
                await supervisor.StopAsync(
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
    /// A port no existing instance is using, spaced two apart so the IPv6 port
    /// beside it stays free too.
    /// </summary>
    public int SuggestPort(int start = 19132)
    {
        var taken = _supervisors.Values
            .Select(s => ServerProperties.PortOf(s.WorkingDirectory))
            .Where(p => p is not null)
            .Select(p => p!.Value)
            .ToHashSet();

        var candidate = start;
        while (taken.Contains(candidate) || taken.Contains(candidate + 1))
        {
            candidate += 2;
        }

        return candidate;
    }

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
