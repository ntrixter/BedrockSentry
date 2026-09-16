using System.Globalization;
using System.IO.Compression;
using BedrockSentry.Core.Backups;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Backups;

/// <summary>
/// Archives the held snapshot into a ZIP (<c>docs/design.md</c> § 8.3).
/// </summary>
/// <remarks>
/// This is the strategy the manifest's byte counts exist for. Files are
/// streamed straight from the live world into archive entries, each stopped at
/// its manifest length — the file on disk may be longer while saves are held,
/// and those extra bytes belong to the <em>next</em> save, not this snapshot.
/// Copying them produces an archive that restores to a corrupt world, silently
/// (<c>docs/design.md</c> § 5.5). No staging copy: streaming means the world is never
/// written to disk twice to be archived once.
///
/// The archive is all or nothing. A manifest file that is missing or shorter
/// than its stated length fails the whole strategy rather than producing a zip
/// with a hole in it — a backup that exists but cannot restore is worse than a
/// failed one, because nobody finds out until they need it.
/// </remarks>
public sealed class ZipBackupStrategy : IBackupStrategy
{
    /// <summary>The prefix every archive carries. Also what retention matches.</summary>
    /// <remarks>
    /// A constant rather than a literal because the pattern was written out in four
    /// places — here, the restore picker, the Backups page and retention — and one
    /// rule spelled four times is enforced in none of them. <c>VssBackupStrategy</c>
    /// already had <c>FolderPrefix</c> for the same reason.
    /// </remarks>
    public const string ArchivePrefix = "backup-";

    /// <summary>The extension every archive carries, used with <see cref="ArchivePrefix"/>.</summary>
    public const string ArchiveSuffix = ".zip";

    private readonly TimeProvider _time;
    private readonly ILogger<ZipBackupStrategy> _log;

    public ZipBackupStrategy(TimeProvider time, ILogger<ZipBackupStrategy> log)
    {
        _time = time;
        _log = log;
    }

    public string Name => "zip";

    public bool IsEnabled(BackupSettings settings) => settings.Zip.Enabled;

    /// <inheritdoc />
    /// <remarks>
    /// Nothing gates the ZIP strategy: it writes a file the service already has
    /// permission to write, into a directory the wrapper owns. Answered explicitly
    /// rather than inherited from a default implementation, so a fourth strategy
    /// has to decide rather than acquire an answer by not thinking about it.
    /// </remarks>
    public BlockedStrategy? Blocked(BackupSettings settings) => null;

