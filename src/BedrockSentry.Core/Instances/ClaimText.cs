namespace BedrockSentry.Core.Instances;

/// <summary>
/// What a server is busy with while one job claims its starts and stops, and the
/// sentences everything that waits for it says.
/// </summary>
/// <remarks>
/// A purpose is a phrase that fits both "busy with …" and "waits for … to end", so
/// a refusal and a deferral name the same job the same way.
/// </remarks>
public static class ClaimText
{
    /// <summary>A sync replacing the server's world (<c>D103</c>).</summary>
    public static string Sync(string source) => $"a sync from {source}";

    /// <summary>An area put back from a backup (<c>D108</c>).</summary>
    public static string AreaRestore(string title) => $"a restore of {title}";

    public const string ScheduledRestart = "a scheduled restart";

    /// <summary>A restart somebody asked for.</summary>
    public const string Restart = "a restart";

    /// <summary>
    /// Whether a job holding a claim for <paramref name="purpose"/> only stops and starts the
    /// server, so what waits for the stop, a queued add-on change, may be written between
    /// (<c>T417</c>). Every other job may be replacing the world or the folder.
    /// </summary>
    public static bool OnlyRestarts(string? purpose) => purpose is Restart or ScheduledRestart;

    public static string Update(string version) => $"an update to Bedrock {version}";

    public static string Restore(string backup) => $"a restore of {backup}";

    public const string Removal = "its removal";

    public const string Retirement = "its retirement";

    public const string Move = "a move to another folder";

    /// <summary>Why a start, a stop or another job is refused while the server is claimed.</summary>
    public static string Refusal(string server, string purpose) =>
        $"{server} is busy with {purpose}; try again once it ends.";

    /// <summary>The Start, Stop and Restart buttons' title while they wait.</summary>
    public static string ButtonsWait(string purpose) => $"Waits for {purpose} to end";

    /// <summary>A scheduled restart came due while the server was claimed.</summary>
    public static string RestartWaits(string purpose) =>
        $"A scheduled restart came due during {purpose}; it waits until that ends.";

    /// <summary>The claim ended with the server running and not started since the restart came due.</summary>
    public static string RestartAfterWait(string purpose) =>
        $"The scheduled restart that waited for {purpose} starts its countdown now.";

    /// <summary>The claim ended with the server started since the restart came due, which stands in for it.</summary>
    public static string RestartNotNeeded(string purpose) =>
        $"The scheduled restart that waited for {purpose} is not needed: the server has started since it came due.";

    /// <summary>The claim ended with the server stopped, so there is nothing to restart.</summary>
    public static string RestartDropped(string purpose) =>
        $"The scheduled restart that waited for {purpose} is dropped: the server is not running.";

    /// <summary>An automatic Bedrock update came due while the server was claimed.</summary>
    public static string UpdateWaits(string version, string purpose) =>
        $"Bedrock {version} is due and waits for {purpose} to end before it is installed.";
}
