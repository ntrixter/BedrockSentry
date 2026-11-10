namespace BedrockSentry.Core.Addons;

/// <summary>Why an archive entry is not safe to write.</summary>
/// <remarks>
/// Named rather than a <c>bool</c> because the refusal is shown to somebody who
/// has just uploaded a file and needs to know whether their archive is
/// malicious, built on another platform, or simply too big — three different
/// next steps.
/// </remarks>
public enum ArchiveEntryVerdict
{
    /// <summary>Safe to write, at the relative path the plan carries.</summary>
    Accept,

    /// <summary>It has no usable name at all.</summary>
    NoName,

    /// <summary>It is an absolute path, or names a drive or a UNC share.</summary>
    Absolute,

    /// <summary>
    /// It climbs out of the destination with <c>..</c>. The classic archive
    /// escape, and the reason this file exists.
    /// </summary>
    EscapesRoot,

    /// <summary>
    /// A segment Windows would not store as written — a trailing dot or space,
    /// which Windows silently strips, so the bytes would land at a path this
    /// never approved.
    /// </summary>
    UnsafeSegment,

    /// <summary>A segment is a reserved Windows device name.</summary>
    ReservedName,

    // The three below are properties of the ARCHIVE rather than of one entry,
    // and they are here rather than left to a null verdict because a refusal
    // carrying `Accept` is a state a caller has no arm for — it would switch,
    // match nothing, and describe a refused archive as fine.
    /// <summary>It unpacks to more than the bound allows.</summary>
    TooLarge,

    /// <summary>It holds more entries than the bound allows.</summary>
    TooManyEntries,

    /// <summary>It holds no files at all — only directory entries, or nothing.</summary>
    NoFiles,
}

/// <summary>One entry as the archive describes it.</summary>
public readonly record struct ArchiveEntry(string Name, long Length);

/// <summary>One entry that may be written, and where.</summary>
/// <param name="Source">The entry's own name, exactly as the archive gave it.</param>
/// <param name="Destination">The relative path it may be written to, normalised.</param>
/// <remarks>
/// <strong>The pair travels together on purpose.</strong> A plan that returned
/// destinations alone would have to be matched back to entries by position,
/// which makes the rule for skipping a directory entry something stated in two
/// places and enforced in one — and when the two drift, files are written to
/// other files' destinations with nothing failing.
/// </remarks>
public readonly record struct PlannedEntry(string Source, string Destination);

/// <summary>What to do with an archive, or why not to.</summary>
/// <param name="Accepted">
/// True when every entry is safe. <strong>The whole archive or none of it</strong>
/// (<c>D82</c>): extracting the safe entries of an archive that also tried to
/// escape leaves a half-written folder from a file that has just been shown to
/// be hostile, and the operator has no way to tell which half.
/// </param>
/// <param name="Entries">
/// Each entry that may be written, paired with where. Empty on a refusal.
/// </param>
/// <param name="Verdict">The first refusing verdict, or Accept.</param>
/// <param name="OffendingEntry">The entry that refused it, named so the refusal can quote it.</param>
public readonly record struct PackArchivePlan(
    bool Accepted,
    IReadOnlyList<PlannedEntry> Entries,
    ArchiveEntryVerdict Verdict,
    string? OffendingEntry,
    string? Reason)
{
    /// <summary>Just the destinations, for callers that only need the shape.</summary>
    public IReadOnlyList<string> Paths => [.. Entries.Select(e => e.Destination)];
}

/// <summary>
/// Whether an uploaded pack archive may be written, decided before anything is.
/// </summary>
/// <remarks>
/// <para><strong>This is the first thing here that takes a file from the web UI
/// and unpacks it</strong>, so the rules are the feature rather than a detail of
/// it. Extraction itself is mandatory: BDS cannot read a packed <c>.mcpack</c>,
/// it wants a folder.</para>
///
/// <para><strong>Pure, and it never touches a disk.</strong> An escape is
/// decided from the entry's own name against a notional root, so every hostile
/// shape is an ordinary unit test rather than something somebody has to build an
/// archive to try. The rules also cannot be bypassed by the platform the build
/// runs on — <c>Path.GetFullPath</c> resolves differently on Linux and Windows,
/// and a check that behaves differently where it is written from where it runs
/// is the one that gets shipped broken.</para>
/// </remarks>
public static class PackArchiveRules
{
    /// <summary>Uncompressed bytes the whole archive may hold.</summary>
    /// <remarks>
    /// A bound rather than trust in the upload's own size: a compressed archive
    /// can declare a few kilobytes and expand to fill a disk, and the disk it
    /// would fill is the one the worlds are on.
    /// </remarks>
    public const long DefaultMaxTotalBytes = 512L * 1024 * 1024;

    /// <summary>Entries the whole archive may hold.</summary>
    public const int DefaultMaxEntries = 20_000;

