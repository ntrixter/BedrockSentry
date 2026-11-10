namespace BedrockSentry.Core.Instances;

/// <summary>Which transport-layer protocol a port is bound with.</summary>
public enum PortProtocol
{
    /// <summary>
    /// What every Bedrock server bound until 1.26.50, and what carries play on
    /// every Bedrock server since.
    /// </summary>
    Udp,

    /// <summary>
    /// <see cref="ServerTransport.NetherNet"/>'s signalling socket on
    /// <c>server-port</c>. No gameplay traffic crosses it — which is why the same
    /// number appears twice for such a server, once per protocol, rather than the
    /// TCP row replacing the UDP one.
    /// </summary>
    Tcp,
}

/// <summary>One socket a Bedrock server binds, as its own file describes it.</summary>
/// <param name="Key">The <c>server.properties</c> key the number came from.</param>
/// <param name="Port">The port number, or BDS's default when the key is unset.</param>
/// <param name="Protocol">Which table to look for it in, and which firewall rule opens it.</param>
public readonly record struct ServerPort(string Key, int Port, PortProtocol Protocol);

/// <summary>
/// Which ports a server binds, and on what protocol, given its transport
/// (<c>D70</c>).
/// </summary>
/// <remarks>
/// <para><strong>Everything that has ever said "the ports" in this codebase meant
/// a UDP pair</strong> — <c>server-port</c> and <c>server-portv6</c>, both UDP,
/// one instance. <see cref="ServerTransport.NetherNet"/> keeps none of that:
/// <c>server-port</c> becomes a <em>TCP</em> listener carrying an HTTP signalling
/// handshake, <c>server-portv6</c> is not bound at all, and gameplay moves to a
/// second set of UDP sockets, one per player online, whose numbers
/// <c>server-udp-ports</c> decides.</para>
///
/// <para>Measured on BDS 1.26.51.1 rather than taken from the release notes: a
/// server on port 19150 held <c>TCP 19150</c> listening and <c>UDP 60365</c>, with
/// nothing whatever on 19151. On the same machine and the same build, a server
/// whose file has no <c>transport</c> line held <c>UDP 19136</c> and
/// <c>UDP 19137</c> and no TCP at all. <c>ServerPortsTests</c> pins both.</para>
///
/// <para>So one install holds both models at once and the answer is per instance,
/// never install-wide. Here rather than at the call sites because the start
/// pre-flight, the Readiness panel, the firewall check, the discovery-pair guard
/// and the Status card all have to give the same answer, and a rule stated in five
/// places is enforced in none.</para>
///
/// <para><strong>⚠ NetherNet has TWO port cases and the first reading here saw
/// only one</strong> (<c>D71</c>). The capture above was taken with nobody
/// connected, and an idle server holds one socket out of the operating system's
/// ephemeral range; that was written up as <em>gameplay is ephemeral, so there is
/// no number to check and nothing a firewall rule could name</em>, and it is
/// false. Measured again with one client and then two: play sockets appear at one
/// per player online, and with <c>server-udp-ports</c> <strong>unset</strong> they
/// land on <c>server-port</c>'s <em>UDP</em> side — the same number as the TCP
/// listener. With a window set, nothing touches that number's UDP side at all.
/// The property does not widen the allocation window, it moves it.</para>
///
/// <para>So the unset case is checkable and forwardable, and saying otherwise is
/// what left the firewall row's fix adding a TCP rule for a server whose
/// play traffic needs UDP on the same number — a rule, a green row, and nobody
/// able to join (<c>T207</c>'s shape, one surface along). <see cref="PlayPorts"/>
/// is where the whole of it is described.</para>
///
/// <para><strong>The pinned window is deliberately not returned here, and it is
/// not an omission to tidy up.</strong> It is a run of ports rather than a socket,
/// so it would be <c>max-players</c> Readiness rows and <c>max-players</c> firewall
/// rules instead of one of each. And what this list feeds is a <em>refusal</em>:
/// the cost of missing one of those ports is a player who cannot join, and the
/// cost of over-gating is a server that will not start because something unrelated
/// holds one port out of ten. That is the opposite balance to <c>T149</c>'s, where
/// under-gating opens a live world twice — so the same rule, weighed again on this
/// case, comes out the other way. Reporting a held window belongs on the panel,
/// which is advisory and has nothing to refuse.</para>
/// </remarks>
/// <summary>
/// A run of ports the outside world has to reach, and on what protocol
/// (<c>T225</c>).
/// </summary>
/// <param name="Ports">One port, or a configured play window.</param>
/// <param name="Protocol">Which firewall rule opens it.</param>
public readonly record struct ReachableSockets(PortSpan Ports, PortProtocol Protocol);

