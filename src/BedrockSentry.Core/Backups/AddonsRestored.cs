namespace BedrockSentry.Core.Backups;

/// <summary>
/// Where each restored world's add-ons came from (<c>D97</c>).
/// </summary>
/// <remarks>
/// <para>Counts of worlds rather than one state, because a restore replaces the whole
/// <c>worlds</c> folder and each world in it is settled on its own: the backup may
/// carry add-ons for one world and none for another.</para>
///
/// <para>A world that neither the backup nor the server gave add-ons to is in no
/// count, and the summary says nothing about it.</para>
/// </remarks>
/// <param name="Asked">Whether the restore was asked to put the backup's add-ons back.</param>
/// <param name="FromBackup">Worlds whose add-ons are the ones in the backup.</param>
/// <param name="LeftOut">Worlds whose add-ons in the backup were left out, as asked.</param>
/// <param name="FromServer">Worlds that kept the add-ons the server had before the restore.</param>
public readonly record struct AddonsRestored(bool Asked, int FromBackup, int LeftOut, int FromServer)
{
    /// <summary>
    /// What the restore's summary says about add-ons, or an empty string.
    /// </summary>
    /// <remarks>
    /// "Kept because the backup has none" and "kept because you asked" are separate
    /// sentences. The first is the case an administrator restoring an older backup
    /// meets, and it answers the question they would otherwise ask: why the add-ons
    /// they have now are still there.
    /// </remarks>
    public string Clause()
    {
        var parts = new List<string>();

        if (FromBackup > 0)
        {
            parts.Add("Its add-ons came from the backup.");
        }

        if (LeftOut > 0)
        {
            parts.Add("The add-ons in the backup were left out, as asked.");
        }

        if (FromServer > 0)
        {
            parts.Add((Asked, FromBackup) switch
            {
                (false, _) => "The add-ons this server had are kept.",
                (true, 0) => "This backup has no add-ons in it, so the ones this server had are kept.",
                _ => $"It has none for {FromServer} of its worlds, which kept the ones this server had.",
            });
        }

        return string.Join(" ", parts);
    }
}
