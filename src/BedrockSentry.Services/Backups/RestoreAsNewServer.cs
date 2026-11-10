using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Backups;

/// <summary>What was done to the copy's <c>server.properties</c>.</summary>
/// <remarks>
/// Three states rather than a nullable port, because a null would be saying two
/// unrelated things at once: the backup carried no properties file, and the
/// rewrite that keeps this copy off the original's port did not happen. The
/// second is a server that will refuse to start while the original runs; the
/// first is nothing at all (<c>T176</c>).
/// </remarks>
public enum CopiedProperties
{
    /// <summary>The backup had none, or the configuration was not asked for.</summary>
    NotRestored,

    /// <summary>Restored, and this copy's own name and ports written over it.</summary>
    Rewritten,

    /// <summary>Restored, and the rewrite failed — it still carries the original's.</summary>
    LeftAsTheOriginals,
}

/// <summary>What to make, and out of what.</summary>
/// <param name="BackupPath">One of the source instance's own backups.</param>
/// <param name="Name">The copy's display name.</param>
/// <param name="PackageFileName">
/// The Bedrock build to install into it. Never optional: a backup holds a world
/// and the configuration beside it, never <c>bedrock_server.exe</c>, so a copy
/// always needs a build from somewhere and choosing it is <c>D41</c>.
/// </param>
/// <param name="Port">
/// Never optional either, and for a blunter reason: the package ships listening on
/// 19132 and a restored <c>server.properties</c> carries the original's number, so
/// every path that does not choose one lands the copy on top of another server.
/// </param>
public sealed record RestoreAsNewRequest(
    string BackupPath,
    string Name,
    string PackageFileName,
    int Port,
    string? WorkingDirectory = null,
    bool RestoreConfig = false,
    bool RestoreAddons = true);

/// <summary>What restoring a backup into a new server did.</summary>
public sealed record RestoreAsNewResult(
    string InstanceId,
    string Name,
    string BackupName,
    int FilesRestored,
    IReadOnlyList<string> ConfigFilesRestored,
    CopiedProperties Properties,
    int Port,
    string? WorldFailure = null,
    bool WorldPartlyWritten = false,
    AddonsRestored Addons = default,
    LevelSettled Level = default)
{
    /// <summary>Whether the world went in.</summary>
    public bool WorldRestored => WorldFailure is null;

    /// <summary>
    /// The whole sentence the panel shows, here rather than in the page so a test
    /// can reach it — the suite cannot see a <c>.razor</c> file at all.
    /// </summary>
    public string Summary()
    {
        // Said in every arm and first in the failing one, because it is the fact
        // somebody most needs and the one this operation exists to guarantee: the
        // server that was backed up is not involved. Nothing below can change it —
        // every write on this path goes into a directory made moments ago.
        const string untouched = "The server it came from was not touched.";

        if (!WorldRestored)
        {
            var leftover = WorldPartlyWritten
                ? " Part of a world was written before it failed and could not be "
                  + "cleared, so do not start this copy — remove it from its own tab."
                : " Its world folder is empty, so remove it from its own tab or start "
                  + "it and Bedrock generates a fresh world.";

            return $"{Name} was created, and the world from {BackupName} could not be put "
                   + $"into it: {WorldFailure}{leftover} Its configuration was not copied "
                   + $"either, so nothing from that backup is in it. {untouched}";
        }

        var files = $"{FilesRestored} file{(FilesRestored == 1 ? "" : "s")}";

        var config = ConfigFilesRestored.Count > 0
            ? $", plus {ConfigFilesRestored.Count} configuration "
              + $"file{(ConfigFilesRestored.Count == 1 ? "" : "s")}"
            : "";

        // `D42` says the copy is a faithful copy, and the port is the one place it
        // cannot be: two servers on one number is a copy that will not start. So the
        // exception is stated rather than left to be discovered on the properties tab.
        var properties = Properties switch
        {
            CopiedProperties.NotRestored => "",

            CopiedProperties.Rewritten =>
                $" Its allowlist and permissions came across as they were; its name and "
                + $"port did not, because the original's are in use — this copy is on {Port}.",

            CopiedProperties.LeftAsTheOriginals =>
                " Its allowlist and permissions came across as they were, and its own "
                + $"name and port could NOT be written over the restored file — it is on "
                + "the original's port and will refuse to start while that server is "
                + "running. Set the port on its Server properties tab.",

            _ => throw new ArgumentOutOfRangeException(
                nameof(Properties), Properties, "Unhandled properties state."),
        };

        // `D97`. Only the backup's add-ons can be here: a new server has none of its
        // own to keep.
        var addons = Addons.Clause() is { Length: > 0 } clause ? " " + clause : "";

        // `T335`. Which world it loads, when that took a change or there are several.
        var level = Level.Clause() is { Length: > 0 } chosen ? " " + chosen : "";

        return $"Created {Name} from {BackupName} ({files}{config})."
               + addons
               + properties
               + level
               + " It is stopped and will not start with the service; start it from its "
               + $"own tab when you are ready. {untouched}";
    }
}