public static class ServerPorts
{
    /// <summary>BDS's own <c>server-port</c> when the key is absent or unparseable.</summary>
    public const int DefaultPort = 19132;

    /// <summary>BDS's own <c>server-portv6</c> when the key is absent or unparseable.</summary>
    public const int DefaultPortV6 = 19133;

    /// <summary>The <c>server.properties</c> key for the IPv4 port.</summary>
    public const string PortKey = "server-port";

    /// <summary>The <c>server.properties</c> key for the IPv6 port.</summary>
    public const string PortV6Key = "server-portv6";

    /// <summary>
    /// The sockets a server on <paramref name="transport"/> binds. Pass null for
    /// <paramref name="port"/> or <paramref name="portV6"/> when the key is
    /// absent, commented out or unparseable — BDS then binds its own default, and
    /// so does this.
    /// </summary>
    /// <param name="transport">
    /// Null for a spelling this wrapper does not recognise, which is <em>not</em>
    /// the same as RakNet — see the remarks.
    /// </param>
    /// <param name="udpPorts">
    /// This instance's <c>server-udp-ports</c>, which decides whether a
    /// <see cref="ServerTransport.NetherNet"/> server puts play traffic on
    /// <c>server-port</c>'s UDP side. Null is read as
    /// <see cref="UdpPortsKind.Absent"/>, and the key is documented as ignored
    /// under <see cref="ServerTransport.RakNet"/>, where it changes nothing here.
    /// </param>
    /// <remarks>
    /// <para>An unset key is never "no port": BDS runs on its default, binds it,
    /// and holds it against everything else. Returning nothing for such a server
    /// left it with no start pre-flight and a Readiness row reading <em>Good, its
    /// ports are free</em> — an assurance rather than an absence, which is the
    /// worse of the two (<c>T202</c>).</para>
    ///
    /// <para><strong>An unrecognised transport returns the union of both models,
    /// which over-gates on purpose.</strong> <c>D70</c> rejected checking both
    /// protocols for every server, because a TCP holder would then falsely refuse
    /// a RakNet start — but that argument rests on knowing the server has no TCP
    /// port, and here the file says something this code has never seen. Refusing
    /// over a holder names it and can be acted on; a false clear opens a live
    /// world twice (<c>T149</c>). When over-gating and under-gating are not
    /// equally cheap, gate (<c>T175</c>).</para>
    ///
    /// <para><strong>An unreadable <paramref name="udpPorts"/> is checked on both
    /// protocols for the same reason</strong>, and not because it is likely to be
    /// one: a window this code cannot read says nothing about where play will
    /// land, so the choice is between naming a holder of a port that may be free
    /// and clearing a port that may be in use.</para>
    /// </remarks>
    public static IReadOnlyList<ServerPort> For(
        ServerTransport? transport,
        int? port,
        int? portV6,
        ServerUdpPorts? udpPorts = null)
    {
        var v4 = port ?? DefaultPort;
        var v6 = portV6 ?? DefaultPortV6;

        // Pinned means play moved off this number's UDP side entirely, so listing
        // it would be a row nothing binds -- which is the lie the NetherNet arm
        // was written to remove, arriving from the other direction.
        var playOnServerPort = udpPorts?.IsPinned is not true;

        return transport switch
        {
            ServerTransport.RakNet =>
            [
                new ServerPort(PortKey, v4, PortProtocol.Udp),
                new ServerPort(PortV6Key, v6, PortProtocol.Udp),
            ],

            // server-portv6 is absent rather than listed-and-ignored: a row
            // nothing binds is the lie this decision exists to remove, and an
            // entry here becomes a Readiness row, a firewall rule and a line on
            // the Status card.
            //
            // The UDP row beside it is the same rule applied to the case the first
            // reading missed. Both sockets are the SAME NUMBER and differ only by
            // protocol: signalling listens on TCP, and with no window set the play
            // socket lands on UDP. NetherNetPortsTests pins each arm.
            ServerTransport.NetherNet when playOnServerPort =>
            [
                new ServerPort(PortKey, v4, PortProtocol.Tcp),
                new ServerPort(PortKey, v4, PortProtocol.Udp),
            ],

            ServerTransport.NetherNet =>
            [
                new ServerPort(PortKey, v4, PortProtocol.Tcp),
            ],

            _ =>
            [
                new ServerPort(PortKey, v4, PortProtocol.Udp),
                new ServerPort(PortKey, v4, PortProtocol.Tcp),
                new ServerPort(PortV6Key, v6, PortProtocol.Udp),
                new ServerPort(PortV6Key, v6, PortProtocol.Tcp),
            ],
        };
    }

