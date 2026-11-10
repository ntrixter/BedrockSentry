namespace BedrockSentry.Core.Instances;

/// <summary>A boxed notice's lines folded under the line that opens it.</summary>
/// <param name="Heading">The opening line's sequence, which carries the toggle.</param>
/// <param name="Lines">The sequences of the lines folded under it, closing rule included.</param>
/// <param name="Banner">The subject from <see cref="ConsoleLineClassifier.Notices"/> the box opened with.</param>
/// <param name="FoldedAtFirst">Whether the box is folded until the viewer chooses otherwise.</param>
public sealed record FoldedNotice(long Heading, IReadOnlyList<long> Lines, string Banner, bool FoldedAtFirst);

/// <summary>
/// Which lines of Bedrock's boxed notices the console folds under the notice's opening
/// line: the TELEMETRY MESSAGE a server not sending telemetry prints on every start
/// (<c>ON2</c>), folded at first, and the ALLOW LIST WARNING and TRANSPORT TYPE ERROR,
/// open at first (<c>T406</c>). The opening line's toggle shows or hides the box.
/// </summary>
/// <remarks>
/// The box is the lines after the opening line that read as part of it, up to and
/// including the closing rule of <c>=</c>: for the telemetry message the lines the
/// classifier dims, for a warning or an error the lines at the opening line's level.
/// Anything else ends it, so a box whose rule has not arrived yet folds what has.
/// Folding is a view decision, like <see cref="ConsoleSaveFold"/>: the buffer and the
/// transcript keep every line. The viewer's choice is held per instance and banner, not
/// per box, so it reaches every start's box and the next start's too (<see cref="ConsoleNoticeChoices"/>).
/// </remarks>
public sealed class ConsoleNoticeFold
{
    private readonly Dictionary<long, FoldedNotice> under = [];
    private readonly Dictionary<long, FoldedNotice> boxOf = [];

    private ConsoleNoticeFold()
    {
    }

    /// <summary>Finds the boxes in <paramref name="lines"/>, which are in buffer order.</summary>
    /// <param name="lines">The lines as shown, after filtering.</param>
    public static ConsoleNoticeFold Of(IEnumerable<ConsoleLine> lines)
    {
        var fold = new ConsoleNoticeFold();
        ConsoleLine? heading = null;
        (string Banner, bool FoldedAtFirst) notice = (string.Empty, false);
        List<long> body = [];

        void Close()
        {
            if (heading is { } owner && body.Count > 0)
            {
                var box = new FoldedNotice(owner.Sequence, body, notice.Banner, notice.FoldedAtFirst);
                fold.under[owner.Sequence] = box;
                foreach (var sequence in body)
                {
                    fold.boxOf[sequence] = box;
                }
            }

            heading = null;
            body = [];
        }

        foreach (var line in lines)
        {
            if (line.Kind is ConsoleLineKind.Blank)
            {
                continue;
            }

            var opens = ConsoleLineClassifier.NoticeOpenedBy(line.Message);

            if (heading is { } open && opens is null && BelongsTo(open, line))
            {
                body.Add(line.Sequence);
                if (ConsoleLineClassifier.IsNoticeRule(line.Message))
                {
                    Close();
                }

                continue;
            }

            Close();

            if (opens is not null && Folds(line))
            {
                heading = line;
                notice = ConsoleLineClassifier.Notices.First(n => n.Banner == opens);
            }
        }

        Close();
        return fold;
    }

    /// <summary>
    /// A dimmed opening line, or one printed as a warning or an error. A banner on an
    /// ordinary line is not a box this knows the shape of.
    /// </summary>
    private static bool Folds(ConsoleLine line) =>
        line.Kind is ConsoleLineKind.Housekeeping || line.Level is BdsLogLevel.Warn or BdsLogLevel.Error;

    /// <summary>
    /// A dimmed box goes on while the classifier dims; a warning or an error goes on while
    /// the line stays at its level, carried onto a wrapped row or printed with the prefix.
    /// </summary>
    private static bool BelongsTo(ConsoleLine heading, ConsoleLine line) =>
        heading.Kind is ConsoleLineKind.Housekeeping
            ? line.Kind is ConsoleLineKind.Housekeeping
            : line.Level == heading.Level;

    /// <summary>The notice folded under <paramref name="line"/>, or null.</summary>
    public FoldedNotice? Under(ConsoleLine line) => under.GetValueOrDefault(line.Sequence);

    /// <summary>The toggle's words: what clicking it will do.</summary>
    public static string Label(bool open) => open ? "Hide notice" : "Show notice";

    /// <summary>Whether <paramref name="notice"/> is shown under the viewer's <paramref name="choices"/>.</summary>
    public static bool IsOpen(FoldedNotice notice, ConsoleNoticeChoices choices) =>
        choices.Open(notice.Banner) ?? !notice.FoldedAtFirst;

    /// <summary>True when <paramref name="line"/> is inside a box that is folded under <paramref name="choices"/>.</summary>
    public bool Hides(ConsoleLine line, ConsoleNoticeChoices choices) =>
        boxOf.TryGetValue(line.Sequence, out var box) && !IsOpen(box, choices);
}
