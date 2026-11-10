using System.Globalization;

namespace BedrockSentry.Core.Commands;

/// <summary>What a throwaway run sends, and how its transcript becomes commands.</summary>
/// <remarks>
/// <para>A run for a version sends, in order: <c>help 1</c>, every other page, <c>help
/// &lt;command&gt;</c> for every name the pages list, a bare <c>gamerule</c>, and each
/// option probe. A run for an instance's add-ons sends the pages and the <c>help</c> of
/// each name the version's own run did not list, and no probes: add-ons do not change
/// the server's option lists.</para>
///
/// <para>Each name is asked about, aliases included, rather than skipping a name an
/// earlier reply already named. The commands are sent in batches, so asking costs a
/// line each and the order of replies needs no bookkeeping.</para>
/// </remarks>
public static class GrammarCapture
{
    /// <summary>The bare gamerule listing.</summary>
    public const string GameruleCommand = "gamerule";

    /// <summary>The command for one help page.</summary>
    public static string PageCommand(int page) =>
        string.Create(CultureInfo.InvariantCulture, $"help {page}");

    /// <summary>The command for one command's description.</summary>
    public static string HelpCommand(string name) => $"help {name}";

    /// <summary>Every probe command, in the order sent.</summary>
    public static IReadOnlyList<string> ProbeCommands() =>
        [.. OptionProbes.All.SelectMany(p => p.Candidates.Select(p.CommandFor)).Distinct(StringComparer.Ordinal)];

    /// <summary>The names the help pages list, in order, each once.</summary>
    public static IReadOnlyList<string> ListedNames(IEnumerable<ConsoleExchange> transcript) =>
        [.. Pages(transcript)
            .Select(HelpLine.Parse)
            .Where(p => p is not null)
            .Select(p => p!.Value.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The commands a version's run read.</summary>
    /// <param name="transcript">Every exchange of the run.</param>
    public static CommandGrammar Version(
        IReadOnlyList<ConsoleExchange> transcript, string bdsVersion, DateTimeOffset capturedUtc, TimeSpan took)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        var commands = Commands(transcript);
        var used = new HashSet<string>(
            commands.SelectMany(c => c.Forms).SelectMany(f => f.Params)
                .Where(p => p.Kind is ParamKind.Typed && p.Type is not null)
                .Select(p => p.Type!),
            StringComparer.Ordinal);

        var byCommand = new Dictionary<string, ConsoleExchange>(StringComparer.Ordinal);
        foreach (var exchange in transcript)
        {
            byCommand.TryAdd(exchange.Command, exchange);
        }

        var options = new List<OptionList>();
        foreach (var probe in OptionProbes.All.Where(p => used.Contains(p.Type)))
        {
            var outcomes = new Dictionary<string, ProbeOutcome>(StringComparer.Ordinal);
            foreach (var candidate in probe.Candidates)
            {
                if (byCommand.TryGetValue(probe.CommandFor(candidate), out var exchange))
                {
                    outcomes[candidate] = OptionProbes.Judge(exchange.Messages, candidate);
                }
            }

            options.Add(OptionProbes.Settle(probe, outcomes));
        }

        foreach (var (type, values) in OptionProbes.Fixed.Where(f => used.Contains(f.Key)))
        {
            options.Add(new OptionList(type, values, true));
        }

        var gamerules = transcript
            .Where(e => e.Command == GameruleCommand)
            .Select(e => GameruleList.Parse(e.Messages))
            .FirstOrDefault(r => r.Count > 0) ?? [];

        return new CommandGrammar(
            bdsVersion, capturedUtc, took, commands,
            [.. options.OrderBy(o => o.Type, StringComparer.Ordinal)], gamerules);
    }

    /// <summary>The commands a run's pages and <c>help &lt;command&gt;</c> replies describe.</summary>
    public static IReadOnlyList<GrammarCommand> Commands(IEnumerable<ConsoleExchange> transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        var list = transcript as IReadOnlyList<ConsoleExchange> ?? [.. transcript];
        var replies = list
            .Where(e => IsHelpFor(e.Command) is not null)
            .Select(e => CommandHelp.Parse(e.Messages))
            .Where(r => r is not null)
            .Select(r => r!);

        return GrammarBuilder.Build(Pages(list), replies);
    }

    private static IEnumerable<string> Pages(IEnumerable<ConsoleExchange> transcript) =>
        transcript.Where(e => IsPage(e.Command)).SelectMany(e => HelpPages.Forms(e.Messages));

    private static bool IsPage(string command) =>
        command.StartsWith("help ", StringComparison.Ordinal)
        && int.TryParse(command.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out _);

    private static string? IsHelpFor(string command) =>
        command.StartsWith("help ", StringComparison.Ordinal) && !IsPage(command) ? command[5..] : null;
}
