using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Instances;

namespace BedrockSentry.Tests;

/// <summary>
/// D25. Every line here is real output from BDS 1.26.40, captured on a live
/// server, rather than a format inferred from documentation — which is exactly
/// how the chat recogniser in Section 5.4 came to guard a shape that is never
/// printed.
/// </summary>
public sealed class PlayerEventParserTests
{
    [Fact]
    public void Reads_a_connect()
    {
        Assert.True(PlayerEventParser.TryParse(
            "Player connected: SteveExample, xuid: 2535000000000001", out var e));

        Assert.Equal(PlayerEventKind.Connected, e.Kind);
        Assert.Equal("SteveExample", e.Gamertag);
        Assert.Equal("2535000000000001", e.Xuid);
    }

    // The disconnect carries a pfid the connect does not, so nothing may require it.
    [Fact]
    public void Reads_a_disconnect_and_ignores_the_trailing_pfid()
    {
        Assert.True(PlayerEventParser.TryParse(
            "Player disconnected: SteveExample, xuid: 2535000000000001, pfid: 0000000000000000",
            out var e));

        Assert.Equal(PlayerEventKind.Disconnected, e.Kind);
        Assert.Equal("SteveExample", e.Gamertag);
        Assert.Equal("2535000000000001", e.Xuid);
    }

    // Follows a connect about two seconds later, carrying the same XUID in a
    // different shape. Treating it as an arrival would open the session twice.
    [Fact]
    public void Ignores_the_spawn_that_follows_a_connect()
    {
        Assert.False(PlayerEventParser.TryParse(
            "Player Spawned: SteveExample xuid: 2535000000000001, pfid: 0000000000000000",
            out _));
    }

    [Fact]
    public void Ignores_party_updates_which_carry_no_xuid()
    {
        Assert.False(PlayerEventParser.TryParse(
            "Player PartyIdUpdate:  pfid: 0000000000000000, partyid: , isLeader: false",
            out _));
    }

    // "disconnected" contains "connected", and getting this wrong would record
    // every departure as an arrival — a session opened and never closed, which is
    // the one shape D26 cannot repair.
    [Fact]
    public void A_disconnect_is_never_read_as_a_connect()
    {
        Assert.True(PlayerEventParser.TryParse(
            "Player disconnected: Steve, xuid: 1", out var e));

        Assert.Equal(PlayerEventKind.Disconnected, e.Kind);
    }

    // BDS stamps its own timestamp and level on the front, and whether the wrapper
    // has stripped them depends on the path the line took to get here.
    [Theory]
    [InlineData("[2026-08-06 16:30:33:097 INFO] Player connected: SteveExample, xuid: 2535000000000001")]
    [InlineData("16:30:33 Player connected: SteveExample, xuid: 2535000000000001")]
    public void Reads_a_connect_behind_a_log_prefix(string line)
    {
        Assert.True(PlayerEventParser.TryParse(line, out var e));
        Assert.Equal("SteveExample", e.Gamertag);
    }

    // Gamertags allow spaces, and the split is on ", xuid:" rather than on
    // whitespace for exactly that reason.
    [Fact]
    public void Keeps_a_gamertag_that_contains_spaces()
    {
        Assert.True(PlayerEventParser.TryParse(
            "Player connected: Some Player 99, xuid: 2535000000000001", out var e));

        Assert.Equal("Some Player 99", e.Gamertag);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Server started.")]
    [InlineData("Player connected: SteveExample")]
    [InlineData("Player connected: , xuid: 2535000000000001")]
    public void Ignores_anything_that_is_not_a_session_boundary(string? line)
    {
        Assert.False(PlayerEventParser.TryParse(line, out _));
    }
}
