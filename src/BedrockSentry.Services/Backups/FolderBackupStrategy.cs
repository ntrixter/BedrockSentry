using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Backups;

/// <summary>
/// Copies the held snapshot out into a plain folder, uncompressed
/// (<c>docs/design.md</c> § 8.4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>This was <c>VssBackupStrategy</c> and it produced no VSS snapshot</strong>
/// (<c>D66</c>). It took a shadow copy itself, read the world out of the frozen
/// view, wrote a folder and released the snapshot — so the one string an
/// administrator reads before choosing offered "ZIP" against "VSS", which reads as
/// <em>file versus snapshot</em> and named a compressed file against an
/// uncompressed folder. The snapshot was a means, and it now belongs to the
/// orchestrator as an acquisition toggle covering both formats. What is left here
/// is the format.
/// </para>
/// <para>
/// So the difference from the ZIP strategy is exactly one thing: no compression.
/// That is worth having on its own — a folder restores by copying, it is readable
/// without tooling, and on a volume running Data Deduplication it is the format
/// that dedupes best, because unchanged world files are byte-identical from one
/// backup to the next and collapse completely (<c>D55</c>). Compression destroys
/// the byte-level similarity dedup depends on, so the choice between these two is
/// a real one rather than a preference.
/// </para>
/// <para>
/// The rest of the <c>worlds</c> folder is copied beside the save, each file whole
/// (<c>D97</c>), under the ZIP strategy's rule for a file that cannot be opened.
/// </para>
/// <para>
/// <strong>The manifest lengths still apply</strong>, and they are the part it
/// would be easy to get wrong. The world files on disk are <em>longer</em> than
/// the save BDS just flushed; those trailing bytes belong to the next save
/// (<c>docs/design.md</c> § 5.5). Copying them whole produces a folder that looks
/// perfect and restores to a corrupt world, which is the failure mode this
/// codebase most wants to avoid, because nobody finds out until they need it.
/// </para>
/// </remarks>
public sealed class FolderBackupStrategy : IBackupStrategy
{
    /// <summary>The prefix every artifact folder carries. Also what retention matches.</summary>
    /// <remarks>
    /// <para><strong>Renamed from <c>vss-</c> with no migration</strong>
    /// (<c>D66</c>, operator's decision): every existing backup was test-server
    /// data, so the prefix moved outright rather than being kept divergent from
    /// the display name for the rest of the project's life. Doing it in the same
    /// change is cheap exactly once.</para>
    ///
    /// <para>A folder left behind under the old prefix is therefore invisible to
    /// retention and to the restore picker — which is the honest consequence of
    /// the rename and is strictly better than a rule that deletes artifacts it
    /// does not recognise.</para>
    /// </remarks>
    public const string FolderPrefix = "folder-";

    /// <summary>
    /// Where a folder copy written before <c>D110</c> kept the server's configuration.
    /// Read, never written: the configuration now sits at the folder's root.
    /// </summary>
    public static readonly string ConfigFolder =
        ServerConfigFiles.ArchivePrefix.TrimEnd('/');

    private readonly TimeProvider _time;
    private readonly ILogger<FolderBackupStrategy> _log;

    /// <summary>
    /// Removes one pruned folder. A seam, for the reason
    /// <see cref="Instances.PortInspector"/> got one: the failure the catch around
    /// it exists for cannot be produced where this is written. The build runs as
    /// root, so a rig that makes a file unwritable and then asks for its folder to
    /// be removed <em>succeeds</em>, and the arm stays uncovered exactly where the
    /// code is (<c>T176</c>).
    /// </summary>
    internal Action<string> RemoveArtifact { get; set; } =
        path => Directory.Delete(path, recursive: true);

    public FolderBackupStrategy(TimeProvider time, ILogger<FolderBackupStrategy> log)
    {
        _time = time;
        _log = log;
    }

    /// <summary>What this strategy is called in settings and in history.</summary>
    /// <remarks>
    /// A constant for <see cref="ZipBackupStrategy.StrategyName"/>'s reason.
    /// </remarks>
    public const string StrategyName = "folder";

    public string Name => StrategyName;

    /// <inheritdoc />
    public bool CountsFiles => true;

    public bool IsEnabled(BackupSettings settings) => settings.Folder.Enabled;

    /// <inheritdoc />
    /// <remarks>
    /// Nothing gates it. The shadow copy was the one thing here that needed a
    /// privilege, and it has moved to the orchestrator — where it falls back
    /// rather than blocking, because a slower backup is not a refused one
    /// (<c>D67</c>). Answered explicitly rather than inherited from a default, so
    /// a fourth strategy has to decide rather than acquire an answer by not
    /// thinking about it.
    /// </remarks>
    public BlockedStrategy? Blocked(BackupSettings settings) => null;

