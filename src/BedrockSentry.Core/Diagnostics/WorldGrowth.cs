namespace BedrockSentry.Core.Diagnostics;

/// <summary>One measurement of a world's size on disk (<c>D48</c>).</summary>
public readonly record struct WorldSample(DateTimeOffset TakenUtc, long WorldBytes);

/// <summary>How much a world changed between two samples.</summary>
/// <param name="FromUtc">When the earlier sample was taken.</param>
/// <param name="ToUtc">When the later one was.</param>
/// <param name="Bytes">Later minus earlier: negative when the world shrank.</param>
public readonly record struct WorldGrowthStep(DateTimeOffset FromUtc, DateTimeOffset ToUtc, long Bytes)
{
    /// <summary>How long the step covers.</summary>
    public TimeSpan Over => ToUtc - FromUtc;
}

/// <summary>
/// The last day of a world's size samples (<c>D48</c>): a ring of
/// <see cref="Capacity"/> samples, one per walk, kept for <see cref="Window"/>.
/// </summary>
/// <remarks>
/// <para><strong>Size deltas, never file counts.</strong> A Bedrock world is LevelDB and its
/// SST files are created and deleted, never modified, on compaction's own schedule; a count
/// would report compaction rather than players (<c>T95</c>, <c>T31</c>). The delta between
/// walks costs nothing extra, because the walk already happens.</para>
///
/// <para><strong>Negatives are kept.</strong> Compaction shrinks a world and a backup's
/// <c>save hold</c> flush makes a burst, so a step can be below zero for reasons that say
/// nothing about players. Clipping them would draw a graph that lies in the tidy
/// direction.</para>
///
/// <para>Immutable, so a page reading it while a walk lands sees one history or the
/// other and never half of each.</para>
/// </remarks>
public sealed class WorldGrowthHistory
{
    /// <summary>A day of fifteen-minute walks.</summary>
    public const int Capacity = 96;

    /// <summary>How far back a sample is kept.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>Nothing measured yet.</summary>
    public static readonly WorldGrowthHistory Empty = new([]);

    private WorldGrowthHistory(IReadOnlyList<WorldSample> samples) => Samples = samples;

    /// <summary>Oldest first.</summary>
    public IReadOnlyList<WorldSample> Samples { get; }

    /// <summary>
    /// This history with <paramref name="sample"/> added, the samples older than
    /// <see cref="Window"/> before it dropped, and at most <see cref="Capacity"/> kept.
    /// </summary>
    /// <remarks>
    /// A sample no later than the newest is ignored: two walks that finish out of order
    /// would otherwise draw a step backwards in time.
    /// </remarks>
    public WorldGrowthHistory With(WorldSample sample)
    {
        if (Samples.Count > 0 && sample.TakenUtc <= Samples[^1].TakenUtc)
        {
            return this;
        }

        var cutoff = sample.TakenUtc - Window;
        var kept = Samples.Where(s => s.TakenUtc >= cutoff).Append(sample).ToList();

        return new WorldGrowthHistory(kept.Count > Capacity ? kept[^Capacity..] : kept);
    }

    /// <summary>The change between each pair of neighbouring samples, oldest first.</summary>
    public IReadOnlyList<WorldGrowthStep> Steps =>
        [.. Samples.Zip(Samples.Skip(1), (a, b) => new WorldGrowthStep(a.TakenUtc, b.TakenUtc, b.WorldBytes - a.WorldBytes))];

    /// <summary>The newest step, or null with fewer than two samples.</summary>
    public WorldGrowthStep? Latest =>
        Samples.Count < 2 ? null : new WorldGrowthStep(Samples[^2].TakenUtc, Samples[^1].TakenUtc, Samples[^1].WorldBytes - Samples[^2].WorldBytes);

    /// <summary>From the oldest sample kept to the newest, or null with fewer than two.</summary>
    public WorldGrowthStep? Whole =>
        Samples.Count < 2 ? null : new WorldGrowthStep(Samples[0].TakenUtc, Samples[^1].TakenUtc, Samples[^1].WorldBytes - Samples[0].WorldBytes);
}

