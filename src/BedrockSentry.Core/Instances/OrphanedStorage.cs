namespace BedrockSentry.Core.Instances;

/// <summary>What a removed server left behind (<c>D63</c>).</summary>
public enum OrphanKind
{
    /// <summary>Its folder in the backups root: archives, folder copies, history.</summary>
    Backups,

    /// <summary>Its folder of console transcripts.</summary>
    ConsoleLogs,

    /// <summary>Its record of which Bedrock build it last ran.</summary>
    VersionRecord,
}

/// <summary>One thing a removed server left behind.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Name">The server id it is named after.</param>
/// <param name="FullPath">Where it is.</param>
/// <param name="SizeBytes">How much disk it holds.</param>
/// <param name="Files">How many files it holds; one for a version record.</param>
/// <param name="LastChangedUtc">The newest write inside it.</param>
public sealed record OrphanedItem(
    OrphanKind Kind, string Name, string FullPath, long SizeBytes, int Files, DateTimeOffset LastChangedUtc)
{
    /// <summary>What a selection names it by: a kind and a name, never a path.</summary>
    /// <remarks>
    /// Not the path, so a deletion is asked of the list as it stands when it runs
    /// rather than of a path the page was handed (<see cref="OrphanedStorageRules.Chosen"/>).
    /// The kind's name comes first, so the two parts split at the first colon.
    /// </remarks>
    public string Key => $"{Kind}:{Name}";
}

/// <summary>What happened to one item a deletion was asked for.</summary>
public enum OrphanDeletionOutcome
{
    /// <summary>It is gone.</summary>
    Deleted,

    /// <summary>
    /// A folder delete stopped partway: some of it is gone and some remains.
    /// <c>Directory.Delete</c> keeps walking after its first failure (<c>T186</c>).
    /// </summary>
    PartlyDeleted,

    /// <summary>Nothing of it was deleted.</summary>
    Refused,

    /// <summary>
    /// It was no longer left behind when the deletion ran: gone already, or a server
    /// of that id exists again. Nothing was touched.
    /// </summary>
    NoLongerLeftBehind,
}

/// <summary>One item's outcome, with the reason when there is one.</summary>
public sealed record OrphanDeletion(
    OrphanKind Kind, string Name, OrphanDeletionOutcome Outcome, string? Reason = null, int FilesLeft = 0)
{
    /// <summary>How a sentence names it: "Backups of dispo-t169".</summary>
    public string Label => $"{OrphanedStorageRules.Describe(Kind)} of {Name}";
}

/// <summary>The rules for what counts as left behind, and the sentences about it (<c>D63</c>).</summary>
/// <remarks>
/// Answered by the operator 2026-09-25: list what removed servers left on Settings →
/// Storage, with a checkbox on each, and delete the ticked ones with one button.
/// Nothing is deleted unasked and nothing is pruned by age, which is what <c>D39</c>
/// keeps a removed server's backups for.
/// </remarks>
public static class OrphanedStorageRules
{
    /// <summary>
    /// Whether a folder or file named <paramref name="name"/> belongs to no configured
    /// server, retired ones included.
    /// </summary>
    /// <param name="name">The folder name, or the file name without its extension.</param>
    /// <param name="known">
    /// The names configured servers use there, compared ignoring case as Windows names
    /// folders.
    /// </param>
    public static bool IsLeftBehind(string name, IReadOnlySet<string> known)
    {
        ArgumentNullException.ThrowIfNull(known);
        return !string.IsNullOrWhiteSpace(name) && !known.Contains(name);
    }

    /// <summary>
    /// Each selected key, paired with the item it names in <paramref name="current"/>,
    /// or with null when that item is no longer left behind.
    /// </summary>
    /// <remarks>
    /// A selection is a list of keys, and the deletion asks the list again when it
    /// runs. A server created since under a removed one's id has taken its folders
    /// back, and a selection made before that must not delete them. A key that names
    /// no kind is dropped: the page never makes one.
    /// </remarks>
    public static IReadOnlyList<(OrphanKind Kind, string Name, OrphanedItem? Item)> Chosen(
        IReadOnlyList<OrphanedItem> current, IReadOnlyCollection<string> selected)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(selected);

        var chosen = new List<(OrphanKind, string, OrphanedItem?)>();

        foreach (var key in selected.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var colon = key.IndexOf(':');

            if (colon <= 0
                || !Enum.TryParse<OrphanKind>(key[..colon], out var kind)
                || !Enum.IsDefined(kind))
            {
                continue;
            }

            var name = key[(colon + 1)..];

            chosen.Add((kind, name, current.FirstOrDefault(i =>
                i.Kind == kind && string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase))));
        }

        return chosen;
    }

    /// <summary>What the page calls a kind, in the What column.</summary>
    public static string Describe(OrphanKind kind) => kind switch
    {
        OrphanKind.Backups => "Backups",
        OrphanKind.ConsoleLogs => "Console logs",
        OrphanKind.VersionRecord => "Version record",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled kind."),
    };

    /// <summary>The sentence the page shows after a deletion.</summary>
    /// <remarks>
    /// A partial delete is said as partial, with what remains. Reporting it as "could
    /// not delete" leaves somebody believing the backups inside are intact when some
    /// are gone (<c>T229</c>).
    /// </remarks>
    public static string Summary(IReadOnlyList<OrphanDeletion> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var deleted = results.Count(r => r.Outcome == OrphanDeletionOutcome.Deleted);
        var parts = new List<string>
        {
            deleted == results.Count
                ? $"Deleted {Items(deleted)}."
                : $"Deleted {deleted} of {Items(results.Count)}.",
        };

        foreach (var result in results.Where(r => r.Outcome != OrphanDeletionOutcome.Deleted))
        {
            parts.Add(result.Outcome switch
            {
                OrphanDeletionOutcome.PartlyDeleted =>
                    $"{result.Label} was only partly deleted: {Files(result.FilesLeft)} "
                    + $"{(result.FilesLeft == 1 ? "remains" : "remain")} ({result.Reason}).",
                OrphanDeletionOutcome.Refused =>
                    $"{result.Label} was not deleted ({result.Reason}).",
                OrphanDeletionOutcome.NoLongerLeftBehind =>
                    $"{result.Label} was left alone: it is gone already, or a server of that name exists again.",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(results), result.Outcome, "Unhandled outcome."),
            });
        }

        return string.Join(" ", parts);
    }

    private static string Items(int count) => count == 1 ? "1 item" : $"{count} items";

    private static string Files(int count) => count == 1 ? "1 file" : $"{count} files";
}
