namespace BedrockSentry.Core.Sync;

/// <summary>
/// What a server takes its world from, and which of its own areas it keeps
/// (<c>D103</c>).
/// </summary>
/// <remarks>
/// A sync copies in the source server's latest backup and then puts the kept
/// areas back over it, so a creative copy of a survival world keeps what was
/// built in creative. The source is only ever read through its backups.
/// </remarks>
public sealed class SyncSettings
{
    /// <summary>The instance whose latest backup a sync copies in; empty when this server takes none.</summary>
    public string SourceInstanceId { get; set; } = string.Empty;

    /// <summary>The areas a sync puts back, in the order they are saved and put back.</summary>
    /// <remarks>
    /// Written whole, never merged: the configuration binder appends to a list,
    /// so a shorter list merged over a longer one would keep the areas it dropped.
    /// Empty by default for the same reason.
    /// </remarks>
    public List<KeptAreaSettings> KeptAreas { get; set; } = [];
}

/// <summary>
/// One Sync Area (called a kept area before <c>D106</c>, and still <c>KeptAreas</c> in the
/// configuration): a name, a dimension and two corners, both included.
/// </summary>
public sealed class KeptAreaSettings
{
    /// <summary>What the console and the page call it, such as <c>Spawn town</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The dimension's id, such as <c>minecraft:nether</c>; empty is the overworld, which is
    /// what every area saved before <c>D106</c> is in.
    /// </summary>
    public string Dimension { get; set; } = string.Empty;

    /// <summary>The outline's colour in game, <c>#rrggbb</c>; empty takes the area's place in the palette.</summary>
    public string Color { get; set; } = string.Empty;

    /// <summary>Whether the area's outline is drawn in game for operators near it.</summary>
    public bool ShowInGame { get; set; }

    /// <summary>
    /// Whether a sync adds a ticking area over this area's two corners once the area is
    /// back, named after it (<c>T439</c>). The world a sync copies in carries its own
    /// ticking areas, not this server's, so one kept loaded here is lost without this.
    /// </summary>
    public bool KeepTicking { get; set; }

    public int X1 { get; set; }

    public int Y1 { get; set; }

    public int Z1 { get; set; }

    public int X2 { get; set; }

    public int Y2 { get; set; }

    public int Z2 { get; set; }
}
