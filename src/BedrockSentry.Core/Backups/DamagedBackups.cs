namespace BedrockSentry.Core.Backups;

/// <summary>
/// Whether the wrapper's own retention destroyed a backup, asked of the history
/// it wrote at the time (<c>T236</c>).
/// </summary>
/// <remarks>
/// <para><strong>The restore guard beside this one asks a different question and
/// cannot answer this one.</strong> <c>WorldRestorer</c> refuses a folder holding
/// no world files, which is <em>is there anything to copy</em>. A folder the
/// prune gutted down to one surviving file answers yes and is still not a world:
/// restoring it replaces a live world with that one file and reports the count as
/// an ordinary fact.</para>
///
/// <para><strong>Nothing on disk can tell an intact small backup from a destroyed
/// large one</strong>, because a world is opaque bytes here and the wrapper knows
/// no format that would say how many files one needs. What it does know is what
/// it broke itself: <c>Directory.Delete(recursive: true)</c> deletes as it walks
/// and rethrows at the end (<c>T186</c>), and the prune records every artifact it
/// was part-way through when it threw. That record is certain where a file count
/// is a guess, which is why the refusal is built on it and not on a threshold.</para>
///
/// <para><strong>It covers damage this wrapper caused and no other.</strong> A
/// folder emptied by a hand delete, an antivirus quarantine or a failing disk is
/// not in any record and is not refused here — said plainly rather than implied,
/// because a guard people believe is total is worse than one they know the edge
/// of. The disclosure for that case is the restore summary, which now states what
/// was put back against what it replaced.</para>
/// </remarks>
public static class DamagedBackups
{
    /// <summary>
    /// Whether any record in <paramref name="history"/> says the prune destroyed
    /// <paramref name="artifactName"/>.
    /// </summary>
    /// <param name="history">This instance's backup records, in any order.</param>
    /// <param name="artifactName">
    /// The artifact's file or folder name, which is the form the prune records —
    /// it works from names rather than paths so the retention decision is made
    /// from the stamp each one carries (<c>T198</c>).
    /// </param>
    public static bool WasDestroyed(IEnumerable<BackupRecord> history, string artifactName)
    {
        if (string.IsNullOrEmpty(artifactName))
        {
            return false;
        }

        foreach (var record in history)
        {
            foreach (var strategy in record.Strategies)
            {
                foreach (var name in strategy.Damaged)
                {
                    if (string.Equals(name, artifactName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// What to say instead of restoring it. Names the backup and what it would
    /// have done, because "the restore failed" sends somebody to try again.
    /// </summary>
    /// <remarks>
    /// <para>It says the files are <em>gone</em> rather than that a deletion failed.
    /// The deletion did not fail: it removed everything it reached and stopped at
    /// the first thing it could not, so the survivors are the refusals rather than
    /// the remainder of a world.</para>
    ///
    /// <para><strong>The consequence clause is the arm, and it is two because there
    /// are two situations and not because a flag was to hand.</strong> Restoring in
    /// place destroys a live world; restoring into a new server (<c>D40</c>)
    /// destroys nothing and instead hands somebody a copy that looks made and is
    /// not. Both are worth refusing and only one of them is a loss — a single
    /// sentence would have had to be wrong about one of them, which is <c>T188</c>:
    /// a justification that covers the branch it was written for and is asserted
    /// over a branch added later.</para>
    /// </remarks>
    /// <param name="artifactName">The backup's file or folder name.</param>
    /// <param name="asNewServer">
    /// True when this would have been restored into a newly created server rather
    /// than over an existing world.
    /// </param>
    public static string Refusal(string artifactName, bool asNewServer = false) =>
        $"'{artifactName}' was destroyed by this instance's own retention rule: the "
        + "files it had already removed are gone, and what is left is only what it "
        + "could not delete. "
        + (asNewServer
            ? "A new server built from it would have those leftovers for a world."
            : "Restoring it would replace this world with those leftovers.")
        + " Pick a backup taken before or after it.";
}
