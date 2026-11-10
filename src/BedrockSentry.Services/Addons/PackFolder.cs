using BedrockSentry.Core.Addons;

namespace BedrockSentry.Services.Addons;

/// <summary>Reading the files that sit beside a pack's manifest.</summary>
/// <remarks>
/// Shared because both readers of a pack folder — the library's and the
/// instance's — need the same answer, and this file's own contents are one rule
/// (where the language file lives, and what an unreadable one means) that was
/// briefly stated in both.
/// </remarks>
internal static class PackFolder
{
    /// <summary>One of a pack's text files, read the way <see cref="PackText"/> reads pack text.</summary>
    public static string ReadText(string path) => PackText.Decode(File.ReadAllBytes(path));

    /// <summary>A pack's own language map, or null when it ships none.</summary>
    /// <remarks>
    /// <strong>Null for an unreadable file as well as an absent one, and that is
    /// deliberate here.</strong> The map decides captions; a pack whose language
    /// file cannot be read still has a name — its header's, or its folder's — so
    /// refusing the pack over its captions would be the louder failure. Every
    /// other read in this feature keeps the two states apart precisely because
    /// they change what the operator is told.
    /// </remarks>
    public static IReadOnlyDictionary<string, string>? ReadLanguage(string packDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packDirectory);

        try
        {
            var file = Path.Combine(
                packDirectory,
                AddonLibraryLayout.LanguageFilePath.Replace('/', Path.DirectorySeparatorChar));

            return File.Exists(file)
                ? PackManifestReader.ReadLanguage(ReadText(file))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
