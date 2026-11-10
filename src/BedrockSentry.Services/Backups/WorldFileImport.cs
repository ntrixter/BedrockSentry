using System.IO.Compression;
using BedrockSentry.Core.Addons;
using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Backups;

/// <summary>What an uploaded world file holds, or why it cannot be used (<c>T334</c>).</summary>
/// <param name="FileName">The name it was uploaded under.</param>
/// <param name="Worlds">
/// For <see cref="WorldFileLayout.WorldFolders"/> and <see cref="WorldFileLayout.ServerFolder"/>, the
/// worlds it holds, by folder name.
/// </param>
/// <param name="WorldName">The name in a <c>.mcworld</c>'s <c>levelname.txt</c>, if it has one.</param>
/// <param name="ConfigFiles">The configuration it carries, as a backup does, by file name.</param>
/// <param name="BackupLevelName">The <c>level-name</c> its own <c>server.properties</c> set.</param>
/// <param name="Files">How many files go into the new server's <c>worlds</c> folder.</param>
/// <param name="UnpackedBytes">What those files come to once written.</param>
/// <param name="Refusal">Why it cannot be used, or null when it can.</param>
public sealed record WorldFile(
    string FileName,
    WorldFileLayout Layout,
    IReadOnlyList<string> Worlds,
    string? WorldName,
    IReadOnlyList<string> ConfigFiles,
    string? BackupLevelName,
    int Files,
    long UnpackedBytes,
    string? Refusal = null)
{
    /// <summary>Whether a server can be made around it.</summary>
    public bool CanUse => Refusal is null;

    internal static WorldFile Refused(string fileName, string refusal) =>
        new(fileName, WorldFileLayout.None, [], null, [], null, 0, 0, refusal);

    /// <summary>What the form says about it, here rather than in the page so a test can reach it.</summary>
    public string Describe()
    {
        if (Refusal is { } refusal)
        {
            return refusal;
        }

        var size = $"{Files} file{(Files == 1 ? "" : "s")}, {ByteSize.Format(UnpackedBytes)} unpacked";

        var world = Layout switch
        {
            WorldFileLayout.WorldAtRoot => WorldName is { Length: > 0 } name
                ? $"A Minecraft world called '{name}' ({size})."
                : $"A Minecraft world ({size}).",

            WorldFileLayout.WorldFolders or WorldFileLayout.ServerFolder when Worlds.Count == 1 =>
                $"The world '{Worlds[0]}' ({size}).",

            WorldFileLayout.WorldFolders or WorldFileLayout.ServerFolder =>
                $"{Worlds.Count} worlds: {string.Join(", ", Worlds.Select(w => $"'{w}'"))} ({size}). "
                + "The server loads one of them, and level-name on its Server properties tab chooses which.",

            WorldFileLayout.None => "It holds no world.",

            _ => throw new ArgumentOutOfRangeException(nameof(Layout), Layout, "Unhandled world file layout."),
        };

        var config = ConfigFiles.Count > 0
            ? $" It also carries a server's configuration: {string.Join(", ", ConfigFiles)}."
            : "";

        return world + config;
    }
}

/// <summary>A new server made around an uploaded world file (<c>T334</c>).</summary>
/// <param name="Instance">
/// The server to make, as the New instance form fills it in. Its port and its package are
/// not optional: a world file never holds <c>bedrock_server.exe</c>, and a configuration that
/// comes with one carries another server's port.
/// </param>
/// <param name="UploadPath">The upload, as <see cref="WorldFileImport.SaveUploadAsync"/> stored it.</param>
/// <param name="FileName">The name it was uploaded under.</param>
/// <param name="UseConfig">
/// Also write the configuration a backup carries, with this server's own name and ports over
/// it (<c>D42</c>). Off unless asked for, as a restore's is.
/// </param>
public sealed record WorldImportRequest(
    NewInstanceRequest Instance,
    string UploadPath,
    string FileName,
    bool UseConfig = false);

