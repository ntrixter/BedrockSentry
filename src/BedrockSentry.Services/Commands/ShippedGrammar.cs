using BedrockSentry.Core.Commands;
using BedrockSentry.Core.Instances;

namespace BedrockSentry.Services.Commands;

/// <summary>The commands that ship with BedrockSentry, for a server no run has read yet (<c>D105</c>).</summary>
/// <remarks>
/// Built at first use from the transcript of a run on BDS 1.26.52.3, by the same reader
/// a run on this machine goes through, so the list completion falls back on is the
/// server's own words rather than a second copy of them. The Commands view labels it
/// as shipped whatever version it is used for.
/// </remarks>
public static class ShippedGrammar
{
    /// <summary>The version the shipped run was made on.</summary>
    public const string Version = "1.26.52.3";

    private const string Resource = "BedrockSentry.Commands.capture-1.26.52.3.txt";

    private static readonly Lazy<(CommandGrammar Grammar, IReadOnlyList<ConsoleExchange> Transcript)> Loaded = new(Load);

    /// <summary>The shipped commands.</summary>
    public static CommandGrammar Grammar => Loaded.Value.Grammar;

    /// <summary>The run they were read from.</summary>
    public static IReadOnlyList<ConsoleExchange> Transcript => Loaded.Value.Transcript;

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlySet<string>>> LoadedRefusals =
        new(() => MeasuredRefusals.Parse(ReadResource("BedrockSentry.Commands.refused-1.26.52.3.txt")));

    /// <summary>The names in Mojang's data that BDS 1.26.52.3 refused, by section (<see cref="MeasuredRefusals"/>).</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Refusals => LoadedRefusals.Value;

    private static string ReadResource(string name)
    {
        using var stream = typeof(ShippedGrammar).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The resource {name} is missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static (CommandGrammar, IReadOnlyList<ConsoleExchange>) Load()
    {
        var transcript = CaptureTranscript.Read(new StringReader(ReadResource(Resource)));

        // The run's own clock, from the stamps on its replies. Only the version is ever
        // shown for this list, so the stamps are read as UTC without asking whose zone
        // they were written in.
        var stamps = transcript
            .SelectMany(e => e.Reply)
            .Select(l => BdsLogParser.Parse(l).Timestamp)
            .Where(t => t is not null)
            .Select(t => t!.Value)
            .ToList();
        var captured = stamps.Count > 0
            ? new DateTimeOffset(DateTime.SpecifyKind(stamps.Max(), DateTimeKind.Utc))
            : DateTimeOffset.UnixEpoch;
        var took = stamps.Count > 0 ? stamps.Max() - stamps.Min() : TimeSpan.Zero;

        return (GrammarCapture.Version(transcript, Version, captured, took), transcript);
    }
}
