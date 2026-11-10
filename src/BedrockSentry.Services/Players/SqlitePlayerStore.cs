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

    /// <summary>How every timestamp column is written, by this store and by an import.</summary>
    internal static string Stamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ReadStamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>A session's four exit columns, in the order <see cref="ReadExit"/> reads them.</summary>
    private const string ExitColumns = "exit_x, exit_y, exit_z, exit_dimension";

    /// <summary>The exit position starting at column <paramref name="first"/>, or null unless all four are set.</summary>
    private static BlockPosition? ReadExit(SqliteDataReader reader, int first) =>
        reader.IsDBNull(first) || reader.IsDBNull(first + 1)
        || reader.IsDBNull(first + 2) || reader.IsDBNull(first + 3)
            ? null
            : new BlockPosition(
                reader.GetInt32(first), reader.GetInt32(first + 1), reader.GetInt32(first + 2),
                reader.GetString(first + 3));

    /// <summary>The exit of the latest session, or null when that session has none.</summary>
    /// <remarks>
    /// The latest session's, not the latest one that has a position: an older
    /// position beside a newer last-seen time would say where somebody was on a
    /// day the page does not name.
    /// </remarks>
    private static BlockPosition? LatestExit(IEnumerable<(DateTimeOffset From, BlockPosition? Exit)> sessions) =>
        sessions.OrderByDescending(s => s.From).Select(s => s.Exit).FirstOrDefault();

    /// <summary>
    /// When the companion pack first reported on one server, and every player's
    /// recorded deaths there.
    /// </summary>
    private static async Task<(DateTimeOffset? Since, Dictionary<string, int> Deaths)> DeathsOnAsync(
        SqliteConnection connection, string instanceId, CancellationToken cancellationToken)
    {
        DateTimeOffset? since;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT MIN(reported_utc) FROM companion_reports WHERE instance_id = $instance;";
            command.Parameters.AddWithValue("$instance", instanceId);

            since = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string stamp
                ? ReadStamp(stamp)
                : null;
        }

        var deaths = new Dictionary<string, int>(StringComparer.Ordinal);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT xuid, COUNT(*) FROM player_deaths WHERE instance_id = $instance GROUP BY xuid;";
            command.Parameters.AddWithValue("$instance", instanceId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                deaths[reader.GetString(0)] = reader.GetInt32(1);
            }
        }

        return (since, deaths);
    }

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

        // The name on this session's own connect line, which is what a relayed
        // companion line is matched against (CompanionAttribution).
        await ExecuteAsync(
            connection, transaction,
            """
            INSERT INTO player_sessions (xuid, instance_id, connected_utc, gamertag)
            VALUES ($xuid, $instance, $now, $gamertag);
            """,
            cancellationToken,
            ("$xuid", playerEvent.Xuid),
            ("$instance", instanceId),
            ("$now", Stamp(atUtc)),
            ("$gamertag", playerEvent.Gamertag))
            .ConfigureAwait(false);
    }

    public async Task<RelayAttribution> RecordDeathAsync(
        string instanceId,
        DateTimeOffset runStartedUtc,
        CompanionDeath death,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(death);

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var tx = (SqliteTransaction)transaction;

        // A death needs only the XUID, so the question is how many PEOPLE match,
        // not how many sessions: a fast rejoin is one person with two sessions.
        var candidates = await CandidatesAsync(
            connection, tx, instanceId, runStartedUtc, death.Gamertag, atUtc,
            includeRecentlyClosed: true, cancellationToken).ConfigureAwait(false);

        var people = candidates.Select(c => c.Xuid).Distinct(StringComparer.Ordinal).ToList();

        if (people.Count != 1)
        {
            return people.Count == 0 ? RelayAttribution.NoSession : RelayAttribution.Ambiguous;
        }

        await ExecuteAsync(
            connection, tx,
            """
            INSERT INTO player_deaths
                (xuid, instance_id, died_utc, cause, killer_type, killer_name, x, y, z, dimension, message)
            VALUES ($xuid, $instance, $at, $cause, $killerType, $killerName, $x, $y, $z, $dimension, $message);
            """,
            cancellationToken,
            ("$xuid", people[0]),
            ("$instance", instanceId),
            ("$at", Stamp(atUtc)),
            ("$cause", death.Cause),
            ("$killerType", (object?)death.KillerType ?? DBNull.Value),
            ("$killerName", (object?)death.KillerName ?? DBNull.Value),
            ("$x", death.Position.X),
            ("$y", death.Position.Y),
            ("$z", death.Position.Z),
            ("$dimension", death.Position.Dimension),
            ("$message", death.Message))
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return RelayAttribution.Recorded;
    }

    public async Task<RelayAttribution> RecordExitAsync(
        string instanceId,
        DateTimeOffset runStartedUtc,
        string gamertag,
        BlockPosition position,
        ExitPositionSource source,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var tx = (SqliteTransaction)transaction;

        // A position belongs to one SESSION, so here two sessions of one person
        // are two candidates: a leave arriving after a fast rejoin cannot say
        // whether it ended the old session or is early for the new one.
        var candidates = await CandidatesAsync(
            connection, tx, instanceId, runStartedUtc, gamertag, atUtc,
            includeRecentlyClosed: source == ExitPositionSource.Leave, cancellationToken)
            .ConfigureAwait(false);

        if (candidates.Count != 1)
        {
            return candidates.Count == 0 ? RelayAttribution.NoSession : RelayAttribution.Ambiguous;
        }

        await ExecuteAsync(
            connection, tx,
            """
            UPDATE player_sessions
            SET exit_x = $x, exit_y = $y, exit_z = $z, exit_dimension = $dimension
            WHERE id = $id;
            """,
            cancellationToken,
            ("$x", position.X),
            ("$y", position.Y),
            ("$z", position.Z),
            ("$dimension", position.Dimension),
            ("$id", candidates[0].Id))
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return RelayAttribution.Recorded;
    }

    public async Task RecordCompanionReportAsync(
        string instanceId,
        string packVersion,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection, null,
            """
            INSERT OR IGNORE INTO companion_reports (instance_id, reported_utc, pack_version)
            VALUES ($instance, $at, $version);
            """,
            cancellationToken,
            ("$instance", instanceId),
            ("$at", Stamp(atUtc)),
            ("$version", packVersion))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The sessions a relayed gamertag can mean, under <see cref="CompanionAttribution"/>:
    /// this instance, opened during this run, carrying exactly that name on their
    /// own connect line; open, or closed recently when asked for.
    /// </summary>
    /// <remarks>
    /// The comparisons are on stored text. Every stamp is written by <see cref="Stamp"/>
    /// in one format and one offset, so text order is time order — the same property
    /// the class summary relies on for ORDER BY. SQLite compares text by bytes, so
    /// the name match is exact and case-sensitive.
    /// </remarks>
    private static async Task<List<(long Id, string Xuid)>> CandidatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string instanceId,
        DateTimeOffset runStartedUtc,
        string gamertag,
        DateTimeOffset atUtc,
        bool includeRecentlyClosed,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT id, xuid FROM player_sessions
            WHERE instance_id = $instance
              AND gamertag = $gamertag
              AND connected_utc >= $runStart
              AND (disconnected_utc IS NULL
                   OR ($recent = 1 AND disconnected_utc >= $cutoff));
            """;
        command.Parameters.AddWithValue("$instance", instanceId);
        command.Parameters.AddWithValue("$gamertag", gamertag);
        command.Parameters.AddWithValue("$runStart", Stamp(runStartedUtc));
        command.Parameters.AddWithValue("$recent", includeRecentlyClosed ? 1 : 0);
        command.Parameters.AddWithValue("$cutoff", Stamp(atUtc - CompanionAttribution.RecentlyClosed));

        var found = new List<(long, string)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            found.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        return found;
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

        var sessions = new Dictionary<string, List<(string Instance, DateTimeOffset From, DateTimeOffset? To, BlockPosition? Exit)>>(
            StringComparer.Ordinal);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"""
                SELECT xuid, instance_id, connected_utc, disconnected_utc, {ExitColumns}
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
                    reader.IsDBNull(3) ? null : ReadStamp(reader.GetString(3)),
                    ReadExit(reader, 4)));
            }
        }

        // One server's roster only: see PlayerSummary.Deaths for why the global
        // view carries none.
        var (reportedSince, deaths) = instanceId is null
            ? (null, new Dictionary<string, int>(StringComparer.Ordinal))
            : await DeathsOnAsync(connection, instanceId, cancellationToken).ConfigureAwait(false);

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

            foreach (var (_, from, to, _) in played)
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
                played.Count,
                reportedSince is { } since
                    ? new DeathTally(deaths.GetValueOrDefault(xuid), since)
                    : null,
                instanceId is null ? null : LatestExit(played.Select(s => (s.From, s.Exit)))));
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

        var byInstance = new Dictionary<string, List<(DateTimeOffset From, DateTimeOffset? To, BlockPosition? Exit)>>(
            StringComparer.OrdinalIgnoreCase);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"""
                SELECT instance_id, connected_utc, disconnected_utc, {ExitColumns}
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
                    reader.IsDBNull(2) ? null : ReadStamp(reader.GetString(2)),
                    ReadExit(reader, 3)));
            }
        }

        var tallies = new Dictionary<string, DeathTally>(StringComparer.OrdinalIgnoreCase);

        foreach (var instance in byInstance.Keys)
        {
            var (since, deaths) = await DeathsOnAsync(connection, instance, cancellationToken)
                .ConfigureAwait(false);

            if (since is { } reported)
            {
                tallies[instance] = new DeathTally(deaths.GetValueOrDefault(xuid), reported);
            }
        }

        var recent = new List<RecordedDeath>();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT instance_id, died_utc, message FROM player_deaths
                WHERE xuid = $xuid ORDER BY died_utc DESC LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$xuid", xuid);
            command.Parameters.AddWithValue("$limit", PlayerProfile.RecentDeathLimit);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                recent.Add(new RecordedDeath(
                    reader.GetString(0), ReadStamp(reader.GetString(1)), reader.GetString(2)));
            }
        }

        var activity = new List<PlayerInstanceActivity>();

        foreach (var (instance, played) in byInstance)
        {
            var total = TimeSpan.Zero;
            foreach (var (from, to, _) in played)
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
                      .FirstOrDefault(),
                tallies.TryGetValue(instance, out var tally) ? tally : null,
                LatestExit(played.Select(s => (s.From, s.Exit)))));
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
            activity,
            recent);
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
    /// One delete, and the cascade takes the sessions, per-instance state and
    /// deaths with it — which is why <see cref="PlayerDatabase.OpenAsync"/> turns
    /// foreign keys on. Counted first so the caller can say how much went.
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
                 + (SELECT COUNT(*) FROM player_instance_state WHERE xuid = $xuid)
                 + (SELECT COUNT(*) FROM player_deaths         WHERE xuid = $xuid);
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
    /// Everything keyed by the instance: sessions, per-instance state, deaths and
    /// the companion pack's reports. The profiles themselves are global and
    /// survive — a person known to this install does not stop being known because
    /// one server they played on was removed.
    /// </remarks>
    public async Task<int> CountAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await ScalarAsync(
            connection,
            """
            SELECT (SELECT COUNT(*) FROM player_sessions       WHERE instance_id = $instance)
                 + (SELECT COUNT(*) FROM player_instance_state WHERE instance_id = $instance)
                 + (SELECT COUNT(*) FROM player_deaths         WHERE instance_id = $instance)
                 + (SELECT COUNT(*) FROM companion_reports     WHERE instance_id = $instance);
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

        await ExecuteAsync(
            connection, (SqliteTransaction)transaction,
            "DELETE FROM player_deaths WHERE instance_id = $instance;",
            cancellationToken, ("$instance", instanceId))
            .ConfigureAwait(false);

        await ExecuteAsync(
            connection, (SqliteTransaction)transaction,
            "DELETE FROM companion_reports WHERE instance_id = $instance;",
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
