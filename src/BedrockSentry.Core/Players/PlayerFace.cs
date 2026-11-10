using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;

namespace BedrockSentry.Core.Players;

/// <summary>
/// The pictures beside a player's name (<c>D47</c>): the face cut from a skin, and
/// the face generated from a XUID when there is no skin to cut it from.
/// </summary>
/// <remarks>
/// Both come out as a small PNG, so the panel shows one kind of image whatever
/// happened. Pure, so the suite can check every arm without a network: what
/// fetches a skin and where the result is kept is <c>PlayerFaces</c> in Services.
/// </remarks>
public static class PlayerFace
{
    /// <summary>
    /// Whether a string can be a XUID: digits only, and a 64-bit number.
    /// </summary>
    /// <remarks>
    /// It reaches the face route from a URL and goes on into a file name and a
    /// request to a third party, so anything else is refused before either.
    /// </remarks>
    public static bool IsXuid(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= 20
        && value.All(char.IsAsciiDigit)
        && ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    /// <summary>
    /// The face from a Java-layout skin: the front of the head with the hat layer
    /// over it, as an 8 by 8 PNG (larger for a high-resolution skin).
    /// </summary>
    /// <returns>Null for anything that is not a skin this can read.</returns>
    public static byte[]? FromSkin(ReadOnlySpan<byte> skinPng)
    {
        if (Png.Decode(skinPng) is not { } skin)
        {
            return null;
        }

        // 64 wide, square or the legacy half-height layout, or a multiple of that.
        if (skin.Width % 64 != 0 || skin.Width > 512
            || (skin.Height != skin.Width && skin.Height != skin.Width / 2))
        {
            return null;
        }

        var scale = skin.Width / 64;
        var size = 8 * scale;
        var face = new byte[size * size * 4];
        var hatIsSolidFill = IsSolidFill(skin, 40 * scale, 8 * scale, size);

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var head = skin.Offset((8 * scale) + x, (8 * scale) + y);
                var hat = skin.Offset((40 * scale) + x, (8 * scale) + y);
                var o = ((y * size) + x) * 4;

                // The head is opaque in the game whatever its alpha says.
                var a = hatIsSolidFill ? 0 : skin.Rgba[hat + 3];
                for (var c = 0; c < 3; c++)
                {
                    face[o + c] = (byte)(((skin.Rgba[hat + c] * a) + (skin.Rgba[head + c] * (255 - a)) + 127) / 255);
                }

                face[o + 3] = 255;
            }
        }