    public async Task<BackupStrategyResult> RunAsync(
        BackupContext context, CancellationToken cancellationToken)
    {
        if (context.Manifest.Count == 0)
        {
            // Same rule as ZIP: a backup of nothing is a folder that looks like
            // one, and the difference is discovered during a restore.
            return new BackupStrategyResult(
                Name, false, "The snapshot lists no files — there is no world here to copy.", null);
        }

        var folderName = BackupRetentionPlan.ComposeName(FolderPrefix, context.StartedUtc);
        var destination = Path.Combine(context.ArtifactDirectory, folderName);

        try
        {
            var copied = await CopyAsync(context, destination, cancellationToken).ConfigureAwait(false);

            var (pruned, damaged) = Prune(context, destination);

            var leftOut = WorldFolder.LeftOut(
                context.OthersUnlisted, context.Beyond.Reserved, copied.Unreadable);

            // The whole folder, read by the rule the Size column beside this sentence
            // reads it with, so the two give one number (`T273`); the world's bytes
            // alone left the configuration files out of it. Through ByteSize for
            // ZipBackupStrategy's reason: the sentence is stored, so its spelling
            // outlives any later fix.
            var size = SizeOf(new DirectoryInfo(destination));

            var detail =
                $"Copied {copied.Files} world {(copied.Files == 1 ? "file" : "files")}"
                + (context.ReadingFromACopy ? " from a shadow copy" : "")
                + (copied.ConfigFiles > 0
                    ? $" and {copied.ConfigFiles} configuration {(copied.ConfigFiles == 1 ? "file" : "files")}"
                    : "")
                + $" ({ByteSize.Format(size)})"
                + (leftOut is null ? "" : $"; {leftOut}")
                + (pruned > 0 ? $". Pruned {pruned} older." : ".");

            return new BackupStrategyResult(Name, true, detail, destination, damaged);
        }
        catch (Exception ex)
        {
            // A half-copied folder is worse than none: it would list in the
            // restore picker looking exactly like a complete one.
            Discard(destination);

            if (ex is OperationCanceledException)
            {
                throw;
            }

            return new BackupStrategyResult(Name, false, ex.Message, null);
        }
    }

    /// <summary>
    /// A folder copy's total size, every file under it: the one number the Backups
    /// tab's Size column shows for it.
    /// </summary>
    /// <remarks>
    /// Best effort. A file that vanishes mid-walk — a prune running alongside a
    /// page load — is worth showing a smaller number for, not worth failing the
    /// whole list over.
    /// </remarks>
    internal static long SizeOf(DirectoryInfo folder)
    {
        try
        {
            return folder
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private sealed record Copied(int Files, int ConfigFiles, IReadOnlyList<string> Unreadable);

    private async Task<Copied> CopyAsync(
        BackupContext context,
        string destination,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);

        var files = 0;

        foreach (var entry in context.Manifest)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = entry.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            var source = Path.Combine(context.SourceWorldsDirectory, relative);
            var target = Path.Combine(destination, NewServerWorld.WorldsFolder, relative);

            if (!File.Exists(source))
            {
                throw new InvalidOperationException(
                    context.ReadingFromACopy
                        ? $"The snapshot names '{entry.RelativePath}', which is not in the shadow copy."
                        : $"The snapshot names '{entry.RelativePath}', which is not on disk.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            await CopyTruncatedAsync(source, target, entry.Length, cancellationToken)
                .ConfigureAwait(false);

            files++;
            context.FileDone();
        }

        // The rest of the worlds folder, each file whole and after the save
        // (`D97`). One that cannot be opened is left out and named; one that fails
        // partway throws, and the half-copied folder is discarded with the rest.
        var unreadable = new List<string>();

        foreach (var relativePath in context.Beyond.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = relativePath.Replace('/', Path.DirectorySeparatorChar);
            var source = Path.Combine(context.SourceWorldsDirectory, relative);
            var target = Path.Combine(destination, NewServerWorld.WorldsFolder, relative);

            if (BeyondTheSaveFiles.Open(source, relativePath, unreadable, _log, context.InstanceId) is not { } input)
            {
                context.FileDone();
                continue;
            }

            await using (input)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                await using var output = File.Create(target);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);

                files++;
            }

            context.FileDone();
        }

        var configFiles = 0;

        // Read from the live instance directory rather than from wherever the
        // world came from. They are not part of the save, so the manifest says
        // nothing about them, and a config file the operator edited a second ago
        // is more useful than the one the volume happened to be holding — the same
        // reasoning § 8.3 gives for the ZIP strategy.
        foreach (var (name, path) in ServerConfigFiles.Present(context.WorkingDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // At the root, where BDS reads it (`D110`).
                var target = Path.Combine(destination, name);

                await using var input = OpenAlongsideTheServer(path);
                await using var output = File.Create(target);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);

                configFiles++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logged, never fatal — which is why the catch names both. An
                // ACL-denied `permissions.json` raises the second, and the whole
                // point is that a world already copied is not thrown away over a
                // file that is added after it and never instead of it (§ 8.3).
                _log.LogWarning(
                    ex, "Could not copy {File} into the backup of {InstanceId}",
                    name, context.InstanceId);
            }
        }

        if (context.ServerVersion is { Length: > 0 } version)
        {
            await File.WriteAllTextAsync(
                    Path.Combine(destination, NewServerWorld.VersionFile), version + "\n", cancellationToken)
                .ConfigureAwait(false);
        }

