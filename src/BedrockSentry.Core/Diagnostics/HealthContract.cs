using System.Text.Json;
using System.Text.Json.Serialization;

namespace BedrockSentry.Core.Diagnostics;

/// <summary>
/// What the two health endpoints expose, and the names the updater parses.
/// </summary>
/// <remarks>
/// Here in <c>Core</c>, on plain <c>net10.0</c> with no ASP.NET dependency
/// (<c>D9</c>), because the thing being pinned is a JSON shape rather than an
/// endpoint. The shape used to be an anonymous object inside a <c>static</c>
/// lambda in <c>Program.cs</c>, which the test project cannot reach at all — so
/// nothing stopped a rename of a field the updater parses by name, and the way
/// anybody found out would have been an update rolling back (`T165`).
/// </remarks>
public static class HealthContract
{
    /// <summary>
    /// The detailed endpoint's path: wrapper and instance state, able to report
    /// something other than healthy.
    /// </summary>
    /// <remarks>
    /// <para><strong>Deliberately not in <c>UpdateOptions</c>.</strong> That file
    /// holds the updater's frozen cross-version contract; this path is new, is
    /// read by nothing across a version boundary, and may be changed freely.
    /// Keeping them apart is what stops a future reader assuming this one is
    /// frozen too — or worse, assuming <c>HealthPath</c> is not.</para>
    ///
    /// <para><strong>This paragraph used to say the opposite, and the gate was
    /// written from it</strong> (<c>T175</c>). It said a single rooted segment was
    /// required because the gate matched with <c>PathString.Equals</c>, and that a
    /// prefix match was the hazard which "would then serve both endpoints on the
    /// public listener". The exact compare was the hazard: endpoint routing
    /// tolerates a trailing slash and <c>Equals</c> does not, so <c>/readyz</c> was
    /// refused on the UI listener while <c>/readyz/</c> returned the full body. A
    /// segment match is what would have prevented it, and is what
    /// <see cref="LoopbackOnlyPaths"/> now does — so a nested path under this one
    /// is gated rather than forbidden. The shape of the path was never the
    /// protection; the gate agreeing with routing is.</para>
    /// </remarks>
    public const string ReadyPath = "/readyz";

    /// <summary>
    /// The exact string the updater compares against, case-insensitively, before
    /// it accepts a replacement build (<c>UpdateRunner</c>).
    /// </summary>
    public const string HealthyStatus = "Healthy";

    /// <summary>Serializes a response body. One writer for both endpoints.</summary>
    public static string Serialize(HealthBody body) => JsonSerializer.Serialize(body);
}

/// <summary>
/// The health response body.
/// </summary>
/// <remarks>
/// <para><strong><c>status</c> and <c>version</c> are a frozen contract.</strong>
/// <c>UpdateRunner</c> reads them by name out of the build that replaced it: it
/// requires <c>status</c> to equal <c>Healthy</c> and <c>version</c> to equal the
/// identity it installed, and rolls the build back otherwise. Renaming either
/// breaks updates from every version that shipped before the rename — and it
/// breaks them silently, looking exactly like a genuinely broken build.</para>
///
/// <para><c>buildChannel</c> is not read by anything. It was called
/// <c>channel</c> until `T166`, which is the name that let two careful readers
/// cite it as the operator's <em>update</em> channel; it is the publish-time
/// build stamp and would have read the same whatever was configured.</para>
///
/// <para><c>detail</c> is omitted entirely on the liveness endpoint, so that
/// response carries only the three fields it always has.</para>
///
/// <para><strong>It is not byte-identical to what every previous build answered,
/// and this paragraph used to say it was</strong> (<c>T178</c>). The third field was
/// <c>channel</c> until <c>T166</c> renamed it to <c>buildChannel</c> — in the same
/// change that wrote the claim. Nothing broke, because <c>UpdateRunner</c> parses
/// <c>status</c> and <c>version</c> only, and that is the real frozen contract: two
/// field names and their meanings, not the whole body. The overstatement mattered
/// because it was repeated three times and used as the justification for the
/// liveness predicate selecting no checks — so the reason given for a load-bearing
/// decision was wrong everywhere it was written and enforced nowhere.</para>
/// </remarks>
public sealed record HealthBody
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("buildChannel")]
    public required string BuildChannel { get; init; }

    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<HealthDetail>? Detail { get; init; }
}

/// <summary>One named check's result, on the detailed endpoint only.</summary>
public sealed record HealthDetail
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }
}