    /// <summary>
    /// What the outside world has to reach for players to connect and play
    /// (<c>T225</c>) — <c>server-port</c> on whichever protocols it uses, plus the
    /// configured play window when there is one.
    /// </summary>
    /// <remarks>
    /// <para><strong>A different question from <see cref="For"/>, and building
    /// firewall rows from that one was the defect.</strong> <see cref="For"/>
    /// answers <em>which sockets must be free for this launch</em>, and it
    /// deliberately leaves out a pinned play window because refusing a start over
    /// one port in ten is worse than a player who cannot join. A firewall row asks
    /// <em>can somebody out there reach this</em>, where leaving the window out
    /// means the administrator is told nothing about the ports they just
    /// configured.</para>
    ///
    /// <para><strong>Following the panel's own advice is what exposed it.</strong>
    /// The suggested <c>server-udp-ports</c> line starts at <c>server-port</c>, so
    /// applying it pins the window, <see cref="For"/> stops returning that number's
    /// UDP side, and play still binds inside the window — leaving those sockets
    /// with no firewall row at all. The panel got quieter because its instruction
    /// had been followed. <c>T151</c> clause 6 for the third time: two callers
    /// sharing an implementation is not the same as two callers asking one
    /// question.</para>
    ///
    /// <para><c>server-portv6</c> is deliberately absent, which is what the
    /// firewall surface already did by filtering on <see cref="PortKey"/>: the
    /// rows are about the port players are given, and a second family is a
    /// different conversation.</para>
    /// </remarks>
    public static IReadOnlyList<ReachableSockets> ReachableFor(
        ServerTransport? transport,
        int? port,
        int? portV6,
        ServerUdpPorts? udpPorts = null)
    {
        var sockets = For(transport, port, portV6, udpPorts)
            .Where(p => p.Key == PortKey)
            .Select(p => new ReachableSockets(new PortSpan(p.Port, p.Port), p.Protocol))
            .ToList();

        // The window, when one is set. PlayPorts is the single reader of that
        // question, so this cannot come to disagree with what the Status card
        // names as the play ports.
        if (PlayPorts.For(transport, port, udpPorts) is
            { Kind: PlayPortKind.Pinned, Span: { } window })
        {
            sockets.Add(new ReachableSockets(window, PortProtocol.Udp));
        }

        return sockets;
    }
}