/// <summary>
/// Restores a backup into a <em>new</em> server instead of over the one it came
/// from (<c>D40</c>, milestone 10).
/// </summary>
/// <remarks>
/// <para><strong>Nothing about the source instance is read except its backups and
/// its build.</strong> It is not stopped, no safety backup is taken of it, and
/// its world is never opened — which is the whole reason somebody reaches for
/// this rather than <see cref="WorldRestorer.RestoreAsync"/>. Every guard in the
/// in-place restore that exists to protect an existing world is therefore absent
/// here on purpose, and the ones that are about the <em>backup</em> are kept.</para>
///
/// <para><strong>The refusals come before the instance is created</strong>, which
/// is <c>T168</c>'s ordering rule with a stray server in place of an outage: a
/// backup this cannot restore is a backup nobody should be given a half-made
/// instance over. The one refusal that can still arrive late is a folder snapshot
/// emptied between the pre-flight and the copy, and it says what it cost.</para>
///
/// <para><strong>The copy is created stopped and with auto-start off</strong>,
/// whatever the source instance does. That is <c>InstanceRegistry.RestoreAsync</c>'s
/// rule for un-retiring and it is stronger here: a copy restored to look at
/// something should not come up and start accepting players, and until somebody
/// has looked at it, its world is a duplicate of one that may still be live.</para>
/// </remarks>
public sealed class RestoreAsNewServer
{
    private readonly InstanceRegistry _registry;
    private readonly WorldRestorer _restorer;
    private readonly BackupHistory _history;
    private readonly TimeProvider _time;
    private readonly ILogger<RestoreAsNewServer> _log;

    public RestoreAsNewServer(
        InstanceRegistry registry,
        WorldRestorer restorer,
        BackupHistory history,
        TimeProvider time,
        ILogger<RestoreAsNewServer> log)
    {
        _registry = registry;
        _restorer = restorer;
        _history = history;
        _time = time;
        _log = log;
    }

