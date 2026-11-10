using BedrockSentry.Core.Backups;

namespace BedrockSentry.Core.Instances;

/// <summary>A <c>save query</c> file list folded under the line that announced it.</summary>
/// <param name="Sequence">The file list's own line.</param>
/// <param name="Files">How many files it names.</param>
public sealed record FoldedManifest(long Sequence, int Files);

/// <summary>
/// Which <see cref="ConsoleLineKind.SaveManifest"/> lines the console folds under the
/// <c>Data saved. Files are now ready to be copied.</c> line before them (<c>ON5</c>).
/// </summary>
/// <remarks>
/// A file list belongs to the nearest <c>Data saved</c> line above it that no later list
/// has taken. One with no such line, because the buffer evicted it or a filter hides it,
/// stays on screen: a fold with nothing to click would hide it for good.
/// Folding is a view decision, like <see cref="ConsoleFilters"/>: the buffer and the
/// transcript keep every line.
/// </remarks>
public sealed class ConsoleSaveFold
{
    private const string Announcement = "Data saved";

    private readonly Dictionary<long, FoldedManifest> under = [];
    private readonly HashSet<long> folded = [];

    private ConsoleSaveFold()
    {
    }

    /// <summary>Pairs the file lists in <paramref name="lines"/>, which are in buffer order.</summary>
    /// <param name="lines">The lines as shown, after filtering.</param>
    public static ConsoleSaveFold Of(IEnumerable<ConsoleLine> lines)
    {
        var fold = new ConsoleSaveFold();
        long? open = null;

        foreach (var line in lines)
        {
            if (line.Kind is ConsoleLineKind.Save
                && line.Message.AsSpan().TrimStart().StartsWith(Announcement, StringComparison.Ordinal))
            {
                open = line.Sequence;
            }
            else if (line.Kind is ConsoleLineKind.SaveManifest && open is { } owner)
            {
                var files = SaveManifest.TryParse(line.Message, out var entries) ? entries.Count : 0;
                fold.under[owner] = new FoldedManifest(line.Sequence, files);
                fold.folded.Add(line.Sequence);
                open = null;
            }
        }

        return fold;
    }

    /// <summary>The file list folded under <paramref name="line"/>, or null.</summary>
    public FoldedManifest? Under(ConsoleLine line) => under.GetValueOrDefault(line.Sequence);

    /// <summary>The toggle's words: what clicking it will do.</summary>
    public static string Label(FoldedManifest manifest, bool open) =>
        open ? "Hide files"
        : manifest.Files == 1 ? "Show 1 file"
        : $"Show {manifest.Files} files";

    /// <summary>
    /// True when <paramref name="line"/> is a folded file list that is not in
    /// <paramref name="opened"/>, the file lists the viewer clicked open.
    /// </summary>
    public bool Hides(ConsoleLine line, IReadOnlySet<long> opened) =>
        folded.Contains(line.Sequence) && !opened.Contains(line.Sequence);
}
