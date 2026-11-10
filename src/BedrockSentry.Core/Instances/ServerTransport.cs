namespace BedrockSentry.Core.Instances;

/// <summary>
/// Which network transport a Bedrock server speaks — <c>server.properties</c>'s
/// <c>transport</c> key.
/// </summary>
public enum ServerTransport
{
    /// <summary>
    /// The transport every Bedrock server used before 1.26.50: gameplay over
    /// UDP on <c>server-port</c>, with <c>server-portv6</c> beside it.
    /// </summary>
    RakNet,

    /// <summary>
    /// Bedrock's newer transport. <c>server-port</c> becomes a TCP listener for
    /// signalling and gameplay moves to a separate UDP socket per player online,
    /// landing on that same port's UDP side unless <c>server-udp-ports</c> moves
    /// it — so nothing about a server's port model carries over from
    /// <see cref="RakNet"/>.
    /// </summary>
    NetherNet,
}

/// <summary>
/// The <c>transport</c> key, its spellings, and what an absent one means.
/// </summary>
/// <remarks>
/// <para>Here rather than at the call sites because the rule that matters is
/// counter-intuitive and is the kind this codebase keeps re-deriving backwards:
/// <strong>an absent key is RakNet, and does not follow whatever the current BDS
/// writes into a fresh file.</strong> Observed on BDS 1.26.51.1 with two servers
/// on the same build — a freshly extracted <c>server.properties</c> ships
/// <c>transport=nethernet</c> explicitly and binds TCP, while a long-running
/// folder with no <c>transport</c> line at all still binds UDP on
/// <c>server-port</c> and <c>server-portv6</c> with no TCP listener.
/// <c>ServerTransportTests</c> pins both readings now.</para>
///
/// <para>So one install holds both kinds at once, and the transport has to be
/// read from each instance's own file rather than inferred from the build.</para>
/// </remarks>
public static class ServerTransports
{
    /// <summary>The <c>server.properties</c> key.</summary>
    public const string PropertyKey = "transport";

    /// <summary>
    /// What a server the wrapper creates is set to (<c>D75</c>).
    /// </summary>
    /// <remarks>
    /// NetherNet since BDS 1.26.60, which prints a <c>TRANSPORT TYPE ERROR</c> for
    /// <c>transport=raknet</c> and runs NetherNet anyway, so a RakNet server's port model,
    /// firewall rows and play window describe ports it does not use (<c>T413</c>). The
    /// form pins a free play window beside it (<see cref="NewServerPorts"/>). A constant
    /// rather than a value written into both the request record and the form that fills
    /// it, which would be the same rule stated twice.
    /// </remarks>
    public const ServerTransport Default = ServerTransport.NetherNet;

    /// <summary>
    /// What to tell the administrator of a server set to <paramref name="transport"/>, or
    /// null when there is nothing to say.
    /// </summary>
    /// <remarks>
    /// Said for every RakNet server rather than by build: the wrapper cannot tell from a
    /// version string which later builds will refuse it, and the cost of the sentence on
    /// an older build is a switch nobody needs yet (<c>T413</c>).
    /// </remarks>
    public static string? Warning(ServerTransport? transport) => transport is ServerTransport.RakNet
        ? "This server is set to RakNet. Bedrock 1.26.60 and later take players only over NetherNet: "
          + "the console prints a TRANSPORT TYPE ERROR at start, and the port, firewall and play-port "
          + "rows here describe ports the server does not use. Set transport to nethernet on the "
          + "Configuration tab, with a server-udp-ports window of its own."
        : null;

    /// <summary>How BDS spells <paramref name="transport"/> in the file.</summary>
    public static string Spell(ServerTransport transport) => transport switch
    {
        ServerTransport.RakNet => "raknet",
        ServerTransport.NetherNet => "nethernet",
        _ => throw new ArgumentOutOfRangeException(nameof(transport)),
    };

    /// <summary>
    /// The transport <paramref name="value"/> names, or null for a spelling this
    /// wrapper does not know. Pass null for a key that is not in the file at all.
    /// </summary>
    /// <remarks>
    /// The three answers are deliberate. A missing key is RakNet, which is a
    /// measurement rather than a default. An unknown spelling is <em>unknown</em>
    /// and never quietly RakNet: a later BDS naming a third transport would
    /// otherwise be described by every port check as the one it is not, which is
    /// the failure this key exists to stop. A present-but-blank value is unknown
    /// for the same reason — nothing has measured what BDS does with one, and
    /// guessing reads identically to knowing.
    /// </remarks>
    public static ServerTransport? Parse(string? value)
    {
        if (value is null)
        {
            return ServerTransport.RakNet;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "raknet" => ServerTransport.RakNet,
            "nethernet" => ServerTransport.NetherNet,
            _ => null,
        };
    }
}