    // Windows refuses these as file names whatever the extension, so an entry
    // named for one either fails the write or reaches a device. Checked against
    // the segment's stem, because "aux.json" is reserved and "auxiliary" is not.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Decide one entry's destination, or why it has none.</summary>
    public static ArchiveEntryVerdict Inspect(string? name, out string? relativePath)
    {
        relativePath = null;

        if (string.IsNullOrWhiteSpace(name) || name.Contains('\0'))
        {
            return ArchiveEntryVerdict.NoName;
        }

        // A zip written on Windows separates with a backslash although the
        // format says otherwise, so both are separators here. Treating only '/'
        // as one would let "..\\escape" through as a single ordinary segment.
        var normalised = name.Replace('\\', '/');

        if (normalised.StartsWith('/')
            || normalised.StartsWith("//", StringComparison.Ordinal)
            || (normalised.Length >= 2 && normalised[1] == ':'))
        {
            return ArchiveEntryVerdict.Absolute;
        }

        var segments = new List<string>();

        foreach (var segment in normalised.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                // Popped rather than refused outright, so "pack/../manifest.json"
                // is allowed: it stays inside. Only climbing past the root is an
                // escape, and that is decided by whether there is anything left
                // to pop.
                if (segments.Count == 0)
                {
                    return ArchiveEntryVerdict.EscapesRoot;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            // Windows strips a trailing dot or space when it stores a name, so
            // the bytes would land somewhere this never approved — and the
            // stripped name can collide with a file already written.
            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                return ArchiveEntryVerdict.UnsafeSegment;
            }

            // A colon inside a segment is an alternate data stream on NTFS —
            // "manifest.json:hidden" writes a stream on a file this DID approve,
            // so the path check alone would pass it. The drive-letter test above
            // only catches it at position 1.
            if (segment.Contains(':'))
            {
                return ArchiveEntryVerdict.UnsafeSegment;
            }

            var stem = segment.Split('.')[0];
            if (ReservedNames.Contains(stem))
            {
                return ArchiveEntryVerdict.ReservedName;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            return ArchiveEntryVerdict.NoName;
        }

        relativePath = string.Join('/', segments);
        return ArchiveEntryVerdict.Accept;
    }

    /// <summary>Plan a whole archive, refusing all of it on the first bad entry.</summary>
    public static PackArchivePlan Plan(
        IEnumerable<ArchiveEntry> entries,
        long maxTotalBytes = DefaultMaxTotalBytes,
        int maxEntries = DefaultMaxEntries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var planned = new List<PlannedEntry>();
        long total = 0;

        foreach (var entry in entries)
        {
            // A directory entry carries a trailing separator and no bytes. It is
            // skipped rather than refused, because the folders are created by
            // writing the files inside them — but it is inspected first, so an
            // escaping directory entry still refuses the archive.
            var verdict = Inspect(entry.Name, out var path);

            if (verdict != ArchiveEntryVerdict.Accept)
            {
                return new PackArchivePlan(false, [], verdict, entry.Name, Describe(verdict));
            }

            if (entry.Name.EndsWith('/') || entry.Name.EndsWith('\\'))
            {
                continue;
            }

            checked
            {
                total += Math.Max(entry.Length, 0);
            }

            if (total > maxTotalBytes)
            {
                return new PackArchivePlan(
                    false, [], ArchiveEntryVerdict.TooLarge, entry.Name,
                    $"The archive unpacks to more than {maxTotalBytes / (1024 * 1024)} MB.");
            }

            planned.Add(new PlannedEntry(entry.Name, path!));

            if (planned.Count > maxEntries)
            {
                return new PackArchivePlan(
                    false, [], ArchiveEntryVerdict.TooManyEntries, entry.Name,
                    $"The archive holds more than {maxEntries} files.");
            }
        }

        return planned.Count == 0
            ? new PackArchivePlan(
                false, [], ArchiveEntryVerdict.NoFiles, null, Describe(ArchiveEntryVerdict.NoFiles))
            : new PackArchivePlan(true, planned, ArchiveEntryVerdict.Accept, null, null);
    }

    /// <summary>A refusal's sentence. Every verdict but Accept has one.</summary>
    public static string Describe(ArchiveEntryVerdict verdict) => verdict switch
    {
        ArchiveEntryVerdict.EscapesRoot =>
            "An entry in this archive points outside the folder it would be unpacked into.",
        ArchiveEntryVerdict.Absolute =>
            "An entry in this archive names an absolute path.",
        ArchiveEntryVerdict.UnsafeSegment =>
            "An entry in this archive has a name Windows would not store as written.",
        ArchiveEntryVerdict.ReservedName =>
            "An entry in this archive is named for a reserved Windows device.",
        ArchiveEntryVerdict.NoName =>
            "An entry in this archive has no usable name.",
        ArchiveEntryVerdict.NoFiles =>
            "The archive holds no files.",

        // The two bounds compose their own sentence, because it has to carry the
        // limit that was exceeded and this method is not given it.
        ArchiveEntryVerdict.TooLarge => "The archive unpacks to more than the limit allows.",
        ArchiveEntryVerdict.TooManyEntries => "The archive holds more files than the limit allows.",

        _ => throw new ArgumentOutOfRangeException(
            nameof(verdict), verdict, "No refusal is written for this verdict."),
    };
}