        // D89. Always: the id is known whatever the server has reported.
        await File.WriteAllTextAsync(
                Path.Combine(destination, NewServerWorld.OriginFile),
                new BackupOrigin(context.InstanceId, context.InstanceName).Write(),
                cancellationToken)
            .ConfigureAwait(false);

                return new Copied(files, configFiles, unreadable);
    }

    /// <summary>
    /// Copies exactly <paramref name="length"/> bytes.
    /// </summary>
    /// <remarks>
    /// Short is fatal, long is normal. A file shorter than the manifest means
    /// the snapshot and the source disagree about what was flushed, and there is
    /// no honest way to finish the copy; longer is simply the next save's bytes,
    /// which stop being copied here.
    /// </remarks>
    private static async Task CopyTruncatedAsync(
        string source, string target, long length, CancellationToken cancellationToken)
    {
        await using var input = OpenAlongsideTheServer(source);

        if (input.Length < length)
        {
            throw new InvalidOperationException(
                $"'{Path.GetFileName(source)}' is {input.Length} bytes but the "
                + $"snapshot says {length} — the world and the manifest disagree, so this backup "
                + "would not restore.");
        }

        await using var output = File.Create(target);

        var buffer = new byte[81920];
        long remaining = length;

        while (remaining > 0)
        {
            var wanted = (int)Math.Min(buffer.Length, remaining);
            var read = await input.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                throw new InvalidOperationException(
                    $"'{Path.GetFileName(source)}' ended {remaining} bytes early.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }

    /// <summary>
    /// Opens a file a running server may be holding.
    /// </summary>
    /// <remarks>
    /// <para><c>FileShare.Read</c> is refused when another handle has write access,
    /// which BDS has on the allowlist and permissions files whenever somebody
    /// changes them in game (§ 5.5).</para>
    ///
    /// <para><strong>The world files need it too now, and that is a consequence of
    /// the split rather than an oversight before it</strong> (<c>D66</c>). This
    /// strategy used to read the world only out of a shadow copy, where nothing
    /// holds anything; with acquisition off it reads the live world exactly as the
    /// ZIP strategy always has, and that strategy has always opened permissively
    /// for this reason. LevelDB's <c>LOCK</c> is the one that bites.</para>
    /// </remarks>
    private static FileStream OpenAlongsideTheServer(string path) => ServerFileReader.Open(path);

    private void Discard(string destination)
    {
        try
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Both, the way `WorldRestorer.TryDeleteDirectory` does and for the
            // same reason it states: a read-only file anywhere in the tree raises
            // the second one, and this runs on a failure path where throwing
            // replaces the failure that happened with one about the clean-up.
            _log.LogWarning(ex, "Could not clean up the failed backup at {Path}", destination);
        }
    }

    /// <summary>
    /// Applies the retention rule, never to the folder just written.
    /// </summary>
    /// <remarks>
    /// Matches only <c>folder-*</c>, so ZIP archives and anything an operator put
    /// in the same folder are never deleted by a rule that was not pointed at them
    /// — the same rule the ZIP strategy follows for the same reason.
    /// </remarks>
    private (int Pruned, IReadOnlyList<string>? Damaged) Prune(
        BackupContext context, string justWritten)
    {
        var retention = context.Settings.Folder.Retention;

        if (retention.Mode is BackupRetentionMode.KeepAll || retention.Value <= 0)
        {
            return (0, null);
        }

        // Names, not FileSystemInfo: the decision is made from the stamp each name
        // carries, in Core where a test can reach it, and identically for both
        // strategies (T198). The filesystem's own timestamps are not consulted.
        var names = Directory
            .EnumerateDirectories(context.ArtifactDirectory, $"{FolderPrefix}*")
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();

        var doomed = BackupRetentionPlan.NamesToRemove(
            names,
            FolderPrefix,
            suffix: "",
            retention,
            _time.GetUtcNow().UtcDateTime,
            Path.GetFileName(justWritten),
            context.Locked);

        var pruned = 0;
        List<string>? damaged = null;

        foreach (var name in doomed)
        {
            var full = Path.Combine(context.ArtifactDirectory, name);

            try
            {
                RemoveArtifact(full);
                pruned++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The folder is not intact and there is no arm in which it might
                // be. `Directory.Delete(recursive: true)` deletes as it walks and
                // rethrows at the end (`T186`), so by the time this runs it has
                // removed everything it reached -- measured at nine files down to
                // one, the world among them. Reporting this as "could not prune"
                // and moving on is what `T229` was: a destroyed backup, a green
                // row and a log line nobody reads.
                //
                // It is named rather than counted because the wrapper cannot
                // finish the job -- whatever refused has not stopped refusing --
                // so the person reading the row is the one who has to.
                (damaged ??= []).Add(name);

                _log.LogWarning(
                    ex, "Pruning {Folder} failed partway; what it had already deleted is gone", full);
            }
        }

        return (pruned, damaged);
    }
}
