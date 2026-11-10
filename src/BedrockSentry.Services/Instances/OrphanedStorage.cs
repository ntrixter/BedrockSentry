using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Instances;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Instances;

/// <summary>What removed servers left behind, or why some of it could not be read.</summary>
/// <param name="Items">Backups first, then console logs, then version records, each by name.</param>
/// <param name="Unreadable">
/// A sentence naming a folder that could not be listed, or null. What was listed
/// elsewhere is still returned, so one denied folder does not hide the rest.
/// </param>
public sealed record OrphanListing(IReadOnlyList<OrphanedItem> Items, string? Unreadable);

/// <summary>
/// Finds and deletes what removed servers left behind (<c>D63</c>): their backup folders,
/// their console-log folders and their version records.
/// </summary>
/// <remarks>
/// Removing a server keeps its backups unless asked (<c>D39</c>), keeps its console logs
/// when that box is left unticked, and before <c>T315</c> kept its version record too.
/// Nothing listed them, so they were invisible. Retired servers are configured and keep
/// theirs, so they are never listed here.
/// </remarks>
public sealed class OrphanedStorage(
    ISentryPaths paths, InstanceRegistry registry, ILogger<OrphanedStorage> log)
{
    /// <summary>
    /// Deletes one folder. A seam: the arm where a delete stops partway cannot be
    /// arranged on a build running as root (<c>T176</c>).
    /// </summary>
    internal Action<string> DeleteFolder { get; set; } = path => Directory.Delete(path, recursive: true);

    private string VersionRecords => Path.Combine(paths.Data, "versions");

    /// <summary>Lists what removed servers left behind, as it is now.</summary>
    public OrphanListing List()
    {
        var ids = registry.All.Select(s => s.InstanceId)
            .Concat(registry.Retired.Select(r => r.InstanceId))
            .ToList();

        var known = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var knownLogs = ids
            .Select(id => InstanceLayout.SanitiseFolderName(id, id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var items = new List<OrphanedItem>();
        var unreadable = new List<string>();

        void Collect(string root, Action<string> collect)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    collect(root);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning(ex, "Could not list {Root} for left-behind folders.", root);
                unreadable.Add($"{root} ({ex.Message.TrimEnd('.')})");
            }
        }

        Collect(paths.Backups, root => items.AddRange(
            Directory.EnumerateDirectories(root)
                .Where(d => OrphanedStorageRules.IsLeftBehind(Path.GetFileName(d), known))
                .Select(d => Folder(OrphanKind.Backups, d))));

        Collect(paths.InstanceLogs, root => items.AddRange(
            Directory.EnumerateDirectories(root)
                .Where(d => OrphanedStorageRules.IsLeftBehind(Path.GetFileName(d), knownLogs))
                .Select(d => Folder(OrphanKind.ConsoleLogs, d))));

        Collect(VersionRecords, root => items.AddRange(
            Directory.EnumerateFiles(root, "*.json")
                .Where(f => OrphanedStorageRules.IsLeftBehind(Path.GetFileNameWithoutExtension(f), known))
                .Select(f =>
                {
                    var file = new FileInfo(f);
                    return new OrphanedItem(
                        OrphanKind.VersionRecord, Path.GetFileNameWithoutExtension(f), f,
                        file.Length, 1, file.LastWriteTimeUtc);
                })));

        return new OrphanListing(
            items
                .OrderBy(i => i.Kind)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            unreadable.Count == 0
                ? null
                : $"Could not be listed, so anything left behind there is not shown: {string.Join("; ", unreadable)}.");
    }

    /// <summary>Deletes the selected items that are still left behind.</summary>
    /// <param name="keys">The selection, by <see cref="OrphanedItem.Key"/>.</param>
    public IReadOnlyList<OrphanDeletion> Delete(IReadOnlyCollection<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var results = new List<OrphanDeletion>();

        foreach (var (kind, name, item) in OrphanedStorageRules.Chosen(List().Items, keys))
        {
            if (item is null)
            {
                results.Add(new OrphanDeletion(kind, name, OrphanDeletionOutcome.NoLongerLeftBehind));
                continue;
            }

            try
            {
                if (kind == OrphanKind.VersionRecord)
                {
                    File.Delete(item.FullPath);
                }
                else
                {
                    DeleteFolder(item.FullPath);
                }

                log.LogInformation("Deleted {Kind} left behind by {Name} at {Path}.", kind, name, item.FullPath);
                results.Add(new OrphanDeletion(kind, name, OrphanDeletionOutcome.Deleted));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning(ex, "Could not delete {Path}.", item.FullPath);
                results.Add(AfterAFailure(item, ex.Message));
            }
        }

        return results;
    }

    /// <summary>
    /// What a failed delete left, read from the disk rather than assumed: a folder
    /// delete keeps walking after its first failure (<c>T186</c>).
    /// </summary>
    private static OrphanDeletion AfterAFailure(OrphanedItem item, string reason)
    {
        var exists = item.Kind == OrphanKind.VersionRecord
            ? File.Exists(item.FullPath)
            : Directory.Exists(item.FullPath);

        if (!exists)
        {
            return new OrphanDeletion(item.Kind, item.Name, OrphanDeletionOutcome.Deleted);
        }

        var left = item.Kind == OrphanKind.VersionRecord ? 1 : CountFiles(item.FullPath);

        if (left is not { } counted)
        {
            // Unknown is neither of the two claims: saying "partly" or "nothing" about
            // a folder that cannot be counted would be a guess either way.
            return new OrphanDeletion(
                item.Kind, item.Name, OrphanDeletionOutcome.Refused,
                $"{reason.TrimEnd('.')}; what remains of it could not be counted");
        }

        return new OrphanDeletion(
            item.Kind,
            item.Name,
            counted < item.Files ? OrphanDeletionOutcome.PartlyDeleted : OrphanDeletionOutcome.Refused,
            reason.TrimEnd('.'),
            counted);
    }

    private static OrphanedItem Folder(OrphanKind kind, string directory)
    {
        var files = new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).ToList();

        return new OrphanedItem(
            kind,
            Path.GetFileName(directory),
            directory,
            files.Sum(f => f.Length),
            files.Count,
            files.Count > 0
                ? files.Max(f => f.LastWriteTimeUtc)
                : Directory.GetLastWriteTimeUtc(directory));
    }

    private static int? CountFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
