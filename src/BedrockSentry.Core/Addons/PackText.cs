using System.Text;

namespace BedrockSentry.Core.Addons;

/// <summary>Turning the bytes of a pack's own text files into text.</summary>
/// <remarks>
/// <para><strong>UTF-8 when the bytes are UTF-8, Windows-1252 when they are
/// not.</strong> Bedrock reads a pack's manifest and <c>.lang</c> files as UTF-8,
/// but a pack edited by hand on Windows is often saved in the system's
/// single-byte encoding, where the section sign that opens a formatting code is
/// the lone byte <c>0xA7</c>. Read as UTF-8 that byte is no character at all,
/// and the reader's replacement character reached the page where the section
/// sign had been (<c>T285</c>). Bytes that are not valid UTF-8 are not UTF-8, so
/// the single-byte reading is the better guess rather than a repair.</para>
///
/// <para>A byte-order mark decides before either: UTF-8's is dropped, and
/// UTF-16's in either order is honoured, because an editor that wrote one meant
/// it.</para>
/// </remarks>
public static class PackText
{
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The encoding bytes that are not valid UTF-8 are read in.</summary>
    public static Encoding SingleByte { get; } =
        CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? Encoding.Latin1;

    /// <summary>The text these bytes hold.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return Encoding.UTF8.GetString(bytes[3..]);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return SingleByte.GetString(bytes);
        }
    }
}
