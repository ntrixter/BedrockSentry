namespace BedrockSentry.Core.Backups;

/// <summary>
/// Whether restoring an archive right now would take a safety backup, and what to
/// do when it cannot (<c>T168</c>).
/// </summary>
/// <remarks>
/// <para><strong>The defect this closes, and it is not the one that was filed.</strong>
/// The report was that an instance with no backup strategy enabled would record a
/// skipped safety backup and write nothing, because pressing <strong>Back up
/// now</strong> on such an instance does exactly that. The restore path does not go
/// through the orchestrator at all: it builds a <c>ZipBackupStrategy</c> itself with
/// <c>Enabled = true</c>, so the instance's strategy settings cannot skip it, and a
/// safety backup that fails aborts the restore. That premise was wrong.</para>
///
/// <para><strong>The promise is broken on a different branch.</strong> The safety
/// backup is skipped when <c>worlds/</c> lists no files — reasonably, since a zip of
/// nothing is a file pretending to be a backup. But <c>server.properties</c>, the
/// allowlist and the permissions file live in the <em>instance</em> directory, not
/// under <c>worlds/</c>, and <strong>Also restore this server's configuration</strong>
/// overwrites them. So on an instance with configuration and an empty world, the most
/// destructive action in the panel ran with no undo while the dialog said one was
/// taken, <c>RestoreResult.Summary</c> said the previous state was in it, and
/// <c>RestoreConfig</c>'s own comment said those files were already safe. Three
/// statements, one branch, all false.</para>
///
/// <para>Here in <c>Core</c> so the dialog and the restorer ask one question rather
/// than two that can disagree — and they do want the same answer, which is what makes
/// sharing it right rather than <c>T151</c> clause 6 again: "if I press Restore with
/// these options, will there be a way back?"</para>
/// </remarks>
public static class SafetyBackupRule
{
    /// <summary>
    /// What a restore would need, from what is on disk and whether the configuration
    /// is being restored too.
    /// </summary>
    /// <param name="worldHasFiles">Whether <c>worlds/</c> lists anything.</param>
    /// <param name="configFilesPresent">
    /// Whether the instance directory holds any of the configuration files a restore
    /// can overwrite. These are what the empty-world case missed.
    /// </param>
    /// <param name="restoringConfig">Whether the configuration box is ticked.</param>
    public static SafetyBackupNeed For(
        bool worldHasFiles, bool configFilesPresent, bool restoringConfig) =>
        (worldHasFiles, configFilesPresent && restoringConfig) switch
        {
            // A world to protect. The archive carries the configuration files with
            // it, so this covers both halves.
            (true, _) => SafetyBackupNeed.Required,

            // No world, and nothing else is being overwritten: the restore writes
            // into an empty worlds/ and there is genuinely nothing to lose.
            (false, false) => SafetyBackupNeed.NotNeeded,

            // No world, but configuration files would be overwritten — and a ZIP of
            // an empty manifest is refused by design, so no archive can be made to
            // hold them.
            (false, true) => SafetyBackupNeed.ImpossibleForConfig,
        };

    /// <summary>
    /// What the dialog says about the undo, before anybody commits to it.
    /// </summary>
    /// <remarks>
    /// It used to say "A safety backup is taken first either way", where "either way"
    /// meant running or stopped. It reads as a guarantee, and on one branch it was
    /// not one.
    /// </remarks>
    public static string Promise(SafetyBackupNeed need) => need switch
    {
        // Shortened at the operator's request (`D111`); it still promises only what the
        // `Required` arm delivers.
        SafetyBackupNeed.Required => "A safety backup is taken first, so you can undo this by restoring it.",
        SafetyBackupNeed.NotNeeded => "There is no world here to overwrite, so no safety backup is taken.",
        SafetyBackupNeed.ImpossibleForConfig => Refusal,
        SafetyBackupNeed.WorldUnreadable => UnreadableWorldRefusal,
        _ => Refusal,
    };

