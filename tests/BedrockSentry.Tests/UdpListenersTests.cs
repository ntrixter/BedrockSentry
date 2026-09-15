using BedrockSentry.Services.Instances;

namespace BedrockSentry.Tests;

/// <summary>
/// The matching half of <c>T151</c> clause 7, split out of a clause that can
/// only run on a real box.
/// </summary>
/// <remarks>
/// Why it is worth a test of its own: during a service restart the outgoing
/// host's LAN discovery sockets outlive the SCM's "stopped" by several seconds
/// (<c>T82</c> measured 6.4s, the 2026-09-09 run measured 11.9s), and the
/// incoming host auto-starts the 19132 owner inside that window. If this
/// matching stops recognising the predecessor, that server faults on the port
/// **on every boot** — the loudest possible regression, and one no unit test
/// covered until now. The clause that was supposed to cover it passed for the
/// wrong reason: the ports were released before the launch, so nothing was ever
/// matched.
/// </remarks>
public class UdpListenersTests
{
    private const string Host = "BedrockSentry.Host";

    private static Func<int, string?> Table(params (int Id, string Name)[] running) =>
        id => running.FirstOrDefault(p => p.Id == id) is { Name: { } name } ? name : null;

    [Fact]
    public void This_very_process_is_this_wrapper()
    {
        Assert.True(UdpListeners.MatchesWrapperProcess(4242, 4242, Host, Table()));
    }

    [Fact]
    public void A_predecessor_host_still_holding_its_discovery_sockets_is_a_reservation()
    {
        // The case the whole mechanism exists for: same executable name, a
        // different PID, still alive as the successor starts.
        Assert.True(
            UdpListeners.MatchesWrapperProcess(
                8100, 9200, Host, Table((8100, Host), (9200, Host))));
    }

    [Fact]
    public void A_bedrock_server_holding_the_port_is_not_this_wrapper()
    {
        // The refusal `T151` is for. Getting this wrong opens the world twice.
        Assert.False(
            UdpListeners.MatchesWrapperProcess(
                8100, 9200, Host, Table((8100, "bedrock_server"))));
    }

    [Fact]
    public void A_process_that_has_since_exited_is_not_a_reservation()
    {
        // netstat's snapshot is older than the lookup. A reservation held by
        // nothing is not a reservation.
        Assert.False(UdpListeners.MatchesWrapperProcess(8100, 9200, Host, Table()));
    }

    [Fact]
    public void Pid_zero_never_matches()
    {
        // netstat reports 0 for a socket it cannot attribute. Matching it would
        // wave through any listener the wrapper cannot see the owner of — and
        // it would match even when the lookup claims our own name.
        Assert.False(UdpListeners.MatchesWrapperProcess(0, 9200, Host, Table((0, Host))));
    }

    [Fact]
    public void The_name_match_ignores_case()
    {
        // Process names come back from two different Windows APIs across the
        // self-update handover; nothing guarantees they agree on case.
        Assert.True(
            UdpListeners.MatchesWrapperProcess(
                8100, 9200, Host, Table((8100, "bedrocksentry.host"))));
    }

    [Fact]
    public void A_second_installs_host_matches_and_that_is_the_accepted_cost()
    {
        // `D49`/`D50`: unsupported but allowed. It is not a bedrock_server.exe,
        // so it cannot have a world open — the refusal it costs was never about
        // it. Pinned so the trade-off is a decision, not a surprise.
        Assert.True(
            UdpListeners.MatchesWrapperProcess(
                8100, 9200, Host, Table((8100, Host))));
    }
}
