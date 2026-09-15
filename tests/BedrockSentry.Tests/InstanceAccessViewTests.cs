using BedrockSentry.Core.Players;
using BedrockSentry.Services.Instances;

namespace BedrockSentry.Tests;

/// <summary>
/// The join behind the merged Players tab: three sources, one row per person.
/// </summary>
/// <remarks>
/// Worth its own file because the awkward part of <c>D31</c> lands here.
/// <c>allowlist.json</c> is keyed by gamertag and <c>permissions.json</c> by
/// XUID, so "the same person" is a judgement rather than a lookup, and getting it
/// wrong shows up as a duplicated row or a player who cannot be found at all.
/// </remarks>
public sealed class InstanceAccessViewTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    private static PlayerSummary Player(
        string xuid,
        string gamertag,
        string? nickname = null,
        bool online = false,
        TimeSpan? playtime = null) =>
        new(
            xuid,
            gamertag,
            nickname,
            online ? "survival" : null,
            online ? Now.AddMinutes(-90) : null,
            Now.AddDays(-30),
            online ? Now : Now.AddDays(-2),
            playtime ?? TimeSpan.FromHours(3),
            SessionCount: 4);

    private static IReadOnlyList<InstanceAccessRow> Build(
        IReadOnlyList<PlayerSummary>? players = null,
        IReadOnlyList<AllowlistEntry>? allowlist = null,
        IReadOnlyList<PermissionEntry>? permissions = null,
        string defaultTier = PermissionTier.Member) =>
        InstanceAccessView.Build(
            players ?? [], allowlist ?? [], permissions ?? [], defaultTier, Now);

    [Fact]
    public void A_player_on_the_allowlist_and_in_permissions_is_one_row()
    {
        var rows = Build(
            players: [Player("2535000000000002", "AlexExample")],
            allowlist: [new AllowlistEntry("AlexExample", true, "2535000000000002")],
            permissions: [new PermissionEntry(PermissionTier.Operator, "2535000000000002")]);

        var row = Assert.Single(rows);

        Assert.Equal("AlexExample", row.Display);
        Assert.True(row.Allowed);
        Assert.True(row.IgnoresPlayerLimit);
        Assert.Equal(PermissionTier.Operator, row.Tier);
    }

    // Bedrock fills the XUID in on a first join, so before that the name is the
    // only thing the two sides share.
    [Fact]
    public void An_allowlist_entry_with_no_xuid_still_matches_its_player_by_name()
    {
        var rows = Build(
            players: [Player("2535000000000002", "AlexExample")],
            allowlist: [new AllowlistEntry("alexexample", false, null)]);

        var row = Assert.Single(rows);
        Assert.True(row.Allowed);
    }

    // A gamertag can be changed; the XUID cannot. When the two disagree the XUID
    // is the one to believe, or a rename would split one person into two rows.
    [Fact]
    public void The_xuid_wins_over_a_stale_name_on_the_entry()
    {
        var rows = Build(
            players: [Player("2535000000000002", "AlexExampleRenamed")],
            allowlist: [new AllowlistEntry("AlexExample", true, "2535000000000002")]);

        var row = Assert.Single(rows);

        Assert.True(row.Allowed);
        Assert.True(row.IgnoresPlayerLimit);
        Assert.Equal("AlexExampleRenamed", row.Display);
    }

    [Fact]
    public void Someone_allowlisted_who_has_never_joined_gets_a_row_of_their_own()
    {
        var rows = Build(allowlist: [new AllowlistEntry("QuietHarbor", false, null)]);

        var row = Assert.Single(rows);

        Assert.Equal("QuietHarbor", row.Display);
        Assert.True(row.Allowed);
        Assert.Null(row.Xuid);

        // permissions.json is keyed on XUID and there is not one yet, so the role
        // control has nothing to write against (D31).
        Assert.False(row.RoleIsSettable);
        Assert.True(row.AllowIsSettable);
    }

    [Fact]
    public void Someone_who_has_played_but_is_not_allowlisted_still_gets_a_row()
    {
        var rows = Build(players: [Player("2535000000000003", "Marrowlight")]);

        var row = Assert.Single(rows);

        Assert.False(row.Allowed);
        Assert.True(row.RoleIsSettable);
    }

    // Rare — a permission override for somebody whose sessions predate the
    // wrapper. Dropping it would hide an operator from the only page that can
    // revoke them.
    [Fact]
    public void A_permission_override_with_nothing_else_behind_it_survives()
    {
        var rows = Build(
            permissions: [new PermissionEntry(PermissionTier.Operator, "2535000000000004")]);

        var row = Assert.Single(rows);

        Assert.Equal(PermissionTier.Operator, row.Tier);
        Assert.Equal("2535000000000004", row.Xuid);
        Assert.Equal("Unknown player", row.Display);

        // There is no gamertag to send `allowlist add` with, so that control has
        // to be refused rather than handed the XUID as if it were a name.
        Assert.False(row.AllowIsSettable);

        // The role still is settable — revoking is exactly why this row is kept.
        Assert.True(row.RoleIsSettable);
    }

    // The file is an override list, not a roster: absent means "whatever the
    // server gives everyone", which is a real tier and not a blank.
    [Fact]
    public void A_player_with_no_override_shows_the_servers_default()
    {
        var rows = Build(
            players: [Player("2535000000000002", "AlexExample")],
            defaultTier: PermissionTier.Visitor);

        Assert.Equal(PermissionTier.Visitor, Assert.Single(rows).Tier);
    }

    [Fact]
    public void A_nickname_is_shown_but_the_gamertag_is_what_commands_use()
    {
        var rows = Build(
            players: [Player("2535000000000002", "AlexExample", nickname: "Steve")]);

        var row = Assert.Single(rows);

        Assert.Equal("Steve", row.Display);
        Assert.Equal("AlexExample", row.Gamertag);
    }

    [Fact]
    public void An_online_player_reports_the_running_session()
    {
        var rows = Build(players: [Player("2535000000000002", "AlexExample", online: true)]);

        var row = Assert.Single(rows);

        Assert.True(row.IsOnline);
        Assert.Equal(TimeSpan.FromMinutes(90), row.Session);
    }

    [Fact]
    public void Nobody_is_listed_twice()
    {
        var rows = Build(
            players:
            [
                Player("2535000000000002", "AlexExample"),
                Player("2535000000000003", "Marrowlight"),
            ],
            allowlist:
            [
                new AllowlistEntry("AlexExample", false, "2535000000000002"),
                new AllowlistEntry("QuietHarbor", false, null),
            ],
            permissions:
            [
                new PermissionEntry(PermissionTier.Operator, "2535000000000002"),
            ]);

        Assert.Equal(3, rows.Count);
        Assert.Equal(3, rows.Select(r => r.Display).Distinct().Count());
        Assert.Equal(2, rows.Count(r => r.Allowed));
    }

    [Fact]
    public void Two_players_never_share_one_allowlist_entry()
    {
        // Both would match the entry by name if the first match did not claim it —
        // which is what the online player's stale gamertag would do here.
        var rows = Build(
            players:
            [
                Player("2535000000000002", "AlexExample"),
                Player("2535000000000003", "AlexExample"),
            ],
            allowlist: [new AllowlistEntry("AlexExample", false, "2535000000000002")]);

        Assert.Equal(2, rows.Count);
        Assert.Single(rows, r => r.Allowed);
    }

    [Fact]
    public void Nothing_at_all_is_an_empty_table_rather_than_a_throw()
    {
        Assert.Empty(Build());
    }
}
