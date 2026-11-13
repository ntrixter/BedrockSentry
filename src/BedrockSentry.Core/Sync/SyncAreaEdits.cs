using System.Text;
using System.Text.Json;
using BedrockSentry.Core.Structures;

namespace BedrockSentry.Core.Sync;

/// <summary>What <c>syncarea</c> asks for (<c>D106</c>).</summary>
public enum SyncAreaAction
{
    /// <summary>Adds an area, both corners at one block.</summary>
    New,

    /// <summary>Moves one corner of an area.</summary>
    Update,

    /// <summary>Ticks or unticks an area's Show in game.</summary>
    Show,

    /// <summary>Lists every area.</summary>
    List,
}

/// <summary>
/// A <c>syncarea</c> typed in game or on the console, as the companion pack relayed it
/// (<c>area|…</c>, <see cref="Instances.AreaRelay"/>).
/// </summary>
/// <param name="Player">Who typed it, or null for the console.</param>
/// <param name="Corner">1 or 2 for <see cref="SyncAreaAction.New"/> and <see cref="SyncAreaAction.Update"/>.</param>
/// <param name="Show">The value <see cref="SyncAreaAction.Show"/> sets.</param>
/// <param name="Position">The corner's block, the player's feet less one when none was typed.</param>
/// <param name="Dimension">The player's dimension, or null for the console.</param>
public sealed record SyncAreaCommand(
    string? Player,
    SyncAreaAction Action,
    string Name,
    int? Corner = null,
    bool? Show = null,
    BlockVector? Position = null,
    BedrockDimension? Dimension = null);

/// <summary>What a <c>syncarea</c> did: the settings to save, if any, and the answer.</summary>
/// <param name="Updated">The Sync Areas to write, or null when nothing changes.</param>
/// <param name="Ok">False when the command was refused; the answer is shown as a refusal.</param>
public sealed record SyncAreaEdit(SyncSettings? Updated, bool Ok, IReadOnlyList<string> Lines);

/// <summary>
/// The Sync Areas' rules for <c>syncarea</c> in game (<c>D106</c> step 4), and every sentence
/// it answers with.
/// </summary>
/// <remarks>
/// An edit that leaves the areas unable to sync, such as two that share blocks, is saved
/// and the reason said, as the Sync tab saves on every change and lists the problems
/// under the table: the next edit is usually the one that fixes it.
/// </remarks>
public static class SyncAreaEdits
{
    /// <summary>The scriptevent id the pack answers a player on: <c>REPLY_EVENT</c> in <c>scripts/main.js</c>.</summary>
    public const string ReplyEventId = "bedrocksentry:reply";

    public static SyncAreaEdit Apply(SyncSettings current, SyncAreaCommand command)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(command);

