using System;
using System.IO;

namespace BedrockSentry.Core.Backups;

/// <summary>
/// What became of one backup artifact somebody asked to delete.
/// </summary>
/// <remarks>
/// <para><strong>Four states rather than a <c>bool</c>, and the fourth is why
/// (<c>T176</c>).</strong> The two formats fail differently and the difference is a
/// property of the API rather than a detail: <c>File.Delete</c> is atomic, so a
/// refused archive is exactly as it was, while
/// <c>Directory.Delete(recursive: true)</c> deletes as it walks — so a folder copy
/// that fails part way is <em>destroyed</em> and no longer restorable. A type that
/// could say only <em>gone</em> and <em>not gone</em> would report the second of
/// those as a backup that is still there.</para>
///
/// <para><see cref="AlreadyGone"/> is separate from <see cref="Deleted"/> for the
/// reason <c>T252</c> records: folding a state into a neighbour hands the reader
/// the neighbour's sentence. Retention, or another browser tab, removes artifacts
/// under this panel's feet, and telling somebody their click deleted a file that
/// had gone an hour earlier is a claim the panel cannot support.</para>
/// </remarks>
public enum BackupDeletionState
{
    /// <summary>Gone, and this is what removed it.</summary>
    Deleted,

    /// <summary>
    /// Gone before this ran — pruned by retention, or removed from another page.
    /// </summary>
    AlreadyGone,

    /// <summary>
    /// Still there, whole and restorable. Nothing was touched.
    /// </summary>
    Refused,

    /// <summary>
    /// A folder copy that began deleting and then failed. An unknown amount is
    /// already gone, so it can no longer be restored.
    /// </summary>
    PartlyDeleted,
}

/// <summary>
/// The rules for turning a failed delete into a state, and a state into the
/// sentence the panel shows.
/// </summary>
/// <remarks>
/// <para><strong>The catch filter lives here beside the mapping, and that is the
/// point of the type</strong> (<c>T251</c>). Which exceptions count as a delete
/// failure, and which of them mean the artifact was never there, is one rule; split
/// between a <c>catch</c> clause in the deleter and a <c>switch</c> somewhere else
/// it is two, and the half that drifts is the one no test can reach. Both halves
/// are pure functions over an exception, so the arm that needs a locked file on
/// NTFS is still covered on a build machine that has neither.</para>
///
/// <para><strong><see cref="DirectoryNotFoundException"/> is an
/// <see cref="IOException"/></strong>, and <see cref="FileNotFoundException"/> is
/// too, so the absent cases are named first or they never exist
/// (<c>T252</c>). <see cref="UnauthorizedAccessException"/> is not one, which is
/// why it is named separately.</para>
/// </remarks>
public static class BackupDeletion
{
    /// <summary>
    /// Whether this exception is a delete refusing, rather than a fault the caller
    /// should not be swallowing.
    /// </summary>
    /// <remarks>
    /// Used as the <c>when</c> of the deleter's own catch, so the set is stated
    /// once. Anything outside it — a cancellation, an out-of-memory — travels on
    /// rather than being reported as a backup that would not delete.
    /// </remarks>
    public static bool IsADeleteFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

    /// <summary>
    /// Which state a thrown delete leaves the artifact in.
    /// </summary>
    /// <param name="exception">What the delete threw.</param>
    /// <param name="isFolder">
    /// Whether the artifact is a folder copy. This is the discriminator, not the
    /// exception type: the same <see cref="IOException"/> means <em>untouched</em>
    /// from an atomic <c>File.Delete</c> and <em>part destroyed</em> from a
    /// recursive directory delete.
    /// </param>
    public static BackupDeletionState From(Exception exception, bool isFolder)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // Named ahead of IOException on purpose: both derive from it, so the
        // general arm first would make these two unreachable.
        if (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return BackupDeletionState.AlreadyGone;
        }

        return isFolder ? BackupDeletionState.PartlyDeleted : BackupDeletionState.Refused;
    }

    /// <summary>
    /// Whether the artifact can still be restored after this outcome.
    /// </summary>
    /// <remarks>
    /// <see cref="BackupDeletionState.PartlyDeleted"/> answers <c>false</c>, which
    /// is the whole reason the state exists: the folder is still on disk and
    /// listing it would offer a restore that copies an arbitrary subset of a world
    /// over a live one (<c>T236</c>).
    /// </remarks>
    public static bool StillRestorable(BackupDeletionState state) =>
        state is BackupDeletionState.Refused;

    /// <summary>
    /// Whether this outcome is something an administrator has to act on, rather
    /// than the ordinary end of a delete.
    /// </summary>
    public static bool NeedsAttention(BackupDeletionState state) =>
        state is BackupDeletionState.Refused or BackupDeletionState.PartlyDeleted;

    /// <summary>
    /// What the panel says happened.
    /// </summary>
    /// <param name="state">The outcome.</param>
    /// <param name="name">The artifact's own file or folder name.</param>
    /// <param name="reason">
    /// Windows's own words, where there are any. A delete that is refused without
    /// throwing — a path outside this instance's backup folder — supplies its own.
    /// </param>
    /// <remarks>
    /// Every arm is written out rather than defaulted, and
    /// <c>BackupDeletionTests</c> walks the enum against this method: a state added
    /// later with no arm of its own throws here and fails that test by name, rather
    /// than rendering as one of the states it is not (<c>T252</c>).
    /// </remarks>
    public static string Sentence(BackupDeletionState state, string name, string? reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var because = string.IsNullOrWhiteSpace(reason) ? null : $" {reason.Trim()}";

        return state switch
        {
            BackupDeletionState.Deleted =>
                $"Deleted {name}.",

            BackupDeletionState.AlreadyGone =>
                $"{name} was already gone — retention or another page had removed it. "
                + "Nothing here was deleted, and the list below is up to date now.",

            BackupDeletionState.Refused =>
                $"{name} was not deleted and is still here, whole.{because}",

            // NOT "what survived is whatever the delete had not reached" (`T186`):
            // Directory.Delete(recursive: true) records the first failure, keeps
            // walking and keeps deleting, and rethrows at the end — so the survivors
            // are exactly the entries that refused, not a prefix of the walk.
            BackupDeletionState.PartlyDeleted =>
                $"{name} was part deleted before the failure, so it can no longer be "
                + "restored. What is left of it is only the entries Windows would not "
                + $"remove.{because} Delete it again once whatever is holding them has let go.",

            _ => throw new ArgumentOutOfRangeException(
                nameof(state),
                state,
                $"BackupDeletion.Sentence has no arm for {state}, so a delete in that state "
                + "would be described as one of the states it is not."),
        };
    }
}

/// <summary>
/// One delete, and what it left behind.
/// </summary>
/// <param name="State">What became of the artifact.</param>
/// <param name="Name">The artifact's own file or folder name.</param>
/// <param name="Reason">Why, where the state has a why. Null otherwise.</param>
public sealed record BackupDeletionOutcome(
    BackupDeletionState State, string Name, string? Reason = null)
{
    /// <summary>What the panel shows.</summary>
    /// <remarks>
    /// The sentence is composed here rather than in the two pages that could render
    /// it, because neither of them is reachable by the test suite (<c>T145</c>).
    /// </remarks>
    public string Summary() => BackupDeletion.Sentence(State, Name, Reason);

    /// <summary>Whether this outcome wants the fault style rather than the ordinary one.</summary>
    public bool NeedsAttention => BackupDeletion.NeedsAttention(State);
}
