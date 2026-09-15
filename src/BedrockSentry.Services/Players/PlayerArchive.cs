using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BedrockSentry.Core.Players;
using Microsoft.Data.Sqlite;

namespace BedrockSentry.Services.Players;

/// <summary>
/// Export to JSON and import back (<c>docs/design.md</c> § 7.4). The manual-editing
/// path, so SQLite can stay the only backend without anyone having to open it
/// with a database tool to fix a typo in a nickname.
/// </summary>
/// <remarks>
/// Import is upsert-only and never deletes: a row missing from the file means
/// the file is older or narrower than the database, which is overwhelmingly more
/// likely than somebody intending to erase a player by leaving them out. D29's
/// erase is the deliberate way to do that, and it asks first.
///
/// § 7.4 calls this a stop-the-service operation, and it means it. The export is
/// a snapshot; re-importing a stale one while servers are running writes old
/// session rows back over a database that has moved on.
/// </remarks>
public sealed class PlayerArchive
{
    private readonly PlayerDatabase _database;

    public PlayerArchive(PlayerDatabase database) => _database = database;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public sealed record ArchiveProfile(
        string Xuid,
        string Gamertag,
        string? Nickname,
        string? Notes,
        bool IsWrapperAdmin,
        string FirstSeenUtc);

    public sealed record ArchiveSession(
        string Xuid, string InstanceId, string ConnectedUtc, string? DisconnectedUtc);

    public sealed record ArchiveInstanceState(
        string Xuid, string InstanceId, bool IsAllowlisted, int OpLevel);

    public sealed record Archive(
        int SchemaVersion,
        string ExportedUtc,
        IReadOnlyList<ArchiveProfile> Players,
        IReadOnlyList<ArchiveSession> Sessions,
        IReadOnlyList<ArchiveInstanceState> InstanceState);

    public async Task<string> ExportAsync(
        DateTimeOffset atUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);

