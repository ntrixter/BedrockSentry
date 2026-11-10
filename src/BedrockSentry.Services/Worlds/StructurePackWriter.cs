using BedrockSentry.Core.Addons;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Structures;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Worlds;

/// <summary>One structure to put in the pack: its name and its <c>.mcstructure</c> bytes.</summary>
public sealed record StructurePackFile(StructureName Name, byte[] Bytes);

/// <summary>What a change to a world's structures pack came to.</summary>
public enum StructurePackState
{
    /// <summary>The pack holds exactly the structures given and is switched on in the world.</summary>
    Placed,

    /// <summary>The pack is switched off and its folder is gone, or neither was there.</summary>
    Cleared,

    /// <summary>Nothing was written; the reason says why.</summary>
    Refused,

    /// <summary>
    /// A write failed partway. The reason says what is left: files without a
    /// registration are not loaded, and a registration without its folder is
    /// reported by BDS as a missing pack.
    /// </summary>
    Failed,
}

/// <summary>What a change to the pack did.</summary>
/// <param name="Structures">What the pack holds afterwards, as far as is known.</param>
public sealed record StructurePackResult(
    StructurePackState State, IReadOnlyList<StructureName> Structures, string? Reason);

/// <summary>
/// Writes <see cref="StructurePack"/> into a world: its folder, its structure
/// files and its registration in <c>world_behavior_packs.json</c> (<c>D86</c>).
/// </summary>
/// <remarks>
/// <para><strong>Only while the world's server is stopped</strong>, and the caller
/// holds that: BDS reads a pack's structures when the world loads, and rewrites
/// the world's pack list when it stops, discarding an edit made while it ran
/// (<c>D85</c>).</para>
///
/// <para><strong>Every input is checked before anything is written.</strong> A name
/// outside the pack's namespace, bytes that are not a structure, or a pack list
/// that cannot be read refuse the whole change. A list nobody could parse is never
/// rewritten from scratch, since that deletes every registration in it.</para>
///
/// <para><strong>Files first, registration last; registration off first, files
/// last.</strong> Stopped partway, a world then holds files that nothing loads
/// rather than a registration naming a pack that is not there.</para>
/// </remarks>
public sealed class StructurePackWriter(ILogger<StructurePackWriter> logger)
{
    private static readonly string BehaviorFolder = PackPlacementLayout.ServerFolder(PackPlacement.Behavior)!;
    private static readonly string ListFileName = PackPlacementLayout.WorldListFileName(PackPlacement.Behavior)!;

    /// <summary>
    /// Removes the pack's folder. A seam, because a folder delete that fails
    /// partway cannot be arranged on every platform the suite runs on.
    /// </summary>
    internal Action<string> DeleteDirectory { get; init; } = path => Directory.Delete(path, recursive: true);

    /// <summary>The pack's folder in a world.</summary>
    public static string PackDirectory(string worldDirectory) =>
        Path.Combine(worldDirectory, BehaviorFolder, StructurePack.FolderName);

