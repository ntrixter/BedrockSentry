namespace BedrockSentry.Core.Instances;

/// <summary>
/// A <c>bedrock_server.exe</c> running out of an instance's own working
/// directory that this wrapper did not start (<c>T149</c>, <c>D56</c>).
/// </summary>
/// <param name="ExecutablePath">
/// Resolved from the process, not from configuration. It is what proves the
/// process belongs to this instance rather than to some other server on the
/// box, and it is half the guard that has to hold before anything is
/// terminated.
/// </param>
public sealed record OrphanedServer(int ProcessId, string ExecutablePath, DateTimeOffset? StartedUtc);

/// <summary>What a termination attempt did.</summary>
public sealed record OrphanTermination(bool Succeeded, string Detail);

/// <summary>
/// Finds, and on request ends, a Bedrock server holding an instance's world
/// that the wrapper did not launch (<c>D56</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> `T149`: the wrapper was killed ungracefully,
/// its children kept running, and the restarted wrapper launched a second copy
/// onto a world the first still had open. BDS opens *and repairs* a level
/// several seconds before it discovers its port is taken, so three watchdog
/// retries were three LevelDB repair passes against a live database and the
/// world came out with files missing.
/// </para>
/// <para>
/// <b>Why detecting is separate from the port pre-flight.</b>
/// <see cref="Services"/>' start pre-flight refuses a launch whose *port* is
/// held, and an orphan usually holds one — but the thing that must not happen is
/// a second process on the world, and the port is only a proxy for it. An orphan
/// whose configured port has since changed would pass the port check and still
/// destroy the world. This asks the question directly.
/// </para>
/// <para>
/// <b>Why it does not kill on its own.</b> Decided 2026-09-07 (`D56`). The
/// orphan's stdin and stdout died with the wrapper that spawned it, so the
/// wrapper cannot read its console and cannot know whether players are
/// connected. An orphan by itself is a working server; the damage came from the
/// second writer, which the refusal already prevents. Terminating one is a
/// person's call, made with the PID and the path in front of them.
/// </para>
/// <para>
/// The <c>D46</c> shape: interface here, one Windows implementation in
/// <c>Services</c>, a fake on a build machine. Two named operations, no general
/// "run this" — and every argument is one the wrapper derived itself.
/// </para>
/// </remarks>
public interface IOrphanInspector
{
    /// <summary>
    /// Bedrock servers running out of <paramref name="workingDirectory"/> whose
    /// process ids are not in <paramref name="ours"/>. Empty when there are
    /// none, and empty rather than throwing when the question cannot be asked.
    /// </summary>
    /// <param name="instanceId">
    /// The server. The broker resolves the folder from this and never uses
    /// <paramref name="workingDirectory"/>, which is for the in-process implementation.
    /// </param>
    Task<IReadOnlyList<OrphanedServer>> FindAsync(
        string instanceId,
        string workingDirectory,
        IReadOnlyCollection<int> ours,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends one, after re-checking the guard against the live process.
    /// </summary>
    /// <remarks>
    /// The re-check is not defensive tidiness. Between a page rendering a PID
    /// and somebody pressing the button, the orphan may have exited and Windows
    /// may have reissued its id to something else — so the identity is proved
    /// again, against the process as it is now, immediately before the kill.
    /// A guard that cannot be evaluated refuses: for an operation that ends a
    /// process, unknown is not permission.
    /// </remarks>
    Task<OrphanTermination> TerminateAsync(
        string instanceId,
        int processId,
        string workingDirectory,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The sentences about an orphaned server, in one place.
/// </summary>
/// <remarks>
/// Four surfaces say some of this — the console at startup, the instance page's
/// banner, the refusal a launch writes, and the entry left in backup history —
/// and `T126`, `T132`, `T136`, `T145` are all one rule stated in several places
/// and enforced in one. `PortConflictText` is the same move for the port
/// sentences; this is deliberately its sibling.
/// </remarks>
public static class OrphanText
{
    /// <summary>One orphan: <c>bedrock_server (PID 8172), running from C:\…</c>.</summary>
    public static string Line(OrphanedServer orphan) =>
        $"bedrock_server (PID {orphan.ProcessId}), running from {orphan.ExecutablePath}";

    public static string Describe(IReadOnlyList<OrphanedServer> orphans) =>
        string.Join("; ", orphans.Select(Line));

    /// <summary>
    /// What the console says when the wrapper finds one, and what the instance
    /// page shows beside the button.
    /// </summary>
    /// <remarks>
    /// Says the world is not being touched, because that is the operator's first
    /// question and the answer is reassuring: the refusal already happened, and
    /// one server on a world is the normal case. Then says what it costs to
    /// leave it — the wrapper cannot reach it, so a graceful stop and a backup
    /// are both off the table until it is gone.
    /// </remarks>
    public static string Found(IReadOnlyList<OrphanedServer> orphans, string instanceName) =>
        // "running" belongs here rather than in Count, because Count returns only
        // the copula so each caller can supply its own verb — RefusalReason says
        // "already running on this server's world" and this one says "running
        // for <instance>". `T161`: this sentence went out without any verb at all,
        // reading "A Bedrock server is for Survival", and it is the first thing an
        // operator reads when `D56` fires.
        $"{Count(orphans)} running for {instanceName} that this wrapper did not start: "
        + $"{Describe(orphans)}. The world is not being written by two processes — this server "
        + "will not be started while that one is running. But it cannot be reached from here: "
        + "its console died with the wrapper that launched it, so it cannot be asked to save or "
        + "stop. End it from the instance page when nobody is playing on it, then start the "
        + "server again.";

    /// <summary>Why a launch was refused. Parallel to `PortConflictText.RefusalReason`.</summary>
    public static string RefusalReason(IReadOnlyList<OrphanedServer> orphans) =>
        $"not started: {Count(orphans)} already running on this server's world — "
        + $"{Describe(orphans)}. Nothing was launched, so the world was not opened twice. "
        + "End that process from the instance page, then start this server again.";

    /// <summary>The line left in backup history when one is ended (`D56`, requirement 3).</summary>
    /// <remarks>
    /// <para>The point of recording it in the timeline rather than annotating an
    /// archive is that an operator's real question is "which of these can I trust",
    /// and the answer is "the ones taken before". It says the unflushed-state cost
    /// out loud, because a terminated BDS loses whatever it had in memory and had
    /// not written — which is exactly what <c>save hold</c> exists to force and what
    /// an orphan cannot be asked to do.</para>
    ///
    /// <para><strong>It names the archive rather than pointing a direction</strong>
    /// (<c>T162</c>). This note used to read "Backups listed above this line were
    /// taken before it was ended" — and the history table is newest-first, so
    /// "above" was precisely the archives taken <em>after</em> the kill. It sent the
    /// operator to the wrong half of the one list <c>D56</c> requirement 3 exists to
    /// divide. Flipping the word would have fixed it until somebody changed the sort
    /// order, which lives in a <c>.razor</c> file no test can see; a named archive
    /// cannot be re-broken that way.</para>
    /// </remarks>
    /// <param name="lastArchiveBefore">
    /// File name of the newest archive taken before the incident, or null when
    /// there was none.
    /// </param>
    public static string IncidentNote(OrphanedServer orphan, string? lastArchiveBefore = null) =>
        $"Ended an orphaned {Line(orphan)}. It could not be asked to save first, so anything it "
        + "held in memory and had not written is lost. "
        + (lastArchiveBefore is { Length: > 0 } name
            ? $"The newest backup taken before this was {name} — it and anything older predate "
              + "the incident."
            : "No backup had been taken before this, so nothing here predates the incident.");

    private static string Count(IReadOnlyList<OrphanedServer> orphans) =>
        orphans.Count == 1 ? "A Bedrock server is" : $"{orphans.Count} Bedrock servers are";
}
