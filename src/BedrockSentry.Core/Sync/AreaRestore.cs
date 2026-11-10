using BedrockSentry.Core.Structures;

namespace BedrockSentry.Core.Sync;

/// <summary>
/// <c>restorearea</c> typed in game or on the console, as the companion pack relayed it
/// (<c>restore|player|backup|dimension|x1|y1|z1|x2|y2|z2|name</c>,
/// <see cref="Instances.RestoreRelay"/>).
/// </summary>
/// <param name="Player">Who typed it, or null for the console.</param>
/// <param name="Backup">The backup asked for, or null for the newest (<see cref="BackupChoice.Latest"/>).</param>
/// <param name="Dimension">The player's dimension, or null for the console.</param>
/// <param name="Target">A Sync Area's name, or two corners in <paramref name="Dimension"/>.</param>
public sealed record AreaRestoreCommand(string? Player, string? Backup, BedrockDimension? Dimension, AreaRestoreTarget Target);

/// <summary>The area a restore puts back: a Sync Area by name, or two corners in a dimension.</summary>
/// <param name="Name">The Sync Area's name; null when the corners say where.</param>
public sealed record AreaRestoreTarget(string? Name, BlockVector? From, BlockVector? To, BedrockDimension Dimension)
{
    /// <summary>A Sync Area of the server's own, by name; its dimension is the area's.</summary>
    public static AreaRestoreTarget Named(string name) => new(name, null, null, BedrockDimension.Overworld);

    /// <summary>The blocks between two corners, both included.</summary>
    public static AreaRestoreTarget Corners(BlockVector from, BlockVector to, BedrockDimension dimension) =>
        new(null, from, to, dimension);

    /// <summary>
    /// A Sync Area's corners and dimension, carried under its name (<c>D111</c>): a restore onto
    /// another server, or a sync of one area, looks nothing up in the server whose world changes,
    /// which may have no area of that name or one somewhere else.
    /// </summary>
    /// <remarks>An area whose dimension Bedrock does not have is asked for by name, which says so.</remarks>
    public static AreaRestoreTarget Of(KeptAreaSettings area)
    {
        ArgumentNullException.ThrowIfNull(area);

        return BedrockDimension.Find(area.Dimension) is { } dimension
            ? new((area.Name ?? string.Empty).Trim(), new BlockVector(area.X1, area.Y1, area.Z1), new BlockVector(area.X2, area.Y2, area.Z2), dimension)
            : Named(area.Name ?? string.Empty);
    }
}

/// <summary>An area to restore, split into the pieces a throwaway saves and the server puts back (<c>D108</c>).</summary>
/// <param name="Title">What every sentence calls it: the Sync Area's name, or its corners and dimension.</param>
/// <param name="Plan">The split; null when the area could not be found.</param>
/// <param name="Problems">Why the restore is refused; empty when it is not.</param>
public sealed record AreaRestorePlan(string Title, BedrockDimension Dimension, StructurePlan? Plan, IReadOnlyList<string> Problems)
{
    /// <summary>
    /// What every piece's name starts with, so a restore's pieces never share a name with a
    /// sync's <c>keep_</c> pieces in the one structures pack.
    /// </summary>
    public const string PiecePrefix = "restore";

    /// <summary>Whether the restore may run.</summary>
    public bool CanRestore => Problems.Count == 0;

    /// <summary>Its pieces, each labelled as the console names it.</summary>
    public IReadOnlyList<LabelledPiece> Pieces => Plan is null
        ? []
        : [.. Plan.Pieces.Select(p => new LabelledPiece(p, $"{Title}, piece {p.Index + 1} of {Plan.Pieces.Count}", Dimension))];

    /// <summary>About how many bytes the pieces' files take.</summary>
    public long EstimatedBytes => Plan?.EstimatedBytes ?? 0;

    /// <summary>Finds and splits the area <paramref name="target"/> names.</summary>
    /// <param name="saved">The server's own Sync Areas, which a name is looked up in.</param>
    /// <remarks>
    /// A name is matched trimmed and without regard to case, as <c>syncarea</c> matches it. The
    /// split is <see cref="StructurePieces.Plan"/>'s within the dimension's height, so corners
    /// above or below the world stop at it, as a Sync Area's do.
    /// </remarks>
    public static AreaRestorePlan For(AreaRestoreTarget target, IReadOnlyList<KeptAreaSettings> saved)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(saved);

