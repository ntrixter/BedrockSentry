using System.Globalization;

namespace BedrockSentry.Core.Commands;

/// <summary>What a gamerule's value is.</summary>
public enum GameRuleKind
{
    /// <summary><c>true</c> or <c>false</c>: the server's <c>BoolGameRule</c>.</summary>
    Boolean,

    /// <summary>A whole number: the server's <c>IntGameRule</c>.</summary>
    Integer,

    /// <summary>Anything else, such as <c>playerWaypoints = everyone</c>, which has forms of its own.</summary>
    Other,
}

/// <summary>One gamerule as the server listed it.</summary>
/// <param name="Name">As printed: <c>doDayLightCycle</c>.</param>
public sealed record GameRule(string Name, GameRuleKind Kind);

/// <summary>Reads the server's reply to a bare <c>gamerule</c>.</summary>
/// <remarks>
/// BDS 1.26.52.3 prints every rule and its value on one line,
/// <c>commandBlockOutput = true, doDayLightCycle = true, …</c>: 39 rules. The help forms
/// say only <c>&lt;rule: BoolGameRule&gt;</c> and <c>&lt;rule: IntGameRule&gt;</c>, so this
/// line is where the names come from, and the value says which list a rule is in.
/// </remarks>
public static class GameruleList
{
    /// <summary>The rules on the first line of the reply that lists any, or none.</summary>
    /// <param name="messages">The reply's lines without their <c>[timestamp LEVEL]</c> prefix.</param>
    public static IReadOnlyList<GameRule> Parse(IEnumerable<string> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        foreach (var message in messages)
        {
            if (!message.Contains(" = ", StringComparison.Ordinal))
            {
                continue;
            }

            var rules = new List<GameRule>();
            foreach (var pair in message.Split(", ", StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = pair.IndexOf(" = ", StringComparison.Ordinal);
                if (equals <= 0)
                {
                    continue;
                }

                var name = pair[..equals].Trim();
                var value = pair[(equals + 3)..].Trim();
                if (name.Length == 0 || name.Contains(' ', StringComparison.Ordinal))
                {
                    continue;
                }

                rules.Add(new GameRule(name, KindOf(value)));
            }

            return rules;
        }

        return [];
    }

    private static GameRuleKind KindOf(string value) =>
        value is "true" or "false" ? GameRuleKind.Boolean
        : int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _) ? GameRuleKind.Integer
        : GameRuleKind.Other;
}
