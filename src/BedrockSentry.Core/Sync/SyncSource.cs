using BedrockSentry.Core.Backups;

namespace BedrockSentry.Core.Sync;

/// <summary>Which of the source's backups a sync may copy in (<c>D103</c>).</summary>
public static class SyncSource
{
    /// <summary>
    /// Whether <paramref name="history"/> records <paramref name="artifactName"/> as
    /// a safety backup, taken before a restore or a sync replaced the world.
    /// </summary>
    /// <remarks>
    /// <para>A safety backup holds the world the server ran <em>until</em> it was
    /// replaced, which is the one world its administrator chose not to keep. The
    /// newest backup of the source is otherwise the nearest thing to its world now,
    /// so a sync passes safety backups over for the newest other one.</para>
    ///
    /// <para>Matched by file name, as the Backups tab matches its notes
    /// (<c>T124</c>): history stores the path at the time, and a backup folder can
    /// have moved since. Both separators are read, since a history written on
    /// Windows can be read here on another platform.</para>
    /// </remarks>
    public static bool IsSafetyBackup(IEnumerable<BackupRecord> history, string artifactName)
    {
        ArgumentNullException.ThrowIfNull(history);

        if (string.IsNullOrEmpty(artifactName))
        {
            return false;
        }

        return history
            .Where(r => r.Reason == BackupReason.Safety)
            .SelectMany(r => r.Strategies)
            .Select(s => s.ArtifactPath)
            .OfType<string>()
            .Any(path => string.Equals(NameOf(path), artifactName, StringComparison.OrdinalIgnoreCase));
    }

    private static string NameOf(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }
}
