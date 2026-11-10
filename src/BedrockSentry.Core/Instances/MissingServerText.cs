namespace BedrockSentry.Core.Instances;

/// <summary>
/// What a start refused for want of a server says (<c>T245</c>).
/// </summary>
/// <remarks>
/// <para>An instance whose folder holds no <c>bedrock_server.exe</c> cannot be
/// launched, and used to be launched anyway: the process start threw, the
/// watchdog counted it as a crash, two backoffs later it gave up, and the
/// sentence an administrator was left with was <em>"gave up after 3 consecutive
/// failures; no further restarts will be attempted"</em> — which describes a
/// server that crash-loops, not one that was never installed. The same wrong
/// sentence arrived on every service start until the instance was removed.</para>
///
/// <para>Two surfaces state the honest version and they must not drift: the
/// refusal the supervisor records, and the Status tab's own <em>"No Bedrock
/// server installed here"</em> panel, which already calls this a setup step
/// rather than a failure. <see cref="OrphanText"/> and
/// <c>PortConflictText</c> are the same move for the other two refusals; this is
/// deliberately their sibling.</para>
///
/// <para><strong>It promises nothing about starting by itself.</strong> A
/// refusal is re-checked on <see cref="RefusedStartRetry.Schedule"/>, which is
/// four attempts over about two minutes and then stops, so an administrator who
/// installs a server an hour later has to start it. Saying otherwise would be a
/// sentence with no execution path behind it.</para>
/// </remarks>
public static class MissingServerText
{
    /// <summary>Why a launch was refused. Parallel to <see cref="OrphanText.RefusalReason"/>.</summary>
    /// <remarks>
    /// One entry point over all three refusing states, so the launch guard cannot
    /// pick the wrong sentence for the state it read (<c>T246</c>). The
    /// <see cref="ServerPresenceState.Installed"/> arm throws rather than
    /// returning something harmless: a caller that reaches here with a launchable
    /// server has a defect, and a bland sentence would hide it.
    /// </remarks>
    public static string RefusalReason(
        string workingDirectory, string executable, ServerPresence presence)
    {
        ArgumentNullException.ThrowIfNull(presence);

        return presence.State switch
        {
            ServerPresenceState.Missing =>
                $"not started: there is no {executable} in {workingDirectory}, so there is "
                + $"nothing here to run{Because(presence)}. This is a server that has not been "
                + "installed yet rather than one that crashed. Install a Bedrock server on this "
                + "instance's Status tab, or unpack one into that folder, then start it.",

            ServerPresenceState.Unreadable =>
                $"not started: {executable} is in {workingDirectory} and this service is not "
                + "allowed to read it. The server is installed — this is a permissions problem "
                + "rather than a missing server, and nothing has been changed. Give the account "
                + "the service runs as read access to that folder, then start it.",

            ServerPresenceState.Unknown =>
                $"not started: {workingDirectory} could not be read, so nothing here can say "
                + $"whether {executable} is in it{Because(presence)}. Nothing has been changed. "
                + "Give the account the service runs as read access to that folder, then start it.",

            // `T252`. This used to fall in with Unknown and inherit its closing
            // advice, which is to grant read access to a folder that is not there.
            // No access right creates a directory, so the repair is named instead:
            // put a server in that folder, or send the instance to the folder its
            // server is actually in.
            ServerPresenceState.NoDirectory =>
                $"not started: {workingDirectory} does not exist, so there is nothing here to "
                + "run. Nothing has been changed. Install a Bedrock server on this instance's "
                + "Status tab, which creates that folder — or point the instance at the folder "
                + "its server is already in, then start it.",

            // A discard rather than a named arm, and the guard is a test rather
            // than the compiler: naming every state leaves CS8524 standing for ever
            // over the cast-an-integer case, and a permanent warning is how a real
            // one goes unread. EveryRefusedStateHasASentence walks the enum and
            // names the state it could not render, which covers both methods here
            // and any state added after them (`T252` compiled clean against this
            // discard, which is how a missing arm would otherwise ship).
            _ => throw new ArgumentOutOfRangeException(
                nameof(presence),
                presence.State,
                "A launchable server has no refusal sentence."),
        };
    }

    /// <summary>
    /// Why a folder cannot be adopted as an existing server.
    /// </summary>
    /// <remarks>
    /// Adoption's own sentence rather than the launch one, because it is offering
    /// a different next step — and because <em>"it does not look like a Bedrock
    /// server folder"</em> is false of a folder whose server is simply not
    /// readable, and sends somebody to re-download a server they already have.
    /// </remarks>
    public static string AdoptionRefusal(
        string directory, string executable, ServerPresence presence)
    {
        ArgumentNullException.ThrowIfNull(presence);

        return presence.State switch
        {
            ServerPresenceState.Missing =>
                $"'{directory}' has no {executable} — it does not look like a Bedrock server "
                + "folder. To create a new server, use New instance instead.",

            ServerPresenceState.Unreadable =>
                $"'{directory}' has a {executable} that this service is not allowed to read. It "
                + "does look like a Bedrock server folder; give the account the service runs as "
                + "read access to it and add it again.",

            ServerPresenceState.Unknown =>
                $"'{directory}' could not be read{Because(presence)}, so nothing here can say "
                + "whether it holds a Bedrock server. Give the account the service runs as read "
                + "access to it and add it again.",

            // `T252`, and this arm is the ordinary path rather than a corner: the
            // adopt pre-flight used to answer it with its own Directory.Exists,
            // which reports false for a folder it may not traverse — so a denied
            // folder was told it does not exist, sending somebody to look for a
            // folder that is sitting there.
            ServerPresenceState.NoDirectory =>
                $"'{directory}' does not exist. Check the path, or use New instance to create a "
                + "server there.",

            _ => throw new ArgumentOutOfRangeException(
                nameof(presence), presence.State, "A launchable server is adoptable."),
        };
    }

    /// <summary>
    /// What else is known about this state, where anything is.
    /// </summary>
    /// <remarks>
    /// <para>Composed rather than interpolated inline because a state often has
    /// nothing to add, and a sentence that renders <c>" ()"</c> when it does not is
    /// the arm that is right by luck (<c>T187</c>).</para>
    ///
    /// <para><strong>Every arm that can carry something renders it</strong>, which
    /// is <c>T250</c>. It used to be the <see cref="ServerPresenceState.Unknown"/>
    /// arms alone, on the reading that Windows's own message is the only thing there
    /// is to add — and then the launch's own check had a count of what <em>is</em> in
    /// the folder, which separates a wrong folder from an empty one somebody was
    /// handed. Two different mistakes, and without this the sentence describes them
    /// identically. The alternative was a second sentence for one state, written
    /// somewhere else.</para>
    /// </remarks>
    private static string Because(ServerPresence presence) =>
        string.IsNullOrWhiteSpace(presence.Reason) ? string.Empty : $" ({presence.Reason})";
}
