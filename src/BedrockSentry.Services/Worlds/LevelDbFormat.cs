using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;

namespace BedrockSentry.Services.Worlds;

/// <summary>
/// The pieces of LevelDB's on-disk format a reader of a Bedrock world needs:
/// varints, the masked CRC32C, block compression and the 32 KiB record framing
/// that logs and manifests share.
/// </summary>
/// <remarks>
/// <para><strong>Bedrock's LevelDB is Mojang's fork, and the fork changes one
/// thing a reader meets.</strong> Upstream compresses blocks with Snappy (type 1);
/// Mojang registers zlib (type 2) and raw deflate (type 4) instead. Every table
/// block BDS 1.26.52.3 wrote was type 4, the metaindex type 0 (stored), so a stock
/// LevelDB library opens a Bedrock world and fails on its first block. The record
/// framing, the manifest's tags and the checksum are upstream's, measured against
/// a world BDS 1.26.52.3 wrote (<c>D86</c>).</para>
///
/// <para>A malformed structure throws <see cref="InvalidDataException"/>, which
/// <see cref="WorldDatabase"/> turns into an unreadable result with the message
/// as its reason.</para>
/// </remarks>
internal static class LevelDbFormat
{
    /// <summary>Logs and manifests are written in blocks of this many bytes.</summary>
    public const int LogBlockSize = 32 * 1024;

    /// <summary>A physical record's header: checksum, length, type.</summary>
    public const int LogHeaderSize = 7;

    /// <summary>The fixed length of a table's footer.</summary>
    public const int FooterSize = 48;

    /// <summary>The last eight bytes of every table.</summary>
    public const ulong TableMagic = 0xdb4775248b80fb57;

    /// <summary>A block's trailer: compression type and checksum.</summary>
    public const int BlockTrailerSize = 5;

    /// <summary>
    /// The most a single block may decompress to. A 64 × 384 × 64 structure, the
    /// largest BDS saves, is a 12.6 MB value; this leaves room for one in a block
    /// and stops a damaged length from asking for the whole of memory.
    /// </summary>
    public const int MaxBlockBytes = 256 * 1024 * 1024;

    /// <summary>The kinds of physical record in a log.</summary>
    public const byte ZeroType = 0, FullType = 1, FirstType = 2, MiddleType = 3, LastType = 4;

    /// <summary>An entry's type in a table or a write batch.</summary>
    public const byte DeletionType = 0, ValueType = 1;

    private const uint MaskDelta = 0xa282ead8;

    public static ulong ReadVarint64(ReadOnlySpan<byte> data, ref int position)
    {
        ulong result = 0;

        for (var shift = 0; shift <= 63; shift += 7)
        {
            if (position >= data.Length)
            {
                throw new InvalidDataException("A number runs past the end of its record.");
            }

            var b = data[position++];
            result |= (ulong)(b & 0x7f) << shift;

            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        throw new InvalidDataException("A number is longer than ten bytes.");
    }

    public static int ReadVarint32(ReadOnlySpan<byte> data, ref int position)
    {
        var value = ReadVarint64(data, ref position);
        return value <= int.MaxValue
            ? (int)value
            : throw new InvalidDataException("A length is larger than any block holds.");
    }

    /// <summary>A length-prefixed run of bytes.</summary>
    public static ReadOnlySpan<byte> ReadSlice(ReadOnlySpan<byte> data, ref int position)
    {
        var length = ReadVarint32(data, ref position);

        if (length > data.Length - position)
        {
            throw new InvalidDataException("A key or value runs past the end of its record.");
        }

        var slice = data.Slice(position, length);
        position += length;
        return slice;
    }

    /// <summary>CRC-32C (Castagnoli), as LevelDB computes it.</summary>
    public static uint Crc32C(ReadOnlySpan<byte> data) => ~Extend(uint.MaxValue, data);

    /// <summary>
    /// The form LevelDB stores a checksum in: rotated and offset, so a CRC of
    /// data that holds CRCs does not cancel itself out.
    /// </summary>
    public static uint Mask(uint crc) => ((crc >> 15) | (crc << 17)) + MaskDelta;

    /// <summary>The checksum a table block's trailer carries: over the contents and the type byte.</summary>
    public static uint BlockChecksum(ReadOnlySpan<byte> contents, byte type) =>
        Mask(~BitOperations.Crc32C(Extend(uint.MaxValue, contents), type));

    /// <summary>The checksum a log record's header carries: over the type byte and the data.</summary>
    public static uint RecordChecksum(byte type, ReadOnlySpan<byte> data) =>
        Mask(~Extend(BitOperations.Crc32C(uint.MaxValue, type), data));

    /// <summary>Runs the CRC register over more bytes, eight at a time where it can.</summary>
    private static uint Extend(uint crc, ReadOnlySpan<byte> data)
    {
        var i = 0;

        for (; i + 8 <= data.Length; i += 8)
        {
            crc = BitOperations.Crc32C(crc, BinaryPrimitives.ReadUInt64LittleEndian(data[i..]));
        }

        for (; i < data.Length; i++)
        {
            crc = BitOperations.Crc32C(crc, data[i]);
        }

        return crc;
    }

    /// <summary>A block's contents, decompressed by its trailer's type.</summary>
    /// <param name="stored">The block as stored, trailer excluded.</param>
    /// <param name="type">The trailer's compression type.</param>
    /// <param name="where">Names the block in a message.</param>
    public static byte[] Decompress(ReadOnlySpan<byte> stored, byte type, string where)
    {
        switch (type)
        {
            case 0:
                return stored.ToArray();

            case 1:
                throw new InvalidDataException(
                    $"{where} is compressed with Snappy, which Bedrock's database does not use.");

            case 2:
            case 4:
                using (var input = new MemoryStream(stored.ToArray(), writable: false))
                using (Stream inflater = type == 2
                    ? new ZLibStream(input, CompressionMode.Decompress)
                    : new DeflateStream(input, CompressionMode.Decompress))
                {
                    return ReadBounded(inflater, where);
                }

            default:
                throw new InvalidDataException(
                    $"{where} uses compression type {type}, which this reader does not know.");
        }
    }

    private static byte[] ReadBounded(Stream inflater, string where)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;

        try
        {
            while ((read = inflater.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > MaxBlockBytes)
                {
                    throw new InvalidDataException(
                        $"{where} decompresses to more than {MaxBlockBytes / (1024 * 1024)} MB.");
                }

                output.Write(buffer, 0, read);
            }
        }
        catch (InvalidDataException ex) when (!ex.Message.StartsWith(where, StringComparison.Ordinal))
        {
            // The framework's own sentence names no file; this one does.
            throw new InvalidDataException($"{where} does not decompress: {ex.Message}", ex);
        }

        return output.ToArray();
    }

