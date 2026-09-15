using System.Net;
using System.Net.Sockets;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Logging.Abstractions;

namespace BedrockSentry.Tests;

/// <summary>
/// D24. The guard exists so the instance that configured 19132 gets it whatever
/// order things start in. These use ports well away from Bedrock's so they can
/// run on a machine with a real server on it.
/// </summary>
public sealed class LanDiscoveryPortGuardTests
{
    // Below the ephemeral range on both platforms — Linux hands out 32768-60999
    // and Windows 49152-65535, so 49132 was inside one of them and any parallel
    // test's outbound socket could transiently claim it. That failed the rebind
    // assertion at the end of the hold test at random, on Linux only.
    private const int TestPortV4 = 24132;
    private const int TestPortV6 = 24133;

    private static LanDiscoveryPortGuard Guard(
        bool ownerExists = true,
        bool ownerRunning = false,
        TimeSpan? retryFor = null,
        TimeSpan? retryEvery = null)
    {
        // Retry timings are cut right down: the production defaults are sized to
        // outlast a wrapper process that has not finished exiting, which is not
        // something a test should sit through.
        var guard = new LanDiscoveryPortGuard(
            NullLogger<LanDiscoveryPortGuard>.Instance,
            TestPortV4,
            TestPortV6,
            retryFor ?? TimeSpan.FromMilliseconds(500),
            retryEvery ?? TimeSpan.FromMilliseconds(25));

        guard.OwnerExists = () => ownerExists;
        guard.OwnerRunning = () => ownerRunning;
        return guard;
    }

