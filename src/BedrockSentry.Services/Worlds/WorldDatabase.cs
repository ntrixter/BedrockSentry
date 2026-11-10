using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BedrockSentry.Services.Worlds;

/// <summary>How opening a world's database went.</summary>
public enum WorldDatabaseOpenState
{
    /// <summary>Opened: its manifest was read and every table it names is in the folder.</summary>
    Opened,

    /// <summary>
    /// The folder holds no database, because it has no <c>CURRENT</c> file. A world
    /// Bedrock has never started has none.
    /// </summary>
    NoDatabase,

    /// <summary>A database that could not be read; the reason says why.</summary>
    Unreadable,
}

/// <summary>An opened database, or why there is none.</summary>
public sealed record WorldDatabaseOpen(
    WorldDatabaseOpenState State, WorldDatabase? Database, string? Reason);

/// <summary>What looking up one key found.</summary>
public enum WorldDatabaseLookupState
{
    /// <summary>The key is there; the value is its newest one.</summary>
    Found,

    /// <summary>
    /// No live entry: never written, or its newest entry is a deletion.
    /// </summary>
    Absent,

    /// <summary>
    /// A file the answer depends on could not be read, so the key's presence is
    /// not known. Never reported as absent: a structure that is there and could
    /// not be read is not a structure that was never saved.
    /// </summary>
    Unreadable,
}

/// <summary>One key's value, or why there is none.</summary>
public sealed record WorldDatabaseValue(
    WorldDatabaseLookupState State, byte[]? Value, string? Reason);

/// <summary>The live keys under a prefix, or why they could not be listed.</summary>
/// <param name="Keys">The keys in bytewise order; null when unreadable.</param>
public sealed record WorldDatabaseKeys(IReadOnlyList<byte[]>? Keys, string? Reason);

/// <summary>
/// Reads keys out of a Bedrock world's <c>db</c> folder, without writing to it.
/// </summary>
/// <remarks>
/// <para><strong>Only a stopped world, or a copy.</strong> A running BDS holds the
/// database and goes on writing to it, so files read beside it can disagree with
/// each other. A copy taken under <c>save hold</c> at the lengths <c>save
/// query</c> lists is consistent, and so is the folder of a server that has
/// stopped (<c>D86</c>). Nothing here takes the database's <c>LOCK</c> file, so a
/// running server is not disturbed by a mistaken read either.</para>
///
/// <para><strong>The live files come from the manifest, as LevelDB itself finds
/// them.</strong> <c>CURRENT</c> names the manifest; replaying it gives the tables
/// in use and the oldest log still needed. A table the manifest names that is not
/// in the folder makes the database unreadable rather than read without it, and a
/// table it does not name is a leftover of a compaction and is not read, since a
/// deletion compacted away would otherwise bring its key back.</para>
///
/// <para><strong>The newest entry for a key wins</strong>, by the sequence number
/// every write carries, across the tables and the logs. A deletion that is newest
/// makes the key absent.</para>
///
/// <para>Only what a world's structures need is here: a lookup by key and a list
/// by prefix. Compression and record framing are in
/// <see cref="LevelDbFormat"/>.</para>
/// </remarks>
public sealed class WorldDatabase
{
    /// <summary>The file naming the database's current manifest.</summary>
    public const string CurrentFileName = "CURRENT";

    /// <summary>The only key order Bedrock's database uses, and the only one read here.</summary>
    private const string BytewiseComparator = "leveldb.BytewiseComparator";

    private readonly IReadOnlyList<LiveTable> _tables;
    private readonly IReadOnlyList<string> _logs;

    private WorldDatabase(string directory, IReadOnlyList<LiveTable> tables, IReadOnlyList<string> logs)
    {
        Directory = directory;
        _tables = tables;
        _logs = logs;
    }

    /// <summary>The <c>db</c> folder this reads.</summary>
    public string Directory { get; }

    /// <summary>The table and log files a lookup reads, tables first.</summary>
    public IReadOnlyList<string> Files => [.. _tables.Select(t => t.Path), .. _logs];

    /// <summary>Opens the database in a world's <c>db</c> folder.</summary>
    public static WorldDatabaseOpen Open(string databaseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseDirectory);

        var current = Path.Combine(databaseDirectory, CurrentFileName);

