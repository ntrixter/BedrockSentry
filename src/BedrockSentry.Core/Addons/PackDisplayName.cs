namespace BedrockSentry.Core.Addons;

/// <summary>What to call a pack on screen.</summary>
/// <remarks>
/// <para><strong>A pack's header name is very often a localisation key rather
/// than a name.</strong> Measured on a stock BDS instance: 20 of 27 pack folders
/// carry a dotted key — <c>pack.name</c> fifteen times over, plus
/// <c>resourcePack.vanilla.name</c> and its siblings — because the client
/// resolves them from its own global language pack, which a server does not
/// have.</para>
///
/// <para><strong>The pack's own <c>texts/en_US.lang</c> does not rescue most of
/// them</strong>, which is why this exists beside
/// <see cref="PackManifest.WithDisplayText"/> rather than inside it. Those packs
/// either ship no <c>texts/</c> folder at all or key the file under a different
/// name than the header asks for, so the overlay finds nothing and the key is
/// rendered verbatim. The remedy is a fallback rather than a lookup.</para>
///
/// <para>The folder name is that fallback because it is the one string that is
/// always present, always specific to the pack, and is what BDS itself prints
/// in its <c>Pack Stack</c> line.</para>
///
/// <para><strong>Formatting codes are dropped before either test</strong>
/// (<c>T285</c>), so a name that is nothing but codes falls through like an
/// empty one, and a key wrapped in a colour code is still a key.</para>
/// </remarks>
public static class PackDisplayName
{
    /// <summary>The best name for a pack, given everything known about it.</summary>
    /// <param name="headerName">The manifest header's <c>name</c>.</param>
    /// <param name="language">
    /// The pack's own language map, or null when it ships none. Applied first:
    /// a pack that localises itself properly is the case this all exists for.
    /// </param>
    /// <param name="folderName">
    /// The folder the pack sits in. Used only when what is left is a key.
    /// </param>
    public static string For(
        string? headerName, IReadOnlyDictionary<string, string>? language, string? folderName)
    {
        if (language is not null
            && language.TryGetValue("pack.name", out var localised)
            && Shown(localised) is { Length: > 0 } fromLanguage
            && !LooksLikeAKey(fromLanguage))
        {
            return fromLanguage;
        }

        if (Shown(headerName) is { Length: > 0 } fromHeader && !LooksLikeAKey(fromHeader))
        {
            return fromHeader;
        }

        if (folderName is { Length: > 0 })
        {
            return folderName;
        }

        // Everything failed, so the key is still better than an empty cell: it
        // is at least something to search for.
        return Shown(headerName) ?? string.Empty;
    }

    /// <summary>A pack's text without Bedrock's formatting codes.</summary>
    /// <remarks>
    /// <c>§</c> and the one character after it are a colour or style code: the
    /// game draws the text in that colour and never shows the pair, so a page
    /// showing the name drops the pair rather than print it (<c>T285</c>). A
    /// <c>§</c> with nothing after it goes too. Text with no section sign comes
    /// back as it was.
    /// </remarks>
    public static string WithoutFormatting(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!text.Contains('§', StringComparison.Ordinal))
        {
            return text;
        }

        var kept = new System.Text.StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '§')
            {
                // Skips the code character too; at the end there is none to skip.
                i++;
                continue;
            }

            kept.Append(text[i]);
        }

        return kept.ToString();
    }

    private static string? Shown(string? text) =>
        text is null ? null : WithoutFormatting(text).Trim();

    /// <summary>Is this a localisation key rather than something to show?</summary>
    /// <remarks>
    /// <para><strong>Whitespace is the discriminator that matters.</strong> A key
    /// is dotted segments with no spaces in it; a real name with a dot in it —
    /// <c>My Pack v1.2</c> — has a space, so it is never mistaken for one. That
    /// leaves a single-word dotted name like <c>v1.2</c> as the one thing this
    /// gets wrong, and getting it wrong costs the folder name rather than a
    /// key nobody can read.</para>
    ///
    /// <para>A name with <strong>no</strong> dot is never a key, whatever else it
    /// looks like.</para>
    /// </remarks>
    public static bool LooksLikeAKey(string? text)
    {
        if (text is not { Length: > 0 })
        {
            return false;
        }

        if (!text.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                return false;
            }

            if (c != '.' && c != '_' && c != '-' && !char.IsLetterOrDigit(c))
            {
                return false;
            }
        }

        // A leading, trailing or doubled dot is not the shape a key has, and
        // treating one as a key would hide a name somebody chose.
        return !text.StartsWith('.')
            && !text.EndsWith('.')
            && !text.Contains("..", StringComparison.Ordinal);
    }
}
