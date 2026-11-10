using System.Text.Json;
using System.Text.Json.Serialization;
using BedrockSentry.Core.Instances;

namespace BedrockSentry.Core.Broker;

/// <summary>
/// The privileged operations the broker offers (milestone 18.5, <c>D46</c>). The list
/// is closed: there is no operation that runs a command, and none that takes a path,
/// a port or a script from its caller.
/// </summary>
/// <remarks>
/// <para>The parameters are the boundary, not the list. Each operation names what it
/// acts on by an identifier the broker resolves itself: an instance id it looks up in
/// the configuration, a shadow copy id it created, a build identity it fetches and
/// verifies. <see cref="BrokerArguments"/> states which fields each operation takes.</para>
/// <para>Numbered explicitly because the values cross a pipe between two executables;
/// a renumbering would make an old main service ask a new broker for the wrong thing.
/// The handshake (<see cref="BrokerContract.Version"/>) refuses that pairing anyway,
/// and the numbers stay fixed so the refusal is the only failure.</para>
/// </remarks>
public enum BrokerOperation
{
    /// <summary>
    /// An inbound firewall rule for one of the sockets the instance's own
    /// <c>server.properties</c> says players reach; the request names which.
    /// </summary>
    AllowServerPorts = 1,

    /// <summary>A Defender exclusion for the instance's working directory.</summary>
    ExcludeServerFolder = 2,

    /// <summary>Defender's exclusion list, which a non-administrator cannot read.</summary>
    ReadDefenderExclusions = 3,

    /// <summary>A loopback exemption for one of <see cref="BrokerArguments.LoopbackFamilies"/>.</summary>
    ExemptLoopback = 4,

    /// <summary>The Volume Shadow Copy service back to demand-start. No arguments.</summary>
    EnableVss = 5,

    /// <summary>A shadow copy of the volume holding the instance's working directory.</summary>
    CreateShadowCopy = 6,

    /// <summary>Releases a shadow copy, only one this broker created.</summary>
    ReleaseShadowCopy = 7,

    /// <summary>Bedrock servers running from the instance's working directory that the wrapper did not start (<c>D56</c>).</summary>
    FindOrphans = 8,

    /// <summary>Ends one orphan, after re-checking it against the instance's working directory.</summary>
    TerminateOrphan = 9,

    /// <summary><c>Modify</c> for the service account on a folder outside the data root.</summary>
    GrantFolder = 10,

    /// <summary>Downloads, verifies and unpacks a build into a fresh version folder.</summary>
    StageUpdate = 11,

    /// <summary>Launches the updater for the build staged last.</summary>
    ApplyUpdate = 12,

    /// <summary>
    /// Whether the broker answers, for the Readiness panel. No arguments, no effect; the
    /// answer's message is the broker's build.
    /// </summary>
    Status = 13,

    /// <summary>
    /// The machine's inbound firewall rules, which the firewall's own cmdlets refuse a
    /// service's virtual account (<c>T394</c>). No arguments.
    /// </summary>
    ReadFirewallRules = 14,

    /// <summary>
    /// The Minecraft packages installed for any user and the loopback exemptions,
    /// since listing every user's packages needs an administrator (<c>T395</c>). No arguments.
    /// </summary>
    ReadLoopbackExemptions = 15,

    /// <summary>
    /// Removes the inbound rules <see cref="AllowServerPorts"/> named for the instance,
    /// before its removal forgets it (<c>T400</c>).
    /// </summary>
    RemoveServerRules = 16,

    /// <summary>
    /// Labels the instance's rules with the name the configuration gives it now,
    /// keeping their ports (<c>T411</c>).
    /// </summary>
    RenameServerRules = 17,

    /// <summary>
    /// Moves the instance's rules to the sockets its own <c>server.properties</c> names
    /// now, rather than adding rules beside them (<c>T411</c>).
    /// </summary>
    UpdateServerRules = 18,

    /// <summary>
    /// Data Deduplication's volume rows, which Windows answers only an administrator,
    /// telling any other caller "Generic failure". No arguments; the answer's JSON is a
    /// <c>DeduplicationReading</c>.
    /// </summary>
    ReadDeduplication = 19,
}

/// <summary>Which folder <see cref="BrokerOperation.GrantFolder"/> grants.</summary>
public enum BrokerGrantTarget
{
    /// <summary>The instance's working directory; needs an instance id.</summary>
    InstanceWorkingDirectory = 1,

    /// <summary>The backups root, when Settings moved it outside the data root.</summary>
    BackupsRoot = 2,

    /// <summary>The server-packages root, when Settings moved it outside the data root.</summary>
    ServerPackagesRoot = 3,
}

/// <summary>One request to the broker.</summary>
/// <remarks>
/// Every field but <see cref="Operation"/> is optional, and
/// <see cref="BrokerArguments.Refuse"/> refuses a request that carries a field its
/// operation does not take as well as one that lacks a field it needs: a field that
/// is ignored today is one a later change starts reading without anyone checking it.
/// </remarks>
public sealed record BrokerRequest
{
    [JsonPropertyName("operation")]
    public BrokerOperation Operation { get; init; }

