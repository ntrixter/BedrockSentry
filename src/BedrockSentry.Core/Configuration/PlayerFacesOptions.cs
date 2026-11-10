namespace BedrockSentry.Core.Configuration;

/// <summary>
/// Whether the player lists show each player's face, fetched by XUID from a
/// third party (<c>D47</c>).
/// </summary>
public sealed class PlayerFacesOptions
{
    public const string SectionName = "Sentry:PlayerFaces";

    /// <summary>Who answers, in the words the Settings page shows beside the switch.</summary>
    public const string SourceName = "GeyserMC (api.geysermc.org)";

    /// <summary>
    /// Whether to ask <see cref="SourceName"/> for faces at all.
    /// </summary>
    /// <remarks>
    /// Off by default, unlike <see cref="HostDetailsOptions.DiscoverPublicAddress"/>,
    /// because of what the request carries: every player's XUID, sent from this
    /// server to a stranger. An install that never touches the setting never
    /// contacts anybody, and every row shows the generated face instead.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// How long a fetched face is used before it is asked for again. Long,
    /// because a skin changes rarely; a face past this age is still shown while
    /// the source cannot be reached.
    /// </summary>
    public TimeSpan CacheFor { get; set; } = TimeSpan.FromDays(7);
}
