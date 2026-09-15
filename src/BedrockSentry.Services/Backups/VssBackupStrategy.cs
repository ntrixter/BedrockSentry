using System.Globalization;
using BedrockSentry.Core.Backups;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Backups;

/// <summary>
/// Copies the held snapshot out of a Volume Shadow Copy (<c>docs/design.md</c> § 8.4).
/// </summary>
/// <remarks>
/// <para>
/// What this buys over the ZIP strategy is not speed or size — it is that
/// nothing on the volume can move while the copy runs, and that a file another
/// process holds exclusively can still be read. The ZIP strategy opens world
/// files permissively and hopes; a shadow copy makes the question not arise.
/// LevelDB's <c>LOCK</c> is the one that bites in practice.
/// </para>
/// <para>
/// <strong>The manifest lengths still apply.</strong> That is the part it would
/// be easy to get wrong: a shadow copy is a faithful image of the volume, and
/// the volume at that instant holds world files that are <em>longer</em> than
/// the save BDS just flushed. Those trailing bytes belong to the next save
/// (<c>docs/design.md</c> § 5.5). Copying them whole produces a folder that looks
/// perfect and restores to a corrupt world, which is the failure mode this
/// codebase most wants to avoid, because nobody finds out until they need it.
/// </para>
/// <para>
/// The output is a folder rather than an archive, deliberately: the point of
/// paying for a snapshot is to get bytes off the volume quickly and let saves
/// resume, and compressing them while the server is held would spend the time
/// this strategy exists to save. Somebody wanting a single file wants the ZIP
/// strategy, and both can be on at once.
/// </para>
/// </remarks>
public sealed class VssBackupStrategy : IBackupStrategy
{
    /// <summary>The prefix every artifact folder carries. Also what retention matches.</summary>
    public const string FolderPrefix = "vss-";

    /// <summary>Where the server's own configuration goes inside the folder.</summary>
    /// <remarks>
    /// The same reserved name the ZIP strategy uses for the same files, so a
    /// restore reads one layout whichever strategy produced the backup.
    /// </remarks>
    public static readonly string ConfigFolder =
        ServerConfigFiles.ArchivePrefix.TrimEnd('/');

    private readonly IShadowCopyService _shadows;
    private readonly TimeProvider _time;
    private readonly ILogger<VssBackupStrategy> _log;

    public VssBackupStrategy(
        IShadowCopyService shadows, TimeProvider time, ILogger<VssBackupStrategy> log)
    {
        _shadows = shadows;
        _time = time;
        _log = log;
    }

    public string Name => "vss";

    public bool IsEnabled(BackupSettings settings) => settings.Vss.Enabled;

    /// <inheritdoc />
    /// <remarks>
    /// Nothing gates it today, and this is the obvious place for the next one: a
    /// shadow copy needs a privilege the service account may not hold, and once the
    /// broker lands (<c>D46</c>) that becomes a real answer rather than a launch
    /// that fails for a reason nobody can read from the panel. Null until then, on
    /// purpose rather than by omission.
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

        var stamp = context.StartedUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var destination = Path.Combine(context.ArtifactDirectory, $"{FolderPrefix}{stamp}");

        string volumeRoot;

        try
        {
            volumeRoot = ShadowCopyPaths.VolumeRootOf(context.WorldsDirectory);
        }
        catch (Exception ex)
        {
            return new BackupStrategyResult(Name, false, ex.Message, null);
        }

        var shadow = await _shadows.CreateAsync(volumeRoot, cancellationToken).ConfigureAwait(false);

