using System.Diagnostics.CodeAnalysis;

namespace BedrockSentry.Core.Backups;

/// <summary>
/// One file in the snapshot <c>save query</c> describes.
/// </summary>
/// <param name="RelativePath">
/// Relative to the <c>worlds</c> directory, which it does not name — the
/// measured line begins <c>Bedrock level/db/000005.ldb:509</c>, not
/// <c>worlds/Bedrock level/...</c> (<c>docs/design.md</c> § 5.5). Anything copying the
/// file joins the instance directory <em>and</em> <c>worlds</c> itself.
/// </param>
/// <param name="Length">
/// How many bytes of the file belong to the snapshot. A byte count, not a
/// checksum — and the file on disk may be longer while saves are held, so a
/// copy must stop at this length. Copying whole files produces an archive that
/// restores to a corrupt world, and does it silently.
/// </param>
public sealed record SaveManifestEntry(string RelativePath, long Length);

/// <summary>
/// Recognises the <c>save query</c> manifest in console output
/// (<c>docs/design.md</c> § 5.5, measured on 1.26.36.1).
/// </summary>
public static class SaveManifest
{
    /// <summary>
    /// Parses a console line as a manifest, if it is one.
    /// </summary>
    /// <remarks>
    /// The manifest is one line of comma-separated <c>path:length</c> entries,
    /// and it arrives <em>bare</em> — no <c>[timestamp LEVEL]</c> prefix — so
    /// this is handed the message text and decides from shape alone. The guard
    /// is that every segment must look like a path with a byte count: a
    /// separator in the path and digits after the last colon. Requiring every
    /// segment keeps ordinary sentences with one <c>word: 5</c> pair out, and
    /// requiring the separator keeps out <c>key:value, key:value</c> status
    /// lines that are not file lists.
    ///
    /// The known gap: a world whose folder name itself contains a comma
    /// followed by a space would split wrongly here. Nothing in the measured
    /// output disambiguates that case, so it is accepted rather than guessed
    /// at.
    /// </remarks>
    public static bool TryParse(
        string? line, [NotNullWhen(true)] out IReadOnlyList<SaveManifestEntry>? entries)
    {
        entries = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var segments = line.Split(", ", StringSplitOptions.TrimEntries);
        var parsed = new List<SaveManifestEntry>(segments.Length);

        foreach (var segment in segments)
        {
            var colon = segment.LastIndexOf(':');
            if (colon <= 0 || colon == segment.Length - 1)
            {
                return false;
            }

            var path = segment[..colon];
            if (!path.Contains('/') && !path.Contains('\\'))
            {
                return false;
            }

            if (!StaysInWorlds(path))
            {
                return false;
            }

            if (!long.TryParse(segment[(colon + 1)..], out var length) || length < 0)
            {
                return false;
            }

            parsed.Add(new SaveManifestEntry(path, length));
        }

        if (parsed.Count == 0)
        {
            return false;
        }

        entries = parsed;
        return true;
    }

    /// <summary>
    /// Whether a path the server printed names a file beneath <c>worlds</c>: relative,
    /// with no <c>..</c> segment, no drive and no stream.
    /// </summary>
    /// <remarks>
    /// The server writes this line, and a backup copies whatever it names with the
    /// service's own rights. At low integrity (milestone 18.5, <c>D126</c>) the server is
    /// less trusted than the service, so a line naming the credential or the
    /// certificate would carry them into an archive the server could later be handed
    /// back. Such a line is not a manifest at all: the real one still arrives.
    /// </remarks>
    public static bool StaysInWorlds(string path)
    {
        // A leading separator, a share's two and an empty path all leave an empty
        // segment, so one rule over the segments covers them.
        return !path.Contains(':') && path.Split('/', '\\').All(part => part is not ("" or "." or ".."));
    }
}
