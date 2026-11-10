using BedrockSentry.Core.Backups;

namespace BedrockSentry.Services.Backups;

/// <summary>What the folders a save names did after <c>save hold</c> listed them (<c>T356</c>).</summary>
public enum SaveLanding
{
    /// <summary>Nothing in them changed while they were watched: the listed save is the whole save.</summary>
    Quiet = 1,

    /// <summary>A write landed past what the save listed, and stopped.</summary>
    Landed = 2,

    /// <summary>A write landed past what the save listed, and was still going when the watch ended.</summary>
    StillWriting = 3,

    /// <summary>A folder could not be read, so nothing is known about what landed.</summary>
    Unreadable = 4,
}

/// <summary>
/// Watches the folders a save's manifest names for the write <c>save hold</c> sets off
/// (<c>T356</c>).
/// </summary>
/// <remarks>
/// <para><strong>Measured on Linux BDS 1.26.52.3, 2026-10-16, on a world of one ticking
/// area with nobody on it.</strong> <c>save query</c> answers at once with the database's
/// lengths as they stood at the hold, and BDS then writes the chunks changed before the hold
/// into <c>db/000003.log</c> past the listed length, 0.9 to 4.2 seconds after it, in one
/// write each time. The manifest does not move while the hold lasts. A copy at the listed
/// lengths read the blocks from before a <c>fill</c> made 0, 2, 5 and 10 seconds ahead of
/// the hold; a copy at the lengths a second hold listed, after that write, read the new
/// blocks four times out of four.</para>
///
/// <para>A missing folder is read as empty, not as unreadable: the fake server's world
/// is a manifest with no files, and a real save names folders that exist.</para>
/// </remarks>
internal static class SaveLandingWatch
{
    public static async Task<SaveLanding> WaitAsync(
        string worldsDirectory,
        IReadOnlyList<SaveManifestEntry> manifest,
        TimeSpan within,
        TimeSpan settle,
        TimeSpan poll,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var folders = manifest
            .Select(e => Path.GetDirectoryName(Path.Combine(worldsDirectory, e.RelativePath)))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var deadline = time.GetUtcNow() + within;

        if (Read(folders) is not { } last)
        {
            return SaveLanding.Unreadable;
        }

        // Against the listed lengths as well as the first reading: the write can start
        // between the manifest and the first look, and is then already in both. A listed
        // file that is gone says nothing about a write.
        // A manifest names its files with '/', which the folder walk never spells on
        // Windows, so both sides are keyed by the full path.
        var landed = manifest.Any(e =>
            last.TryGetValue(Path.GetFullPath(Path.Combine(worldsDirectory, e.RelativePath)), out var length)
            && length != e.Length);
        var lastChange = time.GetUtcNow();

        while (time.GetUtcNow() < deadline)
        {
            if (landed && time.GetUtcNow() - lastChange >= settle)
            {
                return SaveLanding.Landed;
            }

            await Task.Delay(poll, time, cancellationToken).ConfigureAwait(false);

            if (Read(folders) is not { } now)
            {
                return SaveLanding.Unreadable;
            }

            if (!Same(now, last))
            {
                landed = true;
                lastChange = time.GetUtcNow();
                last = now;
            }
        }

        if (!landed)
        {
            return SaveLanding.Quiet;
        }

        return time.GetUtcNow() - lastChange >= settle ? SaveLanding.Landed : SaveLanding.StillWriting;
    }

    private static Dictionary<string, long>? Read(IReadOnlyList<string> folders)
    {
        var lengths = new Dictionary<string, long>(StringComparer.Ordinal);

        try
        {
            foreach (var folder in folders)
            {
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    lengths[Path.GetFullPath(file)] = new FileInfo(file).Length;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return lengths;
    }

    private static bool Same(Dictionary<string, long> a, Dictionary<string, long> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);
}
