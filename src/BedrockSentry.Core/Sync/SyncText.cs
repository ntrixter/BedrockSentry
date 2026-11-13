using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Structures;

namespace BedrockSentry.Core.Sync;

/// <summary>The five steps of a sync, as the page and the console number them.</summary>
public enum SyncStep
{
    /// <summary>Saving this server's Sync Areas while it runs.</summary>
    Saving = 1,

    /// <summary>Stopping it, which writes the saved pieces to disk.</summary>
    Stopping = 2,

    /// <summary>Reading the saved pieces out of the stopped world.</summary>
    Reading = 3,

    /// <summary>Replacing its world with the source's backup and putting the pieces in its pack.</summary>
    Replacing = 4,

    /// <summary>Starting it and putting the Sync Areas back.</summary>
    PuttingBack = 5,
}

/// <summary>How a sync ended, or that it has not.</summary>
public enum SyncOutcome
{
    /// <summary>Still going.</summary>
    Running,

    /// <summary>The world was replaced and every kept piece put back.</summary>
    Finished,

    /// <summary>Stopped before this server's world changed; it is as it was.</summary>
    CalledOff,

    /// <summary>
    /// The world was replaced and not every kept piece was put back. The pieces are
    /// kept in the server's sync folder, and the world from before is in the safety
    /// backup.
    /// </summary>
    Incomplete,
}

/// <summary>Where a sync is, for the page, as of the moment it was read.</summary>
/// <param name="Step">The step under way, or the one it ended in.</param>
/// <param name="CurrentPiece">The piece being saved or put back now, by its label; null between passes.</param>
/// <param name="Starting">Whether it is waiting for this server to start, before a pass.</param>
/// <param name="Message">The last notice, or how it ended.</param>
/// <param name="Detail">
/// When it has ended, the sentence the strip shows under the title: what the server
/// runs now, or why it was called off or stopped.
/// </param>
public sealed record SyncProgress(
    string InstanceId,
    string SourceName,
    string? BackupName,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc,
    SyncStep Step,
    SyncOutcome Outcome,
    int Pieces,
    int PiecesSaved,
    int PiecesPutBack,
    string? CurrentPiece,
    bool Starting,
    string? Message,
    string? Detail);

/// <summary>What a sync says, in the console and on the page (<c>D103</c>).</summary>
/// <remarks>
/// Here rather than in the page or the service, so a test can read every sentence.
/// The console's notices number the steps out of five whatever is kept. A step with
/// nothing to do says nothing: a sync that keeps no areas says so when it starts and
/// then numbers only the stop and the replace, and a server already stopped has no
/// stop to announce.
/// </remarks>
public static class SyncText
{
    public static string Started(string source, SyncPlan plan) => plan.Pieces.Count == 0
        ? $"Sync from {source} started. No areas are kept, so nothing is saved first."
        : $"Sync from {source} started. Step 1 of 5: saving this server's Sync Areas, {Count(plan.Pieces.Count, "piece")} "
          + $"in {Count(plan.Areas.Count, "area")}, about {ByteSize.Format(plan.EstimatedBytes)}.";

    public static string Stopping(int pieces) => pieces == 0
        ? "Step 2 of 5: stopping this server."
        : $"Step 2 of 5: stopping this server, which writes the saved {(pieces == 1 ? "piece" : "pieces")} to disk.";

    public static string Read(int pieces, long bytes) =>
        $"Step 3 of 5: read the {Count(pieces, "saved piece")} out of the stopped world, {ByteSize.Format(bytes)}.";

    public static string Replaced(string source, string backupName, string? safetyBackup) =>
        $"Step 4 of 5: replaced this server's world with {source}'s backup {backupName}. This server keeps its own "
        + "server.properties, name and ports"
        + (safetyBackup is null
            ? "; there was no world here before, so no safety backup was taken."
            : $"; the world from before is in the safety backup {safetyBackup}.");

    public static string PuttingBack(string source, int pieces) =>
        $"Step 5 of 5: putting back the {Count(pieces, "piece")}. Inside each, what {source}'s copy holds is replaced, "
        + "its entities too.";

    public static string Finished(string source, string backupName, TimeSpan took, SyncPlan plan) =>
        $"Sync from {source} finished in {Elapsed(took)}. {Outcome(source, backupName, plan)}";

    /// <summary>What a server runs after a sync that finished.</summary>
    public static string Outcome(string source, string backupName, SyncPlan plan) =>
        $"This server runs {source}'s world from {backupName}"
        + (plan.Areas.Count == 0
            ? "."
            : $", with {Names(plan.Areas.Select(a => a.Name).ToList())} kept from before.");

    /// <summary>
    /// <paramref name="message"/> as one sentence: an exception's text, which .NET writes
    /// for a file as <c>There is not enough space on the disk. : 'path'</c>, read as
    /// <c>…on the disk: 'path'.</c>
    /// </summary>
    public static string Reason(string? message)
    {
        var text = (message ?? "it failed").Trim();
        var at = text.IndexOf(". : '", StringComparison.Ordinal);

        if (at >= 0)
        {
            text = text[..at] + ": '" + text[(at + ". : '".Length)..];
        }

        return text.EndsWith('.') ? text : text + ".";
    }

