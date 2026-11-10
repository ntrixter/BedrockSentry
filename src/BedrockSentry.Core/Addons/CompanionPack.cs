namespace BedrockSentry.Core.Addons;

/// <summary>
/// The wrapper's side of what <c>companion-pack/</c> states about itself.
/// </summary>
/// <remarks>
/// Each value but <see cref="Author"/> is written once in the pack's JSON or
/// JavaScript and once here, and a test reads the pack's files and fails when the
/// two differ. They are two ends of one wire, and an edit to one end alone breaks
/// it silently.
/// </remarks>
public static class CompanionPack
{
    /// <summary>The header uuid in <c>manifest.json</c>, which is the pack's identity (<c>D79</c>).</summary>
    public const string Uuid = "d1054931-8a2d-4ca8-b90f-4c736d640de6";

    /// <summary>
    /// Whether a uuid is this pack's, ignoring case as the library does: a
    /// manifest's uuid is not guaranteed lower case.
    /// </summary>
    public static bool Is(string? uuid) =>
        string.Equals(uuid, Uuid, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The header uuid in <c>companion-resources/manifest.json</c>: the resource pack
    /// that defines the line outlines are drawn with (<c>D109</c>). It is switched on and
    /// off with the companion; the companion does not name it as a dependency, so a server
    /// without it still reports deaths and positions.
    /// </summary>
    public const string ResourceUuid = "85ae0ca9-3ded-45be-a457-84e96a3ec048";

    /// <summary>Whether a uuid is the companion's resource pack's, ignoring case.</summary>
    public static bool IsResource(string? uuid) =>
        string.Equals(uuid, ResourceUuid, StringComparison.OrdinalIgnoreCase);

    /// <summary>Who the Add-ons tab says wrote the pack.</summary>
    /// <remarks>
    /// Not read from <c>manifest.json</c>, which names no author: its version 1.0.0
    /// shipped without <c>metadata.authors</c>, and a shelf keeps a version it
    /// already holds exactly as it is, so adding the key now would give one
    /// version two manifests.
    /// </remarks>
    public const string Author = "BedrockSentry";

    /// <summary>The line format the pack prints: <c>PROTOCOL</c> in <c>scripts/main.js</c>.</summary>
    public const int Protocol = 1;

    /// <summary>
    /// What starts every line the pack prints, after whatever tags BDS puts in front:
    /// <c>MARKER</c> in <c>scripts/main.js</c>.
    /// </summary>
    public const string Marker = "[BedrockSentry] ";

    /// <summary>
    /// The pack setting deciding whether a player is told where they died. Set
    /// with the Add-ons tab's gear, because only the pack can act on it.
    /// </summary>
    public const string TellDeathLocationSetting = "bedrocksentry:tell_death_location";

    /// <summary>
    /// How near an operator must be to an edge, on every axis, to be drawn it:
    /// <c>DRAW_RADIUS</c> in <c>scripts/main.js</c>.
    /// </summary>
    public const int OutlineReach = 128;

    /// <summary>The first version that draws outlines (<c>D106</c> step 3).</summary>
    public const string OutlinesSince = "1.2.0";

    /// <summary>
    /// Whether a pack that said hello as <paramref name="packVersion"/> reads
    /// <c>scriptevent bedrocksentry:outline</c>. An older one would ignore it, so nothing is sent.
    /// </summary>
    public static bool DrawsOutlines(string? packVersion) =>
        PackVersion.Parse(packVersion).Version is { } version
        && version >= PackVersion.Parse(OutlinesSince).Version!.Value;
}
