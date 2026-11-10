using BedrockSentry.Core.Instances;

namespace BedrockSentry.Core.Diagnostics;

/// <summary>
/// Every enabled inbound allow rule on the machine, with everything that decides
/// whether it applies, or why they could not be read.
/// </summary>
public sealed record FirewallRules(bool Read, string Json, string Error);

/// <summary>Defender's configured path exclusions, or why they could not be read.</summary>
public sealed record DefenderExclusions(bool Read, string Json, string Error);

/// <summary>What Minecraft is installed here and what is exempted, or why not.</summary>
public sealed record LoopbackExemptions(bool Read, string Json, string Error);

/// <summary>What the machine said about the Volume Shadow Copy service.</summary>
public sealed record VssState(bool Read, string Json, string Error);

/// <summary>
/// Reads the inbound firewall rules and adds one for a server's ports.
/// </summary>
/// <remarks>
/// The <c>D46</c> shape, like <see cref="IOrphanInspector"/>: an interface here and
/// the implementation in <c>Services</c>, so a caller does not know whether the work
/// runs in this process or in the privilege broker (milestone 18.5).
/// </remarks>
public interface IFirewallService
{
    /// <summary>Reads the machine's inbound rules, once.</summary>
    Task<FirewallRules> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds one rule covering <paramref name="ports"/> (<c>T225</c>).</summary>
    /// <param name="instanceId">
    /// The server the rule is for. The broker resolves the ports from this server's
    /// own files and refuses any it does not name; the name is for the rule's label.
    /// </param>
    Task<SystemCheckFix> AllowAsync(
        string instanceId,
        string instanceName,
        PortSpan ports,
        PortProtocol protocol,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every inbound rule <see cref="AllowAsync"/> named for a server, whatever
    /// its ports, so a removed server leaves no port open behind it (<c>T400</c>).
    /// </summary>
    /// <param name="instanceId">The server; the broker resolves its name itself.</param>
    /// <param name="instanceName">The name the rules carry, for the in-process implementation.</param>
    Task<SystemCheckFix> RemoveServerRulesAsync(
        string instanceId,
        string instanceName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Labels every rule of a server with its name, keeping their ports, so a renamed
    /// server's rules still match it (<c>T411</c>).
    /// </summary>
    /// <param name="instanceId">The server; the broker resolves its name itself.</param>
    /// <param name="instanceName">The name to label them with, for the in-process implementation.</param>
    Task<SystemCheckFix> RenameServerRulesAsync(
        string instanceId,
        string instanceName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a server's rules to the ports it uses now, adding one only where none is
    /// left to move and removing what opens a port it no longer uses (<c>T411</c>).
    /// </summary>
    /// <param name="instanceId">The server; the broker resolves its name and ports itself.</param>
    /// <param name="instanceName">Its name, for the in-process implementation.</param>
    /// <param name="sockets">Its sockets, for the in-process implementation.</param>
    Task<SystemCheckFix> UpdateServerRulesAsync(
        string instanceId,
        string instanceName,
        IReadOnlyList<ReachableSockets> sockets,
        CancellationToken cancellationToken = default);
}

/// <summary>Reads Defender's exclusion list and adds a folder to it.</summary>
/// <remarks>The <c>D46</c> shape; see <see cref="IFirewallService"/>.</remarks>
public interface IDefenderService
{
    /// <summary>Reads Defender's exclusion list, once.</summary>
    Task<DefenderExclusions> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Excludes a server's folder from real-time scanning.</summary>
    /// <param name="instanceId">
    /// The server. The broker resolves its folder from the configuration and never
    /// uses <paramref name="directory"/>, so the request cannot name another folder.
    /// </param>
    /// <param name="directory">The server's working directory, for the in-process implementation.</param>
    Task<SystemCheckFix> ExcludeAsync(
        string instanceId, string directory, CancellationToken cancellationToken = default);
}

/// <summary>Reads the installed Minecraft packages and grants one a loopback exemption.</summary>
/// <remarks>The <c>D46</c> shape; see <see cref="IFirewallService"/>.</remarks>
public interface ILoopbackExemptionService
{
    /// <summary>Reads the installed Minecraft packages and the exemption list, once.</summary>
    Task<LoopbackExemptions> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Grants the loopback exemption to the package <paramref name="family"/>.</summary>
    Task<SystemCheckFix> ExemptAsync(string family, CancellationToken cancellationToken = default);
}

/// <summary>Reads the Volume Shadow Copy service and sets it back to demand-start.</summary>
/// <remarks>The <c>D46</c> shape; see <see cref="IFirewallService"/>.</remarks>
public interface IVssServiceControl
{
    /// <summary>Reads the VSS service's status and start type, once.</summary>
    Task<VssState> ReadVssAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets VSS back to demand-start.</summary>
    Task<SystemCheckFix> EnableVssAsync(CancellationToken cancellationToken = default);
}