        var players = new List<ArchiveProfile>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT xuid, gamertag, nickname, notes, is_wrapper_admin, first_seen_utc
                FROM players ORDER BY xuid;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                players.Add(new ArchiveProfile(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt64(4) != 0,
                    reader.GetString(5)));
            }
        }

        var sessions = new List<ArchiveSession>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT xuid, instance_id, connected_utc, disconnected_utc
                FROM player_sessions ORDER BY xuid, connected_utc;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                sessions.Add(new ArchiveSession(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        var state = new List<ArchiveInstanceState>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT xuid, instance_id, is_allowlisted, op_level
                FROM player_instance_state ORDER BY xuid, instance_id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                state.Add(new ArchiveInstanceState(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2) != 0,
                    (int)reader.GetInt64(3)));
            }
        }

        return JsonSerializer.Serialize(
            new Archive(
                PlayerDatabase.TargetVersion,
                atUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                players,
                sessions,
                state),
            Json);
    }

    public async Task<PlayerImportResult> ImportAsync(
        string json, CancellationToken cancellationToken = default)
    {
        var archive = JsonSerializer.Deserialize<Archive>(json, Json)
            ?? throw new InvalidOperationException("The file is not a player export.");

        var warnings = new List<string>();

        if (archive.SchemaVersion > PlayerDatabase.TargetVersion)
        {
            // Forward-compatible only as far as it can be: additive migrations
            // (D27) mean a newer file's extra fields are simply absent here, and
            // saying so beats importing a subset in silence.
            warnings.Add(
                $"The file was written by schema version {archive.SchemaVersion}; this build "
                + $"understands {PlayerDatabase.TargetVersion}. Anything newer was ignored.");
        }

        var added = 0;
        var updated = 0;
        var sessionsAdded = 0;
        var stateRows = 0;

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var tx = (SqliteTransaction)transaction;

        foreach (var profile in archive.Players ?? [])
        {
            if (string.IsNullOrWhiteSpace(profile.Xuid))
            {
                warnings.Add("Skipped a player with no XUID — it is the key everything hangs off.");
                continue;
            }

            var existed = await ScalarAsync(
                connection, tx, "SELECT COUNT(*) FROM players WHERE xuid = $xuid;",
                cancellationToken, ("$xuid", profile.Xuid)).ConfigureAwait(false);

            // first_seen_utc is written on insert and left alone on conflict.
            // D30 again: it is the one date nothing may move, and an import is
            // very much something.
            await ExecuteAsync(
                connection, tx,
                """
                INSERT INTO players (xuid, gamertag, nickname, notes, first_seen_utc, is_wrapper_admin)
                VALUES ($xuid, $gamertag, $nickname, $notes, $first, $admin)
                ON CONFLICT(xuid) DO UPDATE SET
                    gamertag = excluded.gamertag,
                    nickname = excluded.nickname,
                    notes = excluded.notes,
                    is_wrapper_admin = excluded.is_wrapper_admin;
                """,
                cancellationToken,
                ("$xuid", profile.Xuid),
                ("$gamertag", profile.Gamertag ?? profile.Xuid),
                ("$nickname", (object?)profile.Nickname ?? DBNull.Value),
                ("$notes", (object?)profile.Notes ?? DBNull.Value),
                ("$first", profile.FirstSeenUtc ?? DateTimeOffset.UnixEpoch.ToString("O", CultureInfo.InvariantCulture)),
                ("$admin", profile.IsWrapperAdmin ? 1 : 0))
                .ConfigureAwait(false);

            if (existed > 0) { updated++; } else { added++; }
        }

        foreach (var session in archive.Sessions ?? [])
        {
            if (string.IsNullOrWhiteSpace(session.Xuid) || string.IsNullOrWhiteSpace(session.InstanceId))
            {
                warnings.Add("Skipped a session with no player or no instance.");
                continue;
            }

            // Matched on (xuid, instance, connected_utc) rather than on the row
            // id, which is an autoincrement local to whichever database wrote the
            // file and means nothing in this one. A player cannot connect to the
            // same server twice in the same instant, so the triple is a real key.
            var already = await ScalarAsync(
                connection, tx,
                """
                SELECT COUNT(*) FROM player_sessions
                WHERE xuid = $xuid AND instance_id = $instance AND connected_utc = $from;
                """,
                cancellationToken,
                ("$xuid", session.Xuid),
                ("$instance", session.InstanceId),
                ("$from", session.ConnectedUtc)).ConfigureAwait(false);

            if (already > 0)
            {
                continue;
            }

            var owner = await ScalarAsync(
                connection, tx, "SELECT COUNT(*) FROM players WHERE xuid = $xuid;",
                cancellationToken, ("$xuid", session.Xuid)).ConfigureAwait(false);

            if (owner == 0)
            {
                // The foreign key would reject it anyway; saying which player is
                // missing is more use than a constraint violation.
                warnings.Add($"Skipped sessions for {session.Xuid} — no such player in the file or the database.");
                continue;
            }

            await ExecuteAsync(
                connection, tx,
                """
                INSERT INTO player_sessions (xuid, instance_id, connected_utc, disconnected_utc)
                VALUES ($xuid, $instance, $from, $to);
                """,
                cancellationToken,
                ("$xuid", session.Xuid),
                ("$instance", session.InstanceId),
                ("$from", session.ConnectedUtc),
                ("$to", (object?)session.DisconnectedUtc ?? DBNull.Value))
                .ConfigureAwait(false);

            sessionsAdded++;
        }

        foreach (var row in archive.InstanceState ?? [])
        {
            if (string.IsNullOrWhiteSpace(row.Xuid) || string.IsNullOrWhiteSpace(row.InstanceId))
            {
                continue;
            }

            var owner = await ScalarAsync(
                connection, tx, "SELECT COUNT(*) FROM players WHERE xuid = $xuid;",
                cancellationToken, ("$xuid", row.Xuid)).ConfigureAwait(false);

            if (owner == 0)
            {
                warnings.Add($"Skipped allowlist and OP state for {row.Xuid} — no such player.");
                continue;
            }

            await ExecuteAsync(
                connection, tx,
                """
                INSERT INTO player_instance_state (xuid, instance_id, is_allowlisted, op_level)
                VALUES ($xuid, $instance, $allow, $op)
                ON CONFLICT(xuid, instance_id) DO UPDATE SET
                    is_allowlisted = excluded.is_allowlisted,
                    op_level = excluded.op_level;
                """,
                cancellationToken,
                ("$xuid", row.Xuid),
                ("$instance", row.InstanceId),
                ("$allow", row.IsAllowlisted ? 1 : 0),
                ("$op", row.OpLevel))
                .ConfigureAwait(false);

            stateRows++;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new PlayerImportResult(added, updated, sessionsAdded, stateRows, warnings);
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

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }
}
