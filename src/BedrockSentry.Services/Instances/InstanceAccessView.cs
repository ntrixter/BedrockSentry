using BedrockSentry.Core.Players;

namespace BedrockSentry.Services.Instances;

/// <summary>
/// One person on one server, however many of the three sources know about them.
/// </summary>
/// <param name="Gamertag">
/// What the console calls them, which is what <c>allowlist</c>, <c>op</c> and
/// <c>kick</c> all take as their argument — never the nickname.
/// </param>
/// <param name="NameKnown">
/// False for a row that exists only because <c>permissions.json</c> mentions an
/// XUID nothing else does. There is no name to send a console command with, so
/// everything keyed on one has to be refused rather than guessed at.
/// </param>
/// <param name="Tier">
/// The override in <c>permissions.json</c>, or the server's default when there is
/// none. An effective tier rather than a blank, because "no override" and the
/// default look identical from inside the game.
/// </param>
/// <param name="Deaths">
/// From the player's summary on this server; null for a row nobody has played
/// from, and when the companion pack has never reported here (<c>T293</c>).
/// </param>
/// <param name="LastKnownPosition">Where their latest session here ended, when the pack said.</param>
public sealed record InstanceAccessRow(
    string Gamertag,
    string? Nickname,
    string? Xuid,
    bool NameKnown,
    bool Allowed,
    bool IgnoresPlayerLimit,
    string Tier,
    bool IsOnline,
    TimeSpan Playtime,
    TimeSpan? Session,
    DateTimeOffset? LastSeenUtc,
    DeathTally? Deaths = null,
    BlockPosition? LastKnownPosition = null)
{
    /// <summary>What to put in the Player column.</summary>
    public string Display => Nickname ?? (NameKnown ? Gamertag : "Unknown player");

    /// <summary>
    /// Whether this row's role can be changed. <c>permissions.json</c> is keyed on
    /// XUID and Bedrock only reveals one on a first join (<c>D31</c>), so an
    /// allow-list entry for somebody who has never arrived has nothing to key on.
    /// </summary>
    public bool RoleIsSettable => Xuid is { Length: > 0 };

    /// <summary>
    /// Whether the allow list can be edited from this row. It is keyed by
    /// gamertag, so a row that has only ever been an XUID cannot be added to it
    /// without inventing a name.
    /// </summary>
    public bool AllowIsSettable => NameKnown;
}

/// <summary>
/// Joins a server's players, allow list and permission overrides into one row per
/// person (<c>docs/design.md</c> § 7.5).
/// </summary>
/// <remarks>
/// The join is the whole point of the merged Players tab, and it is not a clean
/// one: <c>allowlist.json</c> is keyed by gamertag, <c>permissions.json</c> by
/// XUID, and the player database by XUID (<c>D31</c>). Presentation only — nothing
/// here writes, and the files stay the source of truth.
/// <para>
/// A static function over three inputs rather than a method on the component, so
/// the part that can actually be wrong is testable without a renderer.
/// </para>
/// </remarks>
public static class InstanceAccessView
{
    /// <summary>
    /// The tier a player gets on this server when <c>permissions.json</c> does not name
    /// them: <c>default-player-permission-level</c>, or member when that is unset or blank.
    /// </summary>
    public static string DefaultTier(IReadOnlyDictionary<string, string> properties) =>
        properties.GetValueOrDefault("default-player-permission-level") is { Length: > 0 } configured
            ? configured
            : PermissionTier.Member;

    /// <summary>
    /// Each player's tier on this server: their <c>permissions.json</c> entry, else
    /// <paramref name="defaultTier"/>. The first entry for an XUID wins, the answer
    /// <see cref="PermissionsFile.TierOf"/> gives.
    /// </summary>
    /// <remarks>
    /// One lookup for the Players tab's Role column (<see cref="Build"/>) and the Status
    /// tab's operator count (<see cref="OperatorsAmong"/>), so the two cannot disagree
    /// about who is an operator.
    /// </remarks>
    public static Func<string, string> TierLookup(IReadOnlyList<PermissionEntry> permissions, string defaultTier)
    {
        var tiers = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in permissions)
        {
            tiers.TryAdd(entry.Xuid, entry.Permission);
        }

