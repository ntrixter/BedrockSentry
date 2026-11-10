namespace BedrockSentry.Core.Backups;

/// <summary>
/// What the Backups tab's standing card says, in an administrator's words.
/// </summary>
/// <remarks>
/// <para>In <c>Core</c> rather than in the card's markup for the reason every
/// sentence in this codebase ends up here: the test project does not reference
/// the web project, so a sentence left in a <c>.razor</c> is checked on a real
/// page or not at all. <c>BackupStandingTextTests</c> walks every
/// <see cref="BackupStandingState"/> and fails on one nobody has written words
/// for, which is what stops a state added later rendering as an empty
/// card.</para>
///
/// <para><strong>No counts and no sizes in here.</strong> Those are a number
/// and a unit, which the card formats; these are the claims. Keeping them
/// apart is what lets the claims be tested without a renderer and stops a
/// pluralisation rule becoming something only a browser can check.</para>
/// </remarks>
public static class BackupStandingText
{
    // No "below", "above" or "on the right" anywhere in these sentences, and
    // that is a rule rather than a preference. The card carrying them is on
    // screen in both of the tab's views while the things they refer to are in
    // only one, so a direction word is false half the time and nothing would
    // fail when the layout moves again (`T162`). Name the section instead.

    /// <summary>The card's own line, four words or so.</summary>
    public static string Headline(BackupStandingState state) => state switch
    {
        BackupStandingState.NothingYet => "No backups yet",
        BackupStandingState.Protected => "Backed up",
        BackupStandingState.ProtectedButFailing => "The last backup failed",
        BackupStandingState.ProtectedButPartlyFailing => "Partly failed",
        BackupStandingState.NothingToRestore => "Nothing to restore",
        BackupStandingState.NeverSucceeded => "No backup has ever worked",

        // Named rather than defaulted: a state nobody has written words for is a
        // defect, and a bland sentence would ship it. The guard is a test walking
        // the enum rather than a fully-named switch, because naming every arm
        // leaves CS8524 standing for ever over the cast-an-integer case and a
        // permanent warning is how a real one goes unread.
        _ => throw new ArgumentOutOfRangeException(
            nameof(state), state, "No headline is written for this standing."),
    };

    /// <summary>The sentence under the headline, saying what to do about it.</summary>
    /// <param name="state">Where the server stands.</param>
    /// <param name="format">
    /// What would write a backup of it. With nothing switched on, neither taking one
    /// nor a schedule produces a backup, so that advice is not given (<c>T286</c>).
    /// </param>
    public static string Detail(
        BackupStandingState state, BackupFormatSummary.BackupFormatState format) => state switch
    {
        BackupStandingState.NothingYet when format == BackupFormatSummary.BackupFormatState.Disabled =>
            "Nothing has backed this server up, and nothing can until a backup type is switched "
            + "on under Settings.",

        BackupStandingState.NothingYet =>
            "Nothing has backed this server up. Take one now, or give it a schedule.",

        BackupStandingState.Protected =>
            "The newest one can be put back from the Restore list.",

        BackupStandingState.ProtectedButFailing =>
            "Older backups are still here and can be put back, but the newest one is "
            + "older than it should be. The History table says what went wrong.",

        // Nothing about the newest backup's age (`T302`): the type that worked may
        // have written it, or may be a custom command whose output this panel
        // cannot restore. The Restore list says which, so the sentence points there.
        BackupStandingState.ProtectedButPartlyFailing =>
            "The last backup worked for one backup type and failed for another. What "
            + "can be put back is in the Restore list, and the History table says which "
            + "type failed.",

        // The reachable case is a custom-command strategy, which is why the
        // sentence names it: its runs succeed, and this wrapper cannot read what
        // it writes, so a green history over an empty picker is correct rather
        // than broken and somebody has to be told which.
        BackupStandingState.NothingToRestore =>
            "Backups have run and worked, but none of them left anything this panel can "
            + "put back. A custom-command backup writes a format it cannot read, so it "
            + "is never offered here.",

        BackupStandingState.NeverSucceeded =>
            "Every backup of this server has failed, so there is nothing to put back. "
            + "The History table says why.",

        _ => throw new ArgumentOutOfRangeException(
            nameof(state), state, "No detail is written for this standing."),
    };
}
