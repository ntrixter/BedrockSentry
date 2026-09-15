namespace BedrockSentry.Services.Instances;

/// <summary>
/// The sentences about a port that is not free, in one place.
/// </summary>
/// <remarks>
/// <para>Three things render these: the Readiness panel on the Status page, the
/// Server properties tab beside the port field, and — since <c>T149</c> — the
/// supervisor when it refuses to launch. Before this class the
/// <c>Port {n} {reason}.</c> line was written out twice, once in
/// <c>ReadinessInspector</c> and once in a <c>.razor</c> file the test project
/// cannot see, and the crash-loop sentence lived in the inspector alone. Adding
/// a third copy for the supervisor would have been the <c>T126</c> shape
/// exactly: one rule, stated in three places, enforced in one.</para>
///
/// <para>Lives in <c>Services</c> rather than <c>Core</c> because
/// <see cref="PortConflict"/> does, and <c>Core</c> references nothing. That is
/// still one owner with three readers, and the test project reaches it.</para>
/// </remarks>
public static class PortConflictText
{
    /// <summary>
    /// Why a held port matters more than it looks. Shown by the Readiness panel
    /// under a conflict; the supervisor does not repeat it, because by the time
    /// the supervisor speaks it has already prevented the thing this warns about.
    /// </summary>
    public const string CrashLoopWarning =
        "A Bedrock server that cannot bind its port starts, binds nothing and exits, "
        + "which the watchdog reports as a crash loop with no mention of ports.";

    /// <summary>One conflict as a sentence: <c>Port 19136 is already open on this machine by bedrock_server (PID 8172).</c></summary>
    public static string Line(PortConflict conflict) =>
        $"Port {conflict.Port} {conflict.Reason}.";

    /// <summary>Every conflict, one sentence each, space-separated.</summary>
    public static string Describe(IReadOnlyList<PortConflict> conflicts) =>
        string.Join(" ", conflicts.Select(Line));

    /// <summary>
    /// What the supervisor records and prints when it refuses to launch.
    /// </summary>
    /// <remarks>
    /// <para>Names the holder — process name and PID wherever the operating
    /// system will give them, which is what lets an operator see that the thing
    /// in the way is a <c>bedrock_server</c> and not some unrelated program
    /// about to be blamed. That is a trust requirement, not decoration: the
    /// panel is about to tell somebody a server did not start because of a PID,
    /// and they need to be able to check.</para>
    ///
    /// <para>Says plainly that nothing was launched. The whole point of refusing
    /// is that the world was never opened by a second process, and a reason
    /// that did not say so would leave the operator wondering whether it had
    /// been.</para>
    /// </remarks>
    public static string RefusalReason(IReadOnlyList<PortConflict> conflicts) =>
        $"not started: {Describe(conflicts)} Nothing was launched, so the world was not opened. "
        + "Stop whatever holds the port, or change this server's, then start it again.";
}