        return xuid => tiers.GetValueOrDefault(xuid, defaultTier);
    }

    /// <summary>How many of <paramref name="xuids"/> are operators on this server.</summary>
    /// <remarks>
    /// Spelled exactly as Bedrock writes the tier, the same match the Role column's
    /// picker makes: a value it does not recognise is not counted as an operator.
    /// </remarks>
    public static int OperatorsAmong(
        IEnumerable<string> xuids,
        IReadOnlyList<PermissionEntry> permissions,
        string defaultTier)
    {
        var tierOf = TierLookup(permissions, defaultTier);
        return xuids.Count(xuid => IsOperator(tierOf(xuid)));
    }

    /// <summary>Whether a tier, as <see cref="TierLookup"/> answers it, is an operator's.</summary>
    public static bool IsOperator(string tier) =>
        string.Equals(tier, PermissionTier.Operator, StringComparison.Ordinal);

    public static IReadOnlyList<InstanceAccessRow> Build(
        IReadOnlyList<PlayerSummary> players,
        IReadOnlyList<AllowlistEntry> allowlist,
        IReadOnlyList<PermissionEntry> permissions,
        string defaultTier,
        DateTimeOffset asOfUtc)
    {
        // Two indexes because the allow list identifies people two ways: Bedrock
        // fills the XUID in after a first join, and until then the name is all
        // there is.
        var byXuid = new Dictionary<string, AllowlistEntry>(StringComparer.Ordinal);
        var byName = new Dictionary<string, AllowlistEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in allowlist)
        {
            if (entry.Xuid is { Length: > 0 } xuid)
            {
                byXuid.TryAdd(xuid, entry);
            }

            byName.TryAdd(entry.Name, entry);
        }

        var tierOf = TierLookup(permissions, defaultTier);

        // Two passes, and the order is load-bearing. An entry belongs to at most
        // one person, and XUID is the identifier that cannot be changed out from
        // under us — so every XUID match is settled before any name match is
        // considered. One pass would let a player who merely shares a gamertag,
        // and happens to come first in the list, take the entry belonging to the
        // XUID it names. Gamertags are unique at a point in time but can be
        // released and taken by somebody else, so two XUIDs with one name is a
        // state this database can genuinely reach.
        var matched = new Dictionary<string, AllowlistEntry>(StringComparer.Ordinal);
        var claimed = new HashSet<AllowlistEntry>();

        foreach (var player in players)
        {
            if (byXuid.GetValueOrDefault(player.Xuid) is { } byId)
            {
                matched[player.Xuid] = byId;
                claimed.Add(byId);
            }
        }

        foreach (var player in players)
        {
            if (matched.ContainsKey(player.Xuid)
                || byName.GetValueOrDefault(player.Gamertag) is not { } byGamertag
                || !claimed.Add(byGamertag))
            {
                continue;
            }

            matched[player.Xuid] = byGamertag;
        }

        var rows = new List<InstanceAccessRow>();

        foreach (var player in players)
        {
            var entry = matched.GetValueOrDefault(player.Xuid);

            rows.Add(new InstanceAccessRow(
                player.Gamertag,
                player.Nickname,
                player.Xuid,
                NameKnown: true,
                Allowed: entry is not null,
                IgnoresPlayerLimit: entry?.IgnoresPlayerLimit ?? false,
                tierOf(player.Xuid),
                player.IsOnline,
                player.Playtime,
                player.CurrentSession(asOfUtc),
                player.LastSeenUtc,
                player.Deaths,
                player.LastKnownPosition));
        }

        // An allow-list entry nothing matched is a row of its own — naming
        // somebody before they arrive is what the list is for.
        foreach (var entry in allowlist)
        {
            if (claimed.Contains(entry))
            {
                continue;
            }

            rows.Add(new InstanceAccessRow(
                entry.Name,
                null,
                entry.Xuid,
                NameKnown: true,
                Allowed: true,
                entry.IgnoresPlayerLimit,
                entry.Xuid is { Length: > 0 } xuid ? tierOf(xuid) : defaultTier,
                IsOnline: false,
                TimeSpan.Zero,
                null,
                null));
        }

        var known = rows
            .Where(r => r.Xuid is { Length: > 0 })
            .Select(r => r.Xuid!)
            .ToHashSet(StringComparer.Ordinal);

        // Rare, but dropping it would hide an operator from the only page that can
        // revoke them — which is the one row where being invisible actually costs
        // something.
        foreach (var entry in permissions)
        {
            if (known.Contains(entry.Xuid))
            {
                continue;
            }

            rows.Add(new InstanceAccessRow(
                entry.Xuid,
                null,
                entry.Xuid,
                NameKnown: false,
                Allowed: false,
                IgnoresPlayerLimit: false,
                entry.Permission,
                IsOnline: false,
                TimeSpan.Zero,
                null,
                null));
        }

        return rows;
    }
}