    public static string CalledOff(string source, string reason) =>
        $"Sync from {source} called off before this server's world changed: {reason}";

    public static string Incomplete(string source, string reason, string piecesFolder) =>
        $"Sync from {source} stopped after this server's world was replaced: {IncompleteDetail(reason, piecesFolder)}";

    /// <summary>Why a sync stopped after the world was replaced, and where what it kept is.</summary>
    public static string IncompleteDetail(string reason, string piecesFolder) =>
        $"{reason} The saved pieces are kept in {piecesFolder}, and the world from before the sync is in its "
        + "safety backup.";

    /// <summary>Why a server in <paramref name="state"/> cannot start a sync now; null when it can.</summary>
    /// <remarks>
    /// A sync stops the server afterwards when it was not running before, so a
    /// server on its way up or down is neither and is refused until it arrives.
    /// A faulted server is not running and will not try again, so it counts as
    /// stopped.
    /// </remarks>
    public static string? Busy(InstanceState state) => state switch
    {
        InstanceState.Stopped or InstanceState.Running or InstanceState.Faulted => null,
        InstanceState.Starting => "This server is starting; sync once it is running or stopped.",
        InstanceState.Stopping => "This server is stopping; sync once it has stopped.",
        InstanceState.Backoff =>
            "This server exited and is waiting to start again; sync once it is running, or stop it first.",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unhandled state."),
    };

    /// <summary>Why a sync is refused when this server's own worlds cannot be read.</summary>
    public const string WorldUnreadable =
        "This server's worlds folder could not be read, so no safety backup of it can be taken. "
        + "Fix the folder's permissions and try again.";

    /// <summary>Why a start the sync made did not lead to a server it can use.</summary>
    public static string NotReady(string target, TimeSpan waited) =>
        $"{target} was not ready {Elapsed(waited)} after it was started.";

    /// <summary>What the dialog says before a sync, each sentence a fact about what is lost or kept.</summary>
    public static IReadOnlyList<string> Confirmation(string target, string source, string backupName, SyncPlan plan)
    {
        var lines = new List<string>
        {
            $"{target}'s world is replaced with {source}'s backup {backupName}. Everything outside the Sync Areas goes, "
            + $"and every player's inventory and position on {target} becomes what {source}'s world holds for them.",
        };

        lines.Add(plan.Areas.Count == 0
            ? $"No areas are kept, so nothing of {target}'s world comes back."
            : $"{Names(plan.Areas.Select(a => a.Name).ToList())} {(plan.Areas.Count == 1 ? "is" : "are")} saved first and put "
              + $"back over whatever {source} has there, entities included: {Count(plan.Pieces.Count, "piece")}, "
              + $"about {ByteSize.Format(plan.EstimatedBytes)}.");

        lines.Add(
            $"{target} stops, and starts again with the new world. A safety backup of its world is taken first, "
            + "and its Backups tab can restore it.");

        return lines;
    }

    /// <summary>
    /// A Sync Area's size as the Sync tab's table shows it: <c>100 × 384 × 80 blocks, 4 pieces</c>,
    /// or empty when it cannot be split, since the reason is listed under the table.
    /// </summary>
    /// <remarks>Says when the area stops at the world's top or bottom, which the corners alone do not show.</remarks>
    public static string AreaSize(StructurePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Box is not { } box)
        {
            return string.Empty;
        }

        var size = box.Size;
        return $"{size.X} × {size.Y} × {size.Z} blocks, {Count(plan.Pieces.Count, "piece")}"
               + (plan.HeightClamped ? $", stopping at y {box.Min.Y} to {box.Max.Y}" : string.Empty);
    }

    /// <summary>The Sync Areas together, under the Sync tab's table.</summary>
    public static string PlanSummary(SyncPlan plan) =>
        $"{Count(plan.Areas.Count, "area")}, {Count(plan.Pieces.Count, "piece")}, about "
        + $"{ByteSize.Format(plan.EstimatedBytes)} to save and put back at each sync.";

    /// <summary>A length of time as a sentence says it: <c>1 minute 29 seconds</c>.</summary>
    public static string Elapsed(TimeSpan took)
    {
        var seconds = Math.Max(0, (int)Math.Round(took.TotalSeconds));
        var minutes = seconds / 60;
        seconds %= 60;

        return (minutes, seconds) switch
        {
            (0, _) => Count(seconds, "second"),
            (_, 0) => Count(minutes, "minute"),
            _ => $"{Count(minutes, "minute")} {Count(seconds, "second")}",
        };
    }

    internal static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    internal static string Names(IReadOnlyList<string> names) => names.Count switch
    {
        0 => string.Empty,
        1 => names[0],
        _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}",
    };
}
