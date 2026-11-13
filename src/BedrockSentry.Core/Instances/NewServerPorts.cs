namespace BedrockSentry.Core.Instances;

/// <summary>
/// Sockets something already holds or a server's file already names, and whose they are.
/// </summary>
/// <param name="Ports">The ports held.</param>
/// <param name="Protocol">The socket table they are in.</param>
/// <param name="Holder">
/// Who holds them, worded to follow "is": <c>Survival's server-port</c>,
/// <c>open on this machine by java.exe (pid 4120)</c>.
/// </param>
public sealed record PortClaim(PortSpan Ports, PortProtocol Protocol, string Holder);

/// <summary>
/// The ports a server about to be created will bind, the first number where none of them
/// is taken, and what is in the way of a number somebody typed (<c>ON1</c>).
/// </summary>
/// <remarks>
/// <para>A number is free only when <em>every</em> socket the new server would bind is
/// free: a RakNet server takes the number and the one after it for IPv6, and a NetherNet
/// server with a play window takes the whole window. Comparing only <c>server-port</c>
/// values suggested a number inside another server's window.</para>
/// <para>Pure, so the rule is tested without a socket table; the claims come from
/// <c>PortInspector</c>.</para>
/// </remarks>
public static class NewServerPorts
{
    /// <summary>
    /// The <c>max-players</c> every BDS package ships, which is the width a new
    /// NetherNet server's play window is suggested at: the form does not ask for it.
    /// </summary>
    public const int PackageMaxPlayers = 10;

    /// <summary>The sockets a new server on <paramref name="port"/> would bind.</summary>
    /// <param name="window">Its <c>server-udp-ports</c>, when one will be written; read only under NetherNet.</param>
    /// <remarks>
    /// RakNet's IPv6 port is the number after, because that is what the create path
    /// writes. The rest is <see cref="ServerPorts.For"/> and <see cref="PlayPorts.For"/>.
    /// </remarks>
    public static IReadOnlyList<ReachableSockets> Wanted(ServerTransport transport, int port, ServerUdpPorts? window)
    {
        var sockets = ServerPorts.For(transport, port, port + 1, window)
            .Select(p => new ReachableSockets(new PortSpan(p.Port, p.Port), p.Protocol))
            .ToList();

        if (PlayPorts.For(transport, port, window) is { Kind: PlayPortKind.Pinned, Span: { } pinned })
        {
            sockets.Add(new ReachableSockets(pinned, PortProtocol.Udp));
        }

        return sockets;
    }

    /// <summary>
    /// The window a new NetherNet server on <paramref name="port"/> is offered: one port
    /// per player from the server port up, published at <paramref name="publicAddress"/>
    /// when that is known. Null when it would run past 65535.
    /// </summary>
    public static string? SuggestWindow(int port, string? publicAddress) =>
        ServerUdpPorts.Suggest(publicAddress, port, PackageMaxPlayers);

    /// <summary>
    /// The window the New instance form offers (<c>T458</c>): from
    /// <paramref name="port"/> when nothing holds those UDP ports, otherwise the first
    /// free one above it (<see cref="FreeWindow"/>). Null when none fits below 65536.
    /// </summary>
    /// <remarks>
    /// A window taken from the port alone started on another server's UDP port when the
    /// port was typed under RakNet and the form was then switched to NetherNet, whose
    /// own TCP port that server does not hold.
    /// </remarks>
    public static string? SuggestWindow(IReadOnlyList<PortClaim> claims, int port, string? publicAddress) =>
        FreeWindow(claims, port, PackageMaxPlayers) is { } first
            ? ServerUdpPorts.Suggest(publicAddress, first, PackageMaxPlayers)
            : null;

