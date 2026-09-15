namespace BedrockSentry.Services.Instances;

/// <summary>
/// Asked once, immediately before a Bedrock server is launched, whether anything
/// on this machine would stop it binding its ports (<c>T149</c>).
/// </summary>
/// <remarks>
/// <para>This exists because of the order BDS does things in. A server whose
/// port is already held does not fail at the bind: it opens the world first,
/// runs a LevelDB repair on it if it finds anything to repair, and only
/// <em>then</em> discovers the port is taken and exits. Every retry the watchdog
/// makes is another process opening a database something else already has
/// open. On 2026-09-04 that was three repair passes against a live world in
/// under two minutes, and the world came out with files missing. The watchdog
/// then reported a crash loop with no mention of a port.</para>
///
/// <para>So the check has to happen <em>before</em> the process exists, not be
/// inferred from how it died. The Readiness panel has asked this exact
/// question for some time; this is the start path asking it too, through the
/// same answer.</para>
///
/// <para>An interface rather than <see cref="PortInspector"/> itself for the
/// reason <c>IBedrockProcess</c> is one: the supervisor's state machine is
/// tested without a registry or a socket table, and a check that read the
/// machine's real UDP table would make every one of those tests depend on
/// whatever happens to be listening on the box running them. Null on the
/// supervisor means the check is off — the same convention as the discovery
/// guard and the player store.</para>
/// </remarks>
public interface IStartPreflight
{
    /// <summary>
    /// Everything in the way of <paramref name="instanceId"/> binding the ports
    /// its own <c>server.properties</c> names. Empty when it is clear to start.
    /// </summary>
    /// <remarks>
    /// Takes the working directory rather than a port list so the caller cannot
    /// pass the wrong ports: the file in that directory is the source of truth
    /// for what the server will try to bind, and reading it here is what keeps
    /// the supervisor and the Readiness panel answering the same question.
    /// </remarks>
    IReadOnlyList<PortConflict> ConflictsForStart(string instanceId, string workingDirectory);
}