    public async Task<BackupStrategyResult> RunAsync(
        BackupContext context, CancellationToken cancellationToken)
    {
        if (context.Manifest.Count == 0)
        {
            // A zip of nothing is not a backup, it is a file that looks like
            // one. Refusing beats somebody discovering the difference during a
            // restore.
            return new BackupStrategyResult(
                Name, false, "The snapshot lists no files — there is no world here to archive.", null);
        }

        var archiveName = BackupRetentionPlan.ComposeName(ArchivePrefix, context.StartedUtc, ArchiveSuffix);
        var finalPath = Path.Combine(context.ArtifactDirectory, archiveName);
        var tempPath = $"{finalPath}.tmp";

        long totalBytes = 0;
        var configFiles = 0;

        // Set inside the write loop, acted on only after the archive's handles
        // are closed. Deleting the temp file from inside the using block works
        // on Linux, where a delete is an unlink, and silently does not on
        // Windows, where a file with an open handle refuses — run #46 caught
        // exactly that, as a .tmp the abort path left behind.
        string? failure = null;

        try
        {
            await using (var output = File.Create(tempPath))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
            {
                foreach (var entry in context.Manifest)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var source = Path.Combine(
                        context.WorldsDirectory,
                        entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));

                    if (!File.Exists(source))
                    {
                        failure = $"The snapshot names '{entry.RelativePath}', which is not on disk.";
                        break;
                    }

                    await using var input = OpenAlongsideTheServer(source);

                    if (input.Length < entry.Length)
                    {
                        // Longer than the manifest is normal while saves are
                        // held; shorter means the snapshot and the disk disagree
                        // about what this file is, and an archive built from it
                        // would be guesswork.
                        failure =
                            $"'{entry.RelativePath}' is {input.Length} bytes on disk but the snapshot "
                            + $"says {entry.Length} — the world changed underneath the backup.";
                        break;
                    }

                    // Entry names keep the manifest's own worlds/-relative shape
                    // and forward slashes, so a restore is "extract into worlds"
                    // with no path arithmetic to get wrong.
                    var zipEntry = archive.CreateEntry(entry.RelativePath, Level(context.Settings.Zip.Compression));
                    zipEntry.LastWriteTime = context.StartedUtc;

                    await using var target = zipEntry.Open();
                    await CopyExactlyAsync(input, target, entry.Length, cancellationToken).ConfigureAwait(false);
                    totalBytes += entry.Length;
                }

                if (failure is null)
                {
                    configFiles = await AddConfigAsync(archive, context, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (failure is not null)
            {
                return Abort(tempPath, failure);
            }

            // Write-then-move: a crash mid-archive leaves a .tmp to sweep up,
            // never a plausible-looking zip that ends mid-file.
            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        var pruned = Prune(context, finalPath);

        var size = new FileInfo(finalPath).Length;
        var detail =
            $"{context.Manifest.Count} files, {totalBytes / 1024.0 / 1024.0:0.0} MB archived to "
            + $"{size / 1024.0 / 1024.0:0.0} MB"
            + (configFiles > 0 ? $"; {configFiles} config file{(configFiles == 1 ? "" : "s")}" : "")
            + (pruned > 0 ? $"; {pruned} old archive{(pruned == 1 ? "" : "s")} removed" : "");

        return new BackupStrategyResult(Name, true, detail, finalPath);
    }

    /// <summary>
    /// Adds the server's own configuration to the archive.
    /// </summary>
    /// <remarks>
    /// After the world and never instead of it: a failure here is logged and the
    /// archive still stands, because a backup with the world and no
    /// <c>server.properties</c> is worth far more than no backup at all. These
    /// are small files read straight from the instance directory rather than
    /// from the manifest — they are not part of the save, so <c>save hold</c>
    /// says nothing about them and there is no byte count to honour.
    ///
    /// Opened the same permissive way world files are, since BDS rewrites
    /// <c>allowlist.json</c> and <c>permissions.json</c> whenever somebody
    /// changes them in game and may well be holding one as this runs.
    /// </remarks>
    private async Task<int> AddConfigAsync(
        ZipArchive archive, BackupContext context, CancellationToken cancellationToken)
    {
        var added = 0;

        foreach (var (name, path) in ServerConfigFiles.Present(context.WorkingDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var zipEntry = archive.CreateEntry(
                    ServerConfigFiles.EntryFor(name), Level(context.Settings.Zip.Compression));
                zipEntry.LastWriteTime = context.StartedUtc;

                await using var input = OpenAlongsideTheServer(path);
                await using var target = zipEntry.Open();

                await input.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                added++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(
                    ex, "Could not add {File} to the backup of {InstanceId}; the world is archived anyway",
                    name, context.InstanceId);
            }
        }

        return added;
    }

    private BackupStrategyResult Abort(string tempPath, string why)
    {
        TryDelete(tempPath);
        _log.LogError("ZIP backup aborted: {Why}", why);
        return new BackupStrategyResult(Name, false, why, null);
    }

    /// <summary>
    /// Applies retention to this strategy's own archives — matched by the name
    /// pattern it writes, so somebody's hand-placed zip in the same folder is
    /// never deleted by a rule they did not point at it.
    /// </summary>
    private int Prune(BackupContext context, string justWritten)
    {
        var retention = context.Settings.Zip.Retention;
        if (retention.Mode is BackupRetentionMode.KeepAll || retention.Value <= 0)
        {
            return 0;
        }

        // Names, not FileInfo: the decision is made from the stamp each name
        // carries, in Core where a test can reach it, and identically for both
        // strategies (T198). The filesystem's own timestamps are not consulted -
        // restoring from an archive or copying it moves those, and the name does
        // not move. Never deleting the one just written is BackupRetentionPlan's
        // job now, where it is pinned ahead of the count rather than skipped
        // after it.
        var names = Directory
            .EnumerateFiles(context.ArtifactDirectory, $"{ArchivePrefix}*{ArchiveSuffix}")
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();

        var doomed = BackupRetentionPlan.NamesToRemove(
            names,
            ArchivePrefix,
            ArchiveSuffix,
            retention,
            _time.GetUtcNow().UtcDateTime,
            Path.GetFileName(justWritten));

        var pruned = 0;

        foreach (var name in doomed)
        {
            var full = Path.Combine(context.ArtifactDirectory, name);

            try
            {
                File.Delete(full);
                pruned++;
            }
            catch (IOException ex)
            {
                _log.LogWarning(ex, "Could not prune old backup {File}", full);
            }
        }

        return pruned;
    }

    private static CompressionLevel Level(ZipCompression compression) => compression switch
    {
        ZipCompression.None => CompressionLevel.NoCompression,
        ZipCompression.Maximum => CompressionLevel.SmallestSize,
        _ => CompressionLevel.Fastest,
    };

    /// <summary>
    /// Opens a world file that a live server still holds open.
    /// </summary>
    /// <remarks>
    /// <see cref="File.OpenRead"/> asks for <see cref="FileShare.Read"/>, which
    /// says "others may read this but not write it" — and BDS holds the current
    /// LevelDB write-ahead log open <em>for writing</em> for the life of the
    /// server. Windows refuses the open outright, and the backup fails with a
    /// sharing violation naming a <c>db\NNNNNN.log</c>. Linux has no mandatory
    /// locking, so every test and every CI run passed while the strategy could
    /// not archive a running server at all.
    ///
    /// The symptom is intermittent in a way that hides the cause: LevelDB
    /// rotates its logs, and only the current one is held open. A backup taken
    /// in a quiet moment can miss it and succeed, which reads as a flaky backup
    /// rather than a broken one.
    ///
    /// <see cref="FileShare.Delete"/> matters as much as the write bit and for
    /// the opposite reason. Without it <em>our</em> handle blocks BDS from
    /// deleting or renaming a log it has finished compacting — the wrapper would
    /// be breaking the server it is backing up.
    ///
    /// Reading a file another process is appending to is safe here precisely
    /// because of the manifest: <c>save hold</c> fixes the first
    /// <c>entry.Length</c> bytes, and everything past that belongs to the next
    /// save and is truncated away (<c>docs/design.md</c> § 5.5).
    /// </remarks>
    private static FileStream OpenAlongsideTheServer(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static async Task CopyExactlyAsync(
        Stream source, Stream target, long count, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        var remaining = count;

        while (remaining > 0)
        {
            var read = await source
                .ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                // Checked before the copy began, so this is a truncation racing
                // us mid-read. The length rule holds either way: fail rather
                // than pad.
                throw new IOException($"The file ended {remaining} bytes before its manifest length.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover .tmp is untidy, not dangerous — the name says exactly
            // what it is.
        }
    }
}
