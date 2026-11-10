using System.Text.Json;

namespace BedrockSentry.Core.Addons;

/// <summary>Reading fields out of a pack's own JSON, once.</summary>
/// <remarks>
/// <para>Every reader here wants the same three answers — is the key there, is
/// it a usable string, is it a usable number — and each one written separately
/// is the shape this codebase keeps paying for. The rule that matters is the
/// one in <see cref="Text"/>: <strong>a key present and empty counts as
/// absent</strong>, so a pack shipping <c>"name": ""</c> falls back the same way
/// as one that omits the key, rather than rendering a blank row.</para>
///
/// <para>Internal on purpose. It is a parsing convenience, not part of what a
/// pack means, and a public one invites a caller to read a field this file has
/// not decided the rule for.</para>
/// </remarks>
internal static class PackJson
{
    /// <summary>The property, or null when the object does not carry it.</summary>
    internal static JsonElement? Field(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value)
            ? value
            : null;

    /// <summary>A non-empty string property, or null.</summary>
    internal static string? Text(JsonElement parent, string name) =>
        Field(parent, name) is { ValueKind: JsonValueKind.String } value
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    /// <summary>An int property, or null.</summary>
    internal static int? Integer(JsonElement parent, string name) =>
        Field(parent, name) is { ValueKind: JsonValueKind.Number } value
        && value.TryGetInt32(out var number)
            ? number
            : null;
}
