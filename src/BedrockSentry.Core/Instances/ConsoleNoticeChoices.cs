using System.Text.Json;

namespace BedrockSentry.Core.Instances;

/// <summary>
/// The viewer's choice, per banner, to show or hide a boxed notice the console folds
/// (<see cref="ConsoleNoticeFold"/>, <c>T406</c>). A banner with no choice keeps its
/// default. The browser keeps one per instance between page loads, as the text
/// <see cref="Serialize"/> writes.
/// </summary>
public sealed class ConsoleNoticeChoices
{
    private readonly Dictionary<string, bool> open = new(StringComparer.Ordinal);

    /// <summary>True or false where the viewer chose, null where the default stands.</summary>
    public bool? Open(string banner) => open.TryGetValue(banner, out var shown) ? shown : null;

    /// <summary>Records the viewer's choice for <paramref name="banner"/>.</summary>
    public void Choose(string banner, bool shown) => open[banner] = shown;

    /// <summary>The choices as a JSON object of banner to shown.</summary>
    public string Serialize() => JsonSerializer.Serialize(open);

    /// <summary>
    /// Reads what <see cref="Serialize"/> wrote. Nothing stored, text that is not that
    /// object, or a banner <see cref="ConsoleLineClassifier.Notices"/> no longer names,
    /// reads as no choice: a stored value never hides a box the console does not know.
    /// </summary>
    public static ConsoleNoticeChoices Parse(string? stored)
    {
        var choices = new ConsoleNoticeChoices();
        if (string.IsNullOrWhiteSpace(stored))
        {
            return choices;
        }

        Dictionary<string, bool>? read;
        try
        {
            read = JsonSerializer.Deserialize<Dictionary<string, bool>>(stored);
        }
        catch (JsonException)
        {
            return choices;
        }

        foreach (var (banner, shown) in read ?? [])
        {
            if (ConsoleLineClassifier.Notices.Any(n => n.Banner == banner))
            {
                choices.Choose(banner, shown);
            }
        }

        return choices;
    }
}
