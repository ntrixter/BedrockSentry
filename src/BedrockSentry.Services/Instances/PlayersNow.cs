using BedrockSentry.Core.Players;

namespace BedrockSentry.Services.Instances;

/// <summary>
/// Who is on one server right now: the players online, the server's <c>max-players</c>,
/// and which of those online are operators on it.
/// </summary>
/// <remarks>
/// The instance page's control row and the Status tab's Players card both show this
/// count on one page, so both read it here. A number on a page twice is a number that
/// one day disagrees with itself (<c>T89</c>) unless one reader serves both.
/// </remarks>
/// <param name="Online">Everybody online, operators included.</param>
/// <param name="MaxPlayers">From the same reader as the start pre-flight; null when unset or unreadable.</param>
/// <param name="Operators">
/// The names of those in <paramref name="Online"/> who are operators, as the player lists
/// show them, in order; null when it was not asked for, or when <c>server.properties</c>
/// or <c>permissions.json</c> could not be read.
/// </param>
/// <remarks>
/// A list makes the record compare by reference, so compare its members, not the record.
/// </remarks>
public sealed record PlayersNow(int Online, int? MaxPlayers, IReadOnlyList<string>? Operators)
{
    /// <summary>Reads the count, the maximum and, when asked, the operators among them.</summary>
    /// <remarks>
    /// A player store that cannot be reached reads as nobody online, as the Status card
    /// always has: every other fact on the page comes from somewhere else.
    /// </remarks>
    public static Task<PlayersNow> ReadAsync(
        IPlayerStore players,
        string instanceId,
        string workingDirectory,
        bool countOperators,
        CancellationToken cancellationToken = default) =>
        ReadAsync(players, instanceId, workingDirectory, countOperators, ServerProperties.ReadFile, cancellationToken);

    /// <summary>The same, with the <c>server.properties</c> reader a seam: an unreadable file cannot be arranged on every platform.</summary>
    internal static async Task<PlayersNow> ReadAsync(
        IPlayerStore players,
        string instanceId,
        string workingDirectory,
        bool countOperators,
        Func<string, PropertiesFile> readProperties,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OnlinePlayer> online;

        try
        {
            online = await players.OnlineAsync(instanceId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            online = [];
        }

        // The port model's reader rather than a second one: the Status card's play-port
        // advice reads max-players from it too, and two readers can disagree about a
        // duplicated line.
        var maxPlayers = PortInspector.ModelOf(workingDirectory).MaxPlayers;

        var operators = countOperators ? OperatorsOnline(online, workingDirectory, readProperties) : null;

        return new PlayersNow(online.Count, maxPlayers, operators);
    }

    /// <summary>
    /// Operators among <paramref name="online"/>, by the Players tab's own rule; null when
    /// either file that decides it could not be read.
    /// </summary>
    private static IReadOnlyList<string>? OperatorsOnline(
        IReadOnlyList<OnlinePlayer> online,
        string workingDirectory,
        Func<string, PropertiesFile> readProperties)
    {
        if (online.Count == 0)
        {
            return [];
        }

        var properties = readProperties(ServerProperties.PathFor(workingDirectory));

        if (!properties.Known)
        {
            // The default tier is unknown, so anybody without an entry could be either.
            return null;
        }

        IReadOnlyList<PermissionEntry> permissions;

        try
        {
            permissions = PermissionsFile.Load(PermissionsFile.PathFor(workingDirectory)).Entries;
        }
        catch (Exception)
        {
            // Missing reads as empty; anything else is a file nobody here can count from.
            return null;
        }

        var tierOf = InstanceAccessView.TierLookup(permissions, InstanceAccessView.DefaultTier(properties.Values));

        // The name the player lists show: the nickname when one is set (PlayerProfile.Display).
        return online
            .Where(p => InstanceAccessView.IsOperator(tierOf(p.Xuid)))
            .Select(p => string.IsNullOrWhiteSpace(p.Nickname) ? p.Gamertag : p.Nickname)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
