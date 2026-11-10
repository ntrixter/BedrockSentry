namespace BedrockSentry.Services.Addons;

/// <summary>What a server's Copy to library did (<c>D78</c> (a)).</summary>
/// <param name="Packs">One per copy tried, in version order.</param>
/// <param name="Refusal">Why nothing was tried, or null.</param>
public sealed record LibraryCopyResult(IReadOnlyList<ImportedPack> Packs, string? Refusal)
{
    /// <summary>Nothing was tried, for this reason.</summary>
    public static LibraryCopyResult Refused(string reason) => new([], reason);

    /// <summary>Whether anything went wrong: a refusal, or a copy that was not taken.</summary>
    public bool Failed => Refusal is not null || Packs.Any(p => p.Outcome == PackImportOutcome.Rejected);
}

/// <summary>What the Add-ons tab says after Copy to library.</summary>
/// <remarks>
/// Here rather than in the markup, so the sentence that says whether the library now
/// holds a pack is checked by the suite.
/// </remarks>
public static class LibraryCopyText
{
    /// <summary>One sentence or more: what was copied, what was already there, what was refused.</summary>
    /// <param name="result">What the copy did.</param>
    /// <param name="name">The add-on's name, as its row shows it.</param>
    /// <param name="server">The server's name.</param>
    public static string Done(LibraryCopyResult result, string name, string server)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(server);

        if (result.Refusal is { } refusal)
        {
            return refusal;
        }

        var sentences = new List<string>();

        var copied = Versions(result, PackImportOutcome.Imported);

        if (copied.Count > 0)
        {
            sentences.Add($"Copied {name} {Join(copied)} into the add-on library. {server} keeps its own copy, "
                + "switched on or off as it was.");
        }

        var present = Versions(result, PackImportOutcome.AlreadyPresent);

        if (present.Count > 0)
        {
            sentences.Add(present.Count == 1
                ? $"The library already held {present[0]}, so that one was left as it is."
                : $"The library already held {Join(present)}, so those were left as they are.");
        }

        foreach (var rejected in result.Packs.Where(p => p.Outcome == PackImportOutcome.Rejected))
        {
            var which = rejected.Version is { } version ? $"{name} {version}" : name;
            sentences.Add($"{which} was not copied. {rejected.Reason}");
        }

        return string.Join(" ", sentences);
    }

    private static List<string> Versions(LibraryCopyResult result, PackImportOutcome outcome) =>
        [.. result.Packs.Where(p => p.Outcome == outcome && p.Version is not null).Select(p => p.Version!.Value.ToString())];

    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        1 => items[0],
        _ => $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}",
    };
}
