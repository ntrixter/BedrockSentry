using System.Globalization;
using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Players;
using Microsoft.Data.Sqlite;

namespace BedrockSentry.Services.Players;

/// <summary>
/// The SQLite player store (<c>docs/design.md</c> § 7.2), and the real implementation
/// behind <see cref="IInstancePlayerData"/> that <see cref="NoPlayerDataStore"/>
/// stood in for.
/// </summary>
/// <remarks>
/// Timestamps are written as round-trip ISO-8601 text. SQLite has no date type,
/// and text in this format sorts correctly as text — so <c>ORDER BY</c> and
/// <c>MIN</c> mean what they look like they mean without a conversion in the
/// query.
/// </remarks>
public sealed class SqlitePlayerStore : IPlayerStore, IInstancePlayerData
{
    private readonly PlayerDatabase _database;

    public SqlitePlayerStore(PlayerDatabase database) => _database = database;

    private static string Stamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ReadStamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public async Task RecordAsync(
        string instanceId,
        PlayerEvent playerEvent,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (playerEvent.Kind == PlayerEventKind.Connected)
        {
            await ConnectAsync(connection, (SqliteTransaction)transaction, instanceId, playerEvent, atUtc, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await DisconnectAsync(connection, (SqliteTransaction)transaction, instanceId, playerEvent, atUtc, cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ConnectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string instanceId,
        PlayerEvent playerEvent,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken)
    {
        // The gamertag is refreshed on every connect because it is the
        // most-recently-seen value (docs/design.md § 7.1); first_seen_utc deliberately is
        // not, because it is the one date nothing is allowed to move (D30).
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO players (xuid, gamertag, first_seen_utc, is_wrapper_admin)
            VALUES ($xuid, $gamertag, $now, 0)
            ON CONFLICT(xuid) DO UPDATE SET gamertag = excluded.gamertag;
            """,
            cancellationToken,
            ("$xuid", playerEvent.Xuid),
            ("$gamertag", playerEvent.Gamertag),
            ("$now", Stamp(atUtc)))
            .ConfigureAwait(false);

        // A connect while a session is already open for this player on this
        // instance means the previous one ended at a time nobody recorded — a
        // missed disconnect, or a restart the wrapper did not see. D26's rule
        // applies: drop it rather than close it at a guessed time, which would
        // silently count the gap between the two connects as playtime.
        await ExecuteAsync(
            connection, transaction,
            """
            DELETE FROM player_sessions
            WHERE xuid = $xuid AND instance_id = $instance AND disconnected_utc IS NULL;
            """,
            cancellationToken,
            ("$xuid", playerEvent.Xuid),
            ("$instance", instanceId))
            .ConfigureAwait(false);

        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO player_sessions (xuid, instance_id, connected_utc)
            VALUES ($xuid, $instance, $now);
            """,
            cancellationToken,
            ("$xuid", playerEvent.Xuid),
            ("$instance", instanceId),
            ("$now", Stamp(atUtc)))
            .ConfigureAwait(false);
    }

    private static async Task DisconnectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string instanceId,
        PlayerEvent playerEvent,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken)
    {
        // Deliberately no insert here. A disconnect with no open session is a
        // player who was already on when the wrapper started watching, and there
        // is no honest connected_utc to give that session — so there is no row.
        await ExecuteAsync(
            connection, transaction,
            "UPDATE players SET gamertag = $gamertag WHERE xuid = $xuid;",
            cancellationToken,
            ("$gamertag", playerEvent.Gamertag),
            ("$xuid", playerEvent.Xuid))
            .ConfigureAwait(false);

        await ExecuteAsync(
            connection, transaction,
            """
            UPDATE player_sessions SET disconnected_utc = $now
            WHERE id = (
                SELECT id FROM player_sessions
                WHERE xuid = $xuid AND instance_id = $instance AND disconnected_utc IS NULL
                ORDER BY connected_utc DESC
                LIMIT 1);
            """,
            cancellationToken,
            ("$now", Stamp(atUtc)),
            ("$xuid", playerEvent.Xuid),
            ("$instance", instanceId))
            .ConfigureAwait(false);
    }

    public async Task<int> CloseOpenSessionsAsync(
        string instanceId, DateTimeOffset endedUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await ExecuteAsync(
            connection, null,
            """
            UPDATE player_sessions SET disconnected_utc = $ended
            WHERE instance_id = $instance AND disconnected_utc IS NULL;
            """,
            cancellationToken,
            ("$ended", Stamp(endedUtc)),
            ("$instance", instanceId))
            .ConfigureAwait(false);
    }

    public async Task<int> DropOpenSessionsAsync(
        string instanceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await ExecuteAsync(
            connection, null,
            "DELETE FROM player_sessions WHERE instance_id = $instance AND disconnected_utc IS NULL;",
            cancellationToken,
            ("$instance", instanceId))
            .ConfigureAwait(false);
    }

    /// <remarks>
    /// Two reads and the aggregation in C#, rather than one query doing sums in
    /// SQL. Deliberate: timestamps are stored as round-trip ISO-8601, which
    /// carries seven fractional digits, and SQLite's own date functions parse at
    /// most three — <c>julianday</c> returns null on our own text rather than
    /// failing, so a SQL sum would quietly report every playtime as zero. At the
    /// scale § 7.2 describes, a handful of players and their sessions, the cost of
    /// doing it here is nothing next to that failure mode.
    /// </remarks>
    public async Task<IReadOnlyList<PlayerSummary>> ListAsync(
        string? instanceId, DateTimeOffset asOfUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);

        var profiles = new Dictionary<string, (string Gamertag, string? Nickname, DateTimeOffset FirstSeen)>(
            StringComparer.Ordinal);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT xuid, gamertag, nickname, first_seen_utc FROM players;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                profiles[reader.GetString(0)] = (
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    ReadStamp(reader.GetString(3)));
            }
        }

        var sessions = new Dictionary<string, List<(string Instance, DateTimeOffset From, DateTimeOffset? To)>>(
            StringComparer.Ordinal);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT xuid, instance_id, connected_utc, disconnected_utc
                FROM player_sessions
                WHERE ($instance IS NULL OR instance_id = $instance);
                """;
            command.Parameters.AddWithValue("$instance", (object?)instanceId ?? DBNull.Value);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var xuid = reader.GetString(0);
                if (!sessions.TryGetValue(xuid, out var list))
                {
                    sessions[xuid] = list = [];
                }

                list.Add((
                    reader.GetString(1),
                    ReadStamp(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : ReadStamp(reader.GetString(3))));
            }
        }

        var summaries = new List<PlayerSummary>();

        foreach (var (xuid, profile) in profiles)
        {
            var played = sessions.GetValueOrDefault(xuid) ?? [];

            // Scoped to one server, a player who has never joined it does not
            // belong on its page at all — not even as an offline row.
            if (instanceId is not null && played.Count == 0)
            {
                continue;
            }

            // Ordered rather than "whichever came back last": the rows arrive in
            // no particular order, and on the global view a player can hold an
            // open session on more than one instance at a time.
            var open = played
                .Where(s => s.To is null)
                .OrderByDescending(s => s.From)
                .Select(s => ((string Instance, DateTimeOffset From)?)(s.Instance, s.From))
                .FirstOrDefault();

            var playtime = TimeSpan.Zero;

            foreach (var (_, from, to) in played)
            {
                // An open session counts up to now rather than to nothing, so a
                // player's total moves while they are still on the server.
                playtime += (to ?? asOfUtc) - from;
            }

            summaries.Add(new PlayerSummary(
                xuid,
                profile.Gamertag,
                profile.Nickname,
                open?.Instance,
                open?.From,
                // D30 globally; derived per instance, because first-seen here and
                // first-seen anywhere are different facts (§ 7.1).
                instanceId is null || played.Count == 0
                    ? profile.FirstSeen
                    : played.Min(s => s.From),
                played.Count == 0 ? null : played.Max(s => s.To ?? s.From),
                playtime,
                played.Count));
        }

        return summaries;
    }

    public async Task<PlayerProfile?> GetAsync(
        string xuid, DateTimeOffset asOfUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);

        string gamertag;
        string? nickname;
        string? notes;
        bool isWrapperAdmin;
        DateTimeOffset firstSeen;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT gamertag, nickname, notes, is_wrapper_admin, first_seen_utc
                FROM players WHERE xuid = $xuid;
                """;
            command.Parameters.AddWithValue("$xuid", xuid);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            gamertag = reader.GetString(0);
            nickname = reader.IsDBNull(1) ? null : reader.GetString(1);
            notes = reader.IsDBNull(2) ? null : reader.GetString(2);
            isWrapperAdmin = reader.GetInt64(3) != 0;
            firstSeen = ReadStamp(reader.GetString(4));
        }

        var byInstance = new Dictionary<string, List<(DateTimeOffset From, DateTimeOffset? To)>>(
            StringComparer.OrdinalIgnoreCase);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT instance_id, connected_utc, disconnected_utc
                FROM player_sessions WHERE xuid = $xuid;
                """;
            command.Parameters.AddWithValue("$xuid", xuid);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var instance = reader.GetString(0);
                if (!byInstance.TryGetValue(instance, out var list))
                {
                    byInstance[instance] = list = [];
                }

                list.Add((
                    ReadStamp(reader.GetString(1)),
                    reader.IsDBNull(2) ? null : ReadStamp(reader.GetString(2))));
            }
        }

        var activity = new List<PlayerInstanceActivity>();

        foreach (var (instance, played) in byInstance)
        {
            var total = TimeSpan.Zero;
            foreach (var (from, to) in played)
            {
                total += (to ?? asOfUtc) - from;
            }

            activity.Add(new PlayerInstanceActivity(
                instance,
                played.Min(s => s.From),
                played.Max(s => s.To ?? s.From),
                total,
                played.Count,
                played.Where(s => s.To is null)
                      .Select(s => (DateTimeOffset?)s.From)
                      .OrderByDescending(s => s)
                      .FirstOrDefault()));
        }

        activity.Sort((a, b) => b.Playtime.CompareTo(a.Playtime));

        return new PlayerProfile(
            xuid,
            gamertag,
            nickname,
            notes,
            isWrapperAdmin,
            // D30. The stored value, not the minimum over what survives — the
            // whole point is that a cleanup rule cannot move it.
            firstSeen,
            activity.Count == 0 ? null : activity.Max(a => a.LastSeenUtc),
            activity.Aggregate(TimeSpan.Zero, (sum, a) => sum + a.Playtime),
            activity.Sum(a => a.SessionCount),
            activity);
    }

    public async Task<bool> UpdateProfileAsync(
        string xuid,
        string? nickname,
        string? notes,
        bool isWrapperAdmin,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Blank and unset are the same thing here. Storing "" would make a
        // cleared field sort and read differently from one never filled in,
        // which is a distinction nobody typing into a text box intended.
        var changed = await ExecuteAsync(
            connection, null,
            """
            UPDATE players
            SET nickname = $nickname, notes = $notes, is_wrapper_admin = $admin
            WHERE xuid = $xuid;
            """,
            cancellationToken,
            ("$nickname", string.IsNullOrWhiteSpace(nickname) ? DBNull.Value : nickname.Trim()),
            ("$notes", string.IsNullOrWhiteSpace(notes) ? DBNull.Value : notes.Trim()),
            ("$admin", isWrapperAdmin ? 1 : 0),
            ("$xuid", xuid))
            .ConfigureAwait(false);

        return changed > 0;
    }

    public async Task<IReadOnlyList<OnlinePlayer>> OnlineAsync(
        string? instanceId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT s.xuid, p.gamertag, p.nickname, s.instance_id, s.connected_utc
            FROM player_sessions s
            JOIN players p ON p.xuid = s.xuid
            WHERE s.disconnected_utc IS NULL
              AND ($instance IS NULL OR s.instance_id = $instance)
            ORDER BY s.instance_id, p.gamertag;
            """;
        command.Parameters.AddWithValue("$instance", (object?)instanceId ?? DBNull.Value);

        var online = new List<OnlinePlayer>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            online.Add(new OnlinePlayer(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                ReadStamp(reader.GetString(4))));
        }

        return online;
    }

    public async Task<bool> AnyActivitySinceAsync(
        string instanceId, DateTimeOffset sinceUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);

        // A session still open counts however old it is — somebody connected
        // before the window began and never leaving is exactly somebody playing.
        // ISO-8601 round-trip text compares correctly as text, which is why the
        // stamps are stored that way.
        var rows = await ScalarAsync(
            connection,
            """
            SELECT COUNT(*) FROM player_sessions
            WHERE instance_id = $instance
              AND (disconnected_utc IS NULL OR disconnected_utc > $since);
            """,
            cancellationToken,
            ("$instance", instanceId),
            ("$since", Stamp(sinceUtc)))
            .ConfigureAwait(false);

        return rows > 0;
    }

    /// <remarks>
    /// One delete, and the cascade takes the sessions and per-instance state with
    /// it — which is why <see cref="PlayerDatabase.OpenAsync"/> turns foreign keys
    /// on. Counted first so the caller can say how much went.
    /// </remarks>
    public async Task<int> ForgetPlayerAsync(
        string xuid, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await ScalarAsync(
            connection,
            """
            SELECT (SELECT COUNT(*) FROM players               WHERE xuid = $xuid)
                 + (SELECT COUNT(*) FROM player_sessions       WHERE xuid = $xuid)
                 + (SELECT COUNT(*) FROM player_instance_state WHERE xuid = $xuid);
            """,
            cancellationToken,
            ("$xuid", xuid))
            .ConfigureAwait(false);

        await ExecuteAsync(
            connection, null, "DELETE FROM players WHERE xuid = $xuid;", cancellationToken, ("$xuid", xuid))
            .ConfigureAwait(false);

        return rows;
    }

    /// <remarks>
    /// Sessions and per-instance state only. The profiles themselves are global
    /// and survive — a person known to this install does not stop being known
    /// because one server they played on was removed.
    /// </remarks>
    public async Task<int> CountAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await ScalarAsync(
            connection,
            """
            SELECT (SELECT COUNT(*) FROM player_sessions       WHERE instance_id = $instance)
                 + (SELECT COUNT(*) FROM player_instance_state WHERE instance_id = $instance);
            """,
            cancellationToken,
            ("$instance", instanceId))
            .ConfigureAwait(false);
    }

    public async Task<int> ForgetAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        var removed = await CountAsync(instanceId, cancellationToken).ConfigureAwait(false);

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection, (SqliteTransaction)transaction,
            "DELETE FROM player_sessions WHERE instance_id = $instance;",
            cancellationToken, ("$instance", instanceId))
            .ConfigureAwait(false);

        await ExecuteAsync(
            connection, (SqliteTransaction)transaction,
            "DELETE FROM player_instance_state WHERE instance_id = $instance;",
            cancellationToken, ("$instance", instanceId))
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return removed;
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ScalarAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }
}
