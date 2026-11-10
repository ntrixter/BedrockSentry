using System.Text.RegularExpressions;
using BedrockSentry.Core.Addons;

namespace BedrockSentry.Core.Commands;

/// <summary>What the server says about one command when asked <c>help &lt;command&gt;</c>.</summary>
/// <param name="Name">
/// The server's own name for the command, which is not always the name asked about:
/// <c>help alwaysday</c> answers for <c>daylock</c>.
/// </param>
/// <param name="Aliases">The other names it answers to, from <c>(also tp)</c>.</param>
/// <param name="Description">The server's sentence about it; empty when it printed none.</param>
public sealed record CommandHelpReply(string Name, IReadOnlyList<string> Aliases, string Description);

/// <summary>Reads the reply to <c>help &lt;command&gt;</c>.</summary>
/// <remarks>
/// The reply is a head, <c>§eteleport (also tp):</c>, the description on the next
/// <c>§e</c> line, then <c>Usage:</c> and the forms (BDS 1.26.52.3). The head is what
/// makes aliases knowable at all: <c>list</c>, <c>stop</c> and <c>toggledownfall</c> take
/// identical forms and are three commands, while <c>daylock</c> and <c>alwaysday</c> are
/// one.
/// </remarks>
public static partial class CommandHelp
{
    // The colour code is part of the match: "Usage:" also ends in a colon.
    [GeneratedRegex(@"^§e(?<name>\S+?)(?: \(also (?<also>[^)]*)\))?:$", RegexOptions.CultureInvariant)]
    private static partial Regex HeadPattern { get; }

    /// <summary>The reply, or null when it names no command, as for <c>Unknown command</c>.</summary>
    /// <param name="messages">The reply's lines without their <c>[timestamp LEVEL]</c> prefix.</param>
    public static CommandHelpReply? Parse(IEnumerable<string> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        string? name = null;
        IReadOnlyList<string> aliases = [];

        foreach (var message in messages)
        {
            if (name is null)
            {
                var head = HeadPattern.Match(message);
                if (!head.Success)
                {
                    continue;
                }

                name = head.Groups["name"].Value;
                aliases = head.Groups["also"].Success
                    ? [.. head.Groups["also"].Value.Split(", ", StringSplitOptions.RemoveEmptyEntries)]
                    : [];
                continue;
            }

            if (message.StartsWith("§e", StringComparison.Ordinal))
            {
                return new CommandHelpReply(name, aliases, PackDisplayName.WithoutFormatting(message).Trim());
            }

            // A command registered without a description goes straight to its usage.
            if (message.StartsWith("Usage:", StringComparison.Ordinal) || message.StartsWith("- /", StringComparison.Ordinal))
            {
                break;
            }
        }

        return name is null ? null : new CommandHelpReply(name, aliases, string.Empty);
    }
}
