namespace BedrockSentry.Core.Backups;

/// <summary>
/// What an administrator has said about one backup artifact: a note, and whether
/// it is locked (<c>D68</c>).
/// </summary>
/// <param name="Note">
/// Free text, or null. Why this one matters — <em>"before the road build"</em>,
/// <em>"last good world before the 1.26.51 upgrade"</em>.
/// </param>
/// <param name="Locked">
/// Never removed by retention, and not counted toward the retention number. Keep
/// 5 with 2 locked is 5 unlocked plus 2 locked on disk.
/// </param>
/// <remarks>
/// <para><strong>Keyed by artifact NAME, not by run, and not by path.</strong> One
/// backup run with both formats enabled writes <em>one</em>
/// <see cref="BackupRecord"/> and <em>two</em> artifacts, so "a note on a backup"
/// would be ambiguous per-run in a way a lock is not — and the two controls sit on
/// the same row of the same table, so they have to mean the same "this backup".
/// The name rather than the path because the path is written on one machine and
/// read on the panel, and because retention already decides from names
/// (<c>T198</c>).</para>
///
/// <para><strong>Not part of <see cref="BackupRecord"/>, and that was the fork.</strong>
/// The history records <em>what happened</em>; a lock is <em>present state</em>. A
/// history that gets rewritten when somebody ticks a box is no longer a history —
/// so this lives in its own file beside it, sharing the same directory, the same
/// orphan fate (<c>D63</c>) and the same atomic write.</para>
/// </remarks>
public sealed record BackupArtifactState(string? Note = null, bool Locked = false)
{
    /// <summary>Whether this state is worth storing at all.</summary>
    /// <remarks>
    /// Clearing a note and unlocking leaves an entry saying nothing, and a file
    /// that accumulates those is a file nobody can read by hand. The store drops
    /// them rather than writing empty objects.
    /// </remarks>
    public bool IsEmpty => !Locked && string.IsNullOrWhiteSpace(Note);

    /// <summary>
    /// What a Note cell shows for one artifact, given the administrator's note and
    /// the note the run was started with.
    /// </summary>
    /// <param name="artifactNote">What somebody typed against this artifact later.</param>
    /// <param name="runNote">
    /// What the backup was asked for at the time — <em>"before the update"</em>.
    /// Immutable; it is history.
    /// </param>
    /// <remarks>
    /// <para><strong>There are two notes and they are not the same thing</strong>
    /// (<c>D68</c>). The run note says why the backup was <em>taken</em> and belongs
    /// to the history; the artifact note says why it <em>matters</em> and is typed
    /// afterwards. Showing only one loses information in whichever direction is
    /// chosen, and letting the second overwrite the first would rewrite a
    /// history.</para>
    ///
    /// <para>So both are shown, artifact note first, joined by an em dash. Here
    /// rather than in the two pages that render backup rows, because that is the
    /// arrangement this codebase keeps paying for — and neither page can be reached
    /// by the test suite.</para>
    /// </remarks>
    public static string? DisplayNote(string? artifactNote, string? runNote)
    {
        var mine = string.IsNullOrWhiteSpace(artifactNote) ? null : artifactNote.Trim();
        var theirs = string.IsNullOrWhiteSpace(runNote) ? null : runNote.Trim();

        if (mine is null) { return theirs; }
        if (theirs is null) { return mine; }

        // Not concatenated when they are the same sentence: somebody who typed the
        // run note again as an annotation gets one copy rather than a stutter.
        return string.Equals(mine, theirs, StringComparison.OrdinalIgnoreCase)
            ? mine
            : $"{mine} — {theirs}";
    }
}