    /// <summary>
    /// The first number from <paramref name="start"/> up where a new server's sockets
    /// clash with no claim, with its suggested window under NetherNet when
    /// <paramref name="pinWindow"/> is set.
    /// </summary>
    /// <returns>Null when nothing up to 65535 is free.</returns>
    public static int? Next(
        IReadOnlyList<PortClaim> claims,
        ServerTransport transport,
        bool pinWindow,
        int start = ServerPorts.DefaultPort)
    {
        for (var candidate = Math.Max(1, start); candidate < 65535; candidate++)
        {
            var window = pinWindow && transport is ServerTransport.NetherNet
                ? ServerUdpPorts.Parse(SuggestWindow(candidate, publicAddress: null))
                : null;

            if (window is { Kind: UdpPortsKind.Absent } && pinWindow && transport is ServerTransport.NetherNet)
            {
                // The window would run past 65535, so no number from here up has one.
                return null;
            }

            if (Clashes(claims, Wanted(transport, candidate, window)).Count == 0)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// The first port from <paramref name="start"/> up where a play window
    /// <paramref name="width"/> ports wide runs into no UDP claim (<c>T415</c>).
    /// </summary>
    /// <remarks>
    /// The window a server switched to NetherNet, or told to pin its play ports, is
    /// offered at: from its own <c>server-port</c> when that is free, past whatever
    /// the machine has open when it is not. A window taken from the server port alone
    /// landed on another program's UDP 19132 and the server was refused at start.
    /// </remarks>
    /// <returns>Null when no window that wide fits below 65536.</returns>
    public static int? FreeWindow(IReadOnlyList<PortClaim> claims, int start, int width)
    {
        if (width < 1)
        {
            return null;
        }

        for (var first = Math.Max(1, start); first + width - 1 <= 65535; first++)
        {
            var window = new PortSpan(first, first + width - 1);
            var taken = claims
                .Where(claim => claim.Protocol is PortProtocol.Udp && Overlap(window, claim.Ports))
                .ToList();

            if (taken.Count == 0)
            {
                return first;
            }

            // Past the furthest claim in the way; the loop's step makes it the port after.
            first = taken.Max(claim => claim.Ports.Last);
        }

        return null;
    }

    /// <summary>
    /// One sentence per holder <paramref name="wanted"/> runs into, naming only the
    /// ports it shares with each: <c>UDP 19132 is already open on this machine by …</c>.
    /// </summary>
    /// <param name="udpLabel">Put in front of each UDP sentence, to say which of the new server's settings wants the ports.</param>
    public static IReadOnlyList<string> Describe(
        IReadOnlyList<PortClaim> claims, IReadOnlyList<ReachableSockets> wanted, string? udpLabel = null) =>
        Clashes(claims, wanted)
            .Select(claim =>
                (claim.Protocol is PortProtocol.Udp ? udpLabel : null)
                + $"{Protocol(claim.Protocol)} {Shared(claim.Ports, wanted, claim.Protocol)} is {claim.Holder}.")
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// What goes in front of a UDP clash on a NetherNet server (<c>T458</c>): its port is
    /// TCP, and every UDP port it binds is a play port, in a window or on the port's
    /// UDP side. Without it the sentence read as RakNet's, as if the port were UDP.
    /// </summary>
    public const string PlayPortsLabel = "Play ports: ";

    /// <summary>Every claim one of <paramref name="wanted"/>'s sockets runs into, in claim order.</summary>
    public static IReadOnlyList<PortClaim> Clashes(IReadOnlyList<PortClaim> claims, IReadOnlyList<ReachableSockets> wanted) =>
        claims
            .Where(claim => wanted.Any(w => w.Protocol == claim.Protocol && Overlap(w.Ports, claim.Ports)))
            .ToList();

    /// <summary>
    /// What the form says about a number somebody typed: one sentence per holder, or
    /// none when the server's sockets are all free.
    /// </summary>
    public static IReadOnlyList<string> Warnings(
        IReadOnlyList<PortClaim> claims,
        ServerTransport transport,
        int port,
        ServerUdpPorts? window)
    {
        if (port is < 1 or > 65535)
        {
            return [$"{port} is not a port number; the range is 1 to 65535."];
        }

        return Describe(
            claims, Wanted(transport, port, window), transport is ServerTransport.NetherNet ? PlayPortsLabel : null);
    }

    /// <summary>What the form says under the warnings once <strong>Create</strong> was held back for them.</summary>
    public const string KeepPrompt = "Change the port, or press Create again to keep it.";

    /// <summary>
    /// Names the ports a create was held back over: null when nothing is in their way,
    /// otherwise a value that changes whenever the transport, port or window does.
    /// </summary>
    public static string? ClashKey(
        IReadOnlyList<string> warnings, ServerTransport transport, int port, string? window) =>
        warnings.Count == 0
            ? null
            : FormattableString.Invariant($"{transport}|{port}|{window?.Trim()}");

    /// <summary>
    /// Whether <strong>Create</strong> goes ahead (<c>T455</c>): at once when the ports
    /// are free, and on a second press when they are not and have not changed since the
    /// first. A warning shown as the field lost focus to the button was never read, and
    /// the server made was refused at its first start.
    /// </summary>
    /// <param name="clash"><see cref="ClashKey"/> for the form as it stands.</param>
    /// <param name="acknowledged">The key the last held-back press saw, if any.</param>
    public static bool MayCreate(string? clash, string? acknowledged) =>
        clash is null || string.Equals(clash, acknowledged, StringComparison.Ordinal);

    private static bool Overlap(PortSpan a, PortSpan b) => a.First <= b.Last && b.First <= a.Last;

    /// <summary>The part of a claim the new server wants, so a sentence names the ports in question.</summary>
    private static PortSpan Shared(PortSpan claimed, IReadOnlyList<ReachableSockets> wanted, PortProtocol protocol)
    {
        var first = int.MaxValue;
        var last = int.MinValue;

        foreach (var socket in wanted.Where(w => w.Protocol == protocol && Overlap(w.Ports, claimed)))
        {
            first = Math.Min(first, Math.Max(socket.Ports.First, claimed.First));
            last = Math.Max(last, Math.Min(socket.Ports.Last, claimed.Last));
        }

        return new PortSpan(first, last);
    }

    private static string Protocol(PortProtocol protocol) => protocol is PortProtocol.Tcp ? "TCP" : "UDP";
}