        if (!File.Exists(current))
        {
            return new(WorldDatabaseOpenState.NoDatabase, null,
                $"{databaseDirectory} holds no world database: it has no {CurrentFileName} file.");
        }

        try
        {
            var manifestName = File.ReadAllText(current).TrimEnd('\n', '\r');

            if (!IsManifestName(manifestName))
            {
                throw new InvalidDataException(
                    $"{CurrentFileName} names \"{manifestName}\", which is not a manifest.");
            }

            var manifestPath = Path.Combine(databaseDirectory, manifestName);

            if (!File.Exists(manifestPath))
            {
                throw new InvalidDataException(
                    $"{CurrentFileName} names {manifestName}, which is not in the folder.");
            }

            var version = Replay(File.ReadAllBytes(manifestPath), manifestName);
            var tables = new List<LiveTable>(version.Tables.Count);

            foreach (var (number, smallest, largest) in version.Tables)
            {
                var path = TablePath(databaseDirectory, number)
                    ?? throw new InvalidDataException(
                        $"{manifestName} names table {number:D6}.ldb, which is not in the folder.");

                tables.Add(new LiveTable(path, smallest, largest));
            }

            var logs = System.IO.Directory.EnumerateFiles(databaseDirectory, "*.log")
                .Select(path => (Path: path, Number: FileNumber(path, ".log")))
                .Where(log => log.Number is { } n
                    && (n >= version.LogNumber || (version.PrevLogNumber != 0 && n == version.PrevLogNumber)))
                .OrderBy(log => log.Number)
                .Select(log => log.Path)
                .ToList();

            return new(WorldDatabaseOpenState.Opened, new WorldDatabase(databaseDirectory, tables, logs), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(WorldDatabaseOpenState.Unreadable, null, ex.Message);
        }
    }

