using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Structures;

namespace BedrockSentry.Services.Worlds;

/// <summary>What reading one structure out of a world found.</summary>
public enum WorldStructureState
{
    /// <summary>There, and its bytes read as a structure file.</summary>
    Found,

    /// <summary>The world holds no structure of that name.</summary>
    Absent,

    /// <summary>A value is stored under the name and it does not read as a structure.</summary>
    NotAStructure,

    /// <summary>The database could not be read, so whether the structure is there is not known.</summary>
    Unreadable,
}

/// <summary>One structure read out of a world, or why it was not.</summary>
/// <param name="Bytes">The structure exactly as stored, which is a <c>.mcstructure</c> file.</param>
public sealed record WorldStructureRead(
    WorldStructureState State, StructureName Name, byte[]? Bytes, StructureFileInfo? Info, string? Reason);

/// <summary>The structures a world holds, or why they could not be listed.</summary>
public sealed record WorldStructureNames(IReadOnlyList<StructureName>? Names, string? Reason);

/// <summary>
/// Reads the structures <c>/structure save … disk</c> stored in a world, out of
/// a stopped world or a copy of one (<c>D86</c>).
/// </summary>
/// <remarks>
/// <para><strong>Why a world, and not a file.</strong> Measured on BDS 1.26.52.3: a
/// <c>disk</c> save writes no file anywhere; it stores one key in the world's
/// database, whose value is a whole <c>.mcstructure</c>. Moving a structure to
/// another world is reading that key and writing the bytes as a file.</para>
///
/// <para><strong>A save reaches the files a few seconds after the command
/// answers.</strong> A copy taken under <c>save hold</c> 1.5 s after a save lacked
/// the key and one taken 4 s after had it, so <see cref="WorldStructureState.Absent"/>
/// from a copy taken straight after a save is a reason to take another, and from a
/// world whose server has stopped it means the save never happened.</para>
/// </remarks>
public static class WorldStructures
{
    /// <summary>Opens the database of the world in <paramref name="worldDirectory"/>.</summary>
    public static WorldDatabaseOpen Open(string worldDirectory) =>
        WorldDatabase.Open(Path.Combine(worldDirectory, WorldFolder.DatabaseFolder));

    /// <summary>Reads one structure.</summary>
    public static WorldStructureRead Read(WorldDatabase database, StructureName name)
    {
        ArgumentNullException.ThrowIfNull(database);

        var value = database.Get(name.DatabaseKeyBytes);

        switch (value.State)
        {
            case WorldDatabaseLookupState.Absent:
                return new(WorldStructureState.Absent, name, null, null, null);

            case WorldDatabaseLookupState.Unreadable:
                return new(WorldStructureState.Unreadable, name, null, null, value.Reason);
        }

        var file = StructureFile.Read(value.Value);

        return file.Info is { } info
            ? new(WorldStructureState.Found, name, value.Value, info, null)
            : new(WorldStructureState.NotAStructure, name, null, null,
                $"The value stored as {name} is not a structure: {file.Reason}");
    }

    /// <summary>Every structure the world holds, in key order.</summary>
    public static WorldStructureNames Names(WorldDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        var keys = database.KeysStartingWith(StructureName.KeyPrefixBytes);

        if (keys.Keys is null)
        {
            return new(null, keys.Reason);
        }

        var names = new List<StructureName>();

        foreach (var key in keys.Keys)
        {
            if (StructureName.TryFromDatabaseKey(key, out var name))
            {
                names.Add(name);
            }
        }

        return new(names, null);
    }
}
