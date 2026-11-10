using System.Collections.Immutable;

namespace BedrockSentry.Core.Broker;

/// <summary>
/// The ports <see cref="BrokerOperation.AllowServerPorts"/> refuses to open, whatever
/// the instance's configuration says.
/// </summary>
/// <remarks>
/// The broker takes no port from its caller and reads the instance's own, but the
/// configuration is written by the service account, so a compromised main service
/// still chooses the number. These refusals bound what that buys: no well-known port
/// and none of the remote-administration and database ports an inbound rule would
/// expose to the network. Whether another instance already uses the port is the
/// broker's to check against the configuration when it resolves the instance.
/// </remarks>
public static class BrokerPortRules
{
    /// <summary>The first port outside the well-known range.</summary>
    public const int FirstAllowed = 1024;

    /// <summary>Registered ports above <see cref="FirstAllowed"/> that a server never needs.</summary>
    public static readonly ImmutableArray<int> Refused =
    [
        1433, // SQL Server
        3306, // MySQL
        3389, // Remote Desktop
        5432, // PostgreSQL
        5985, // WinRM over HTTP
        5986, // WinRM over HTTPS
    ];

    /// <summary>Why the broker refuses to open <paramref name="port"/>, or null.</summary>
    public static string? Refuse(int port)
    {
        if (port is < FirstAllowed or > 65535)
        {
            return $"Port {port} is outside {FirstAllowed}–65535; a Bedrock server does not need a well-known port.";
        }

        return Refused.Contains(port)
            ? $"Port {port} belongs to a remote-administration or database service, not a game server."
            : null;
    }

    /// <summary>
    /// Why the broker refuses to open <paramref name="first"/> to <paramref name="last"/>,
    /// or null: both ends, and every refused port between them.
    /// </summary>
    public static string? RefuseSpan(int first, int last)
    {
        if ((Refuse(first) ?? Refuse(last)) is { } end)
        {
            return end;
        }

        return Refused.FirstOrDefault(p => p >= first && p <= last) is var inside and not 0
            ? $"The span {first}-{last} includes port {inside}, which belongs to a remote-administration or database service."
            : null;
    }
}
