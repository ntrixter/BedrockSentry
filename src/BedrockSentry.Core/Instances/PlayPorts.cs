namespace BedrockSentry.Core.Instances;

/// <summary>Where a server's gameplay traffic lands.</summary>
/// <remarks>
/// Separate from <see cref="ServerPorts.For"/>, which answers <em>which sockets
/// must be free for this launch</em>. The two overlap and are not the same
/// question: a pinned window is checkable in principle and is a run of ports
/// rather than a socket, and sharing one answer between two callers that want
/// different ones is the defect a shared method looks like the fix for
/// (<c>T151</c> clause 6).
/// </remarks>
public enum PlayPortKind
{
    /// <summary>
    /// <see cref="ServerTransport.RakNet"/>: play shares <c>server-port</c>, which
    /// <see cref="ServerPorts.For"/> already names. There is no second set.
    /// </summary>
    SameAsServerPort,

    /// <summary>
    /// <see cref="ServerTransport.NetherNet"/> with <c>server-udp-ports</c> unset:
    /// one socket per player online, on <c>server-port</c>'s UDP side.
    /// </summary>
    ServerPortUdp,

    /// <summary>
    /// <see cref="ServerTransport.NetherNet"/> with a window set: one socket per
    /// player online, taken from that window.
    /// </summary>
    Pinned,

    /// <summary>
    /// The transport is a spelling this wrapper does not know, its file could not
    /// be read, or <c>server-udp-ports</c> is set to something unreadable.
    /// </summary>
    Unknown,
}

/// <summary>
/// Which UDP ports a server's play traffic uses, and how to say so (<c>D71</c>).
/// </summary>
/// <param name="Kind">Which of the four cases this server is in.</param>
/// <param name="Span">
/// The ports, when they are knowable. A single port for
/// <see cref="PlayPortKind.ServerPortUdp"/>, since play lands on
/// <c>server-port</c>'s UDP side; the configured window for
/// <see cref="PlayPortKind.Pinned"/>; null otherwise.
/// </param>
/// <param name="Published">
/// The ports the outside world reaches, when <c>server-udp-ports</c> publishes a
/// NAT mapping whose external half differs from the internal one.
/// </param>
public readonly record struct PlayPorts(
    PlayPortKind Kind,
    PortSpan? Span = null,
    PortSpan? Published = null)
{
    /// <summary>
    /// Where <paramref name="transport"/> puts play traffic, given its
    /// <c>server-port</c> and its <c>server-udp-ports</c>.
    /// </summary>
    /// <remarks>
    /// <para><strong>Unreadable is its own answer and is never
    /// <see cref="PlayPortKind.ServerPortUdp"/>.</strong> That case carries a
    /// measurement — play on <c>server-port</c>'s UDP side — and a server whose
    /// window cannot be read is one the measurement was never taken on. Telling
    /// somebody to forward a port on that basis is a guess wearing the shape of an
    /// instruction.</para>
    /// </remarks>
    public static PlayPorts For(ServerTransport? transport, int? port, ServerUdpPorts? udpPorts)
    {
        if (transport is ServerTransport.RakNet)
        {
            return new PlayPorts(PlayPortKind.SameAsServerPort);
        }

        if (transport is not ServerTransport.NetherNet)
        {
            return new PlayPorts(PlayPortKind.Unknown);
        }

        var configured = udpPorts ?? ServerUdpPorts.Absent;

        return configured.Kind switch
        {
            UdpPortsKind.Absent => OnServerPort(),

            UdpPortsKind.LocalWindow or UdpPortsKind.NatMapping =>
                new PlayPorts(
                    PlayPortKind.Pinned,
                    configured.Local,
                    configured.External == configured.Local ? null : configured.External),

            _ => new PlayPorts(PlayPortKind.Unknown),
        };

        PlayPorts OnServerPort()
        {
            var number = port ?? ServerPorts.DefaultPort;
            return new PlayPorts(PlayPortKind.ServerPortUdp, new PortSpan(number, number));
        }
    }

    /// <summary>
    /// What to render on the Status card, in one sentence per case.
    /// </summary>
    /// <remarks>
    /// Here rather than in <c>StatusTab.razor</c>, which the test project cannot
    /// reference: a sentence stating which ports to forward is exactly the kind of
    /// rule this codebase keeps writing twice and checking once.
    /// <c>PlayPortsTests</c> pins one case each.
    /// <para>Short, because it is a value in a card's list and a long one
    /// stretched every card in the row; what the ports are for is
    /// <see cref="Explanation"/>, shown on hover.</para>
    /// </remarks>
    public string Sentence => Kind switch
    {
        PlayPortKind.SameAsServerPort => "the server port above",

        PlayPortKind.ServerPortUdp => $"UDP {Span}, the server port",

        PlayPortKind.Pinned when Published is { } outside => $"UDP {Span}, published as {outside}",

        PlayPortKind.Pinned => $"UDP {Span}",

        _ => "unknown",
    };

    /// <summary>
    /// The longer reading of <see cref="Sentence"/>, for its tooltip. Null where
    /// the sentence already says all there is.
    /// </summary>
    public string? Explanation => Kind switch
    {
        PlayPortKind.ServerPortUdp => "One socket per player online, on the server port's UDP side.",
        PlayPortKind.Pinned => "One socket per player online, taken from server-udp-ports.",
        PlayPortKind.Unknown => "This server's transport or server-udp-ports could not be read.",
        _ => null,
    };
}
