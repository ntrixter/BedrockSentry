using System.Globalization;
using System.Text.RegularExpressions;

namespace BedrockSentry.Core.Packages;

/// <summary>
/// A Bedrock server zip sitting in the package store (<c>D19</c>).
/// </summary>
/// <param name="FileName">The file as it appears on disk.</param>
/// <param name="FullPath">Where it is.</param>
/// <param name="Version">
/// The version parsed out of the file name, or null when the name does not
/// follow Mojang's convention. Null is not a rejection — an unparseable name
/// still lists and still installs, it just cannot be ordered or matched by
/// version. Refusing to show a file because its name is unfamiliar would be a
/// worse outcome than showing it without a version.
/// </param>
public sealed record ServerPackage(
    string FileName,
    string FullPath,
    Version? Version,
    string? VersionText,
    long SizeBytes,
    DateTimeOffset ModifiedUtc)
{
    /// <summary>What to show in a list: the version if known, otherwise the file name.</summary>
    public string Display => VersionText ?? FileName;
}

/// <summary>
/// Reads the version out of a Bedrock server package file name.
/// </summary>
public static partial class ServerPackageName
{
    // Mojang ships bedrock-server-1.26.36.1.zip. The suffix group catches
    // preview and beta builds, which carry the same shape plus a tail.
    [GeneratedRegex(
        @"^bedrock-server[-_](?<version>\d+(?:\.\d+)*)(?<suffix>[-_.][A-Za-z0-9._-]*)?\.zip$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern { get; }

    /// <summary>True for anything the store should treat as a server package.</summary>
    public static bool LooksLikeAPackage(string fileName) =>
        fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Pulls the version out of <paramref name="fileName"/>. Returns nulls
    /// rather than throwing: an unrecognised name is a listing detail, not an
    /// error, and the file may still be a perfectly good server.
    /// </summary>
    public static (Version? Version, string? Text) Parse(string fileName)
    {
        var match = Pattern.Match(fileName);
        if (!match.Success)
        {
            return (null, null);
        }

        var text = match.Groups["version"].Value;
        var suffix = match.Groups["suffix"].Success
            ? match.Groups["suffix"].Value.TrimStart('-', '_', '.')
            : string.Empty;

        // System.Version needs at least major.minor and at most four parts.
        // Mojang uses four, but a hand-renamed file might not.
        Version? version = null;
        var parts = text.Split('.');
        if (parts.Length is >= 2 and <= 4
            && parts.All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            Version.TryParse(text, out version);
        }

        var display = suffix.Length > 0 ? $"{text} ({suffix})" : text;
        return (version, display);
    }
}
