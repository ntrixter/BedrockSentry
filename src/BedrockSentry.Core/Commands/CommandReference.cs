namespace BedrockSentry.Core.Commands;

/// <summary>The filter on the Commands view.</summary>
public static class CommandReference
{
    /// <summary>Whether a command shows under <paramref name="filter"/>.</summary>
    /// <remarks>
    /// The filter is matched, ignoring case, against everything the row shows: the name,
    /// its aliases, the description, every form line and the add-on it comes from. So
    /// <c>Easing</c> finds every command with an ease type, and <c>tp</c> finds
    /// <c>teleport</c> through its alias. An empty filter shows everything.
    /// </remarks>
    /// <param name="addon">The add-on the command comes from, or null for the server's own.</param>
    public static bool Matches(GrammarCommand command, string? filter, string? addon = null)
    {
        ArgumentNullException.ThrowIfNull(command);

        var needle = filter?.Trim();
        if (string.IsNullOrEmpty(needle))
        {
            return true;
        }

        return Contains(command.Name)
            || command.Aliases.Any(Contains)
            || Contains(command.Description)
            || command.Forms.Any(f => Contains(f.Line))
            || (addon is not null && Contains(addon));

        bool Contains(string text) => text.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }
}
