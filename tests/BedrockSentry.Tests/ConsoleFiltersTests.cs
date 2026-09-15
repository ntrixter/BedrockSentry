using BedrockSentry.Core.Instances;

namespace BedrockSentry.Tests;

/// <summary>
/// The predicate lives in Core rather than the console page precisely so it can
/// be tested — there is no bUnit here, so anything left in a <c>.razor</c> file
/// has no cover at all.
/// </summary>
public sealed class ConsoleFiltersTests
{
    /// <summary>
    /// Built through a real buffer rather than by hand, so the Kind these
    /// filters read is the one production would have stamped on the line.
    /// </summary>
    private static ConsoleLine Line(string raw, ConsoleOrigin origin = ConsoleOrigin.Stdout) =>
        new ConsoleBuffer().Append(origin, raw, DateTimeOffset.UnixEpoch);

    private static readonly ConsoleLine Join =
        Line("[2026-08-15 12:00:00:000 INFO] Player connected: SteveExample, xuid: 2535000000000001");

    private static readonly ConsoleLine Leave = Line(
        "[2026-08-15 12:00:00:000 INFO] Player disconnected: SteveExample, xuid: 2535000000000001, pfid: 0000000000000000");

    /// Chat is filtered on origin rather than kind, so this is the one line that
    /// still has to be stated rather than recognised — BDS prints no chat of its
    /// own until the companion pack ships (docs/design.md § 5.4).
    private static readonly ConsoleLine Chat =
        Line("SteveExample: anyone seen my pickaxe", ConsoleOrigin.Chat);

    private static readonly ConsoleLine Noise =
        Line("[2026-08-15 12:00:00:000 INFO] Running AutoCompaction...");

    private static readonly ConsoleLine Started =
        Line("[2026-08-15 12:00:00:000 INFO] Server started.");

    private static readonly ConsoleLine Warning = Line(
        "[2026-08-15 12:00:00:000 WARN] Allow list is enabled but contains no entries.");

    [Fact]
    public void Everything_is_shown_by_default()
    {
        var filters = ConsoleFilters.All;

        Assert.True(filters.ShowingEverything);
        Assert.All(
            new[] { Join, Leave, Chat, Noise, Started, Warning },
            line => Assert.True(filters.Shows(line)));
    }

    // The view the whole feature exists for: who is on the server and what they
    // are doing, with the machine underneath them out of the way.
    [Fact]
    public void Players_only_keeps_the_people_and_drops_the_machine()
    {
        var filters = ConsoleFilters.PlayersOnly;

        Assert.True(filters.Shows(Join));
        Assert.True(filters.Shows(Leave));
        Assert.True(filters.Shows(Chat));

        Assert.False(filters.Shows(Noise));
        Assert.False(filters.Shows(Started));
        Assert.False(filters.Shows(Warning));
    }

    [Fact]
    public void Joins_and_leaves_are_filtered_separately()
    {
        var noJoins = ConsoleFilters.All with { Joins = false };

        Assert.False(noJoins.Shows(Join));
        Assert.True(noJoins.Shows(Leave));

        var noLeaves = ConsoleFilters.All with { Leaves = false };

        Assert.True(noLeaves.Shows(Join));
        Assert.False(noLeaves.Shows(Leave));
    }

    [Fact]
    public void Chat_is_its_own_switch()
    {
        var filters = ConsoleFilters.All with { Chat = false };

        Assert.False(filters.Shows(Chat));
        Assert.True(filters.Shows(Join));
    }

    // A restart in the middle of a filtered feed would otherwise be invisible,
    // and "why did everyone leave and rejoin" has no answer without it.
    [Fact]
    public void A_run_boundary_is_never_hidden()
    {
        var line = Line("bedrock_server.exe started", ConsoleOrigin.Launch);

        Assert.True(ConsoleFilters.PlayersOnly.Shows(line));
        Assert.True(new ConsoleFilters(false, false, false, false).Shows(line));
    }

    // Warnings and errors are server output, not a category of their own: an
    // operator who hides the machine has hidden its complaints too, which is
    // what "show me the players" means.
    [Fact]
    public void Warnings_follow_the_server_output_switch()
    {
        Assert.True((ConsoleFilters.All with { ServerOutput = true }).Shows(Warning));
        Assert.False((ConsoleFilters.All with { ServerOutput = false }).Shows(Warning));
    }

    [Fact]
    public void Counts_what_it_would_hide()
    {
        var lines = new[] { Join, Leave, Chat, Noise, Started, Warning };

        Assert.Equal(0, ConsoleFilters.All.CountHidden(lines));
        Assert.Equal(3, ConsoleFilters.PlayersOnly.CountHidden(lines));
    }
}
