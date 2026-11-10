using System.Buffers.Binary;
using System.Text;

namespace BedrockSentry.Core.Backups;

/// <summary>
/// Times inside a ZIP, which keeps each entry's as a clock reading with no zone (<c>T359</c>
/// clause 3).
/// </summary>
/// <remarks>
/// <para><strong>Written as local time</strong>, the ZIP convention Windows Explorer and
/// <c>System.IO.Compression</c> both read by. <see cref="System.IO.Compression.ZipArchiveEntry"/>
/// stores the clock reading of the offset it is given, so a UTC value went in as a UTC reading
/// and came back read as local: on a machine behind UTC every entry was late by the machine's offset.</para>
///
/// <para><strong>Read from the world itself</strong> where it can be: <c>level.dat</c>'s
/// <c>LastPlayed</c> is seconds since 1970 in UTC, which no writer's zone can move.</para>
/// </remarks>
public static class ArchiveTimes
{
    /// <summary>The value to give an entry's <c>LastWriteTime</c> for a moment.</summary>
    public static DateTimeOffset ForEntry(DateTimeOffset moment) => moment.ToLocalTime();

    /// <summary>The earliest <c>LastPlayed</c> believed: before it, the field is not a time.</summary>
    public static readonly DateTimeOffset Earliest = new(2011, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly byte[] LastPlayedTag = BuildTag("LastPlayed");

    /// <summary>
    /// When a Bedrock <c>level.dat</c> says its world was last saved, or null when it carries
    /// no such field or one that is not a plausible time.
    /// </summary>
    /// <param name="levelDat">The file's bytes: an 8-byte header, then little-endian NBT.</param>
    /// <param name="now">Anything later than a day past this is not believed either.</param>
    /// <remarks>
    /// Finds the named long tag (type 4, name length, <c>LastPlayed</c>) rather than walking the
    /// whole compound: the name sits at the root of every Bedrock <c>level.dat</c>, and the
    /// value is checked for being a time, so a match elsewhere that is not one is ignored.
    /// </remarks>
    public static DateTimeOffset? LastPlayed(ReadOnlySpan<byte> levelDat, DateTimeOffset now)
    {
        var at = levelDat.IndexOf(LastPlayedTag);

        if (at < 0 || at + LastPlayedTag.Length + sizeof(long) > levelDat.Length)
        {
            return null;
        }

        var seconds = BinaryPrimitives.ReadInt64LittleEndian(levelDat[(at + LastPlayedTag.Length)..]);

        if (seconds < Earliest.ToUnixTimeSeconds() || seconds > now.AddDays(1).ToUnixTimeSeconds())
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    /// <summary>A long tag's header: its type, its name's length, then the name.</summary>
    internal static byte[] BuildTag(string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        var tag = new byte[3 + bytes.Length];
        tag[0] = 4;
        BinaryPrimitives.WriteUInt16LittleEndian(tag.AsSpan(1), (ushort)bytes.Length);
        bytes.CopyTo(tag, 3);
        return tag;
    }
}
