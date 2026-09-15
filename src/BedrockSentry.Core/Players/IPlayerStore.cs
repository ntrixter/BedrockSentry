using BedrockSentry.Core.Instances;

namespace BedrockSentry.Core.Players;

/// <summary>A player connected right now, and which server they are on.</summary>
/// <remarks>
/// Online status is per-instance and the global view reports <em>which</em>
/// server rather than a yes/no column (<c>docs/design.md</c> § 7.1), which is why the
/// instance is on the row rather than implied by the query that produced it.
/// </remarks>
public sealed record OnlinePlayer(
    string Xuid,
    string Gamertag,
    string? Nickname,
    string InstanceId,
    DateTimeOffset ConnectedUtc);

/// <summary>
/// One player as a page shows them, with the aggregates that scope implies.
/// </summary>
/// <param name="OnlineOn">
/// Which instance they are on right now, or null when offline. Scoped: on an
/// instance's own page this is only ever that instance, so somebody playing
/// elsewhere reads as offline <em>here</em>, which is the truthful answer to the
/// question that page is asking.
/// </param>
/// <param name="FirstSeenUtc">
/// Globally the stored value that nothing may move (<c>D30</c>); per instance the
/// earliest connection to <em>that</em> server, which is a different fact and is
/// derived (<c>docs/design.md</c> § 7.1).
/// </param>
/// <param name="Playtime">
/// Summed over sessions in scope, never a stored counter. Open sessions count up
/// to the moment the caller asked, so a page left open does not freeze somebody's
/// total at the instant it loaded.
/// </param>
public sealed record PlayerSummary(
    string Xuid,
    string Gamertag,
    string? Nickname,
    string? OnlineOn,
    DateTimeOffset? OnlineSince,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset? LastSeenUtc,
    TimeSpan Playtime,
    int SessionCount)
{
    public bool IsOnline => OnlineOn is not null;

    /// <summary>How long the current session has run, or null when offline.</summary>
    public TimeSpan? CurrentSession(DateTimeOffset asOfUtc) =>
        OnlineSince is { } since ? asOfUtc - since : null;
}

/// <summary>
/// The player database (<c>docs/design.md</c> § 7). One store for the whole install:
/// identity is global, activity is per-instance, and both views read the same
/// rows.
/// </summary>
public interface IPlayerStore
{
    /// <summary>
    /// Everyone in scope — online and not. Null <paramref name="instanceId"/> is
    /// the global view; anything else is that server's own, and only players who
    /// have actually connected to it appear.
    /// </summary>
    Task<IReadOnlyList<PlayerSummary>> ListAsync(
        string? instanceId, DateTimeOffset asOfUtc, CancellationToken cancellationToken = default);

    /// <summary>One player in full, or null if nothing has ever seen that XUID.</summary>
    Task<PlayerProfile?> GetAsync(
        string xuid, DateTimeOffset asOfUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the fields an administrator owns rather than the server does. False
    /// when there is no such player, so a caller can say so instead of silently
    /// doing nothing.
    /// </summary>
    /// <remarks>
    /// Gamertag is not among them: it is what the server last called this person,
    /// and letting the panel overwrite it would put the wrapper's idea of a name
    /// in permanent disagreement with the console's. That is what the nickname is
    /// for.
    /// </remarks>
    Task<bool> UpdateProfileAsync(
        string xuid,
        string? nickname,
        string? notes,
        bool isWrapperAdmin,
        CancellationToken cancellationToken = default);

    /// <summary>Writes one end of a session, as read off the console (<c>D25</c>).</summary>
    Task RecordAsync(
        string instanceId,
        PlayerEvent playerEvent,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes every session still open on an instance, at the moment its process
    /// went (<c>D26</c>). For the case where the wrapper saw the exit and so knows
    /// when the sessions really ended.
    /// </summary>
    Task<int> CloseOpenSessionsAsync(
        string instanceId, DateTimeOffset endedUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes every session still open on an instance (<c>D26</c>). For the case
    /// where the wrapper did not see the exit, so there is no honest end time —
    /// losing a session beats inventing one, and beats counting an outage as
    /// playtime.
    /// </summary>
    Task<int> DropOpenSessionsAsync(string instanceId, CancellationToken cancellationToken = default);

    /// <summary>Who is online, across everything or on one instance.</summary>
    Task<IReadOnlyList<OnlinePlayer>> OnlineAsync(
        string? instanceId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether anyone was on this instance at any point since
    /// <paramref name="sinceUtc"/>. The question the scheduled-backup skip asks
    /// (<c>docs/design.md</c> § 8.1): a world nobody has touched since the last backup
    /// has nothing new worth archiving.
    /// </summary>
    Task<bool> AnyActivitySinceAsync(
        string instanceId, DateTimeOffset sinceUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Erases one person everywhere — profile, sessions, and per-instance state
    /// (<c>D29</c>). Returns how many rows went, so the caller can report what it
    /// actually did rather than implying it.
    /// </summary>
    Task<int> ForgetPlayerAsync(string xuid, CancellationToken cancellationToken = default);
}