    /// <summary>The newest value of one key.</summary>
    public WorldDatabaseValue Get(ReadOnlySpan<byte> key)
    {
        var target = key.ToArray();

        try
        {
            Entry? newest = null;

            foreach (var log in _logs)
            {
                foreach (var entry in LogEntries(log, k => k.AsSpan().SequenceEqual(target), withValues: true))
                {
                    newest = Newer(newest, entry);
                }
            }

            foreach (var table in _tables.Where(t => t.MayHold(target)))
            {
                using var reader = TableReader.Open(table.Path);

                foreach (var entry in reader.From(target))
                {
                    var order = entry.Key.AsSpan().SequenceCompareTo(target);

                    if (order > 0)
                    {
                        break;
                    }

                    if (order == 0)
                    {
                        newest = Newer(newest, entry);
                    }
                }
            }

            return newest is { Type: LevelDbFormat.ValueType }
                ? new(WorldDatabaseLookupState.Found, newest.Value, null)
                : new(WorldDatabaseLookupState.Absent, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(WorldDatabaseLookupState.Unreadable, null, ex.Message);
        }
    }

    /// <summary>Every live key that starts with <paramref name="prefix"/>, in bytewise order.</summary>
    public WorldDatabaseKeys KeysStartingWith(ReadOnlySpan<byte> prefix)
    {
        var start = prefix.ToArray();

        try
        {
            var newest = new Dictionary<byte[], Entry>(ByteArrayComparer.Instance);

            void Keep(Entry entry)
            {
                if (!newest.TryGetValue(entry.Key, out var held) || entry.Sequence > held.Sequence)
                {
                    newest[entry.Key] = entry;
                }
            }

            foreach (var log in _logs)
            {
                foreach (var entry in LogEntries(log, k => k.AsSpan().StartsWith(start), withValues: false))
                {
                    Keep(entry);
                }
            }

            foreach (var table in _tables.Where(t => t.MayHoldPrefix(start)))
            {
                using var reader = TableReader.Open(table.Path);

                foreach (var entry in reader.From(start))
                {
                    if (!entry.Key.AsSpan().StartsWith(start))
                    {
                        break;
                    }

                    Keep(entry with { Value = [] });
                }
            }

            var keys = newest.Values
                .Where(e => e.Type == LevelDbFormat.ValueType)
                .Select(e => e.Key)
                .ToList();

            keys.Sort((a, b) => a.AsSpan().SequenceCompareTo(b));
            return new(keys, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(null, ex.Message);
        }
    }

    private static Entry? Newer(Entry? held, Entry candidate) =>
        held is null || candidate.Sequence > held.Sequence ? candidate : held;

    /// <summary>The entries a log's write batches hold whose key <paramref name="wanted"/> accepts.</summary>
    /// <param name="withValues">
    /// False when only the keys are wanted: a log can hold a 12.6 MB structure, and
    /// a listing has no use for it.
    /// </param>
    private static List<Entry> LogEntries(string path, Func<byte[], bool> wanted, bool withValues)
    {
        var name = Path.GetFileName(path);
        var found = new List<Entry>();

        foreach (var batch in LevelDbFormat.Records(File.ReadAllBytes(path)))
        {
            if (batch.Length < 12)
            {
                throw new InvalidDataException($"{name} holds a write batch shorter than its header.");
            }

            var sequence = BinaryPrimitives.ReadUInt64LittleEndian(batch);
            var count = BinaryPrimitives.ReadUInt32LittleEndian(batch.AsSpan(8));
            var data = batch.AsSpan();
            var position = 12;

            for (var i = 0u; i < count; i++)
            {
                if (position >= data.Length)
                {
                    throw new InvalidDataException($"{name} holds a write batch shorter than its count.");
                }

                var type = data[position++];

                if (type is not (LevelDbFormat.ValueType or LevelDbFormat.DeletionType))
                {
                    throw new InvalidDataException($"{name} holds a write of kind {type}, which is neither a value nor a deletion.");
                }

                var key = LevelDbFormat.ReadSlice(data, ref position).ToArray();
                var value = type == LevelDbFormat.ValueType
                    ? LevelDbFormat.ReadSlice(data, ref position)
                    : [];

                if (wanted(key))
                {
                    found.Add(new Entry(key, sequence + i, type, withValues ? value.ToArray() : []));
                }
            }
        }

        return found;
    }

    /// <summary>The live tables and logs a manifest describes, replayed edit by edit.</summary>
    private static ManifestVersion Replay(byte[] manifest, string name)
    {
        var live = new Dictionary<ulong, (byte[] Smallest, byte[] Largest)>();
        ulong logNumber = 0, prevLogNumber = 0;
        var records = LevelDbFormat.Records(manifest);

        if (records.Count == 0)
        {
            throw new InvalidDataException($"{name} holds no readable record.");
        }

        foreach (var record in records)
        {
            var data = record.AsSpan();
            var position = 0;

            while (position < data.Length)
            {
                var tag = LevelDbFormat.ReadVarint32(data, ref position);

                switch (tag)
                {
                    case 1:
                        var comparator = Encoding.UTF8.GetString(LevelDbFormat.ReadSlice(data, ref position));

                        if (comparator != BytewiseComparator)
                        {
                            throw new InvalidDataException(
                                $"{name} orders its keys with {comparator}, which this reader does not.");
                        }

                        break;

                    case 2:
                        logNumber = LevelDbFormat.ReadVarint64(data, ref position);
                        break;

                    case 3:
                    case 4:
                        // The next file number and the last sequence: a writer's business.
                        LevelDbFormat.ReadVarint64(data, ref position);
                        break;

                    case 5:
                        // A compaction pointer: a level and a key, neither needed to read.
                        LevelDbFormat.ReadVarint32(data, ref position);
                        LevelDbFormat.ReadSlice(data, ref position);
                        break;

                    case 6:
                        LevelDbFormat.ReadVarint32(data, ref position);
                        live.Remove(LevelDbFormat.ReadVarint64(data, ref position));
                        break;

                    case 7:
                        LevelDbFormat.ReadVarint32(data, ref position);
                        var number = LevelDbFormat.ReadVarint64(data, ref position);
                        LevelDbFormat.ReadVarint64(data, ref position);
                        var smallest = UserKey(LevelDbFormat.ReadSlice(data, ref position), name);
                        var largest = UserKey(LevelDbFormat.ReadSlice(data, ref position), name);
                        live[number] = (smallest, largest);
                        break;

                    case 9:
                        prevLogNumber = LevelDbFormat.ReadVarint64(data, ref position);
                        break;

                    default:
                        throw new InvalidDataException(
                            $"{name} holds an edit of kind {tag}, which this reader does not know.");
                }
            }
        }

        return new ManifestVersion(
            [.. live.OrderBy(t => t.Key).Select(t => (t.Key, t.Value.Smallest, t.Value.Largest))],
            logNumber,
            prevLogNumber);
    }

    /// <summary>The user key inside an internal key, which ends in eight bytes of sequence and type.</summary>
    private static byte[] UserKey(ReadOnlySpan<byte> internalKey, string where) =>
        internalKey.Length >= 8
            ? internalKey[..^8].ToArray()
            : throw new InvalidDataException($"{where} holds a key shorter than its sequence number.");

    private static bool IsManifestName(string name) =>
        name.StartsWith("MANIFEST-", StringComparison.Ordinal)
        && name.Length > "MANIFEST-".Length
        && name.AsSpan("MANIFEST-".Length).IndexOfAnyExceptInRange('0', '9') < 0;

    /// <summary>A table's file: <c>.ldb</c>, or <c>.sst</c> as older LevelDB named it.</summary>
    private static string? TablePath(string directory, ulong number) =>
        new[] { ".ldb", ".sst" }
            .Select(extension => Path.Combine(directory, $"{number:D6}{extension}"))
            .FirstOrDefault(File.Exists);

    private static ulong? FileNumber(string path, string extension)
    {
        var name = Path.GetFileName(path);

        // EnumerateFiles' "*.log" also matches a longer extension on Windows.
        if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return ulong.TryParse(name.AsSpan(0, name.Length - extension.Length), out var number)
            ? number
            : null;
    }

    private sealed record ManifestVersion(
        IReadOnlyList<(ulong Number, byte[] Smallest, byte[] Largest)> Tables,
        ulong LogNumber,
        ulong PrevLogNumber);

    /// <summary>One write: a key, its sequence number, value or deletion, and the value.</summary>
    private sealed record Entry(byte[] Key, ulong Sequence, byte Type, byte[] Value);

    /// <summary>A table the manifest names, with the range of user keys it holds.</summary>
    private sealed record LiveTable(string Path, byte[] Smallest, byte[] Largest)
    {
        public bool MayHold(byte[] key) =>
            Smallest.AsSpan().SequenceCompareTo(key) <= 0 && Largest.AsSpan().SequenceCompareTo(key) >= 0;

        /// <summary>
        /// Could a key starting with <paramref name="prefix"/> be in range? A
        /// smallest key above the prefix that does not start with it is above
        /// every key that does.
        /// </summary>
        public bool MayHoldPrefix(byte[] prefix) =>
            Largest.AsSpan().SequenceCompareTo(prefix) >= 0
            && (Smallest.AsSpan().SequenceCompareTo(prefix) <= 0 || Smallest.AsSpan().StartsWith(prefix));
    }

    /// <summary>One table file, read through its index a block at a time.</summary>
    private sealed class TableReader : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly string _name;
        private readonly long _length;

        private TableReader(SafeFileHandle handle, string name)
        {
            _handle = handle;
            _name = name;
            _length = RandomAccess.GetLength(handle);
        }

        public static TableReader Open(string path) =>
            new(File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
                Path.GetFileName(path));

        public void Dispose() => _handle.Dispose();

        /// <summary>
        /// The table's entries from the first block that can hold
        /// <paramref name="userKey"/>, in key order, newest first within a key.
        /// </summary>
        /// <remarks>
        /// An index entry's key is at or after the last key of its block, so a
        /// block whose index key is below <paramref name="userKey"/> holds only
        /// keys below it and is not read.
        /// </remarks>
        public IEnumerable<Entry> From(byte[] userKey)
        {
            if (_length < LevelDbFormat.FooterSize)
            {
                throw new InvalidDataException($"{_name} is shorter than a table's footer.");
            }

            var footer = Read(_length - LevelDbFormat.FooterSize, LevelDbFormat.FooterSize);

            if (BinaryPrimitives.ReadUInt64LittleEndian(footer.AsSpan(40)) != LevelDbFormat.TableMagic)
            {
                throw new InvalidDataException($"{_name} does not end the way a table does.");
            }

            var position = 0;
            LevelDbFormat.ReadVarint64(footer, ref position);
            LevelDbFormat.ReadVarint64(footer, ref position);
            var indexOffset = LevelDbFormat.ReadVarint64(footer, ref position);
            var indexSize = LevelDbFormat.ReadVarint64(footer, ref position);

            foreach (var (separator, handle) in BlockEntries(ReadBlock(indexOffset, indexSize), _name))
            {
                if (UserKey(separator, _name).AsSpan().SequenceCompareTo(userKey) < 0)
                {
                    continue;
                }

                var at = 0;
                var offset = LevelDbFormat.ReadVarint64(handle, ref at);
                var size = LevelDbFormat.ReadVarint64(handle, ref at);

                foreach (var (key, value) in BlockEntries(ReadBlock(offset, size), _name))
                {
                    if (key.Length < 8)
                    {
                        throw new InvalidDataException($"{_name} holds a key shorter than its sequence number.");
                    }

                    var user = key[..^8];

                    if (user.AsSpan().SequenceCompareTo(userKey) < 0)
                    {
                        continue;
                    }

                    var tag = BinaryPrimitives.ReadUInt64LittleEndian(key.AsSpan(key.Length - 8));
                    yield return new Entry(user, tag >> 8, (byte)(tag & 0xff), value);
                }
            }
        }

        private byte[] ReadBlock(ulong offset, ulong size)
        {
            if (size > LevelDbFormat.MaxBlockBytes
                || offset > (ulong)_length
                || size + LevelDbFormat.BlockTrailerSize > (ulong)_length - offset)
            {
                throw new InvalidDataException($"{_name} points to a block past its own end.");
            }

            var raw = Read((long)offset, (int)size + LevelDbFormat.BlockTrailerSize);
            var contents = raw.AsSpan(0, (int)size);
            var type = raw[(int)size];
            var checksum = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan((int)size + 1));

            if (LevelDbFormat.BlockChecksum(contents, type) != checksum)
            {
                throw new InvalidDataException(
                    $"The block at byte {offset} of {_name} fails its checksum.");
            }

            return LevelDbFormat.Decompress(contents, type, $"The block at byte {offset} of {_name}");
        }

        private byte[] Read(long offset, int count)
        {
            var buffer = new byte[count];
            var read = 0;

            while (read < count)
            {
                var n = RandomAccess.Read(_handle, buffer.AsSpan(read), offset + read);

                if (n == 0)
                {
                    throw new InvalidDataException($"{_name} ends before byte {offset + count}.");
                }

                read += n;
            }

            return buffer;
        }

        /// <summary>A block's entries, each key rebuilt from the prefix it shares with the one before.</summary>
        private static List<(byte[] Key, byte[] Value)> BlockEntries(byte[] block, string name)
        {
            if (block.Length < 4)
            {
                throw new InvalidDataException($"{name} holds a block shorter than its restart count.");
            }

            var restarts = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(block.Length - 4));
            var limit = block.Length - 4L - (4L * restarts);

            if (limit < 0)
            {
                throw new InvalidDataException($"{name} holds a block with more restarts than room for them.");
            }

            var data = block.AsSpan(0, (int)limit);
            var entries = new List<(byte[], byte[])>();
            var key = Array.Empty<byte>();
            var position = 0;

            while (position < data.Length)
            {
                var shared = LevelDbFormat.ReadVarint32(data, ref position);
                var unshared = LevelDbFormat.ReadVarint32(data, ref position);
                var valueLength = LevelDbFormat.ReadVarint32(data, ref position);

                if (shared > key.Length
                    || unshared > data.Length - position
                    || valueLength > data.Length - position - unshared)
                {
                    throw new InvalidDataException($"{name} holds an entry that runs past its block.");
                }

                var next = new byte[shared + unshared];
                key.AsSpan(0, shared).CopyTo(next);
                data.Slice(position, unshared).CopyTo(next.AsSpan(shared));
                position += unshared;

                entries.Add((next, data.Slice(position, valueLength).ToArray()));
                position += valueLength;
                key = next;
            }

            return entries;
        }
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y) =>
            x is not null && y is not null && x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj)
        {
            var hash = new HashCode();
            hash.AddBytes(obj);
            return hash.ToHashCode();
        }
    }
}
