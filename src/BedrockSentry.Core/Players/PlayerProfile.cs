namespace BedrockSentry.Core.Players;

/// <summary>
/// What one player has done on one server. The per-instance half of the scope
/// split in <c>docs/design.md</c> § 7.1 — every field here is a fact about this player
/// <em>on this instance</em>, not about them generally.
/// </summary>
public sealed record PlayerInstanceActivity(
    string InstanceId,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset? LastSeenUtc,
    TimeSpan Playtime,
    int SessionCount,
    DateTimeOffset? OnlineSince)
{
    public bool IsOnline => OnlineSince is not null;
}

/// <summary>
/// One player in full: the global identity, and the per-instance activity broken
/// out beneath it.
/// </summary>
/// <remarks>
/// The two halves are deliberately not merged into a flat row. Nickname, notes
/// and the wrapper-admin flag belong to the person and there is one of each;
/// playtime, first-seen and online status belong to a person <em>and</em> a
/// server, and there is one per pair. Flattening them is how a page ends up
/// implying somebody is opped everywhere because they are opped somewhere.
/// </remarks>
public sealed record PlayerProfile(
    string Xuid,
    string Gamertag,
    string? Nickname,
    string? Notes,
    bool IsWrapperAdmin,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset? LastSeenUtc,
    TimeSpan TotalPlaytime,
    int TotalSessions,
    IReadOnlyList<PlayerInstanceActivity> Instances)
{
    /// <summary>Which server they are on right now, or null.</summary>
    public string? OnlineOn =>
        Instances.FirstOrDefault(i => i.IsOnline)?.InstanceId;

    /// <summary>What to call them: their own name unless somebody set one.</summary>
    public string Display => string.IsNullOrWhiteSpace(Nickname) ? Gamertag : Nickname;
}

/// <summary>What an import actually did, so the UI can report it rather than imply it.</summary>
public sealed record PlayerImportResult(
    int ProfilesAdded,
    int ProfilesUpdated,
    int SessionsAdded,
    int InstanceStateRows,
    IReadOnlyList<string> Warnings);