        BlockVector from, to;
        BedrockDimension dimension;
        string title;

        if (target is { From: { } a, To: { } b })
        {
            // Corners first: a Sync Area carried with its corners (`AreaRestoreTarget.Of`) is named
            // by them, and its name is only what the sentences call it.
            dimension = target.Dimension;
            from = a;
            to = b;
            title = target.Name is { Length: > 0 } named
                ? named
                : AreaRestoreText.CornersTitle(BlockBox.Between(a, b), dimension);
        }
        else if (target.Name is { } asked)
        {
            var name = asked.Trim();
            var area = saved.FirstOrDefault(k => string.Equals((k.Name ?? string.Empty).Trim(), name, StringComparison.OrdinalIgnoreCase));

            if (area is null)
            {
                return new AreaRestorePlan(name, target.Dimension, null, [AreaRestoreText.NoSuchArea(name, saved)]);
            }

            title = area.Name.Trim();

            if (BedrockDimension.Find(area.Dimension) is not { } found)
            {
                return new AreaRestorePlan(title, target.Dimension, null, [$"{title}: {area.Dimension} is not a dimension Bedrock has."]);
            }

            dimension = found;
            from = new BlockVector(area.X1, area.Y1, area.Z1);
            to = new BlockVector(area.X2, area.Y2, area.Z2);
        }
        else
        {
            return new AreaRestorePlan("the area", target.Dimension, null, ["Name a Sync Area, or give both corners."]);
        }

        var plan = StructurePieces.Plan(from, to, PiecePrefix, dimension.Height);

        return plan.State == StructurePlanState.Planned
            ? new AreaRestorePlan(title, dimension, plan, [])
            : new AreaRestorePlan(title, dimension, plan, [$"{title}: {plan.Reason}"]);
    }
}

/// <summary>One backup a restore could read, newest first, and why it may not be read.</summary>
/// <param name="Name">Its name on disk, as the Backups tab shows it.</param>
/// <param name="Unusable">Why an area cannot be restored from it, worded to follow its name; null when it can.</param>
public sealed record BackupCandidate(string Name, string? Unusable);

/// <summary>The backup a restore reads, or why none was chosen.</summary>
public sealed record BackupChosen(string? Name, string? Refusal);

/// <summary>Which backup an area restore reads (<c>D108</c>).</summary>
public static class BackupChoice
{
    /// <summary>The word that asks for the newest usable backup; an empty choice means the same.</summary>
    public const string Latest = "latest";

    /// <summary>How many usable backups a refusal names.</summary>
    public const int Suggested = 3;

    /// <summary>Chooses from <paramref name="newestFirst"/>.</summary>
    /// <param name="wanted">
    /// <see cref="Latest"/>, empty, or a backup's name on disk, matched without regard to
    /// case and with or without its extension.
    /// </param>
    /// <param name="owner">
    /// The server the backups are of, when it is not the one whose world changes (<c>D111</c>);
    /// null for the server's own.
    /// </param>
    public static BackupChosen Pick(IReadOnlyList<BackupCandidate> newestFirst, string? wanted, string? owner = null)
    {
        ArgumentNullException.ThrowIfNull(newestFirst);

        var usable = newestFirst.Where(b => b.Unusable is null).ToList();
        var asked = (wanted ?? string.Empty).Trim();

        if (asked.Length == 0 || string.Equals(asked, Latest, StringComparison.OrdinalIgnoreCase))
        {
            return usable.Count > 0
                ? new BackupChosen(usable[0].Name, null)
                : new BackupChosen(null, owner is null ? AreaRestoreText.NoUsableBackup : AreaRestoreText.NoUsableBackupOf(owner));
        }

        var match = newestFirst.FirstOrDefault(b =>
            string.Equals(b.Name, asked, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileNameWithoutExtension(b.Name), asked, StringComparison.OrdinalIgnoreCase));

        return match switch
        {
            { Unusable: null } => new BackupChosen(match.Name, null),
            { Unusable: { } why } => new BackupChosen(null, $"{match.Name} {why}."),
            null => new BackupChosen(null, AreaRestoreText.NoSuchBackup(asked, [.. usable.Take(Suggested).Select(b => b.Name)], owner)),
        };
    }
}
