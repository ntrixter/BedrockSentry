namespace BedrockSentry.Core.Configuration;

/// <summary>
/// What the status page is allowed to find out about the host.
/// </summary>
public sealed class HostDetailsOptions
{
    public const string SectionName = "Sentry:HostDetails";

    /// <summary>
    /// Whether to ask a third-party echo service for this machine's public
    /// address (<c>D20</c>).
    /// </summary>
    /// <remarks>
    /// On by default, and named here so it is disclosed rather than discovered.
    /// A machine cannot know its own public address — the only way to learn it
    /// is to have something outside tell you — so this is genuinely an outbound
    /// request to a third party, and worth being able to turn off. Defaulting it
    /// off would hide the answer to "what do I give my friends" behind a toggle
    /// most people never find, which is the wrong trade for a panel whose whole
    /// job is running a game server for other people.
    /// </remarks>
    public bool DiscoverPublicAddress { get; set; } = true;

    /// <summary>
    /// Where to ask. Anything that answers a bare GET with the caller's address
    /// as plain text works, so an administrator who would rather use their own
    /// endpoint can.
    /// </summary>
    public string PublicAddressEndpoint { get; set; } = "https://api.ipify.org";

    /// <summary>
    /// How long an answer is good for. Long, because a public address changes
    /// rarely and the page it feeds re-renders every second — without this the
    /// wrapper would hammer somebody else's service for the life of the process.
    /// </summary>
    public TimeSpan PublicAddressCacheFor { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The name players actually use, checked against the public address.
    /// Empty means no check.
    /// </summary>
    public string Domain { get; set; } = string.Empty;
}
