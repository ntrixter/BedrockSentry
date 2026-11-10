using System.Text.Json.Serialization;

namespace BedrockSentry.Core.Commands;

/// <summary>One command the server takes, under the server's own name for it.</summary>
/// <param name="Name">The name <c>help &lt;command&gt;</c> answers with: <c>daylock</c>, not <c>alwaysday</c>.</param>
/// <param name="Description">The server's sentence about it; empty when it has none.</param>
/// <param name="Forms">Every way to write it, in the order <c>help</c> lists them.</param>
/// <param name="Aliases">The other names it answers to, sorted.</param>
public sealed record GrammarCommand(
    string Name, string Description, IReadOnlyList<CommandForm> Forms, IReadOnlyList<string> Aliases);

/// <summary>What a throwaway copy of one server version said about its commands.</summary>
/// <param name="BdsVersion">The version the copy reported at its start.</param>
/// <param name="CapturedUtc">When the run finished.</param>
/// <param name="Took">How long the run took, start to stop.</param>
/// <param name="Commands">Every command, sorted by name.</param>
/// <param name="Options">Every option list, probed or fixed.</param>
/// <param name="GameRules">The rules a bare <c>gamerule</c> listed.</param>
public sealed record CommandGrammar(
    string BdsVersion,
    DateTimeOffset CapturedUtc,
    TimeSpan Took,
    IReadOnlyList<GrammarCommand> Commands,
    IReadOnlyList<OptionList> Options,
    IReadOnlyList<GameRule> GameRules)
{
    /// <summary>Forms across every command.</summary>
    [JsonIgnore]
    public int FormCount => Commands.Sum(c => c.Forms.Count);

    /// <summary>Aliases across every command.</summary>
    [JsonIgnore]
    public int AliasCount => Commands.Sum(c => c.Aliases.Count);
}

/// <summary>Puts the server's listing and its replies together into commands.</summary>
public static class GrammarBuilder
{
    /// <summary>The commands, from every form line and every <c>help &lt;command&gt;</c> reply.</summary>
    /// <remarks>
    /// <para><strong>Aliases come from the replies, never from the forms.</strong> Two names
    /// with identical forms are not one command: <c>list</c>, <c>stop</c> and
    /// <c>toggledownfall</c> take nothing alike, and <c>op</c> and <c>deop</c> take one
    /// player each. A reply's head says which names are one command, and its first name
    /// is the one shown.</para>
    ///
    /// <para>A name with no reply is still a command, with no description: the listing is
    /// what says the server takes it.</para>
    /// </remarks>
    public static IReadOnlyList<GrammarCommand> Build(
        IEnumerable<string> formLines, IEnumerable<CommandHelpReply> replies)
    {
        ArgumentNullException.ThrowIfNull(formLines);
        ArgumentNullException.ThrowIfNull(replies);

        var forms = new Dictionary<string, List<CommandForm>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var line in formLines)
        {
            if (HelpLine.Parse(line) is not { } parsed)
            {
                continue;
            }

            var (name, form) = parsed;
            if (!forms.TryGetValue(name, out var list))
            {
                forms[name] = list = [];
                order.Add(name);
            }

            if (!list.Any(f => f.Line == form.Line))
            {
                list.Add(form);
            }
        }

        var aliasOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var named = new List<string>();
        foreach (var reply in replies)
        {
            descriptions.TryAdd(reply.Name, reply.Description);
            named.Add(reply.Name);
            foreach (var alias in reply.Aliases)
            {
                if (!string.Equals(alias, reply.Name, StringComparison.OrdinalIgnoreCase))
                {
                    aliasOf.TryAdd(alias, reply.Name);
                }
            }
        }

        var canonical = order.Concat(named)
            .Where(n => !aliasOf.ContainsKey(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal);

        var commands = new List<GrammarCommand>();
        foreach (var name in canonical)
        {
            var aliases = aliasOf
                .Where(a => string.Equals(a.Value, name, StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Key)
                .Order(StringComparer.Ordinal)
                .ToList();

            // Listed only under an alias: the server takes the alias's forms under both names.
            var own = forms.GetValueOrDefault(name)
                ?? aliases.Select(forms.GetValueOrDefault).FirstOrDefault(f => f is not null)
                ?? [];

            commands.Add(new GrammarCommand(name, descriptions.GetValueOrDefault(name, string.Empty), own, aliases));
        }

        return commands;
    }

    /// <summary>The commands in <paramref name="withAddons"/> that the version's own server does not have.</summary>
    /// <remarks>
    /// An add-on's commands are told apart by difference, not by spelling: a namespace such
    /// as <c>example:home</c> is how add-ons write them, but its alias <c>home</c> has
    /// none, and the difference needs no rule about either.
    /// </remarks>
    public static IReadOnlyList<GrammarCommand> AddedTo(
        IReadOnlyList<GrammarCommand> withAddons, CommandGrammar vanilla)
    {
        ArgumentNullException.ThrowIfNull(withAddons);
        ArgumentNullException.ThrowIfNull(vanilla);

        var known = new HashSet<string>(
            vanilla.Commands.SelectMany(c => c.Aliases.Prepend(c.Name)), StringComparer.OrdinalIgnoreCase);

        return [.. withAddons.Where(c => !known.Contains(c.Name))];
    }
}
