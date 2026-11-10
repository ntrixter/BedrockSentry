using System.Text.Json;
using System.Text.Json.Serialization;

namespace BedrockSentry.Core.Broker;

/// <summary>What <c>install.ps1</c> asks on the admin pipe (<see cref="BrokerNames.AdminPipe"/>).</summary>
public enum AdminOperation
{
    /// <summary>Warn players, drain every server and take any backup-on-shutdown.</summary>
    Drain = 1,

    /// <summary>Call off every drain still counting down (<c>D100</c>).</summary>
    CallOff = 2,
}

/// <summary>One request on the admin pipe.</summary>
public sealed record AdminRequest(
    [property: JsonPropertyName("operation")] AdminOperation Operation);

/// <summary>The answer to <see cref="AdminOperation.Drain"/>, in the fields the HTTP drain answered with.</summary>
public sealed record AdminDrainAnswer(
    [property: JsonPropertyName("stopped")] IReadOnlyList<string> Stopped,
    [property: JsonPropertyName("failed")] IReadOnlyList<string> Failed,
    [property: JsonPropertyName("calledOff")] bool CalledOff,
    [property: JsonPropertyName("warningSeconds")] int WarningSeconds,
    [property: JsonPropertyName("version")] string Version);

/// <summary>The answer to <see cref="AdminOperation.CallOff"/>.</summary>
public sealed record AdminCallOffAnswer(
    [property: JsonPropertyName("calledOff")] int CalledOff,
    [property: JsonPropertyName("tooLate")] int TooLate);

/// <summary>The answer to a request the pipe cannot read.</summary>
public sealed record AdminRefusal(
    [property: JsonPropertyName("error")] string Error);

/// <summary>
/// The admin pipe's messages: a four-byte little-endian length, then that many bytes
/// of UTF-8 JSON, one request and one answer per connection.
/// </summary>
/// <remarks>
/// Frozen with the installer contract (<c>docs/design.md</c> § 12.4): an installer
/// of any later version must be able to drain this one. So no handshake and no
/// version field; a new operation is a new name, and an old wrapper refuses it.
/// </remarks>
public static class AdminPipeContract
{
    /// <summary>
    /// Operations as camel-case names (<c>"drain"</c>, <c>"callOff"</c>), never numbers,
    /// and nothing the records do not name. Property names match exactly; an
    /// operation's name is read in any case, which is how the converter reads enums.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };
}