    public async Task<RestoreAsNewResult> RestoreAsync(
        InstanceSupervisor source,
        RestoreAsNewRequest request,
        CancellationToken cancellationToken = default)
    {
        // The same authorisation the in-place path makes, and for the same reason:
        // a picker is not an authorisation, and this one hands the chosen path to a
        // directory the caller also names.
        var backup = _restorer.ListBackups(source.InstanceId)
            .FirstOrDefault(b => string.Equals(
                b.FullName, Path.GetFullPath(request.BackupPath), StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException(
                $"'{Path.GetFileName(request.BackupPath)}' is not one of this instance's backups.");

        // `T251`, and it crosses for the reason the two below it do: it is a
        // question about the BACKUP, and the cost of answering it late is what
        // differs rather than whether it is worth asking. Late here means a server
        // created, named and listed, with the extract failing into it — which is
        // the state this path's own refusals exist to prevent.
        var contents = _restorer.ReadContents(backup.FullName);

        if (!contents.CanRestore)
        {
            throw new InvalidOperationException(
                contents.Refusal(backup.Name, asNewServer: true));
        }

        if (backup.IsFolder && !WorldRestorer.HoldsAWorld(backup.FullName))
        {
            throw new InvalidOperationException(
                WorldRestorer.EmptySnapshotRefusal(backup.Name, EmptySnapshotStage.BeforeTheStop));
        }

        if (DamagedBackups.WasDestroyed(
                await _history.ReadAsync(source.InstanceId, cancellationToken).ConfigureAwait(false),
                backup.Name))
        {
            throw new InvalidOperationException(
                DamagedBackups.Refusal(backup.Name, asNewServer: true));
        }

        var created = await _registry.CreateAsync(
                new NewInstanceRequest(
                    request.Name,
                    request.PackageFileName,
                    request.Port,
                    // Both off, and neither is the source instance's answer. See the
                    // class remarks: a copy is not a server anybody has decided to run.
                    AutoStart: false,
                    WorkingDirectory: request.WorkingDirectory,
                    StartNow: false,
                    // The transport is a fact about the server rather than about this
                    // machine, so the copy speaks what the original speaks. The port
                    // below is the opposite case and is why it cannot simply be copied.
                    Transport: TransportOf(source),

                    // `D40`: the copy is marked as one. Three weeks on, a second
                    // server with a familiar world is indistinguishable from the
                    // real one, and the mistake that invites is acting on it.
                    RestoredFrom: new RestoredFrom
                    {
                        BackupName = backup.Name,
                        SourceInstanceId = source.InstanceId,
                        SourceName = source.Definition.Name,
                        WhenUtc = _time.GetUtcNow(),
                        BackupTakenUtc = backup.TakenUtc,
                    }),
                cancellationToken)
            .ConfigureAwait(false);

        var worldsDirectory = Path.Combine(created.WorkingDirectory, "worlds");

        var files = 0;
        string? worldFailure = null;
        var partlyWritten = false;
        AddonsRestored addons = default;

        try
        {
            // A Bedrock package ships its own `worlds/` with a default level in it,
            // so extracting on top would mix two worlds into one directory — the
            // corruption `WorldRestorer` moves an existing world aside to avoid. It
            // is deleted rather than moved aside because this directory is seconds
            // old and was written by the extraction above: there is nothing here
            // anybody could want back, which is exactly what is NOT true in place.
            //
            // INSIDE the try, because the instance already exists by this line and a
            // throw from here would reach the panel as a failed operation with a
            // server left behind that nothing mentioned — the same hole the catch
            // below exists to close. Unproven on this platform: the delete only
            // refuses on a lock or an ACL, and neither can be arranged as root.
            if (Directory.Exists(worldsDirectory))
            {
                Directory.Delete(worldsDirectory, recursive: true);
            }

            files = WorldRestorer.Extract(
                backup, worldsDirectory, EmptySnapshotStage.IntoANewServer);

            // `D97`. No world is being replaced, so leaving the backup's add-ons out
            // leaves the new world with none.
            var settled = WorldRestorer.SettleAddons(
                worldsDirectory, replacedWorlds: null, request.RestoreAddons);

            addons = settled.Outcome;
            files -= settled.FilesLeftOut;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidOperationException
                  or InvalidDataException)
        {
            // Reported rather than thrown, because by here the instance exists and an
            // exception reaching the panel would say the operation failed while
            // leaving a server behind that nobody was told about (`T229`, `D67`).
            _log.LogError(
                ex, "Restoring {Backup} into the new instance {InstanceId} failed",
                backup.Name, created.InstanceId);

            worldFailure = ex.Message;
            partlyWritten = !NewServerLevel.TryClear(worldsDirectory, _log);
        }

        // Not when the world failed. A server holding this backup's allowlist and
        // none of its world is the partly-applied state `T176` and `D67` are both
        // about — it looks like a copy of something and is a copy of nothing, and
        // the operator's next move either way is to remove it and pick another
        // backup. Failing whole is the state that can be described in one sentence.
        var configFiles = request.RestoreConfig && worldFailure is null
            ? _restorer.RestoreConfig(backup, created.WorkingDirectory)
            : [];

        var properties = await NewServerLevel.ReapplyIdentityAsync(
                created.WorkingDirectory, created.Definition.Name, request.Port, configFiles, _log,
                cancellationToken)
            .ConfigureAwait(false);

        // `T335`. After the configuration, which can bring the backup's own level-name
        // with it. Without it, the new server's level-name is the package's, and a
        // backup of a server whose world has another name would sit unread beside the
        // new world Bedrock generated.
        var level = worldFailure is null
            ? await NewServerLevel.SettleAsync(
                    created.WorkingDirectory, WorldRestorer.LevelNameIn(backup), _log, cancellationToken)
                .ConfigureAwait(false)
            : default;

        _log.LogInformation(
            "Restored {Backup} from {Source} into the new instance {InstanceId}: {Files} files",
            backup.Name, source.InstanceId, created.InstanceId, files);

        return new RestoreAsNewResult(
            created.InstanceId,
            created.Definition.Name,
            backup.Name,
            files,
            configFiles,
            properties,
            request.Port,
            worldFailure,
            partlyWritten,
            addons,
            level);
    }

    /// <summary>The transport the source instance speaks, or the wrapper's default.</summary>
    /// <remarks>
    /// An unreadable or unrecognised file gives the default rather than a guess.
    /// Getting this wrong costs one edit on the copy's properties tab and nothing
    /// irreversible, which is why it is inferred here and the Bedrock build — where
    /// being wrong upgrades a world for good — is asked of the operator (<c>D41</c>).
    /// </remarks>
    private static ServerTransport TransportOf(InstanceSupervisor source)
    {
        var file = ServerProperties.ReadFile(
            ServerProperties.PathFor(source.WorkingDirectory));

        // An absent key is RakNet, measured (`D75`), and a copy keeps what its source
        // runs (`D40`); only a spelling nothing knows falls back to the default.
        return ServerTransports.Parse(file.Values.GetValueOrDefault(ServerTransports.PropertyKey))
            ?? ServerTransports.Default;
    }
}
