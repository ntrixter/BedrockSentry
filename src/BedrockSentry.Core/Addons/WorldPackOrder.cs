namespace BedrockSentry.Core.Addons;

/// <summary>A move on the Load order view.</summary>
public enum PackOrderMove
{
    /// <summary>One place nearer the top, which wins more.</summary>
    Up,

    /// <summary>One place further down.</summary>
    Down,

    /// <summary>To the top of its list.</summary>
    Top,
}

/// <summary>Where a move puts a registration in a world's pack list.</summary>
/// <remarks>
/// <para><strong>Positions are counted in the whole list, not in the rows
/// shown.</strong> The view lists the entries it understands, and an entry with
/// no <c>pack_id</c> is kept in the file untouched (<see cref="WorldPackList"/>),
/// so "one up" means "before the understood entry above", wherever that sits in
/// the file.</para>
///
/// <para><strong>A pack registered twice cannot be moved.</strong>
/// <see cref="WorldPackList.MoveTo"/> finds a registration by uuid and takes the
/// first, so pressing a button on the second row would move the first. The rows
/// say it is listed twice instead, and the finding says why that matters
/// (<see cref="AddonFindingKind.RegisteredTwice"/>).</para>
///
/// <para><strong>Nor can another pack be moved past it</strong> (`T314`). Which
/// of the two entries Bedrock ranks it by is not measured, so a pack carried
/// past one of them and not the other ends up both above and below it, and
/// nobody can say which the server obeys. A move that would pass either entry
/// is refused, and so is To top when one sits above.</para>
/// </remarks>
public static class WorldPackOrder
{
    /// <summary>
    /// The index to hand <see cref="AddonChangePlanner.Reorder"/>, or null when
    /// the move is not one this pack can make.
    /// </summary>
    public static int? TargetOf(WorldPackList list, string uuid, PackOrderMove move)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentException.ThrowIfNullOrWhiteSpace(uuid);

        var entries = list.Entries;
        var shown = new List<int>();

        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].IsUnderstood)
            {
                shown.Add(i);
            }
        }

        var mine = shown.Where(i => Is(entries[i], uuid)).ToList();

        if (mine.Count != 1)
        {
            return null;
        }

        var at = shown.IndexOf(mine[0]);

        // The rows this move carries the pack past.
        var passes = move switch
        {
            PackOrderMove.Up => shown.Take(at).TakeLast(1),
            PackOrderMove.Down => shown.Skip(at + 1).Take(1),
            PackOrderMove.Top => shown.Take(at),
            _ => throw new ArgumentOutOfRangeException(nameof(move), move, "No target is worked out for this move."),
        };

        if (passes.Any(i => IsListedTwice(list, entries[i].PackId!)))
        {
            return null;
        }

        return move switch
        {
            // Before the understood entry above. Removing this one leaves that
            // entry's index unchanged, because it comes earlier in the file.
            PackOrderMove.Up => at == 0 ? null : shown[at - 1],

            // After the understood entry below. Removing this one shifts that
            // entry back by one, so inserting at its old index lands just after it.
            PackOrderMove.Down => at == shown.Count - 1 ? null : shown[at + 1],

            PackOrderMove.Top => at == 0 ? null : shown[0],

            _ => throw new ArgumentOutOfRangeException(nameof(move), move, "No target is worked out for this move."),
        };
    }

    /// <summary>Whether a uuid is registered more than once in this list.</summary>
    public static bool IsListedTwice(WorldPackList list, string uuid)
    {
        ArgumentNullException.ThrowIfNull(list);

        return list.Entries.Count(e => Is(e, uuid)) > 1;
    }

    private static bool Is(WorldPackEntry entry, string uuid) =>
        entry.IsUnderstood && string.Equals(entry.PackId, uuid, StringComparison.OrdinalIgnoreCase);
}
