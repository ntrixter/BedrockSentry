using System.Buffers.Binary;
using System.Text;

namespace BedrockSentry.Core.Structures;

/// <summary>Three whole-number coordinates, as a structure stores its size and origin.</summary>
public readonly record struct BlockVector(int X, int Y, int Z)
{
    public override string ToString() => $"{X}, {Y}, {Z}";
}

/// <summary>What a <c>.mcstructure</c> says about itself.</summary>
/// <param name="FormatVersion">The file's <c>format_version</c>; 2 on every structure BDS 1.26.52.3 saved.</param>
/// <param name="Size">How many blocks it spans along each axis.</param>
/// <param name="Origin">
/// <c>structure_world_origin</c>: the lowest corner of the area it was saved from,
/// which is where a load puts it back.
/// </param>
/// <param name="Entities">How many entities it carries: mobs, armor stands, item frames.</param>
/// <param name="BlockEntities">
/// How many of its blocks carry data of their own: a chest's contents, a sign's text.
/// </param>
public sealed record StructureFileInfo(
    int FormatVersion, BlockVector Size, BlockVector Origin, int Entities, int BlockEntities)
{
    /// <summary>How many block positions the structure covers.</summary>
    public long Volume => (long)Size.X * Size.Y * Size.Z;
}

/// <summary>A structure's facts, or why the bytes are not a structure.</summary>
public readonly record struct StructureFileRead(StructureFileInfo? Info, string? Reason)
{
    public bool IsStructure => Info is not null;
}

/// <summary>
/// Reads the facts a <c>.mcstructure</c> carries at its top level, without
/// building its blocks.
/// </summary>
/// <remarks>
/// <para><strong>A <c>.mcstructure</c> is little-endian NBT</strong> whose root
/// compound holds <c>format_version</c>, <c>size</c>, <c>structure</c> and
/// <c>structure_world_origin</c>. The value BDS stores under a structure's key
/// (<see cref="StructureName.DatabaseKey"/>) has exactly that shape, the shape of
/// the files in BDS's own vanilla behaviour packs, measured on 1.26.52.3
/// (<c>D86</c>); so a value read out of a world is a file as it stands.</para>
///
/// <para><strong>Skipped, not read:</strong> <c>block_indices</c> is two lists
/// of one integer per block position, 12.6 MB at the largest size BDS saves, and
/// nothing here needs a single one of them.</para>
/// </remarks>
public static class StructureFile
{
    /// <summary>A structure file's extension.</summary>
    public const string Extension = ".mcstructure";

    /// <summary>
    /// The largest box one structure holds, measured on BDS 1.26.52.3: <em>"A
    /// structure's size cannot be larger than (64, 384, 64)"</em>.
    /// </summary>
    public static BlockVector MaxSize { get; } = new(64, 384, 64);

    /// <summary>Nesting deeper than this is refused rather than followed.</summary>
    private const int MaxDepth = 512;

    private const byte End = 0, Byte = 1, Short = 2, Int = 3, Long = 4, Float = 5, Double = 6,
        ByteArray = 7, String = 8, List = 9, Compound = 10, IntArray = 11, LongArray = 12;