    private static async Task<bool> Eventually(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    private static bool CanBind(IPAddress address, int port)
    {
        try
        {
            using var probe = new UdpClient(new IPEndPoint(address, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// The bug reported on build.60, as a test. BDS binds the LAN discovery pair
    /// <em>after</em> it reports started — measured from a real console, where
    /// "used for LAN discovery" prints after "Server started." — so a hold
    /// released on ready was released exactly in time for the wrong server to
    /// take the pair, and the server configured for 19132 then crash-looped
    /// against it.
    /// </summary>
    [Fact]
    public async Task The_hold_outlives_the_starting_servers_ready_line()
    {
        using var guard = Guard();

        // The non-owner starts; its start finishes; the slot goes back.
        await using (await guard.HoldAsync("test3", instanceOwnsPair: false))
        {
            Assert.False(CanBind(IPAddress.Any, guard.PortV4));
        }

        // This is the moment build.60 got wrong: after ready, when BDS actually
        // reaches for the pair, it must still be held.
        Assert.False(CanBind(IPAddress.Any, guard.PortV4));

        if (Socket.OSSupportsIPv6)
        {
            Assert.False(CanBind(IPAddress.IPv6Any, guard.PortV6));
        }

        // And the owner's own start is what frees it — inside the same
        // serialised slot, so nothing can slip in between.
        await using (await guard.HoldAsync("survival", instanceOwnsPair: true))
        {
            Assert.True(CanBind(IPAddress.Any, guard.PortV4));
        }
    }

    // The user's exact sequence: server2 (19136) up first, then server1 (19132).
    [Fact]
    public async Task The_owner_can_start_after_a_non_owner_ran_first()
    {
        using var guard = Guard();

        await using (await guard.HoldAsync("test3", instanceOwnsPair: false)) { }

        // Held for the owner across the gap...
        Assert.False(CanBind(IPAddress.Any, guard.PortV4));

        await using (await guard.HoldAsync("survival", instanceOwnsPair: true))
        {
            // ...and handed over the moment the owner starts, which is when its
            // BDS binds server-port 19132 for gameplay.
            Assert.True(CanBind(IPAddress.Any, guard.PortV4));

            if (Socket.OSSupportsIPv6)
            {
                Assert.True(CanBind(IPAddress.IPv6Any, guard.PortV6));
            }
        }
    }

    // A wrapper squatting on 19132 forever, when no configured instance owns it,
    // would make every server on the box undiscoverable on the LAN for the
    // benefit of an owner that does not exist.
    [Fact]
    public async Task Nothing_is_held_when_no_instance_owns_the_pair()
    {
        using var guard = Guard(ownerExists: false);

        await using (await guard.HoldAsync("test3", instanceOwnsPair: false))
        {
            Assert.True(CanBind(IPAddress.Any, guard.PortV4));
        }

        Assert.True(CanBind(IPAddress.Any, guard.PortV4));
    }

    // The owner was retired mid-run. The standing hold must go with it, on the
    // next start rather than never.
    [Fact]
    public async Task Retiring_the_owner_frees_the_standing_hold()
    {
        var ownerExists = true;
        using var guard = new LanDiscoveryPortGuard(
            NullLogger<LanDiscoveryPortGuard>.Instance, TestPortV4, TestPortV6)
        {
            OwnerExists = () => ownerExists,
        };

        await using (await guard.HoldAsync("test3", instanceOwnsPair: false)) { }
        Assert.False(CanBind(IPAddress.Any, guard.PortV4));

        ownerExists = false;

        await using (await guard.HoldAsync("creative", instanceOwnsPair: false)) { }
        Assert.True(CanBind(IPAddress.Any, guard.PortV4));
    }

    // The pair is held *for* this instance, so holding it against it would lock
    // it out of the very port the guard is keeping free.
    [Fact]
    public async Task Leaves_the_pair_alone_for_the_instance_that_owns_it()
    {
        using var guard = Guard();

        await using (await guard.HoldAsync("survival", instanceOwnsPair: true))
        {
            Assert.True(CanBind(IPAddress.Any, guard.PortV4));
        }
    }

    // Second and later non-owner starts, with the pair already standing-held by
    // the guard itself, must not throw or double-bind.
    [Fact]
    public async Task A_second_non_owner_start_reuses_the_standing_hold()
    {
        using var guard = Guard();

        await using (await guard.HoldAsync("test3", instanceOwnsPair: false)) { }
        await using (await guard.HoldAsync("creative", instanceOwnsPair: false)) { }

        Assert.False(CanBind(IPAddress.Any, guard.PortV4));
    }

    /// <summary>
    /// `T82`, as a test. A failed bind has two very different meanings, and the guard
    /// used to treat both as "the owner has it, nothing to do".
    /// </summary>
    /// <remarks>
    /// On a real box this lost the pair once in nine auto-starts. The holder was the
    /// *outgoing wrapper process*: Windows reports a service stopped before its
    /// process has exited, so the incoming guard found the pair taken, gave up
    /// silently, and the outgoing process let go a moment later — leaving it free for
    /// whichever BDS bound discovery next, which happens seconds after that server's
    /// own ready line. The owner was then locked out of the ports it configured.
    /// </remarks>
    [Fact]
    public async Task The_pair_is_taken_back_once_whatever_held_it_lets_go()
    {
        using var guard = Guard(
            ownerExists: true,
            ownerRunning: false,
            retryFor: TimeSpan.FromSeconds(5));

        var squatter = new UdpClient(new IPEndPoint(IPAddress.Any, TestPortV4));

        // The non-owner starts while something else still holds v4.
        var hold = await guard.HoldAsync("test3", instanceOwnsPair: false);
        Assert.False(CanBind(IPAddress.Any, TestPortV4));

        // Its start finishes and the slot goes back, which is what lets the retry run.
        await hold.DisposeAsync();

        squatter.Dispose();

        Assert.True(
            await Eventually(() => !CanBind(IPAddress.Any, TestPortV4)),
            "the guard never went back for the pair after it was released");
    }

    /// <summary>
    /// The other half of the same distinction: a bind that fails because the owner is
    /// up and holding its own pair is the ordinary case, and must not be retried.
    /// </summary>
    /// <remarks>
    /// Going back for it would make the guard the thing that steals the pair — taking
    /// it the moment a running owner restarts, which is the failure this class exists
    /// to prevent, caused by the class itself.
    /// </remarks>
    [Fact]
    public async Task A_running_owner_holding_its_own_pair_is_left_alone()
    {
        using var guard = Guard(
            ownerExists: true,
            ownerRunning: true,
            retryFor: TimeSpan.FromSeconds(5));

        var owner = new UdpClient(new IPEndPoint(IPAddress.Any, TestPortV4));

        var hold = await guard.HoldAsync("test3", instanceOwnsPair: false);
        await hold.DisposeAsync();

        owner.Dispose();
        await Task.Delay(300);

        Assert.True(
            CanBind(IPAddress.Any, TestPortV4),
            "the guard went back for a pair its running owner was holding");
    }

    // Usually because the owning instance is running and holds them itself, which
    // protects it exactly as well as the guard would have.
    [Fact]
    public async Task A_pair_already_held_by_something_else_is_not_an_error()
    {
        using var guard = Guard();
        using var squatter = new UdpClient(new IPEndPoint(IPAddress.Any, TestPortV4));

        await using var hold = await guard.HoldAsync("test3", instanceOwnsPair: false);

        Assert.True(squatter.Client.IsBound);
    }

    [Fact]
    public async Task Starts_are_serialised_and_the_slot_comes_back()
    {
        using var guard = Guard();
        var first = await guard.HoldAsync("test3", instanceOwnsPair: false);

        var second = guard.HoldAsync("creative", instanceOwnsPair: false);
        Assert.False(second.IsCompleted);

        await first.DisposeAsync();

        await using var _ = await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Disposing twice is the ordinary case: the start path releases on its own
    // failure and the detached waiter releases again. Releasing the slot twice
    // would let two instances start at once, which is the race being prevented.
    [Fact]
    public async Task Releasing_twice_does_not_hand_out_the_slot_twice()
    {
        using var guard = Guard();
        var hold = await guard.HoldAsync("test3", instanceOwnsPair: false);

        await hold.DisposeAsync();
        await hold.DisposeAsync();

        await using var next = await guard.HoldAsync("creative", instanceOwnsPair: false);

        // One slot, still one: a second concurrent hold has to wait for this one.
        var queued = guard.HoldAsync("survival", instanceOwnsPair: false);
        Assert.False(queued.IsCompleted);

        await next.DisposeAsync();
        await using var _ = await queued.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Owning_the_pair_is_decided_by_the_ports_the_file_names()
    {
        using var guard = Guard();

        Assert.True(guard.OwnsDiscoveryPair(
            [("server-port", TestPortV4), ("server-portv6", TestPortV6)]));

        Assert.False(guard.OwnsDiscoveryPair(
            [("server-port", 19136), ("server-portv6", 19137)]));
    }
}