        return Png.Encode(size, size, face);
    }

    /// <summary>
    /// A face drawn from the XUID alone, the same every time for one XUID.
    /// </summary>
    /// <remarks>
    /// What a row shows when faces are off, the source has nothing, or it cannot
    /// be reached, so a row is never empty and never waits on anybody. Hair, skin
    /// and eye colours are each picked from the XUID's hash.
    /// </remarks>
    public static byte[] Generated(string xuid)
    {
        var hash = Fnv1a(xuid);

        ReadOnlySpan<uint> hairs = [0x2B1D0E, 0x4A3018, 0x7A5230, 0xC9A45C, 0x8E3B1F, 0x1A1A1A, 0x6B6B6B, 0x3C5A8A];
        ReadOnlySpan<uint> skins = [0xF2C9A0, 0xE0AC7E, 0xC68A5E, 0x9C6644, 0x6E4630, 0xF5D7C4];
        ReadOnlySpan<uint> eyes = [0x3B6FB6, 0x4E8A3E, 0x6B4423, 0x2E2E2E, 0x7A4FA0, 0x3A8C8C];

        var hair = hairs[(int)(hash % (uint)hairs.Length)];
        var skin = skins[(int)((hash >> 8) % (uint)skins.Length)];
        var eye = eyes[(int)((hash >> 16) % (uint)eyes.Length)];
        var fringe = (hash >> 24) % 3;

        const uint white = 0xFFFFFF;
        var mouth = Darker(skin);

        var rgba = new byte[8 * 8 * 4];
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var colour =
                    y < 2 ? hair :
                    y == 2 && (x == 0 || x == 7 || (fringe == 1 && x is 1 or 6) || (fringe == 2 && x < 4)) ? hair :
                    y == 4 && x is 1 or 6 ? white :
                    y == 4 && x is 2 or 5 ? eye :
                    y == 6 && x is 3 or 4 ? mouth :
                    skin;

                var o = ((y * 8) + x) * 4;
                rgba[o] = (byte)(colour >> 16);
                rgba[o + 1] = (byte)(colour >> 8);
                rgba[o + 2] = (byte)colour;
                rgba[o + 3] = 255;
            }
        }

        return Png.Encode(8, 8, rgba);
    }

    private static uint Darker(uint rgb) =>
        ((((rgb >> 16) & 0xFF) * 3 / 5) << 16) | ((((rgb >> 8) & 0xFF) * 3 / 5) << 8) | ((rgb & 0xFF) * 3 / 5);

    /// <summary>
    /// A hat layer that is one opaque colour all over is the legacy skins' filled
    /// overlay, which the game does not draw; drawing it would hide the face.
    /// </summary>
    private static bool IsSolidFill(Png.Image skin, int left, int top, int size)
    {
        var first = skin.Offset(left, top);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var p = skin.Offset(left + x, top + y);
                if (skin.Rgba[p + 3] != 255
                    || skin.Rgba[p] != skin.Rgba[first]
                    || skin.Rgba[p + 1] != skin.Rgba[first + 1]
                    || skin.Rgba[p + 2] != skin.Rgba[first + 2])
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>FNV-1a: stable across processes, unlike <see cref="string.GetHashCode()"/>.</summary>
    private static uint Fnv1a(string value)
    {
        var hash = 2166136261u;
        foreach (var c in value)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return hash;
    }

    /// <summary>
    /// The PNG this needs and no more: 8-bit, non-interlaced, every colour type in
    /// and RGBA out.
    /// </summary>
    public static class Png
    {
        private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

        /// <summary>The largest side read. A skin is 64; anything near this is not one.</summary>
        public const int MaxSide = 1024;

        public sealed record Image(int Width, int Height, byte[] Rgba)
        {
            public int Offset(int x, int y) => ((y * Width) + x) * 4;
        }

        /// <returns>Null for anything this cannot read, never a throw.</returns>
        public static Image? Decode(ReadOnlySpan<byte> png)
        {
            try
            {
                return DecodeOrThrow(png);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or IndexOutOfRangeException)
            {
                return null;
            }
        }

        private static Image? DecodeOrThrow(ReadOnlySpan<byte> png)
        {
            if (png.Length < Signature.Length + 25 || !png[..Signature.Length].SequenceEqual(Signature))
            {
                return null;
            }

            int width = 0, height = 0, colourType = -1;
            byte[] palette = [];
            byte[] paletteAlpha = [];
            using var compressed = new MemoryStream();

            var at = Signature.Length;
            while (at + 12 <= png.Length)
            {
                var length = BinaryPrimitives.ReadUInt32BigEndian(png[at..]);
                if (length > (uint)(png.Length - at - 12))
                {
                    return null;
                }

                var type = png.Slice(at + 4, 4);
                var data = png.Slice(at + 8, (int)length);
                at += 12 + (int)length;

                if (type.SequenceEqual("IHDR"u8))
                {
                    width = BinaryPrimitives.ReadInt32BigEndian(data);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    colourType = data[9];

                    // Bit depth 8, deflate, filter method 0, no interlace.
                    if (data[8] != 8 || data[10] != 0 || data[11] != 0 || data[12] != 0
                        || width is < 1 or > MaxSide || height is < 1 or > MaxSide)
                    {
                        return null;
                    }
                }
                else if (type.SequenceEqual("PLTE"u8))
                {
                    palette = data.ToArray();
                }
                else if (type.SequenceEqual("tRNS"u8))
                {
                    paletteAlpha = data.ToArray();
                }
                else if (type.SequenceEqual("IDAT"u8))
                {
                    compressed.Write(data);
                }
                else if (type.SequenceEqual("IEND"u8))
                {
                    break;
                }
            }

            var channels = colourType switch
            {
                0 => 1,
                2 => 3,
                3 => 1,
                4 => 2,
                6 => 4,
                _ => 0,
            };

            if (channels == 0 || (colourType == 3 && palette.Length < 3))
            {
                return null;
            }

            var stride = width * channels;
            var raw = new byte[height * (stride + 1)];

            // Read exactly what the header promises and no more, so a small file
            // that inflates to gigabytes stops at the image's own size.
            compressed.Position = 0;
            using (var inflate = new ZLibStream(compressed, CompressionMode.Decompress))
            {
                inflate.ReadExactly(raw);
            }

            var pixels = Unfilter(raw, height, stride, channels);
            if (pixels is null)
            {
                return null;
            }

            var rgba = new byte[width * height * 4];
            for (var i = 0; i < width * height; i++)
            {
                var s = i * channels;
                var o = i * 4;
                switch (colourType)
                {
                    case 0:
                        rgba[o] = rgba[o + 1] = rgba[o + 2] = pixels[s];
                        rgba[o + 3] = 255;
                        break;
                    case 2:
                        rgba[o] = pixels[s];
                        rgba[o + 1] = pixels[s + 1];
                        rgba[o + 2] = pixels[s + 2];
                        rgba[o + 3] = 255;
                        break;
                    case 3:
                        var index = pixels[s];
                        if ((index * 3) + 2 >= palette.Length)
                        {
                            return null;
                        }

                        rgba[o] = palette[index * 3];
                        rgba[o + 1] = palette[(index * 3) + 1];
                        rgba[o + 2] = palette[(index * 3) + 2];
                        rgba[o + 3] = index < paletteAlpha.Length ? paletteAlpha[index] : (byte)255;
                        break;
                    case 4:
                        rgba[o] = rgba[o + 1] = rgba[o + 2] = pixels[s];
                        rgba[o + 3] = pixels[s + 1];
                        break;
                    default:
                        Array.Copy(pixels, s, rgba, o, 4);
                        break;
                }
            }

            return new Image(width, height, rgba);
        }

        private static byte[]? Unfilter(byte[] raw, int height, int stride, int bpp)
        {
            var pixels = new byte[height * stride];
            for (var y = 0; y < height; y++)
            {
                var filter = raw[y * (stride + 1)];
                var src = (y * (stride + 1)) + 1;
                var row = y * stride;
                var up = row - stride;

                for (var x = 0; x < stride; x++)
                {
                    int a = x >= bpp ? pixels[row + x - bpp] : 0;
                    int b = y > 0 ? pixels[up + x] : 0;
                    int c = x >= bpp && y > 0 ? pixels[up + x - bpp] : 0;

                    int predictor = filter switch
                    {
                        0 => 0,
                        1 => a,
                        2 => b,
                        3 => (a + b) / 2,
                        4 => Paeth(a, b, c),
                        _ => -1,
                    };

                    if (predictor < 0)
                    {
                        return null;
                    }

                    pixels[row + x] = (byte)(raw[src + x] + predictor);
                }
            }

            return pixels;
        }

        private static int Paeth(int a, int b, int c)
        {
            var p = a + b - c;
            var pa = Math.Abs(p - a);
            var pb = Math.Abs(p - b);
            var pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        /// <summary>An 8-bit RGBA PNG, every row unfiltered.</summary>
        public static byte[] Encode(int width, int height, byte[] rgba)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(rgba.Length, width * height * 4);

            using var output = new MemoryStream();
            output.Write(Signature);

            var header = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header, width);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
            header[8] = 8;
            header[9] = 6;
            WriteChunk(output, "IHDR"u8, header);

            using var body = new MemoryStream();
            using (var deflate = new ZLibStream(body, CompressionLevel.Optimal, leaveOpen: true))
            {
                for (var y = 0; y < height; y++)
                {
                    deflate.WriteByte(0);
                    deflate.Write(rgba, y * width * 4, width * 4);
                }
            }

            WriteChunk(output, "IDAT"u8, body.ToArray());
            WriteChunk(output, "IEND"u8, []);
            return output.ToArray();
        }

        private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            Span<byte> number = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
            output.Write(number);
            output.Write(type);
            output.Write(data);

            var crc = Crc32(Crc32(0xFFFFFFFFu, type), data) ^ 0xFFFFFFFFu;
            BinaryPrimitives.WriteUInt32BigEndian(number, crc);
            output.Write(number);
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (var n = 0u; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }

        private static uint Crc32(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var b in data)
            {
                crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return crc;
        }
    }
}