    /// <summary>Reads a structure's facts from its bytes.</summary>
    public static StructureFileRead Read(ReadOnlySpan<byte> data)
    {
        try
        {
            var reader = new Reader(data);

            if (reader.ReadByte() != Compound)
            {
                return new(null, "It does not start with a compound, as every structure file does.");
            }

            reader.ReadString();

            int? format = null;
            BlockVector? size = null, origin = null;
            (int Entities, int BlockEntities)? body = null;

            for (var tag = reader.ReadByte(); tag != End; tag = reader.ReadByte())
            {
                var name = reader.ReadString();

                switch (name)
                {
                    case "format_version" when tag == Int:
                        format = reader.ReadInt();
                        break;

                    case "size" when tag == List:
                        size = reader.ReadVector();
                        break;

                    case "structure_world_origin" when tag == List:
                        origin = reader.ReadVector();
                        break;

                    case "structure" when tag == Compound:
                        body = reader.ReadBody();
                        break;

                    default:
                        reader.Skip(tag, 1);
                        break;
                }
            }

            if (format is null || size is null || origin is null || body is null)
            {
                return new(null,
                    "It lacks "
                    + string.Join(", ", new[]
                        {
                            format is null ? "format_version" : null,
                            size is null ? "size" : null,
                            body is null ? "structure" : null,
                            origin is null ? "structure_world_origin" : null,
                        }.Where(n => n is not null))
                    + ", which every structure file carries.");
            }

            return new(new StructureFileInfo(format.Value, size.Value, origin.Value,
                body.Value.Entities, body.Value.BlockEntities), null);
        }
        catch (InvalidDataException ex)
        {
            return new(null, ex.Message);
        }
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;

        public byte ReadByte()
        {
            Need(1);
            return _data[_position++];
        }

        public int ReadInt()
        {
            Need(4);
            var value = BinaryPrimitives.ReadInt32LittleEndian(_data[_position..]);
            _position += 4;
            return value;
        }

        public string ReadString()
        {
            Need(2);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(_data[_position..]);
            _position += 2;
            Need(length);
            var text = Encoding.UTF8.GetString(_data.Slice(_position, length));
            _position += length;
            return text;
        }

        /// <summary>A list of three integers: a size or an origin.</summary>
        public BlockVector ReadVector()
        {
            var type = ReadByte();
            var count = ReadInt();

            if (type != Int || count != 3)
            {
                throw new InvalidDataException("A size or origin is not a list of three whole numbers.");
            }

            return new BlockVector(ReadInt(), ReadInt(), ReadInt());
        }

        /// <summary>
        /// The <c>structure</c> compound: counts its <c>entities</c>, and the block
        /// positions under <c>palette</c> → <c>default</c> →
        /// <c>block_position_data</c> that carry <c>block_entity_data</c>.
        /// </summary>
        public (int Entities, int BlockEntities) ReadBody()
        {
            int entities = 0, blockEntities = 0;

            for (var tag = ReadByte(); tag != End; tag = ReadByte())
            {
                var name = ReadString();

                if (name == "entities" && tag == List)
                {
                    var type = ReadByte();
                    entities = ReadInt();

                    if (entities < 0)
                    {
                        throw new InvalidDataException("The entity list has a negative length.");
                    }

                    for (var i = 0; i < entities; i++)
                    {
                        Skip(type, 2);
                    }
                }
                else if (name == "palette" && tag == Compound)
                {
                    blockEntities += ReadPalettes();
                }
                else
                {
                    Skip(tag, 2);
                }
            }

            return (entities, blockEntities);
        }

        private int ReadPalettes()
        {
            var count = 0;

            for (var tag = ReadByte(); tag != End; tag = ReadByte())
            {
                var name = ReadString();

                if (name == "default" && tag == Compound)
                {
                    for (var inner = ReadByte(); inner != End; inner = ReadByte())
                    {
                        var key = ReadString();

                        if (key == "block_position_data" && inner == Compound)
                        {
                            count += CountBlockEntities();
                        }
                        else
                        {
                            Skip(inner, 4);
                        }
                    }
                }
                else
                {
                    Skip(tag, 3);
                }
            }

            return count;
        }

        /// <summary>Each child of <c>block_position_data</c> that holds <c>block_entity_data</c>.</summary>
        private int CountBlockEntities()
        {
            var count = 0;

            for (var tag = ReadByte(); tag != End; tag = ReadByte())
            {
                ReadString();

                if (tag != Compound)
                {
                    Skip(tag, 5);
                    continue;
                }

                var carries = false;

                for (var inner = ReadByte(); inner != End; inner = ReadByte())
                {
                    carries |= ReadString() == "block_entity_data" && inner == Compound;
                    Skip(inner, 6);
                }

                if (carries)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>Steps over one value of the given type.</summary>
        public void Skip(byte type, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new InvalidDataException("It nests deeper than any structure file does.");
            }

            switch (type)
            {
                case Byte: Advance(1); break;
                case Short: Advance(2); break;
                case Int or Float: Advance(4); break;
                case Long or Double: Advance(8); break;
                case ByteArray: Advance(Length(1)); break;
                case String: ReadString(); break;
                case IntArray: Advance(Length(4)); break;
                case LongArray: Advance(Length(8)); break;

                case List:
                    var element = ReadByte();
                    var count = ReadInt();

                    if (count < 0)
                    {
                        throw new InvalidDataException("A list has a negative length.");
                    }

                    if (FixedWidth(element) is { } width)
                    {
                        Advance((long)count * width);
                    }
                    else
                    {
                        for (var i = 0; i < count; i++)
                        {
                            Skip(element, depth + 1);
                        }
                    }

                    break;

                case Compound:
                    for (var tag = ReadByte(); tag != End; tag = ReadByte())
                    {
                        ReadString();
                        Skip(tag, depth + 1);
                    }

                    break;

                default:
                    throw new InvalidDataException($"It holds a value of type {type}, which NBT does not have.");
            }
        }

        private static int? FixedWidth(byte type) => type switch
        {
            End => 0,
            Byte => 1,
            Short => 2,
            Int or Float => 4,
            Long or Double => 8,
            _ => null,
        };

        private long Length(int width)
        {
            var count = ReadInt();
            return count >= 0
                ? (long)count * width
                : throw new InvalidDataException("An array has a negative length.");
        }

        private void Advance(long count)
        {
            Need(count);
            _position += (int)count;
        }

        private readonly void Need(long count)
        {
            if (count > _data.Length - _position)
            {
                throw new InvalidDataException("It ends partway through a value.");
            }
        }
    }
}