/// <summary>What making a server around a world file did (<c>T334</c>).</summary>
/// <param name="StartFailure">Why it did not start when it was asked to, said on its console too.</param>
public sealed record WorldImportResult(
    string InstanceId,
    string Name,
    string FileName,
    int FilesPlaced,
    IReadOnlyList<string> ConfigFilesWritten,
    CopiedProperties Properties,
    int Port,
    LevelSettled Level = default,
    string? WorldFailure = null,
    bool WorldPartlyWritten = false,
    string? AutoStartFailure = null,
    string? StartFailure = null)
{
    /// <summary>Whether the world went in.</summary>
    public bool WorldPlaced => WorldFailure is null;

    /// <summary>Whether starting it loads the world that went in.</summary>
    public bool Ready => WorldPlaced && Level.LoadsTheWorld;

    /// <summary>
    /// What its console says once it is made, here rather than in the page so a test can
    /// reach it.
    /// </summary>
    public string Summary()
    {
        if (!WorldPlaced)
        {
            var leftover = WorldPartlyWritten
                ? " Part of it was written before it failed and could not be cleared, so do not start "
                  + "this server; remove it from its own tab."
                : " Its world folder is empty, so remove it from its own tab, or start it and Bedrock "
                  + "generates a new world.";

            return $"{Name} was created, and the world in {FileName} could not be put into it: "
                   + $"{WorldFailure}{leftover} Nothing else from that file was used, and it will not "
                   + "start with the service.";
        }

        var files = $"{FilesPlaced} file{(FilesPlaced == 1 ? "" : "s")}";

        var config = ConfigFilesWritten.Count > 0
            ? $", plus {ConfigFilesWritten.Count} configuration "
              + $"file{(ConfigFilesWritten.Count == 1 ? "" : "s")}"
            : "";

        var properties = Properties switch
        {
            CopiedProperties.NotRestored => "",

            CopiedProperties.Rewritten =>
                " Its settings, allowlist and permissions came from the file; its name and port did not, "
                + $"and it is on {Port}.",

            CopiedProperties.LeftAsTheOriginals =>
                " Its settings, allowlist and permissions came from the file, and its own name and port "
                + "could NOT be written over them, so it is on the port that file names. Set the port on "
                + "its Server properties tab.",

            _ => throw new ArgumentOutOfRangeException(
                nameof(Properties), Properties, "Unhandled properties state."),
        };

        var level = Level.Clause() is { Length: > 0 } clause ? " " + clause : "";

        var stopped = Ready ? "" : " It was left stopped, and it will not start with the service.";

        var autoStart = AutoStartFailure is { } why
            ? $" Starting it with the service could not be turned on: {why}"
            : "";

        return $"Created {Name} with the world from {FileName} ({files}{config})."
               + properties + level + stopped + autoStart;
    }
}

/// <summary>
/// Makes a new server around a world file uploaded from the panel: a <c>.mcworld</c> exported
/// from Minecraft, or a ZIP backup this panel wrote (<c>T334</c>).
/// </summary>
/// <remarks>
/// <para><strong>The upload is written to disk first</strong>, under the data root: a ZIP's
/// directory is at its end, and a world can be gigabytes, which is too much to hold in
/// memory. What is left there is swept a day later.</para>
///
/// <para><strong>Every refusal about the file comes before the server is created</strong>
/// (<c>T168</c>), and every entry is decided before any is written (<c>D82</c>). From the
/// moment the server exists, what goes wrong is reported rather than thrown (<c>D67</c>):
/// the server is there, and an exception would leave it unmentioned.</para>
///
/// <para><strong>It starts stopped with auto-start off</strong>, whatever the form asked,
/// until the world is in and <c>level-name</c> names it; then the form's answers apply.
/// A service restart in between would otherwise start the package's own world, and a
/// world that fails to go in leaves a server nobody should start.</para>
/// </remarks>
public sealed class WorldFileImport
{
    /// <summary>The largest file that may be uploaded.</summary>
    public const long MaxUploadBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>What a world file may unpack to.</summary>
    /// <remarks>
    /// Separate from the upload bound, as the add-on library's two are: this one stops a
    /// small archive expanding to fill the disk the worlds are on. A world's LevelDB files
    /// are compressed already, so twice the upload is room enough.
    /// </remarks>
    public const long MaxUnpackedBytes = 16L * 1024 * 1024 * 1024;

    /// <summary>How many files a world file may hold.</summary>
    public const int MaxEntries = 200_000;

    /// <summary>How long a stored upload nobody used is kept.</summary>
    public static readonly TimeSpan UploadLifetime = TimeSpan.FromDays(1);

    private const string UploadSuffix = ".upload";

    private readonly InstanceRegistry _registry;
    private readonly ISentryPaths _paths;
    private readonly TimeProvider _time;
    private readonly ILogger<WorldFileImport> _log;

