namespace BedrockSentry.Core.Structures;

/// <summary>
/// <c>showtickingarea</c> typed in game or on the console, as the companion pack relayed it
/// (<c>tickshow|player|show|dimension|name</c>, <see cref="Instances.TickShowRelay"/>).
/// </summary>
/// <param name="Player">Who typed it, or null for the console.</param>
/// <param name="Dimension">The player's dimension, or null for the console.</param>
public sealed record TickShowCommand(string? Player, bool Show, BedrockDimension? Dimension, string Name);

/// <summary>What a display change did: the settings to save, if any, and the answer.</summary>
public sealed record TickingAreaEdit(List<TickingAreaSettings>? Updated, bool Ok, IReadOnlyList<string> Lines);

/// <summary>
/// The rules for showing a ticking area in game (<c>D107</c>), from the panel or from
/// <c>showtickingarea</c>, and every sentence they answer with.
/// </summary>
public static class TickingAreaEdits
{
    /// <summary>
    /// Shows or hides the areas <paramref name="command"/> names. One name may be used in two
    /// dimensions: a player's own dimension is taken when it has one by that name, and the
    /// console's command reaches every one.
    /// </summary>
    public static TickingAreaEdit Show(TickingAreaList list, IReadOnlyList<TickingAreaSettings> settings, TickShowCommand command)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(command);

        var named = list.Areas
            .Where(a => !a.IsSyncs && string.Equals(a.Name, command.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (command.Dimension is { } here && named.Any(a => a.Dimension == here))
        {
            named = [.. named.Where(a => a.Dimension == here)];
        }

        if (named.Count == 0)
        {
            return new TickingAreaEdit(null, false, [$"No ticking area named {command.Name.Trim()}. The Ticking Areas view lists them."]);
        }

        var updated = Copy(settings);
        foreach (var area in named)
        {
            updated = WithDisplay(updated, area, command.Show, color: null);
        }

        return new TickingAreaEdit(
            updated,
            true,
            [.. named.Select(a => $"{a.Name} in the {a.Dimension.Title} is {(command.Show ? "shown" : "hidden")} in game.")]);
    }

    /// <summary>
    /// The settings with <paramref name="area"/> shown or hidden and, when given, coloured. An
    /// area seen for the first time takes the first colour no other ticking area has.
    /// </summary>
    public static List<TickingAreaSettings> WithDisplay(
        IReadOnlyList<TickingAreaSettings> settings, TickingArea area, bool show, string? color)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(area);

        var updated = Copy(settings);
        var index = TickingAreaOutlines.Index(updated, area);

        if (index < 0)
        {
            updated.Add(new TickingAreaSettings
            {
                Name = area.Name,
                Dimension = area.Dimension.Id,
                Color = AreaColors.Next(updated.Select((s, i) => AreaColors.Of(s.Color, i))),
            });
            index = updated.Count - 1;
        }

        updated[index].ShowInGame = show;
        if (color is not null)
        {
            updated[index].Color = AreaColors.IsValid(color) ? color.ToLowerInvariant() : string.Empty;
        }

        return updated;
    }

    /// <summary>The settings without <paramref name="area"/>'s, for an area removed or renamed.</summary>
    public static List<TickingAreaSettings> Without(IReadOnlyList<TickingAreaSettings> settings, TickingArea area)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(area);
        return [.. Copy(settings).Where(s => !s.IsFor(area))];
    }

    private static List<TickingAreaSettings> Copy(IEnumerable<TickingAreaSettings> settings) =>
    [
        .. settings.Select(s => new TickingAreaSettings
        {
            Name = s.Name, Dimension = s.Dimension, ShowInGame = s.ShowInGame, Color = s.Color,
        }),
    ];
}