        return command.Action switch
        {
            SyncAreaAction.List => List(current),
            SyncAreaAction.New => New(current, command),
            SyncAreaAction.Update => Update(current, command),
            SyncAreaAction.Show => Show(current, command),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command.Action, "Not a syncarea action."),
        };
    }

    /// <summary>The sentence a <c>syncarea</c> gets while a sync runs on its server.</summary>
    public const string SyncRunning = "A sync is running on this server; try again when it ends.";

    private static SyncAreaEdit List(SyncSettings current)
    {
        var areas = current.KeptAreas;
        if (areas.Count == 0)
        {
            return new SyncAreaEdit(null, true, ["No Sync Areas."]);
        }

        var heading = areas.Count == 1 ? "1 Sync Area:" : $"{areas.Count} Sync Areas:";
        return new SyncAreaEdit(null, true, [heading, .. areas.Select(Describe)]);
    }

    private static SyncAreaEdit New(SyncSettings current, SyncAreaCommand command)
    {
        var name = command.Name.Trim();
        if (Refusal(name) is { } refused)
        {
            return refused;
        }

        // `new` with a corner on an area that exists sets that corner, so `syncarea new Base pos1`
        // then `syncarea new Base pos2` makes the box, as the operator asked on 2026-10-09.
        if (Find(current, name) is { } existing)
        {
            return command.Corner is 1 or 2
                ? Update(current, command)
                : Refuse($"A Sync Area named {existing.Name} exists. syncarea new {existing.Name} pos2 sets its other corner.");
        }

        if (command.Position is not { } at)
        {
            return Refuse($"syncarea new {name} needs a position.");
        }

        var dimension = command.Dimension ?? BedrockDimension.Overworld;
        var areas = Copy(current.KeptAreas);
        areas.Add(new KeptAreaSettings
        {
            Name = name,
            Dimension = dimension.Id,
            Color = AreaColors.Next(areas.Select((a, i) => AreaColors.Of(a.Color, i))),
            ShowInGame = true,
            X1 = at.X, Y1 = at.Y, Z1 = at.Z,
            X2 = at.X, Y2 = at.Y, Z2 = at.Z,
        });

        return Saved(current, areas,
            $"Added Sync Area {name} in the {dimension.Title} at {at.X} {at.Y} {at.Z}, one block. "
            + $"Set its other corner with syncarea new {name} pos2.");
    }

    private static SyncAreaEdit Update(SyncSettings current, SyncAreaCommand command)
    {
        var areas = Copy(current.KeptAreas);
        var index = areas.FindIndex(a => Same(a.Name, command.Name));
        if (index < 0)
        {
            return Missing(command.Name);
        }

        var area = areas[index];
        var dimension = BedrockDimension.Find(area.Dimension);

        if (command.Dimension is { } here && dimension is not null && here != dimension)
        {
            return Refuse($"{area.Name} is in the {dimension.Title} and you are in the {here.Title}.");
        }

        if (command.Position is not { } at || command.Corner is not (1 or 2))
        {
            return Refuse($"Say which corner to move: syncarea update {area.Name} pos1, or pos2.");
        }

        if (command.Corner == 1)
        {
            (area.X1, area.Y1, area.Z1) = (at.X, at.Y, at.Z);
        }
        else
        {
            (area.X2, area.Y2, area.Z2) = (at.X, at.Y, at.Z);
        }

        var size = BlockBox.Between(new BlockVector(area.X1, area.Y1, area.Z1), new BlockVector(area.X2, area.Y2, area.Z2)).Size;
        return Saved(current, areas,
            $"{area.Name} now runs from {area.X1} {area.Y1} {area.Z1} to {area.X2} {area.Y2} {area.Z2}, "
            + $"{size.X} × {size.Y} × {size.Z} blocks.");
    }

    private static SyncAreaEdit Show(SyncSettings current, SyncAreaCommand command)
    {
        var areas = Copy(current.KeptAreas);
        var area = areas.Find(a => Same(a.Name, command.Name));
        if (area is null)
        {
            return Missing(command.Name);
        }

        if (command.Show is not { } show)
        {
            return Refuse($"syncarea show takes true or false: syncarea show {area.Name} true.");
        }

        area.ShowInGame = show;
        return Saved(current, areas, show ? $"{area.Name} is shown in game." : $"{area.Name} is hidden in game.");
    }

    /// <summary>
    /// The commands that answer <paramref name="player"/> through the pack, cut between lines
    /// under BDS's 2,048-character message (<see cref="AreaOutlines.MaxMessageLength"/>).
    /// </summary>
    public static IReadOnlyList<string> Replies(string player, bool ok, IReadOnlyList<string> lines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(player);
        ArgumentNullException.ThrowIfNull(lines);

        var heading = $"{{\"to\":{JsonSerializer.Serialize(player)},\"ok\":{(ok ? "true" : "false")},\"lines\":[";
        var parts = new List<List<string>> { new() };
        var length = heading.Length + "]}".Length;

        foreach (var line in lines)
        {
            var text = JsonSerializer.Serialize(line);
            var added = text.Length + (parts[^1].Count > 0 ? 1 : 0);

            if (parts[^1].Count > 0 && length + added > AreaOutlines.MaxMessageLength)
            {
                parts.Add([]);
                length = heading.Length + "]}".Length;
                added = text.Length;
            }

            parts[^1].Add(text);
            length += added;
        }

        return [.. parts.Select(p => $"scriptevent {ReplyEventId} {heading}{string.Join(',', p)}]}}")];
    }

    private static SyncAreaEdit? Refusal(string name) =>
        name.Length == 0 ? Refuse("Name the Sync Area: syncarea new <name>.")
        : name.Length > SyncPlan.MaxNameLength
            ? Refuse($"{name} is longer than {SyncPlan.MaxNameLength} characters, the most a Sync Area's name may be.")
        : null;

    private static SyncAreaEdit Saved(SyncSettings current, List<KeptAreaSettings> areas, string said)
    {
        var lines = new List<string> { said };
        lines.AddRange(SyncPlan.For(areas).Problems.Select(p => $"A sync is refused until this is fixed: {p}"));
        return new SyncAreaEdit(new SyncSettings { SourceInstanceId = current.SourceInstanceId, KeptAreas = areas }, true, lines);
    }

    private static SyncAreaEdit Missing(string name) =>
        Refuse($"No Sync Area named {name.Trim()}. syncarea list names them.");

    private static SyncAreaEdit Refuse(string said) => new(null, false, [said]);

    private static KeptAreaSettings? Find(SyncSettings settings, string name) =>
        settings.KeptAreas.Find(a => Same(a.Name, name));

    private static bool Same(string? a, string? b) =>
        string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Describe(KeptAreaSettings area)
    {
        var where = BedrockDimension.Find(area.Dimension)?.Title ?? area.Dimension;
        var text = new StringBuilder()
            .Append(area.Name).Append(": ")
            .Append($"{area.X1} {area.Y1} {area.Z1} to {area.X2} {area.Y2} {area.Z2} in the {where}, ")
            .Append(area.ShowInGame ? "shown" : "hidden");
        return text.ToString();
    }

    private static List<KeptAreaSettings> Copy(IEnumerable<KeptAreaSettings> areas) =>
    [
        .. areas.Select(a => new KeptAreaSettings
        {
            Name = a.Name, Dimension = a.Dimension, Color = a.Color, ShowInGame = a.ShowInGame, KeepTicking = a.KeepTicking,
            X1 = a.X1, Y1 = a.Y1, Z1 = a.Z1, X2 = a.X2, Y2 = a.Y2, Z2 = a.Z2,
        }),
    ];
}