    [JsonPropertyName("instanceId")]
    public string? InstanceId { get; init; }

    [JsonPropertyName("grantTarget")]
    public BrokerGrantTarget? GrantTarget { get; init; }

    [JsonPropertyName("packageFamily")]
    public string? PackageFamily { get; init; }

    [JsonPropertyName("shadowId")]
    public string? ShadowId { get; init; }

    [JsonPropertyName("processId")]
    public int? ProcessId { get; init; }

    [JsonPropertyName("buildIdentity")]
    public string? BuildIdentity { get; init; }

    /// <summary>The first port of the socket to open; one of the instance's own.</summary>
    [JsonPropertyName("firstPort")]
    public int? FirstPort { get; init; }

    /// <summary>The last port of the socket to open; equal to the first for one port.</summary>
    [JsonPropertyName("lastPort")]
    public int? LastPort { get; init; }

    [JsonPropertyName("protocol")]
    public PortProtocol? Protocol { get; init; }
}

/// <summary>The broker's answer to one request.</summary>
/// <param name="Succeeded">Whether the operation ran and did what it was asked.</param>
/// <param name="Message">What a person reads: the result, or why it was refused or failed.</param>
/// <param name="Refused">True when a rule refused the request, before anything ran.</param>
/// <param name="Json">A read's raw answer, for the same parser the in-process read feeds.</param>
/// <param name="ShadowId">The shadow copy <see cref="BrokerOperation.CreateShadowCopy"/> took.</param>
/// <param name="DeviceObject">Where that shadow copy's contents are read from.</param>
/// <param name="ProcessId">The process <see cref="BrokerOperation.ApplyUpdate"/> started.</param>
public sealed record BrokerResponse(
    [property: JsonPropertyName("succeeded")] bool Succeeded,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("refused")] bool Refused = false,
    [property: JsonPropertyName("json")] string? Json = null,
    [property: JsonPropertyName("shadowId")] string? ShadowId = null,
    [property: JsonPropertyName("deviceObject")] string? DeviceObject = null,
    [property: JsonPropertyName("processId")] int? ProcessId = null)
{
    /// <summary>A request a rule refused; nothing ran.</summary>
    public static BrokerResponse Refusal(string why) => new(false, why, Refused: true);

    /// <summary>
    /// No broker answered, or one answered that this build cannot talk to; nothing ran.
    /// Built on the caller's side only, never sent.
    /// </summary>
    public static BrokerResponse Unreachable(string why) => new(false, why);
}

/// <summary>The first message on a connection, from either side.</summary>
/// <param name="ContractVersion">The sender's <see cref="BrokerContract.Version"/>.</param>
/// <param name="BuildIdentity">The sender's build, for the log only.</param>
public sealed record BrokerHello(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("buildIdentity")] string BuildIdentity);

/// <summary>The version handshake.</summary>
public static class BrokerContract
{
    /// <summary>
    /// Raised whenever <see cref="BrokerOperation"/>, <see cref="BrokerRequest"/> or an
    /// argument rule changes. Both executables ship in one <c>versions\&lt;id&gt;</c>
    /// folder, so a mismatch means a half-applied update or a hand-copied file.
    /// </summary>
    public const int Version = 6;

    /// <summary>
    /// How both sides read and write the contract's messages. An unknown member is an
    /// error and names are matched exactly, so a field
    /// <see cref="BrokerArguments.Refuse"/> does not know is refused at the wire
    /// rather than dropped before the rule could see it.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
    };

    /// <summary>
    /// Why a peer that sent <paramref name="hello"/> is refused, or null when it is
    /// at this contract version.
    /// </summary>
    /// <remarks>
    /// Equality, not "at least": neither side can know what a newer peer's operations
    /// or fields mean, and a broker reading an older request by newer rules is the
    /// skew the version exists to stop.
    /// </remarks>
    public static string? Refuse(BrokerHello? hello)
    {
        if (hello is null)
        {
            return "The peer did not say which broker contract it speaks.";
        }

        return hello.ContractVersion == Version
            ? null
            : $"The peer ({Shown(hello.BuildIdentity)}) speaks broker contract {hello.ContractVersion}; "
              + $"this build speaks {Version}. Reinstall so both services come from one release.";
    }

    /// <summary>
    /// A peer's build identity as it may appear in a refusal or a log: the peer
    /// wrote it, so it is cut short and loses anything that could start a new line, the Unicode
    /// line and paragraph separators included.
    /// </summary>
    public static string Shown(string? identity)
    {
        if (string.IsNullOrEmpty(identity))
        {
            return "an unknown build";
        }

        var printable = new string(identity.Take(64).Select(c => char.IsControl(c) || c is '\u2028' or '\u2029' ? '?' : c).ToArray());

        return identity.Length > 64 ? printable + "…" : printable;
    }
}
