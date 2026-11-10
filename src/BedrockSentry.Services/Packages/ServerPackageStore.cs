using System.IO.Compression;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Packages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BedrockSentry.Services.Packages;

/// <summary>
/// The Bedrock server packages available to install (<c>D19</c>).
///
/// One store, filled two ways: an administrator drops a zip in by hand, or
/// milestone 13's updater downloads into it. Creating an instance extracts a
/// package into that instance's own directory — each instance gets its own copy,
/// because BDS keeps <c>worlds/</c>, <c>allowlist.json</c>,
/// <c>permissions.json</c> and <c>server.properties</c> beside its executable,
/// so a shared install directory would mean a shared world.
/// </summary>
public sealed class ServerPackageStore(
    ISentryPaths paths, ILogger<ServerPackageStore> logger, ServerPackageLog? log = null)
{
    /// <summary>Where packages live.</summary>
    public string Directory => paths.ServerPackages;

    /// <summary>Each zip's channel and the EULA acceptance (<c>T333</c>, <c>D102</c>).</summary>
    public ServerPackageLog Log { get; } =
        log ?? new ServerPackageLog(paths, NullLogger<ServerPackageLog>.Instance);

    /// <summary>
    /// Everything in the store, newest version first. Packages whose names do
    /// not parse sort last but are still listed.
    /// </summary>
    public IReadOnlyList<ServerPackage> List()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        var packages = new List<ServerPackage>();
        var channels = Log.Entries();

        foreach (var path in System.IO.Directory.EnumerateFiles(Directory))
        {
            var name = Path.GetFileName(path);
            if (!ServerPackageName.LooksLikeAPackage(name))
            {
                continue;
            }

            var info = new FileInfo(path);
            var (version, text) = ServerPackageName.Parse(name);

            packages.Add(new ServerPackage(
                name, path, version, text, info.Length, info.LastWriteTimeUtc,
                PackageChannels.For(name, channels)));
        }

        return packages
            .OrderByDescending(p => p.Version is not null)
            .ThenByDescending(p => p.Version)
            .ThenBy(p => p.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public ServerPackage? Find(string fileName) =>
        List().FirstOrDefault(p => string.Equals(p.FileName, fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>What the whole store occupies on disk.</summary>
    public long TotalBytes() => List().Sum(p => p.SizeBytes);

    /// <summary>
    /// Removes a package from the store.
    /// </summary>
    /// <remarks>
    /// The zip only. Nothing that was ever installed from it is touched —
    /// extraction copies files into the instance directory, so deleting the
    /// package a running server was installed from does nothing to that server.
    /// Worth stating because the opposite is the natural assumption, and it is
    /// what would make this button frightening.
    ///
    /// Matched by name against the store's own listing rather than taking a path,
    /// for the reason the restore picker does: a listing is not an authorisation,
    /// and this deletes files with the service's rights.
    /// </remarks>
    public bool Delete(string fileName)
    {
        if (Find(fileName) is not { } package)
        {
            return false;
        }

        try
        {
            File.Delete(package.FullPath);
            logger.LogInformation("Removed {FileName} from the package store", package.FileName);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remove {FileName} from the package store", package.FileName);
            throw new InvalidOperationException(
                $"'{package.FileName}' could not be deleted: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Extracts a package into a fresh instance directory.
    /// </summary>
    /// <remarks>
    /// Refuses a directory that already contains files. This is the *provision a
    /// new instance* path, and a BDS zip carries a default
    /// <c>server.properties</c>, <c>allowlist.json</c> and <c>permissions.json</c>
    /// — extracting over a configured server would silently reset its settings
    /// and its allowlist. Updating an existing install is milestone 13's job and
    /// has to preserve those files deliberately, so it does not come through
    /// here.
    /// </remarks>
    public async Task ExtractAsync(
        ServerPackage package, string targetDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        if (!File.Exists(package.FullPath))
        {
            throw new FileNotFoundException(
                $"The package '{package.FileName}' is no longer in {Directory}.", package.FullPath);
        }

        System.IO.Directory.CreateDirectory(targetDirectory);

        var existing = System.IO.Directory
            .EnumerateFileSystemEntries(targetDirectory, "*", SearchOption.AllDirectories)
            .Where(File.Exists)
            .Take(1)
            .ToList();

        if (existing.Count > 0)
        {
            throw new InvalidOperationException(
                $"{targetDirectory} already contains files. Installing a package here would "
                + "overwrite server.properties, allowlist.json and permissions.json with the "
                + "defaults from the zip. Empty the directory first, or install into a new instance.");
        }

        // GetFullPath normalises away any "..", and the trailing separator stops
        // "C:\instances\survival-evil" from matching a prefix check against
        // "C:\instances\survival".
        var root = Path.GetFullPath(targetDirectory);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(package.FullPath);
        var prefix = CommonRootFolder(archive);
        var written = 0;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A directory entry has an empty Name. Nothing to write; the
            // directories get created from the file paths below.
            if (entry.Name.Length == 0)
            {
                continue;
            }

            // Checked before any prefix handling, and on the raw entry name. A
            // ".." segment is never legitimate in a server package, and stripping
            // a "wrapping folder" first can turn one into a path that resolves
            // innocently — which is the same traversal, just harder to see.
            if (HasTraversalSegment(entry.FullName))
            {
                throw new InvalidDataException(
                    $"'{package.FileName}' contains an entry that would be written outside the "
                    + $"instance directory: {entry.FullName}");
            }

            var relative = entry.FullName;
            if (prefix is not null && relative.StartsWith(prefix, StringComparison.Ordinal))
            {
                relative = relative[prefix.Length..];
            }

            var destination = Path.GetFullPath(Path.Combine(root, relative));

            // Zip slip. An entry named "..\..\Windows\System32\evil.dll" would
            // otherwise be written wherever it asked, with the service's rights. The check
            // is on the *resolved* path, because the entry name alone can hide
            // the traversal behind separators and encodings.
            if (!destination.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"'{package.FileName}' contains an entry that would be written outside the "
                    + $"instance directory: {entry.FullName}");
            }

            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            await using var source = entry.Open();
            await using var target = new FileStream(
                destination, FileMode.Create, FileAccess.Write, FileShare.None);

            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            written++;
        }

        logger.LogInformation(
            "Installed {Package} into {Directory} ({Count} files)",
            package.FileName, targetDirectory, written);
    }

    /// <summary>
    /// What an in-place update did, so it can be undone.
    /// </summary>
    /// <param name="Overwritten">
    /// Files that existed and were replaced. The previous copy of each is under
    /// <paramref name="BackupDirectory"/> at the same relative path.
    /// </param>
    /// <param name="Added">
    /// Files the package brought that were not there before. Rolling back means
    /// deleting these, not restoring them.
    /// </param>
    public sealed record ServerUpdateSwap(
        string BackupDirectory,
        IReadOnlyList<string> Overwritten,
        IReadOnlyList<string> Added)
    {
        public int FilesWritten => Overwritten.Count + Added.Count;
    }

    /// <summary>
    /// Paths an update must never write over, relative to the instance root.
    /// </summary>
    /// <remarks>
    /// The world first, obviously. Then the three files that carry everything an
    /// administrator configured — a BDS zip ships defaults for all of them, and
    /// letting those land would reset a server's settings, its allowlist and its
    /// operators in one step. <c>whitelist.json</c> is the pre-1.16 name Bedrock
    /// still honours, which is exactly the kind of file that gets forgotten.
    /// <c>config/</c> holds Scripting API module permissions (Section 9), which
    /// are a deliberate opt-in nobody wants silently reverted.
    ///
    /// Behavior and resource packs are deliberately <em>absent</em> from this
    /// list, and survive anyway: an update writes only the entries the package
    /// contains and deletes nothing, so Mojang's own vanilla packs are refreshed
    /// while any pack an administrator added is simply never touched.
    /// </remarks>
    private static readonly string[] Preserved =
    [
        "worlds",
        "config",
        "server.properties",
        "allowlist.json",
        "whitelist.json",
        "permissions.json",
    ];

    private static bool IsPreserved(string relative)
    {
        var normalised = relative.Replace('\\', '/').TrimStart('/');

        foreach (var keep in Preserved)
        {
            if (normalised.Equals(keep, StringComparison.OrdinalIgnoreCase)
                || normalised.StartsWith(keep + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Updates a server already installed in <paramref name="targetDirectory"/>,
    /// keeping its world and configuration (<c>docs/design.md</c> § 11).
    /// </summary>
    /// <remarks>
    /// In place rather than staged. Staging would mean copying a world that can
    /// run to many gigabytes in order to replace an executable that is tens of
    /// megabytes, and the world is the one thing an update never touches — so
    /// only what the package actually contains is written, and everything else
    /// in the directory is left exactly where it is.
    ///
    /// Every file about to be replaced is copied into
    /// <paramref name="backupDirectory"/> first, and files the package adds are
    /// recorded. Between them those two lists are a complete undo, which is what
    /// makes the caller's health gate able to put a broken update back.
    ///
    /// The caller must have stopped the server. Overwriting
    /// <c>bedrock_server.exe</c> underneath a running process fails on Windows
    /// and would be wrong even where it succeeds.
    /// </remarks>
    public async Task<ServerUpdateSwap> UpdateInPlaceAsync(
        ServerPackage package,
        string targetDirectory,
        string backupDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        if (!File.Exists(package.FullPath))
        {
            throw new FileNotFoundException(
                $"The package '{package.FileName}' is no longer in {Directory}.", package.FullPath);
        }

        var root = Path.GetFullPath(targetDirectory);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        System.IO.Directory.CreateDirectory(backupDirectory);

        var overwritten = new List<string>();
        var added = new List<string>();

        using var archive = ZipFile.OpenRead(package.FullPath);
        var prefix = CommonRootFolder(archive);

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.Name.Length == 0)
            {
                continue;
            }

            if (HasTraversalSegment(entry.FullName))
            {
                throw new InvalidDataException(
                    $"'{package.FileName}' contains an entry that would be written outside the "
                    + $"instance directory: {entry.FullName}");
            }

            var relative = entry.FullName;
            if (prefix is not null && relative.StartsWith(prefix, StringComparison.Ordinal))
            {
                relative = relative[prefix.Length..];
            }

            if (IsPreserved(relative))
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(root, relative));

            if (!destination.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"'{package.FileName}' contains an entry that would be written outside the "
                    + $"instance directory: {entry.FullName}");
            }

            if (File.Exists(destination))
            {
                var kept = Path.Combine(backupDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
                File.Copy(destination, kept, overwrite: true);
                overwritten.Add(relative);
            }
            else
            {
                added.Add(relative);
            }

            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            await using var source = entry.Open();
            await using var target = new FileStream(
                destination, FileMode.Create, FileAccess.Write, FileShare.None);

            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation(
            "Updated {Directory} to {Package}: {Replaced} replaced, {Added} added, world and "
            + "configuration untouched",
            targetDirectory, package.FileName, overwritten.Count, added.Count);

        return new ServerUpdateSwap(backupDirectory, overwritten, added);
    }

    /// <summary>Puts back what <see cref="UpdateInPlaceAsync"/> replaced.</summary>
    public void RollBack(string targetDirectory, ServerUpdateSwap swap)
    {
        foreach (var relative in swap.Overwritten)
        {
            var kept = Path.Combine(swap.BackupDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            var destination = Path.Combine(targetDirectory, relative.Replace('/', Path.DirectorySeparatorChar));

            try
            {
                if (File.Exists(kept))
                {
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(kept, destination, overwrite: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Every file is attempted: stopping at the first failure would
                // leave a half-rolled-back install, which is worse than either
                // version whole.
                logger.LogError(ex, "Could not restore {File} while rolling back", relative);
            }
        }

        foreach (var relative in swap.Added)
        {
            try
            {
                var destination = Path.Combine(targetDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not remove {File} while rolling back", relative);
            }
        }

        logger.LogInformation(
            "Rolled {Directory} back: {Restored} restored, {Removed} removed",
            targetDirectory, swap.Overwritten.Count, swap.Added.Count);
    }

    /// <summary>
    /// Finds a single top-level folder shared by every entry, so a zip that
    /// wraps its contents in one produces a flat instance directory rather than
    /// a directory containing a directory — with the executable one level below
    /// where everything expects it.
    /// </summary>
    private static string? CommonRootFolder(ZipArchive archive)
    {
        string? candidate = null;

        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var slash = name.IndexOf('/');

            // Anything at the top level means there is no single wrapping folder.
            if (slash <= 0)
            {
                return null;
            }

            var top = name[..(slash + 1)];

            // "." and ".." are not folders to unwrap. Treating ".." as one would
            // strip the traversal and leave a path that looks fine.
            if (top is "./" or "../")
            {
                return null;
            }
            if (candidate is null)
            {
                candidate = top;
            }
            else if (!string.Equals(candidate, top, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return candidate;
    }

    /// <summary>
    /// True if any path segment is <c>..</c>, treating both separators as one —
    /// zip entries are meant to use forward slashes, but nothing enforces it and
    /// Windows honours backslashes anyway.
    /// </summary>
    private static bool HasTraversalSegment(string entryName) =>
        entryName
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment == "..");
}
