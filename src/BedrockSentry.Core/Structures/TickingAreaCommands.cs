using System.Text.RegularExpressions;
using BedrockSentry.Core.Instances;

namespace BedrockSentry.Core.Structures;

/// <summary>What a ticking-area command does.</summary>
public enum TickingAreaVerb
{
    List,
    Add,
    Remove,
    Preload,
}

/// <summary>
/// A <c>tickingarea</c> command the panel sends, and how its answer starts and ends on the
/// console (<c>D107</c>). Every one but the list runs in its area's dimension through
/// <c>execute in</c>, as <see cref="BedrockDimension.Run"/> does.
/// </summary>
public sealed record TickingAreaCommand(TickingAreaVerb Verb, string Text)
{
    /// <summary>Whether <paramref name="line"/> is the first line of this command's answer.</summary>
    public bool Starts(ConsoleLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var message = TickingAreaAnswers.Plain(line.Message);
        return TickingAreaAnswers.IsRefusal(line) || Verb switch
        {
            TickingAreaVerb.List => message.Contains(TickingAreaAnswers.ListHeader, StringComparison.Ordinal)
                                    || message == TickingAreaAnswers.NoneInAnyDimension,
            TickingAreaVerb.Add => message.StartsWith("Added ticking area", StringComparison.Ordinal),
            TickingAreaVerb.Remove => message.StartsWith("Removed ticking area(s)", StringComparison.Ordinal),
            TickingAreaVerb.Preload => message.StartsWith("Updated ticking area(s)", StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>Whether <paramref name="line"/> is the last line of this command's answer.</summary>
    public bool Ends(ConsoleLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var message = TickingAreaAnswers.Plain(line.Message).Trim();
        return TickingAreaAnswers.IsRefusal(line)
               || message == TickingAreaAnswers.NoneInAnyDimension
               || (Verb == TickingAreaVerb.Preload
                   ? message.EndsWith("matching area(s) are marked for preload.", StringComparison.Ordinal)
                   : TickingAreaAnswers.IsCount(message));
    }
}

/// <summary>The <c>tickingarea</c> commands, each with its name quoted when it holds a space.</summary>
public static class TickingAreaCommands
{
    public static TickingAreaCommand List() => new(TickingAreaVerb.List, "tickingarea list all-dimensions");

    public static TickingAreaCommand AddBox(string name, BedrockDimension dimension, int x1, int z1, int x2, int z2, bool preload) =>
        new(TickingAreaVerb.Add, dimension.Run($"tickingarea add {x1} 0 {z1} {x2} 0 {z2} {Name(name)}{Preload(preload)}"));

    public static TickingAreaCommand AddCircle(string name, BedrockDimension dimension, int x, int z, int radius, bool preload) =>
        new(TickingAreaVerb.Add, dimension.Run($"tickingarea add circle {x} 0 {z} {radius} {Name(name)}{Preload(preload)}"));

    /// <summary>Puts back an area as it was listed.</summary>
    public static TickingAreaCommand Add(TickingArea area)
    {
        ArgumentNullException.ThrowIfNull(area);

        return area.Shape == TickingAreaShape.Circle
            ? AddCircle(area.Name, area.Dimension, area.From.X, area.From.Z, area.Radius, area.Preload)
            : AddBox(area.Name, area.Dimension, area.From.X, area.From.Z, area.To.X, area.To.Z, area.Preload);
    }

    public static TickingAreaCommand Remove(string name, BedrockDimension dimension) =>
        new(TickingAreaVerb.Remove, dimension.Run($"tickingarea remove {Name(name)}"));

    public static TickingAreaCommand SetPreload(string name, BedrockDimension dimension, bool preload) =>
        new(TickingAreaVerb.Preload, dimension.Run($"tickingarea preload {Name(name)} {(preload ? "true" : "false")}"));

    /// <summary>Why <paramref name="name"/> cannot name a ticking area here, or null when it can.</summary>
    public static string? NameProblem(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        return trimmed.Length == 0 ? "Name the ticking area."
            : trimmed.Contains('"', StringComparison.Ordinal) || trimmed.Any(char.IsControl) ? "A ticking area's name cannot hold \" or a line break."
            : trimmed == StructureCommands.TickingAreaName ? $"{StructureCommands.TickingAreaName} is the name a sync uses while it runs."
            : null;
    }

    private static string Name(string name) => name.Contains(' ', StringComparison.Ordinal) ? $"\"{name}\"" : name;

    private static string Preload(bool preload) => preload ? " true" : string.Empty;
}

/// <summary>What BDS answers to <c>tickingarea</c>, measured on 1.26.52.3 (<c>D107</c>).</summary>
public static partial class TickingAreaAnswers
{
    /// <summary>The list's header, after its colour code.</summary>
    public const string ListHeader = "List of all ticking areas in all dimensions";

    /// <summary>The list's answer when the world has none, an <c>ERROR</c> line.</summary>
    public const string NoneInAnyDimension = "No ticking areas exist in any dimension.";

    /// <summary>A message without the <c>§</c> colour codes BDS puts in some answers.</summary>
    public static string Plain(string? message) => ColourCode().Replace(message ?? string.Empty, string.Empty);

    /// <summary>The <c>n/10 ticking areas in use.</c> line that ends a list, an add and a remove.</summary>
    public static bool IsCount(string message) => InUse().IsMatch(message);

    /// <summary>
    /// A one-line refusal: an <c>ERROR</c> about a ticking area, such as
    /// <em>Max number of ticking areas (10) has already been reached.</em>, <em>No ticking areas
    /// named x exist in the current dimension.</em> or <em>Radius cannot be larger than 4, …</em>.
    /// </summary>
    public static bool IsRefusal(ConsoleLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (line.Level != BdsLogLevel.Error)
        {
            return false;
        }

        var message = Plain(line.Message);
        return message != NoneInAnyDimension
               && (message.Contains("ticking area", StringComparison.OrdinalIgnoreCase)
                   || message.StartsWith("Radius cannot be larger", StringComparison.Ordinal));
    }

    [GeneratedRegex("§.")]
    private static partial Regex ColourCode();

    [GeneratedRegex(@"^\d+/\d+ ticking areas in use\.$")]
    private static partial Regex InUse();
}