    /// <summary>
    /// Why the restore is refused, and the way out.
    /// </summary>
    /// <remarks>
    /// The remedy has to be one the operator can actually carry out. "Take a backup
    /// first" is not: <strong>Back up now</strong> on an empty world refuses for the
    /// same reason this does. Untick the configuration, which leaves a world restore
    /// that overwrites nothing, and is therefore safe on its own.
    /// </remarks>
    public const string Refusal =
        "This would overwrite this server's configuration files, and no safety backup can "
        + "be made to hold them: there is no world here, and an archive of nothing is "
        + "refused. Untick the configuration to restore the world on its own — that "
        + "overwrites nothing and needs no undo.";

    /// <summary>
    /// Whether this need is a refusal rather than a promise (<c>T244</c>).
    /// </summary>
    /// <remarks>
    /// One predicate because the dialog asks twice — once for the warning style, once
    /// for whether the confirm button may act — and `T244` added a state that both had
    /// to learn. Two lists of members in a <c>.razor</c> the suite cannot reach is this
    /// file's own recurring defect waiting to happen; here a test can hold it.
    /// </remarks>
    public static bool Refuses(SafetyBackupNeed need) =>
        need is SafetyBackupNeed.ImpossibleForConfig or SafetyBackupNeed.WorldUnreadable;

    /// <summary>
    /// Why a restore is refused when this server's own world cannot be read
    /// (<c>T244</c>).
    /// </summary>
    /// <remarks>
    /// <para><strong>A fourth state rather than a catch, because the third one lies.</strong>
    /// Treating an unreadable world as "it has files" reaches
    /// <see cref="SafetyBackupNeed.Required"/>, whose promise says a safety backup
    /// <em>is taken first, so undoing this is just restoring that one</em> — and a
    /// safety backup of a directory nobody can enumerate does not get taken. The
    /// conservative branch is the right ACTION and the wrong SENTENCE, which is
    /// `T176` arriving through the one door that looked safe.</para>
    ///
    /// <para><strong>Refused before the stop</strong>, like the guard beside it and for
    /// `T168`'s reason: finding this out after the server is down and the world has
    /// been moved aside costs uptime for an answer the disk gives for free.</para>
    /// </remarks>
    public const string UnreadableWorldRefusal =
        "This server's own world could not be read, so nothing here can say what a "
        + "restore would overwrite and no safety backup can be taken of it. Nothing has "
        + "been changed. Fix the folder's permissions and try again.";
}

/// <summary>What a restore needs before it may overwrite anything.</summary>
public enum SafetyBackupNeed
{
    /// <summary>Take one, and abort the restore if it fails.</summary>
    Required,

    /// <summary>Nothing is being overwritten, so there is nothing to protect.</summary>
    NotNeeded,

    /// <summary>
    /// Something is being overwritten and no safety backup can cover it. Refuse.
    /// </summary>
    ImpossibleForConfig,

    /// <summary>
    /// This server's own world could not be read, so what a restore would overwrite
    /// is unknown and no safety backup can be taken. Refuse (<c>T244</c>).
    /// </summary>
    WorldUnreadable,
}

/// <summary>
/// What a restore's safety backup actually did, as distinct from what was asked of
/// it.
/// </summary>
/// <remarks>
/// <c>T176</c>'s rule: count the states before choosing the type. This was a
/// <c>string</c> holding either an archive path or the sentence
/// "(no world to protect — nothing was overwritten)", which is how
/// <c>RestoreResult.Summary</c> came to end with "The previous state is in the safety
/// backup above." on a restore that took no safety backup at all.
/// </remarks>
public sealed record SafetyBackup(string? ArchivePath)
{
    /// <summary>Nothing was overwritten, so nothing was copied first.</summary>
    public static readonly SafetyBackup NothingToProtect = new((string?)null);

    public static SafetyBackup Taken(string archivePath) => new(archivePath);

    /// <summary>Whether there is something to restore in order to undo this.</summary>
    public bool Exists => ArchivePath is { Length: > 0 };

    /// <summary>The clause a summary adds about the undo. Empty when there is none.</summary>
    public string SummaryClause => Exists
        // Not "above": the sentence is shown under the server's controls, away from the list (`D111`).
        ? " The world from before is in a safety backup."

        // Deliberately says what happened rather than nothing at all: somebody
        // reading a restore summary is entitled to know no undo was recorded, even
        // when the reason is that there was nothing to undo.
        : " There was no world here to overwrite, so no safety backup was taken.";
}