        try
        {
            var copied = await CopyAsync(context, shadow, destination, cancellationToken)
                .ConfigureAwait(false);

            var pruned = Prune(context, destination);

            var detail =
                $"Copied {copied.Files} world "
                + $"{(copied.Files == 1 ? "file" : "files")} "
                + $"({copied.Bytes / 1024.0 / 1024.0:0.0} MB) from a shadow copy of {volumeRoot}"
                + (copied.ConfigFiles > 0 ? $", plus {copied.ConfigFiles} configuration files" : "")
                + (pruned > 0 ? $". Pruned {pruned} older." : ".");

            return new BackupStrategyResult(Name, true, detail, destination);
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
        finally
        {
            // Unconditional, in the same spirit as `save resume`. A shadow copy
            // left behind holds volume space until somebody notices, and on a
            // schedule it is left behind every time.
            await _shadows.ReleaseAsync(shadow, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private sealed record Copied(int Files, long Bytes, int ConfigFiles);

    private async Task<Copied> CopyAsync(
        BackupContext context,
        ShadowCopy shadow,
        string destination,
        CancellationToken cancellationToken)
    {
        var worldsInShadow = ShadowCopyPaths.Inside(shadow.DeviceObject, context.WorldsDirectory);

        Directory.CreateDirectory(destination);

        var files = 0;
        long bytes = 0;

        foreach (var entry in context.Manifest)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = entry.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            var source = Path.Combine(worldsInShadow, relative);
            var target = Path.Combine(destination, relative);

            if (!File.Exists(source))
            {
                throw new InvalidOperationException(
                    $"The snapshot names '{entry.RelativePath}', which is not in the shadow copy.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            bytes += await CopyTruncatedAsync(source, target, entry.Length, cancellationToken)
                .ConfigureAwait(false);

            files++;
        }

        var configFiles = 0;

        // Read from the live instance directory rather than from the shadow.
        // They are not part of the save, so the manifest says nothing about
        // them, and a config file the operator edited a second ago is more
        // useful than the one the volume happened to be holding — the same
        // reasoning § 8.3 gives for the ZIP strategy.
        foreach (var (name, path) in ServerConfigFiles.Present(context.WorkingDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var target = Path.Combine(destination, ConfigFolder, name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                await using var input = OpenAlongsideTheServer(path);
                await using var output = File.Create(target);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);

                configFiles++;
            }
            catch (IOException ex)
            {
                // Logged, never fatal. They are added after the world and never
                // instead of it (§ 8.3).
                _log.LogWarning(
                    ex, "Could not copy {File} into the backup of {InstanceId}",
                    name, context.InstanceId);
            }
        }

        return new Copied(files, bytes, configFiles);
    }

    /// <summary>
    /// Copies exactly <paramref name="length"/> bytes.
    /// </summary>
    /// <remarks>
    /// Short is fatal, long is normal. A file shorter than the manifest means
    /// the snapshot and the volume disagree about what was flushed, and there is
    /// no honest way to finish the copy; longer is simply the next save's bytes,
    /// which stop being copied here.
    /// </remarks>
    private static async Task<long> CopyTruncatedAsync(
        string source, string target, long length, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(source);

        if (input.Length < length)
        {
            throw new InvalidOperationException(
                $"'{Path.GetFileName(source)}' is {input.Length} bytes in the shadow copy but the "
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

        return length;
    }

    /// <summary>
    /// Opens a file a running server may be holding.
    /// </summary>
    /// <remarks>
    /// <c>FileShare.Read</c> is refused when another handle has write access,
    /// which BDS has on the allowlist and permissions files whenever somebody
    /// changes them in game (§ 5.5). Only the config files need this — the world
    /// is read out of the shadow copy, where nothing holds anything.
    /// </remarks>
    private static FileStream OpenAlongsideTheServer(string path) =>
        new(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

    private void Discard(string destination)
    {
        try
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
        }
        catch (IOException ex)
        {
            _log.LogWarning(ex, "Could not clean up the failed backup at {Path}", destination);
        }
    }

    /// <summary>
    /// Applies the retention rule, never to the folder just written.
    /// </summary>
    /// <remarks>
    /// Matches only <c>vss-*</c>, so ZIP archives and anything an operator put in
    /// the same folder are never deleted by a rule that was not pointed at them
    /// — the same rule the ZIP strategy follows for the same reason.
    /// </remarks>
    private int Prune(BackupContext context, string justWritten)
    {
        var retention = context.Settings.Vss.Retention;

        if (retention.Mode is BackupRetentionMode.KeepAll || retention.Value <= 0)
        {
            return 0;
        }

        var folders = Directory
            .EnumerateDirectories(context.ArtifactDirectory, $"{FolderPrefix}*")
            .Select(d => new DirectoryInfo(d))
            .OrderByDescending(d => d.LastWriteTimeUtc)
            .ToList();

        var doomed = retention.Mode switch
        {
            BackupRetentionMode.Count => folders.Skip(retention.Value),
            BackupRetentionMode.Days => folders.Where(d =>
                d.LastWriteTimeUtc < _time.GetUtcNow().UtcDateTime.AddDays(-retention.Value)),
            _ => [],
        };

        var pruned = 0;

        foreach (var folder in doomed)
        {
            if (string.Equals(folder.FullName, justWritten, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                folder.Delete(recursive: true);
                pruned++;
            }
            catch (IOException ex)
            {
                _log.LogWarning(ex, "Could not prune old backup {Folder}", folder.FullName);
            }
        }

        return pruned;
    }
}
