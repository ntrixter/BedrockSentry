namespace BedrockSentry.Core.Instances;

/// <summary>
/// The server in an instance's folder cannot be started, and a relaunch will not
/// change that (<c>T250</c>).
/// </summary>
/// <remarks>
/// <para><strong>It exists so the launch can be refused rather than counted as a
/// crash.</strong> A present-but-denied <c>bedrock_server.exe</c> used to throw out
/// of the process start, land in the watchdog's accounting, and give up three
/// attempts later under <em>"exited unexpectedly after 0s"</em> and <em>"gave up
/// after 3 consecutive failures"</em> — measured as <strong>0</strong> launch lines
/// beside <strong>3</strong> failures, which is a sentence about a crash loop on a
/// process that was never started.</para>
///
/// <para><strong>The routing is by this type, never by reading which framework
/// exception arrived.</strong> A <see cref="System.IO.FileNotFoundException"/> out
/// of a process start can mean a missing dependency rather than a missing server,
/// and deciding from the type would be guessing at a state — the mistake
/// <c>T246</c> is made of, wearing a <c>catch</c> clause. Only the check that
/// established the state throws this, and it carries that state in
/// <see cref="Presence"/> so the caller states it rather than deducing it
/// again.</para>
///
/// <para><see cref="MissingServerText.RefusalReason"/> composes the sentence from
/// <see cref="Presence"/>, so the launch refusal reads identically whether the
/// state was established by the directory's own listing or by opening the file.
/// One sentence per state, which is what stops the two paths drifting.</para>
/// </remarks>
public sealed class ServerNotLaunchableException : Exception
{
    public ServerNotLaunchableException(
        string message, ServerPresence presence, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentNullException.ThrowIfNull(presence);

        // Refused at the throw site rather than where the sentence is composed. The
        // caller composing it is inside a catch block, so a throw there would
        // replace the exception it was handling and lose the launch's real answer —
        // a defect reporting itself as a different defect.
        if (presence.CanLaunch)
        {
            throw new ArgumentOutOfRangeException(
                nameof(presence),
                presence.State,
                "A server that can be launched is not a reason to refuse a launch.");
        }

        Presence = presence;
    }

    /// <summary>
    /// What was established about the executable, and Windows's own message where
    /// there was one.
    /// </summary>
    /// <remarks>
    /// Never <see cref="ServerPresenceState.Installed"/>: this is thrown only where
    /// a launch cannot happen, and a caller switching on it has no arm for a server
    /// that is fine.
    /// </remarks>
    public ServerPresence Presence { get; }
}