    public WorldFileImport(
        InstanceRegistry registry, ISentryPaths paths, TimeProvider time, ILogger<WorldFileImport> log)
    {
        _registry = registry;
        _paths = paths;
        _time = time;
        _log = log;
    }

    /// <summary>Where uploads wait to be used.</summary>
    public string UploadsDirectory => Path.Combine(_paths.Data, "uploads");

    /// <summary>The upload bound this instance enforces; a seam, since no test uploads gigabytes.</summary>
    internal long UploadLimit { get; set; } = MaxUploadBytes;

    /// <summary>The unpacked bound this instance enforces; a seam for the same reason.</summary>
    internal long UnpackedLimit { get; set; } = MaxUnpackedBytes;

    /// <summary>Writes an upload to disk, returning where.</summary>
    /// <param name="progress">Told the bytes written so far.</param>
    /// <remarks>A partial file is deleted, whatever stopped it.</remarks>
    public async Task<string> SaveUploadAsync(
        Stream upload, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);

        Directory.CreateDirectory(UploadsDirectory);
        SweepOldUploads();

        var path = Path.Combine(UploadsDirectory, $"{Guid.NewGuid():N}{UploadSuffix}");

        try
        {
            await using var file = new FileStream(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

            var buffer = new byte[1 << 20];
            long total = 0;
            int read;

            while ((read = await upload.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;

                if (total > UploadLimit)
                {
                    throw new InvalidDataException(
                        $"The file is larger than {ByteSize.Format(UploadLimit)}, so it was not kept.");
                }

                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                progress?.Report(total);
            }

            return path;
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    /// <summary>Deletes an upload that will not be used. Anything outside the uploads folder is left alone.</summary>
    public void Discard(string? uploadPath)
    {
        if (uploadPath is not null && IsUpload(uploadPath))
        {
            TryDelete(uploadPath);
        }
    }

    /// <summary>What a stored upload holds, or why it cannot be used.</summary>
    /// <param name="uploadPath">Where <see cref="SaveUploadAsync"/> stored it.</param>
    /// <param name="fileName">The name it was uploaded under, which the refusals name.</param>
    public WorldFile Read(string uploadPath, string fileName)
    {
        RefuseAPathNotUploadedHere(uploadPath);

        try
        {
            using var zip = ZipFile.OpenRead(uploadPath);
            return Read(zip, fileName);
        }
        catch (InvalidDataException ex)
        {
            return WorldFile.Refused(
                fileName,
                $"{fileName} is not a ZIP archive, so there is no world in it this can read. {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return WorldFile.Refused(fileName, $"{fileName} could not be read: {ex.Message}");
        }
    }

    /// <summary>Makes the server and puts the world into it.</summary>
    /// <exception cref="InvalidOperationException">
    /// The file cannot be used, or the server could not be created; either way nothing was made
    /// and the upload is kept, so the form can try again without another upload.
    /// </exception>
    public async Task<WorldImportResult> CreateAsync(
        WorldImportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Instance.Port is not { } port)
        {
            throw new ArgumentException(
                "A server made around a world file needs a port of its own.", nameof(request));
        }

        if (request.Instance.PackageFileName is null)
        {
            throw new ArgumentException(
                "A server made around a world file needs a Bedrock build to run it.", nameof(request));
        }

        var file = Read(request.UploadPath, request.FileName);

        if (file.Refusal is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }

        var created = await _registry.CreateAsync(
                request.Instance with { AutoStart = false, StartNow = false, CompanionPack = false },
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            return await FillAsync(created, request, file, port, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Once the server exists the upload has been used, whatever became of the world:
            // trying again means a new server, and the panel asks for the file again.
            Discard(request.UploadPath);
        }
    }

    private async Task<WorldImportResult> FillAsync(
        InstanceSupervisor created,
        WorldImportRequest request,
        WorldFile file,
        int port,
        CancellationToken cancellationToken)
    {
        var worldsDirectory = Path.Combine(created.WorkingDirectory, "worlds");
        var placed = 0;
        string? worldFailure = null;
        var partlyWritten = false;

        try
        {
            // The package's own default world goes, as a restore into a new server's does:
            // two worlds in one folder is what LevelDB reads as corruption.
            if (Directory.Exists(worldsDirectory))
            {
                Directory.Delete(worldsDirectory, recursive: true);
            }

            placed = PlaceWorld(request.UploadPath, file, NewServerLevel.LevelOf(created.WorkingDirectory), worldsDirectory);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            _log.LogError(
                ex, "Putting the world from {File} into the new instance {InstanceId} failed",
                request.FileName, created.InstanceId);

            worldFailure = ex.Message;
            partlyWritten = !NewServerLevel.TryClear(worldsDirectory, _log);
        }

        // Not when the world failed: a server holding a file's allowlist and none of its
        // world looks like a copy of something and is a copy of nothing (`T176`, `D67`).
        var configFiles = request.UseConfig && worldFailure is null
            ? WriteConfig(request.UploadPath, created.WorkingDirectory)
            : [];

        var properties = await NewServerLevel.ReapplyIdentityAsync(
                created.WorkingDirectory, created.Definition.Name, port, configFiles, _log, cancellationToken)
            .ConfigureAwait(false);

        var level = worldFailure is null
            ? await NewServerLevel.SettleAsync(
                    created.WorkingDirectory, file.BackupLevelName, _log, cancellationToken)
                .ConfigureAwait(false)
            : default;

        var ready = worldFailure is null && level.LoadsTheWorld;

        // D93, into the world that went in, which is the one level-name now names.
        if (ready && request.Instance.CompanionPack)
        {
            await _registry.SwitchOnCompanionAsync(created, cancellationToken).ConfigureAwait(false);
        }

        string? autoStartFailure = null;

        if (ready && request.Instance.AutoStart)
        {
            try
            {
                await _registry.SetAutoStartAsync(created.InstanceId, autoStart: true, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogError(ex, "Could not turn on auto-start for {InstanceId}", created.InstanceId);
                autoStartFailure = ex.Message;
            }
        }

        var result = new WorldImportResult(
            created.InstanceId, created.Definition.Name, request.FileName, placed, configFiles, properties, port, level,
            worldFailure, partlyWritten, autoStartFailure);

        _log.LogInformation(
            "Made {InstanceId} around {File}: {Files} world files, world placed {Placed}",
            created.InstanceId, request.FileName, placed, result.WorldPlaced);

        // Before any start, so the console reads in order: what was made, then the server.
        created.Console.Append(ConsoleOrigin.Notice, result.Summary(), _time.GetUtcNow());

        if (!ready || !request.Instance.StartNow)
        {
            return result;
        }

        try
        {
            await created.StartAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "The new instance {InstanceId} did not start", created.InstanceId);
            created.Console.Append(ConsoleOrigin.Notice, $"it did not start: {ex.Message}", _time.GetUtcNow());
            return result with { StartFailure = ex.Message };
        }
    }

    private WorldFile Read(ZipArchive zip, string fileName)
    {
        var plan = Plan(zip);

        if (!plan.Accepted)
        {
            _log.LogWarning("Refused the world file {File}: {Verdict} on {Entry}", fileName, plan.Verdict, plan.OffendingEntry);

            return WorldFile.Refused(fileName, plan.Verdict switch
            {
                ArchiveEntryVerdict.TooLarge =>
                    $"{fileName} unpacks to more than {ByteSize.Format(UnpackedLimit)}, the most a world file may.",
                ArchiveEntryVerdict.TooManyEntries =>
                    $"{fileName} holds more than {MaxEntries:N0} files, the most a world file may.",
                _ => $"{plan.Reason ?? PackArchiveRules.Describe(plan.Verdict)} Nothing in {fileName} was used.",
            });
        }

        if (NewServerWorld.Collision(plan.Paths) is { } collision)
        {
            return WorldFile.Refused(
                fileName,
                $"Two entries in {fileName} are the same file on Windows, '{collision}' among them, so it "
                + "cannot be unpacked as it is.");
        }

        var (layout, worlds) = NewServerWorld.Classify(plan.Paths);

        if (layout == WorldFileLayout.None)
        {
            return WorldFile.Refused(
                fileName,
                $"{fileName} holds no level.dat at its top, one folder down or under worlds/, so there is no "
                + "world in it. A .mcworld exported from Minecraft has one, and so does a ZIP backup from this panel.");
        }

        var files = 0;
        long bytes = 0;
        var config = new List<string>();
        string? worldName = null;
        string? backupLevelName = null;

        foreach (var (entry, destination) in Paired(zip, plan))
        {
            if (NewServerWorld.ConfigNameOf(destination, layout) is { } name)
            {
                config.Add(name);

                if (name == ServerProperties.FileName)
                {
                    backupLevelName = NewServerLevel.LevelNameIn(NewServerLevel.LinesOf(entry));
                }

                continue;
            }

            if (NewServerWorld.TargetOf(destination, layout, NewServerWorld.DefaultLevelName, worlds) is null)
            {
                continue;
            }

            files++;
            bytes += entry.Length;

            if (layout == WorldFileLayout.WorldAtRoot
                && string.Equals(destination, "levelname.txt", StringComparison.OrdinalIgnoreCase))
            {
                worldName = NameIn(entry);
            }
        }

        return new WorldFile(
            fileName, layout, worlds, worldName, [.. config.Order(StringComparer.Ordinal)], backupLevelName,
            files, bytes);
    }

    private PackArchivePlan Plan(ZipArchive zip) =>
        PackArchiveRules.Plan(
            zip.Entries.Select(e => new ArchiveEntry(e.FullName, e.Length)), UnpackedLimit, MaxEntries);

    /// <summary>Each file entry with the path the plan approved for it.</summary>
    /// <remarks>
    /// Matched by the entry's own name rather than by position, because the plan skips
    /// directory entries. Nothing below ever writes to an entry's own name.
    /// </remarks>
    private static IEnumerable<(ZipArchiveEntry Entry, string Destination)> Paired(
        ZipArchive zip, PackArchivePlan plan)
    {
        var destinations = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var planned in plan.Entries)
        {
            destinations.TryAdd(planned.Source, planned.Destination);
        }

        foreach (var entry in zip.Entries)
        {
            if (destinations.TryGetValue(entry.FullName, out var destination))
            {
                yield return (entry, destination);
            }
        }
    }

    /// <summary>The world's name as Minecraft shows it, from its <c>levelname.txt</c>.</summary>
    private static string? NameIn(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        var buffer = new byte[512];
        var length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);

        var text = PackDisplayName.WithoutFormatting(PackText.Decode(buffer.AsSpan(0, length)));
        var line = text.Split('\n', 2)[0].Trim();

        return line.Length > 0 ? line : null;
    }

    private int PlaceWorld(string uploadPath, WorldFile file, string level, string worldsDirectory)
    {
        Directory.CreateDirectory(worldsDirectory);
        var root = Path.GetFullPath(worldsDirectory);
        var placed = 0;

        using var zip = ZipFile.OpenRead(uploadPath);
        var plan = Plan(zip);

        if (!plan.Accepted)
        {
            throw new InvalidDataException(plan.Reason ?? PackArchiveRules.Describe(plan.Verdict));
        }

        foreach (var (entry, destination) in Paired(zip, plan))
        {
            if (NewServerWorld.TargetOf(destination, file.Layout, level, file.Worlds) is not { } relative)
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(root, relative));

            // The plan already refused every escape. Checked again at the write, because a
            // level-name is a path segment too.
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The entry '{entry.FullName}' would be written outside the worlds folder.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
            placed++;
        }

        return placed;
    }

    private IReadOnlyList<string> WriteConfig(string uploadPath, string workingDirectory)
    {
        var written = new List<string>();

        using var zip = ZipFile.OpenRead(uploadPath);
        var plan = Plan(zip);
        var (layout, _) = NewServerWorld.Classify(plan.Paths);

        foreach (var (entry, destination) in Paired(zip, plan))
        {
            if (NewServerWorld.ConfigNameOf(destination, layout) is not { } name)
            {
                continue;
            }

            try
            {
                entry.ExtractToFile(Path.Combine(workingDirectory, name), overwrite: true);
                written.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Could not write {File} from an uploaded world file", name);
            }
        }

        return written;
    }

    private bool IsUpload(string path)
    {
        var full = Path.GetFullPath(path);

        return full.EndsWith(UploadSuffix, StringComparison.OrdinalIgnoreCase)
               && string.Equals(
                   Path.GetDirectoryName(full),
                   Path.GetFullPath(UploadsDirectory).TrimEnd(Path.DirectorySeparatorChar),
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Only a file this class stored is read: the path reaches here from a page.</summary>
    internal void RefuseAPathNotUploadedHere(string uploadPath)
    {
        if (!IsUpload(uploadPath))
        {
            throw new ArgumentException("That is not an upload this panel stored.", nameof(uploadPath));
        }
    }

    private void SweepOldUploads()
    {
        var cutoff = _time.GetUtcNow().UtcDateTime - UploadLifetime;

        foreach (var file in Directory.EnumerateFiles(UploadsDirectory, "*" + UploadSuffix))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Could not remove the old upload {File}", file);
            }
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not remove the upload {File}", path);
        }
    }
}
