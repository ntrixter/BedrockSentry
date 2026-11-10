using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BedrockSentry.Services.Security;

/// <summary>
/// Time-based one-time passwords as authenticator apps compute them (RFC 6238 over
/// RFC 4226), for the second sign-in step (<c>D125</c>).
/// </summary>
/// <remarks>
/// <para>HMAC-SHA1, six digits, a thirty-second step: the parameters every
/// authenticator app supports. The apps that accept other parameters ignore them in
/// enough cases that offering them would be a setting which sometimes lies.</para>
///
/// <para>Pure functions with the time passed in, so the suite checks the RFC's own test
/// vectors and the drift window without a clock.</para>
/// </remarks>
public static class Totp
{
    /// <summary>Seconds per code.</summary>
    public const int StepSeconds = 30;

    /// <summary>Digits per code.</summary>
    public const int Digits = 6;

    /// <summary>
    /// Steps either side of now that still count: thirty seconds of clock difference
    /// between the phone and the host, either way.
    /// </summary>
    public const int DriftSteps = 1;

    /// <summary>Bytes in a new secret: 160 bits, RFC 4226's recommended length.</summary>
    public const int SecretBytes = 20;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>A new random secret.</summary>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    /// <summary>The time step <paramref name="at"/> falls in.</summary>
    public static long StepAt(DateTimeOffset at) => at.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>The code for one step.</summary>
    public static string CodeAt(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);

        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(secret, counter, hash);

        // RFC 4226 § 5.3, dynamic truncation.
        var offset = hash[^1] & 0x0F;
        var binary = (BinaryPrimitives.ReadInt32BigEndian(hash.Slice(offset, 4)) & 0x7FFFFFFF)
            % 1_000_000;

        return binary.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The step a submitted code matches, or null.
    /// </summary>
    /// <param name="secret">The shared secret.</param>
    /// <param name="code">What was typed. Spaces are ignored; apps show codes as "123 456".</param>
    /// <param name="now">The host's time.</param>
    /// <param name="lastAcceptedStep">
    /// The step of the last code accepted, or null. A code at or before it is refused, so
    /// a code read over a shoulder or out of a log cannot be used a second time.
    /// </param>
    public static long? Match(byte[] secret, string? code, DateTimeOffset now, long? lastAcceptedStep)
    {
        var typed = Normalise(code);

        if (typed is null)
        {
            return null;
        }

        var current = StepAt(now);
        long? matched = null;

        // Every candidate is computed and compared, matched or not, so the time taken
        // does not say which step matched.
        for (var step = current - DriftSteps; step <= current + DriftSteps; step++)
        {
            var expected = Encoding.ASCII.GetBytes(CodeAt(secret, step));

            if (CryptographicOperations.FixedTimeEquals(expected, typed)
                && (lastAcceptedStep is null || step > lastAcceptedStep))
            {
                matched ??= step;
            }
        }

        return matched;
    }

    /// <summary>
    /// The <c>otpauth://</c> address the QR code carries, in the form Google
    /// Authenticator reads.
    /// </summary>
    public static string ProvisioningUri(byte[] secret, string account, string issuer)
    {
        var label = Uri.EscapeDataString(issuer) + ":" + Uri.EscapeDataString(account);

        return $"otpauth://totp/{label}?secret={ToBase32(secret)}&issuer={Uri.EscapeDataString(issuer)}"
            + $"&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }

    /// <summary>RFC 4648 base32 without padding, as authenticator apps expect.</summary>
    public static string ToBase32(ReadOnlySpan<byte> data)
    {
        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;

            while (bits >= 5)
            {
                output.Append(Base32Alphabet[(buffer >> (bits - 5)) & 0x1F]);
                bits -= 5;
            }

            buffer &= (1 << bits) - 1;
        }

        if (bits > 0)
        {
            output.Append(Base32Alphabet[(buffer << (5 - bits)) & 0x1F]);
        }

        return output.ToString();
    }

    /// <summary>
    /// Decodes base32, ignoring case, spaces and padding. Null when a character is not
    /// in the alphabet.
    /// </summary>
    public static byte[]? FromBase32(string text)
    {
        var output = new List<byte>(text.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;

        foreach (var c in text)
        {
            if (c is ' ' or '=' or '-')
            {
                continue;
            }

            var value = Base32Alphabet.IndexOf(char.ToUpperInvariant(c));

            if (value < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | value;
            bits += 5;

            if (bits >= 8)
            {
                output.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }

            buffer &= (1 << bits) - 1;
        }

        return [.. output];
    }

    /// <summary>The typed code as six ASCII digits, or null when it is not one.</summary>
    private static byte[]? Normalise(string? code)
    {
        if (code is null)
        {
            return null;
        }

        var digits = code.Replace(" ", string.Empty, StringComparison.Ordinal).Trim();

        if (digits.Length != Digits || !digits.All(char.IsAsciiDigit))
        {
            return null;
        }

        return Encoding.ASCII.GetBytes(digits);
    }
}