    /// <summary>The structures the pack in this world holds, by file.</summary>
    public static IReadOnlyList<StructureName> Held(string worldDirectory)
    {
        var folder = Path.Combine(
            PackDirectory(worldDirectory), StructurePack.StructuresFolder, StructurePack.Namespace);

        if (!Directory.Exists(folder))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(folder, "*" + StructureFile.Extension)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(StructureName.IsPlainPart)
            .Order(StringComparer.Ordinal)
            .Select(name => StructureName.Of(StructurePack.Namespace, name!))];
    }

    /// <summary>
    /// Makes the pack in this world hold exactly <paramref name="files"/>, and
    /// switches it on.
    /// </summary>
    /// <remarks>
    /// Structures an earlier change left that are not among these are removed,
    /// so the server loads what this change was for and nothing older.
    /// </remarks>
    public async Task<StructurePackResult> PlaceAsync(
        string worldDirectory, IReadOnlyList<StructurePackFile> files, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldDirectory);
        ArgumentNullException.ThrowIfNull(files);

        if (!Directory.Exists(worldDirectory))
        {
            return Refused($"The world folder {worldDirectory} is not there.");
        }

        var placed = new List<(StructureName Name, string Path, byte[] Bytes)>();

        foreach (var file in files)
        {
            if (StructurePack.RelativePathFor(file.Name) is not { } relative)
            {
                return Refused(
                    $"{file.Name} cannot go in {StructurePack.Name}: its structures are named "
                    + $"{StructurePack.Namespace}: followed by lower-case letters, digits and underscores.");
            }

            if (placed.Any(p => p.Name == file.Name))
            {
                return Refused($"{file.Name} was given twice.");
            }

            if (StructureFile.Read(file.Bytes) is { IsStructure: false } read)
            {
                return Refused($"The bytes given for {file.Name} are not a structure: {read.Reason}");
            }

            placed.Add((file.Name, Path.Combine(PackDirectory(worldDirectory), relative), file.Bytes));
        }

        var list = ReadList(worldDirectory);

        if (list.Refusal is { } unreadable)
        {
            return Refused(unreadable);
        }

        var change = list.List.Enable(StructurePack.Uuid, StructurePack.Version);

        if (change.Kind == WorldPackChangeKind.RefusedAlreadyAtAnotherVersion)
        {
            // Registered at another version, which only another build of this
            // wrapper could have written; the pack is this wrapper's, so it moves.
            change = list.List.SetVersion(StructurePack.Uuid, StructurePack.Version);
        }

        var names = placed.Select(p => p.Name).ToList();

        try
        {
            var pack = PackDirectory(worldDirectory);
            var structures = Path.Combine(pack, StructurePack.StructuresFolder, StructurePack.Namespace);
            Directory.CreateDirectory(structures);

            var manifest = Path.Combine(pack, AddonLibraryLayout.ManifestFileName);
            var text = StructurePack.Manifest();

            if (!File.Exists(manifest) || await File.ReadAllTextAsync(manifest, cancellationToken).ConfigureAwait(false) != text)
            {
                await AtomicFile.WriteAllTextAsync(manifest, text, cancellationToken).ConfigureAwait(false);
            }

            foreach (var stale in Held(worldDirectory).Except(names))
            {
                File.Delete(Path.Combine(pack, StructurePack.RelativePathFor(stale)!));
            }

            foreach (var (_, path, bytes) in placed)
            {
                await AtomicFile.WriteAsync(
                        path,
                        temp => File.WriteAllBytesAsync(temp, bytes, cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not write {Pack} into {World}", StructurePack.Name, worldDirectory);
            return new(StructurePackState.Failed, Held(worldDirectory),
                $"The structures could not all be written, and {StructurePack.Name} was not switched on: {ex.Message}");
        }

        if (change.Writes)
        {
            try
            {
                await AtomicFile.WriteAllTextAsync(
                        Path.Combine(worldDirectory, ListFileName), change.List.Write(), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not switch {Pack} on in {World}", StructurePack.Name, worldDirectory);
                return new(StructurePackState.Failed, names,
                    $"The structures are in place and {StructurePack.Name} could not be switched on: {ex.Message}");
            }
        }

        logger.LogInformation(
            "{Pack} in {World} holds {Count} structure(s)", StructurePack.Name, worldDirectory, names.Count);

        return new(StructurePackState.Placed, names, null);
    }

    /// <summary>Switches the pack off in this world and removes its folder.</summary>
    public async Task<StructurePackResult> ClearAsync(
        string worldDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldDirectory);

        if (!Directory.Exists(worldDirectory))
        {
            return new(StructurePackState.Cleared, [], null);
        }

        var list = ReadList(worldDirectory);

        if (list.Refusal is { } unreadable)
        {
            return Refused(unreadable);
        }

        var change = list.List.Disable(StructurePack.Uuid);

        try
        {
            if (change.Writes)
            {
                await AtomicFile.WriteAllTextAsync(
                        Path.Combine(worldDirectory, ListFileName), change.List.Write(), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not switch {Pack} off in {World}", StructurePack.Name, worldDirectory);
            return new(StructurePackState.Failed, Held(worldDirectory),
                $"{StructurePack.Name} could not be switched off, so its folder was left in place: {ex.Message}");
        }

        var pack = PackDirectory(worldDirectory);

        try
        {
            if (Directory.Exists(pack))
            {
                DeleteDirectory(pack);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A recursive delete keeps going after its first failure (T185), so
            // what is left is read rather than assumed.
            logger.LogWarning(ex, "Could not remove {Pack}'s folder from {World}", StructurePack.Name, worldDirectory);
            return new(StructurePackState.Failed, Held(worldDirectory),
                $"{StructurePack.Name} is switched off and part of its folder could not be removed: {ex.Message}");
        }

        return new(StructurePackState.Cleared, [], null);
    }

    private static StructurePackResult Refused(string reason) => new(StructurePackState.Refused, [], reason);

    /// <summary>The world's behaviour pack list, or the sentence refusing to touch it.</summary>
    private static (WorldPackList List, string? Refusal) ReadList(string worldDirectory)
    {
        var path = Path.Combine(worldDirectory, ListFileName);
        string? json;

        try
        {
            json = File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (WorldPackList.Empty, $"{ListFileName} could not be read, so nothing was changed: {ex.Message}");
        }

        var read = WorldPackList.Read(json);

        return read.State is WorldPackListState.Read or WorldPackListState.Absent
            ? (read.List, null)
            : (WorldPackList.Empty,
                $"{ListFileName} could not be understood, so nothing was changed"
                + (read.Reason is { Length: > 0 } why ? $": {why.TrimEnd('.')}." : "."));
    }
}