    /// <summary>
    /// The logical records in a log or manifest, reassembled from their 32 KiB
    /// blocks.
    /// </summary>
    /// <remarks>
    /// <para><strong>The same choices LevelDB makes when it recovers a log</strong>,
    /// so what is read here is what BDS reads when it opens the world. A record
    /// whose checksum fails, or one that runs past its block, drops the rest of
    /// that block. A header or record cut short by the end of the file ends the
    /// log, which is how a copy taken at the lengths <c>save query</c> lists, or a
    /// writer that stopped mid-record, looks. A zero-length record of type 0 is
    /// the padding of a preallocated file and is skipped.</para>
    ///
    /// <para>A fragment that arrives out of order (a middle with no first, a first
    /// while another record is open) is dropped with the record it belonged to,
    /// as LevelDB drops it.</para>
    /// </remarks>
    public static List<byte[]> Records(ReadOnlySpan<byte> file)
    {
        var records = new List<byte[]>();
        List<byte>? open = null;
        var position = 0;

        while (position < file.Length)
        {
            var left = LogBlockSize - (position % LogBlockSize);

            if (left < LogHeaderSize)
            {
                // The trailer of a block too short for a header is zero padding.
                position += left;
                continue;
            }

            if (file.Length - position < LogHeaderSize)
            {
                break;
            }

            var checksum = BinaryPrimitives.ReadUInt32LittleEndian(file[position..]);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(file[(position + 4)..]);
            var type = file[position + 6];

            if (type == ZeroType && length == 0)
            {
                position += left;
                open = null;
                continue;
            }

            if (length > left - LogHeaderSize)
            {
                // A length that leaves its block was damaged after it was written.
                position += left;
                open = null;
                continue;
            }

            if (file.Length - position - LogHeaderSize < length)
            {
                break;
            }

            var data = file.Slice(position + LogHeaderSize, length);

            if (RecordChecksum(type, data) != checksum)
            {
                position += left;
                open = null;
                continue;
            }

            position += LogHeaderSize + length;

            switch (type)
            {
                case FullType:
                    open = null;
                    records.Add(data.ToArray());
                    break;

                case FirstType:
                    open = [.. data];
                    break;

                case MiddleType:
                    open?.AddRange(data);
                    break;

                case LastType:
                    if (open is not null)
                    {
                        open.AddRange(data);
                        records.Add([.. open]);
                        open = null;
                    }

                    break;

                default:
                    open = null;
                    break;
            }
        }

        return records;
    }
}
