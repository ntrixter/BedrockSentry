namespace BedrockSentry.Core.Commands;

/// <summary>The structures <c>locate structure</c> finds.</summary>
/// <remarks>
/// <para>The form types the name <c>string</c>, and a probe cannot check one: BDS 1.26.52.3
/// answers <c>No valid structure found within a reasonable distance</c> for
/// <c>zzbad</c>, the same words as for a real structure with none nearby. So these ship
/// as measured: each was found by <c>locate</c> on that version, <c>fortress</c> and
/// <c>bastion_remnant</c> from the nether and <c>end_city</c> from the end.</para>
///
/// <para><c>buriedtreasure</c> found the same structure as <c>buried_treasure</c> and is
/// left out as a second spelling.</para>
/// </remarks>
public static class StructureNames
{
    /// <summary>The names, sorted.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        "ancient_city", "bastion_remnant", "buried_treasure", "end_city", "fortress", "mansion",
        "mineshaft", "monument", "pillager_outpost", "ruined_portal", "ruins", "shipwreck",
        "stronghold", "temple", "trail_ruins", "trial_chambers", "village",
    ];
}