/// <summary>What the Status tab says about a world's growth (<c>D48</c>).</summary>
/// <remarks>
/// "World grew 40 MB", never an activity score: a proxy that says what it measured
/// degrades gracefully when it is wrong, and an invented percentage does not.
/// </remarks>
public static class WorldGrowthText
{
    /// <summary>One step as a sentence: <c>grew 40 MB in 15 min</c>.</summary>
    public static string Step(WorldGrowthStep step) =>
        $"{Change(step.Bytes)} in {Span(step.Over)}";

    /// <summary>
    /// A span to the minute: <c>15 min</c>, <c>6 h</c>, <c>23 h 45 min</c>. Walks are
    /// fifteen minutes apart, so the seconds <see cref="UptimeFormatter"/> shows would be
    /// the walk's own duration rather than anything about the world.
    /// </summary>
    public static string Span(TimeSpan span)
    {
        var minutes = Math.Max(0, (long)Math.Round(span.TotalMinutes));
        var (hours, rest) = (minutes / 60, minutes % 60);

        return hours == 0 ? $"{rest} min"
            : rest == 0 ? $"{hours} h"
            : $"{hours} h {rest} min";
    }

    /// <summary>
    /// The row's text: the newest step, and the whole history kept when it covers more
    /// than one step.
    /// </summary>
    /// <returns>Null with fewer than two samples, which the page shows as a dash.</returns>
    public static string? Describe(WorldGrowthHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);

        if (history.Latest is not { } latest || history.Whole is not { } whole)
        {
            return null;
        }

        return history.Samples.Count == 2
            ? $"World {Step(latest)}"
            : $"World {Step(latest)}; {Step(whole)}";
    }

    private static string Change(long bytes) => bytes switch
    {
        > 0 => $"grew {ByteSize.Format(bytes)}",
        < 0 => $"shrank {ByteSize.Format(-bytes)}",
        _ => "did not change",
    };
}

/// <summary>One bar of a sparkline, in the drawing's own units.</summary>
/// <param name="Below">True for a value under zero, drawn under the baseline.</param>
public readonly record struct SparkBar(double X, double Y, double Width, double Height, bool Below);

/// <summary>A sparkline of signed values as bars about a zero baseline (<c>D48</c>).</summary>
/// <param name="Baseline">Where zero is, from the top.</param>
public sealed record Sparkline(IReadOnlyList<SparkBar> Bars, double Baseline, double Width, double Height)
{
    /// <summary>
    /// Bars for <paramref name="values"/>, scaled so the largest reaches the edge on its
    /// side, with the baseline placed so negatives have room below it.
    /// </summary>
    /// <remarks>
    /// The baseline sits where zero falls between the smallest and largest value, so a
    /// history with no negatives draws on the bottom edge and one with a single dip still
    /// shows it. All zeros draw a baseline in the middle and no bars, which is a flat
    /// world rather than a broken graph.
    /// </remarks>
    public static Sparkline Of(IReadOnlyList<long> values, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(values);

        var high = Math.Max(0, values.Count == 0 ? 0 : values.Max());
        var low = Math.Min(0, values.Count == 0 ? 0 : values.Min());
        var range = (double)high - low;

        if (range == 0)
        {
            return new Sparkline([], height / 2, width, height);
        }

        var baseline = height * high / range;
        var slot = width / values.Count;
        var bars = new List<SparkBar>(values.Count);

        for (var i = 0; i < values.Count; i++)
        {
            var size = Math.Abs(values[i]) / range * height;

            if (size == 0)
            {
                continue;
            }

            bars.Add(values[i] > 0
                ? new SparkBar(i * slot, baseline - size, slot, size, false)
                : new SparkBar(i * slot, baseline, slot, size, true));
        }

        return new Sparkline(bars, baseline, width, height);
    }
}
