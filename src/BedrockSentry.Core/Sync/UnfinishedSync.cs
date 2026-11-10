namespace BedrockSentry.Core.Sync;

/// <summary>
/// A sync that replaced a server's world and stopped before its Sync Areas were all put
/// back, kept so they can be put back later (<c>D103</c>).
/// </summary>
/// <param name="SourceName">The server the world came from, as it was named then.</param>
/// <param name="BackupName">The backup copied in.</param>
/// <param name="StoppedUtc">When the sync stopped.</param>
/// <param name="Reason">Why it stopped.</param>
/// <param name="Areas">
/// The Sync Areas as the sync saved them. Kept here rather than read from the settings, so
/// an area edited or removed since still names the pieces saved for it.
/// </param>
public sealed record UnfinishedSync(
    string SourceName,
    string BackupName,
    DateTimeOffset StoppedUtc,
    string Reason,
    IReadOnlyList<KeptAreaSettings> Areas)
{
    /// <summary>The areas split as the sync split them, so each piece has the name its file has.</summary>
    public SyncPlan Plan => SyncPlan.For(Areas);
}

/// <summary>What the Sync tab and the console say about pieces waiting to be put back.</summary>
public static class UnfinishedSyncText
{
    /// <summary>Why a new sync is refused while pieces wait.</summary>
    public static string SyncRefused(UnfinishedSync waiting) =>
        $"The last sync from {waiting.SourceName} stopped before its Sync Areas were put back. Put them back or "
        + "discard them first: a new sync would save this world's areas over the pieces waiting.";

    /// <summary>Why Put back is refused when nothing waits.</summary>
    public const string NothingWaiting = "No sync on this server is waiting for its Sync Areas to be put back.";

    /// <summary>A piece the sync folder no longer holds.</summary>
    public static string Missing(string label, string file, string folder) =>
        $"{label} cannot be put back: {file} is no longer in {folder}.";

    /// <summary>The panel's opening sentence.</summary>
    public static string Waiting(UnfinishedSync waiting, string stoppedAt) =>
        $"A sync from {waiting.SourceName} replaced this server's world at {stoppedAt} and stopped before "
        + $"{SyncText.Names([.. waiting.Plan.Areas.Select(a => a.Name)])} "
        + $"{(waiting.Plan.Areas.Count == 1 ? "was" : "were")} put back: {waiting.Reason}";

    /// <summary>The confirmation's heading.</summary>
    public static string ConfirmTitle(UnfinishedSync waiting) =>
        $"Put back {SyncText.Names([.. waiting.Plan.Areas.Select(a => a.Name)])}";

    /// <summary>What Put back does, said before it is confirmed.</summary>
    public static IReadOnlyList<string> Confirmation(string target, UnfinishedSync waiting)
    {
        var plan = waiting.Plan;
        return
        [
            $"Inside {SyncText.Names([.. plan.Areas.Select(a => a.Name)])}, what {target}'s world holds now is replaced "
            + $"with what was saved before the sync, entities included: {SyncText.Count(plan.Pieces.Count, "piece")}.",
            $"{target} stops if it is running and starts with the pieces in its structures pack. A server that was "
            + "stopped is stopped again after.",
        ];
    }

    /// <summary>What Discard does.</summary>
    public const string DiscardMeans =
        "Discard leaves the world as it is and lets a new sync run; the saved pieces stay in the folder until it does.";

    /// <summary>The console's first line of a put-back.</summary>
    public static string Started(string source, int pieces) =>
        $"Putting back the {SyncText.Count(pieces, "piece")} a sync from {source} saved before it stopped.";

    /// <summary>The console's line when the pieces are discarded.</summary>
    public static string Discarded(string source) =>
        $"The Sync Areas a sync from {source} saved were discarded; this server keeps the world it has now.";
}
