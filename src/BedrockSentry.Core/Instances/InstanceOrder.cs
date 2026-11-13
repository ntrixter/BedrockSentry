namespace BedrockSentry.Core.Instances;

/// <summary>
/// The order the panel lists servers in: the order an administrator dragged the
/// instance tabs into, then by name (<c>T437</c>).
/// </summary>
/// <remarks>
/// One rule for the instance tabs and the Status page's table, so the two cannot
/// list the same servers in two orders. A server never moved has no place and
/// sorts after every placed one, by name, which is how every server sorted before
/// tabs could be dragged; a new server therefore lands at the end.
/// </remarks>
public static class InstanceOrder
{
    /// <summary><paramref name="items"/> in display order.</summary>
    public static IReadOnlyList<T> Sort<T>(IEnumerable<T> items, Func<T, int?> placeOf, Func<T, string> nameOf) =>
        items
            .OrderBy(i => placeOf(i) is null ? 1 : 0)
            .ThenBy(i => placeOf(i) ?? 0)
            .ThenBy(nameOf, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>
    /// The order after <paramref name="moved"/> is dropped on <paramref name="target"/>:
    /// it takes the target's place, so a tab dragged right lands after the tab it
    /// was dropped on and one dragged left lands before it.
    /// </summary>
    /// <returns>The new order, or null when the drop changes nothing.</returns>
    public static IReadOnlyList<string>? Move(IReadOnlyList<string> current, string moved, string target)
    {
        ArgumentNullException.ThrowIfNull(current);

        var from = IndexOf(current, moved);
        var to = IndexOf(current, target);

        if (from < 0 || to < 0 || from == to)
        {
            return null;
        }

        var order = current.ToList();
        order.RemoveAt(from);
        order.Insert(to, moved);
        return order;
    }

    private static int IndexOf(IReadOnlyList<string> list, string id)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
