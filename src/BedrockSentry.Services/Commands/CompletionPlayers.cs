using BedrockSentry.Core.Players;
using BedrockSentry.Services.Instances;

namespace BedrockSentry.Services.Commands;

/// <summary>The player names completion offers one instance (<c>D105</c>).</summary>
/// <param name="Online">Who is on this server now: wherever a player or a selector goes.</param>
/// <param name="Known">Everyone BedrockSentry has seen on any server, for <c>allowlist add</c>.</param>
/// <param name="Allowlist">The names on this server's allowlist, or null when it could not be read.</param>
public sealed record CompletionPlayers(IReadOnlyList<string> Online, IReadOnlyList<string> Known, IReadOnlyList<string>? Allowlist)
{
    /// <summary>Players seen anywhere who are not on the allowlist, the count the Commands view gives.</summary>
    public int SeenNotListed =>
        Known.Count(n => Allowlist is null || !Allowlist.Contains(n, StringComparer.OrdinalIgnoreCase));

    /// <summary>Reads them; a player store that cannot be reached reads as nobody.</summary>
    /// <remarks>
    /// The allowlist is the server's own file, read as the server wrote it. The server
    /// types the name <c>allowlist</c> takes as a plain string, so these are the only
    /// names that can be offered there: the players seen who are not on it for
    /// <c>add</c>, and the names on it for <c>remove</c>.
    /// </remarks>
    public static async Task<CompletionPlayers> ReadAsync(
        IPlayerStore players, InstanceSupervisor supervisor, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(supervisor);

        IReadOnlyList<string> online = [];
        IReadOnlyList<string> known = [];
        try
        {
            online = [.. (await players.OnlineAsync(supervisor.InstanceId, cancellationToken).ConfigureAwait(false))
                .Select(p => p.Gamertag)
                .Distinct(StringComparer.OrdinalIgnoreCase)];
            known = [.. (await players.ListAsync(null, now, cancellationToken).ConfigureAwait(false))
                .Select(p => p.Gamertag)
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Names are a convenience; a store that cannot answer leaves them empty.
        }

        IReadOnlyList<string>? allowlist;
        try
        {
            allowlist = [.. AllowlistFile.Load(AllowlistFile.PathFor(supervisor.WorkingDirectory)).Entries
                .Select(e => e.Name)
                .Where(n => n.Length > 0)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException)
        {
            allowlist = null;
        }

        return new CompletionPlayers(online, known, allowlist);
    }
}
