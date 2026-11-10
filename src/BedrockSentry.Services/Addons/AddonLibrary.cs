using System.IO.Compression;
using BedrockSentry.Core.Addons;
using BedrockSentry.Core.Configuration;
using Microsoft.Extensions.Logging;

using BedrockSentry.Services.Backups;

namespace BedrockSentry.Services.Addons;

/// <summary>One version of a pack, as the library holds it.</summary>
public sealed record LibraryPackVersion(
    PackVersion Version, PackManifest Manifest, string Directory);

/// <summary>A pack in the library: one uuid, every version of it.</summary>
/// <remarks>
/// Grouped by uuid because that is what identity means here (<c>D79</c>) — two
/// files that look like different add-ons are the same add-on when their uuids
/// match, and an instance enables a <em>version</em> rather than a file.
/// </remarks>
public sealed record LibraryPack(string Uuid, string Name, IReadOnlyList<LibraryPackVersion> Versions)
{
    /// <summary>The highest version held, which is what an update would offer.</summary>
    public LibraryPackVersion Newest => Versions.MaxBy(v => v.Version)!;
}

/// <summary>What became of one pack inside an imported archive.</summary>
public enum PackImportOutcome
{
    Imported,

    /// <summary>
    /// This uuid and version are already on the shelf, so nothing was written.
    /// Deliberately not an overwrite: a library copy somebody has edited is
    /// theirs, and replacing it silently is how that edit disappears.
    /// </summary>
    AlreadyPresent,

    /// <summary>Read, and not usable — the reason says why.</summary>
    Rejected,
}

/// <summary>What the library holds, as far as it could be read.</summary>
/// <param name="Packs">Every pack that could be read.</param>
/// <param name="Unreadable">Why something under the library could not be read, or null.</param>
public sealed record LibraryListing(IReadOnlyList<LibraryPack> Packs, string? Unreadable)
{
    /// <summary>What a page says about a listing that could not be read whole, or null.</summary>
    public string? Sentence => Unreadable is not { } reason
        ? null
        : Packs.Count == 0
            ? $"The add-on library could not be read, so nothing on the shelf is listed: {reason.TrimEnd('.')}."
            : $"Part of the add-on library could not be read, so some packs are not listed: {reason.TrimEnd('.')}.";

    /// <summary>What a page says in place of the table when the shelf is empty, or null.</summary>
    /// <remarks>
    /// Only for a library read whole: under <see cref="Sentence"/> it would be a
    /// second statement about the same shelf (<c>T283</c>'s shape). A listing with
    /// no packs gets no table either way, header row included (<c>T310</c>).
    /// </remarks>
    public string? EmptyState => Packs.Count == 0 && Unreadable is null ? "Nothing here yet." : null;
}

/// <summary>One pack's fate in an import.</summary>
public sealed record ImportedPack(
    string? Uuid,
    string Name,
    PackVersion? Version,
    PackPlacement Placement,
    PackImportOutcome Outcome,
    string? Reason);

/// <summary>What an import did.</summary>
/// <param name="Accepted">
/// True when the archive itself was usable. Individual packs inside it may
/// still have been rejected — <strong>the two are different questions</strong>,
/// and collapsing them would report an archive holding one good pack and one
/// broken one as a flat failure.
/// </param>
public sealed record PackImportResult(
    bool Accepted, IReadOnlyList<ImportedPack> Packs, string? Reason)
{
    public static PackImportResult Refused(string reason) => new(false, [], reason);
}

/// <summary>
/// The shared shelf of add-ons, and the only writer of it.
/// </summary>
/// <remarks>
/// <para><strong>Adding to the library switches nothing on anywhere.</strong> A
/// pack reaches an instance by being copied into that instance's own folders
/// and registered in its world's pack list; this type does the copying and
/// nothing else decides.</para>
///
/// <para><strong>Extraction is mandatory rather than a convenience</strong>: BDS
/// cannot read a packed <c>.mcpack</c>, it wants a folder. Every write goes
/// through <see cref="PackArchiveRules"/> first, and an archive is taken whole
/// or not at all.</para>
///
/// <para><strong>A pack is assembled somewhere else and moved into place.</strong>
/// A half-extracted folder under the library would list as a pack and be
/// offered for copying into a server, so the move is what makes it visible and
/// a failure leaves the shelf as it was.</para>
/// </remarks>
public sealed class AddonLibrary(ISentryPaths paths, ILogger<AddonLibrary> logger)
{
    private const string IncomingFolder = ".incoming";

    /// <summary>Where the shelf is.</summary>
    public string Directory => paths.AddonLibrary;

    /// <summary>
    /// Lists a folder's subfolders. A seam, because a folder the process cannot read
    /// cannot be arranged where the suite runs as root (<c>T298</c>).
    /// </summary>
    internal Func<string, IEnumerable<string>> EnumerateDirectories { get; init; } =
        System.IO.Directory.EnumerateDirectories;

    /// <summary>Every pack held, grouped by uuid, newest version first within each.</summary>
    /// <remarks>
    /// What could be read. A library that could not be read lists as empty here, which is
    /// the safe answer for a caller asking whether a pack is held; the pages ask
    /// <see cref="Read"/> so they can say so (<c>T298</c>).
    /// </remarks>
    public IReadOnlyList<LibraryPack> List() => Read().Packs;

    /// <summary>
    /// Every pack held, and whether anything under the library could not be read.
    /// </summary>
    /// <remarks>
    /// <para><strong>An unreadable folder is a state, not an exception</strong>
    /// (<c>T298</c>). The listing enumerated the folder with no catch, both add-on pages
    /// call it as they load, and an access-denied library ended the circuit on each,
    /// so the seeding's own sentence about the same failure was never seen. A pack
    /// folder that cannot be read is skipped and the rest are listed; the library
    /// folder itself unreadable lists nothing. Either way the reason is kept.</para>
    /// </remarks>
    public LibraryListing Read()
    {
        try
        {
            return ReadListing();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The add-on library at {Directory} could not be read.", Directory);
            return new LibraryListing([], ex.Message);
        }
    }

    private LibraryListing ReadListing()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return new LibraryListing([], null);
        }

        var packs = new List<LibraryPack>();
        string? unreadable = null;

        foreach (var uuidDirectory in EnumerateDirectories(Directory))
        {
            var uuid = Path.GetFileName(uuidDirectory);

            // The shelf's own folders are named by a GUID. Anything else was put
            // here by hand and is skipped rather than half-read — including the
            // staging folder, which is a partial pack by definition.
            if (!AddonLibraryLayout.IsUsableIdentity(uuid))
            {
                continue;
            }

            var versions = new List<LibraryPackVersion>();

            try
            {
                foreach (var versionDirectory in EnumerateDirectories(uuidDirectory))
                {
                    if (ReadManifest(versionDirectory) is not { } manifest
                        || manifest.Version.Version is not { } version)
                    {
                        continue;
                    }

                    versions.Add(new LibraryPackVersion(version, manifest, versionDirectory));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "A pack under {Directory} could not be read.", uuidDirectory);
                unreadable ??= ex.Message;
                continue;
            }

            if (versions.Count > 0)
            {
                packs.Add(new LibraryPack(
                    uuid,
                    versions.MaxBy(v => v.Version)!.Manifest.Name,
                    [.. versions.OrderByDescending(v => v.Version)]));
            }
        }

        return new LibraryListing(
            [.. packs.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)], unreadable);
    }

    /// <summary>Find one version of one pack.</summary>
    public LibraryPackVersion? Find(string uuid, PackVersion version) =>
        List()
            .FirstOrDefault(p => string.Equals(p.Uuid, uuid, StringComparison.OrdinalIgnoreCase))
            ?.Versions.FirstOrDefault(v => v.Version == version);

    /// <summary>The largest archive that may be uploaded, compressed.</summary>
    /// <remarks>
    /// Separate from the uncompressed bound in <see cref="PackArchiveRules"/>:
    /// this one stops the bytes arriving at all, and that one stops them being
    /// written. A file that clears this and fails that is a compression bomb,
    /// which is exactly the pair being guarded against.
    /// </remarks>
    public const long MaxUploadBytes = 256L * 1024 * 1024;

    /// <summary>Take one version off the shelf.</summary>
    /// <remarks>
    /// <strong>It does not check who is using it</strong>, deliberately. Which
    /// instances have a pack switched on is a question about every instance,
    /// and answering it here would make the library depend on the registry it
    /// is shared by. <see cref="LibraryPackRemover"/> asks
    /// <see cref="LibraryRemoval.Plan"/> first, which refuses the companion pack
    /// (<c>D93</c>) and a pack a running server has, and takes it off the stopped
    /// servers that have it once that is confirmed (<c>D83</c>); this only does what
    /// it is told, once.
    /// </remarks>
    public bool Remove(string uuid, PackVersion version)
    {
        if (Find(uuid, version) is not { } pack)
        {
            return false;
        }

        try
        {
            System.IO.Directory.Delete(pack.Directory, recursive: true);

            // A uuid folder with no versions left is a folder that lists as
            // nothing, so it goes with the last version rather than accumulating.
            var parent = System.IO.Directory.GetParent(pack.Directory)!.FullName;

            if (!System.IO.Directory.EnumerateFileSystemEntries(parent).Any())
            {
                System.IO.Directory.Delete(parent);
            }

            logger.LogInformation("Removed add-on {Uuid} {Version} from the library.", uuid, version);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remove add-on {Uuid} {Version}.", uuid, version);
            return false;
        }
    }

    /// <summary>Take an uploaded archive onto the shelf.</summary>
    /// <param name="archive">
    /// The archive's bytes. Must be seekable: the manifests are read before
    /// anything is written, so the entries are visited twice.
    /// </param>
    /// <param name="archiveName">
    /// The uploaded file's own name, used to name a pack that has none of its
    /// own. An <c>.mcpack</c> holds one pack at the archive root, so its folder
    /// is the empty string and this is the only thing left to call it — which
    /// is exactly the case a rejection has to be able to name.
    /// </param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    public async Task<PackImportResult> ImportAsync(
        Stream archive,
        string? archiveName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);

        ZipArchive zip;
        try
        {
            zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            return PackImportResult.Refused(
                $"That file is not a readable archive. {ex.Message}");
        }

        using (zip)
        {
            var plan = PackArchiveRules.Plan(
                zip.Entries.Select(e => new ArchiveEntry(e.FullName, e.Length)));

            if (!plan.Accepted)
            {
                logger.LogWarning(
                    "Refused an add-on archive: {Verdict} on {Entry}.",
                    plan.Verdict, plan.OffendingEntry);

                return PackImportResult.Refused(plan.Reason ?? PackArchiveRules.Describe(plan.Verdict));
            }

            var found = AddonLibraryLayout.FindPacks(plan.Paths);
            if (found.Verdict != PackDiscoveryVerdict.Accept)
            {
                return PackImportResult.Refused(found.Reason!);
            }

            // The plan pairs each entry with its approved destination, so an
            // entry is matched to where it may go rather than to its own name —
            // nothing below ever writes to `entry.FullName`.
            var destinations = plan.Entries.ToDictionary(e => e.Source, e => e.Destination);

            var results = new List<ImportedPack>();

            foreach (var pack in found.Packs)
            {
                results.Add(await ImportOneAsync(
                        zip, destinations, pack, archiveName, cancellationToken)
                    .ConfigureAwait(false));
            }

            return new PackImportResult(true, results, null);
        }
    }

    /// <summary>Put a copy of a pack folder from a server onto the shelf (<c>D78</c> (a)).</summary>
    /// <param name="packDirectory">The pack's folder on the server.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <remarks>
    /// <para><strong>The folder is read, never changed.</strong> The copy is the
    /// library's; the server keeps its own, switched on or not as it was.</para>
    ///
    /// <para><strong>The same refusals as an upload, where they can apply.</strong> The
    /// manifest must name a usable uuid and a version; a version already on the shelf is
    /// left as it is; the size and file-count bounds of <see cref="PackArchiveRules"/>
    /// hold. A folder's names are already names this disk stores, so the per-entry
    /// checks have nothing to inspect, but a link inside it may lead anywhere, so a pack
    /// holding one is refused whole rather than copied without it.</para>
    ///
    /// <para><strong>Assembled under <c>.incoming</c> and moved</strong>, as an import
    /// is, so a failure leaves the shelf as it was.</para>
    /// </remarks>
    public async Task<ImportedPack> CopyFromFolderAsync(
        string packDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packDirectory);

        var source = Path.GetFileName(packDirectory.TrimEnd('/', '\\'));
        var manifestFile = Path.Combine(packDirectory, AddonLibraryLayout.ManifestFileName);

        PackManifestRead read;

        try
        {
            read = PackManifestReader.Read(PackFolder.ReadText(manifestFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Rejected(source, $"Its manifest could not be read: {ex.Message}");
        }

        if (read.Manifest is not { } manifest)
        {
            return Rejected(source, read.Reason ?? "Its manifest could not be read.");
        }

        var name = PackDisplayName.For(manifest.Name, PackFolder.ReadLanguage(packDirectory), source);

        if (!AddonLibraryLayout.IsUsableIdentity(manifest.Uuid))
        {
            return Rejected(
                name,
                "Its uuid is not a GUID, so this cannot tell it apart from another pack.");
        }

        if (manifest.Version.Version is not { } version)
        {
            return new ImportedPack(
                manifest.Uuid, name, null, manifest.Placement, PackImportOutcome.Rejected,
                manifest.Version.State == PackVersionState.Absent
                    ? "Its manifest carries no version, so there is nothing to keep it under."
                    : $"Its version could not be read: '{manifest.Version.Raw}'.");
        }

        var target = Path.Combine(
            Directory,
            AddonLibraryLayout.FolderName(manifest.Uuid),
            AddonLibraryLayout.VersionFolderName(version));

        if (System.IO.Directory.Exists(target))
        {
            return new ImportedPack(
                manifest.Uuid, name, version, manifest.Placement,
                PackImportOutcome.AlreadyPresent, null);
        }

        var staging = Path.Combine(Directory, IncomingFolder, Guid.NewGuid().ToString("N"));

        try
        {
            var files = new List<FileInfo>();
            long total = 0;

            // Walked first one folder at a time: the recursive walk below opens a junction
            // RedirectionGuard refuses and throws before the link is seen (T392 clause 4).
            if (ServerTree.FirstLink(packDirectory) is { } linked)
            {
                return Rejected(
                    name,
                    $"It holds a link ({Path.GetRelativePath(packDirectory, linked)}) to "
                    + "somewhere outside it, so it was not copied.");
            }

            foreach (var entry in new DirectoryInfo(packDirectory).EnumerateFileSystemInfos(
                         "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            {
                // A link is refused rather than followed or left out: followed, it copies
                // whatever it points at into the shelf; left out, the copy is not the pack.
                if (Security.ReparseTags.IsLink(entry))
                {
                    return Rejected(
                        name,
                        $"It holds a link ({Path.GetRelativePath(packDirectory, entry.FullName)}) to "
                        + "somewhere outside it, so it was not copied.");
                }

                if (entry is not FileInfo file)
                {
                    continue;
                }

                files.Add(file);
                total += file.Length;

                if (total > PackArchiveRules.DefaultMaxTotalBytes)
                {
                    return Rejected(
                        name,
                        $"It holds more than {PackArchiveRules.DefaultMaxTotalBytes / (1024 * 1024)} MB.");
                }

                if (files.Count > PackArchiveRules.DefaultMaxEntries)
                {
                    return Rejected(name, $"It holds more than {PackArchiveRules.DefaultMaxEntries} files.");
                }
            }

            foreach (var file in files)
            {
                var destination = Path.Combine(staging, Path.GetRelativePath(packDirectory, file.FullName));

                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                // Opened as the backups open a server's files: a folder swapped for a
                // link since the walk above is refused on the open handle.
                await using var from = ServerFileReader.Open(file.FullName);
                await using var to = File.Create(destination);
                await from.CopyToAsync(to, cancellationToken).ConfigureAwait(false);
            }

            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            System.IO.Directory.Move(staging, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(staging);
            return Rejected(name, ex.Message);
        }

        logger.LogInformation(
            "Copied add-on {Name} {Version} into the library from {Directory}.", name, version, packDirectory);

        return new ImportedPack(
            manifest.Uuid, name, version, manifest.Placement,
            PackImportOutcome.Imported, null);
    }

    /// <summary>
    /// Copy a pack out of the library into an instance's own folders.
    /// </summary>
    /// <remarks>
    /// <para><strong>Into the world, not the server root.</strong> Bedrock reads
    /// both, and the registration that switches a pack on is the world's — so
    /// putting the pack beside it keeps the two together: a world backup carries
    /// its add-ons (<c>D97</c>), removing the world removes them, and an
    /// administrator's own packs stay clear of the folders a Bedrock package ships
    /// in the root.
    /// <see cref="PackPlacementLayout.Writes"/> is the single statement of that,
    /// and the remover asks for every home rather than this one.</para>
    ///
    /// <para><strong>The destination folder is named by the uuid</strong>, not
    /// by the pack's display name. BDS resolves by the uuid inside the manifest
    /// and does not care what the folder is called, while a display name comes
    /// from a file the wrapper did not write and two packs may share one.</para>
    ///
    /// <para><strong>A pack cannot be placed without a world.</strong> Null is
    /// returned rather than falling back to the root: the fallback would put the
    /// pack somewhere this build's remover still reaches but the page would
    /// describe wrongly, and the caller already refuses before here when the
    /// world could not be resolved.</para>
    /// </remarks>
    public async Task<string?> CopyToInstanceAsync(
        LibraryPackVersion pack,
        string instanceDirectory,
        string? worldDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceDirectory);

        // The same rule that picks the world list this pack is registered in —
        // asked once, in one place. Asked twice, the copy went to the right
        // folder and the registration to the wrong file, which is a pack that is
        // present, listed, and never loaded.
        //
        // Null is refused rather than guessed: putting a pack in the wrong
        // folder is a write to somebody's server that looks like it worked.
        if (PackPlacementLayout.DirectoryFor(
                instanceDirectory,
                worldDirectory,
                PackPlacementLayout.Writes,
                pack.Manifest.Placement) is not { } directory)
        {
            return null;
        }

        var target = Path.Combine(
            directory, AddonLibraryLayout.FolderName(pack.Manifest.Uuid));

        // Milestone 18.5: a link the server made on the way to the pack folder would
        // carry this write out of the server's folder.
        if ((ServerTree.LinkOnPath(instanceDirectory, target) ?? ServerTree.FirstLink(target)) is { } link)
        {
            throw new IOException(ServerTree.Refusal(link));
        }

        await CopyTreeAsync(pack.Directory, target, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Copied add-on {Uuid} {Version} into {Home} as {Placement}.",
            pack.Manifest.Uuid,
            pack.Version,
            PackPlacementLayout.DescribeHome(PackPlacementLayout.Writes),
            PackPlacementLayout.Describe(pack.Manifest.Placement));

        return target;
    }

    private async Task<ImportedPack> ImportOneAsync(
        ZipArchive zip,
        IReadOnlyDictionary<string, string> destinations,
        DiscoveredPack pack,
        string? archiveName,
        CancellationToken cancellationToken)
    {
        // What this pack came out of, which is what every arm below falls back
        // on. Worked out before the manifest is opened, because the arms that
        // cannot read one have nothing else at all to say.
        var source = AddonLibraryLayout.SourceName(pack.Root, archiveName);

        var manifestSource = destinations
            .FirstOrDefault(d => d.Value == pack.ManifestPath).Key;

        var manifestEntry = manifestSource is null ? null : zip.GetEntry(manifestSource);

        if (manifestEntry is null)
        {
            return Rejected(source, "Its manifest could not be read out of the archive.");
        }

        var json = await ReadTextAsync(manifestEntry, cancellationToken).ConfigureAwait(false);

        var read = PackManifestReader.Read(json);
        if (read.Manifest is not { } manifest)
        {
            return Rejected(source, read.Reason ?? "Its manifest could not be read.");
        }

        // Resolved ONCE, here, so every arm below says the same thing. A
        // header name is very often a localisation key rather than a name, and
        // reporting one back reads as the upload having mangled the pack.
        //
        // The fallback is the archive rather than the shelf's uuid: the arm
        // immediately below refuses a pack precisely because its uuid is not
        // usable, so falling back to it would name nothing.
        var name = PackDisplayName.For(
            manifest.Name,
            await ReadLanguageAsync(zip, destinations, pack, cancellationToken)
                .ConfigureAwait(false),
            source);

        if (!AddonLibraryLayout.IsUsableIdentity(manifest.Uuid))
        {
            // The uuid names the folder this would be written into, so an
            // unusable one is refused rather than repaired — see the layout's
            // own reasoning for why sanitising is worse than refusing.
            return Rejected(
                name,
                "Its uuid is not a GUID, so this cannot tell it apart from another pack.");
        }

        if (manifest.Version.Version is not { } version)
        {
            return new ImportedPack(
                manifest.Uuid, name, null, manifest.Placement, PackImportOutcome.Rejected,
                manifest.Version.State == PackVersionState.Absent
                    ? "Its manifest carries no version, so there is nothing to keep it under."
                    : $"Its version could not be read: '{manifest.Version.Raw}'.");
        }

        var target = Path.Combine(
            Directory,
            AddonLibraryLayout.FolderName(manifest.Uuid),
            AddonLibraryLayout.VersionFolderName(version));

        if (System.IO.Directory.Exists(target))
        {
            return new ImportedPack(
                manifest.Uuid, name, version, manifest.Placement,
                PackImportOutcome.AlreadyPresent, null);
        }

        // Assembled under .incoming and moved, so a failure part-way leaves the
        // shelf exactly as it was rather than a partial pack that lists as real.
        var staging = Path.Combine(Directory, IncomingFolder, Guid.NewGuid().ToString("N"));

        try
        {
            foreach (var (entryName, path) in destinations)
            {
                if (!AddonLibraryLayout.Contains(pack.Root, path)
                    || zip.GetEntry(entryName) is not { } entry)
                {
                    continue;
                }

                var relative = AddonLibraryLayout.RelativeToPack(pack.Root, path);
                var destination = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));

                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                await using var reading = entry.Open();
                await using var file = File.Create(destination);
                await reading.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            System.IO.Directory.Move(staging, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(staging);
            return Rejected(name, ex.Message);
        }

        logger.LogInformation(
            "Added add-on {Name} {Version} to the library.", name, version);

        return new ImportedPack(
            manifest.Uuid, name, version, manifest.Placement,
            PackImportOutcome.Imported, null);
    }

    /// <summary>A pack's language map, read out of the archive it arrived in.</summary>
    /// <remarks>
    /// <strong>Null for absent and for unreadable alike, as on disk.</strong>
    /// The map decides what a pack is called; a pack whose captions cannot be
    /// read still has a name, so refusing the import over them would be the
    /// louder failure by a long way.
    /// </remarks>
    private static async Task<IReadOnlyDictionary<string, string>?> ReadLanguageAsync(
        ZipArchive zip,
        IReadOnlyDictionary<string, string> destinations,
        DiscoveredPack pack,
        CancellationToken cancellationToken)
    {
        // Matched through the plan's approved destinations, like every other
        // read here, so an entry is found by where it may go rather than by
        // what it calls itself.
        var source = destinations.FirstOrDefault(d =>
            AddonLibraryLayout.Contains(pack.Root, d.Value)
            && string.Equals(
                AddonLibraryLayout.RelativeToPack(pack.Root, d.Value),
                AddonLibraryLayout.LanguageFilePath,
                StringComparison.OrdinalIgnoreCase)).Key;

        if (source is null || zip.GetEntry(source) is not { } entry)
        {
            return null;
        }

        try
        {
            return PackManifestReader.ReadLanguage(
                await ReadTextAsync(entry, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>One of a pack's text files out of an archive, read as <see cref="PackText"/> reads pack text.</summary>
    private static async Task<string> ReadTextAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();

        await using (var stream = entry.Open())
        {
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        return PackText.Decode(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    private static ImportedPack Rejected(string name, string reason) =>
        new(null, name, null, PackPlacement.Unknown, PackImportOutcome.Rejected, reason);

    private PackManifest? ReadManifest(string packDirectory)
    {
        var file = Path.Combine(packDirectory, AddonLibraryLayout.ManifestFileName);

        try
        {
            if (!File.Exists(file)
                || PackManifestReader.Read(PackFolder.ReadText(file)).Manifest is not { } manifest)
            {
                return null;
            }

            var language = PackFolder.ReadLanguage(packDirectory);

            // The shelf's fallback is the UUID rather than the folder name: a
            // library pack sits at <uuid>/<version>, so its folder is named for
            // the version and says nothing about which pack it is.
            return manifest.WithDisplayText(language) with
            {
                Name = PackDisplayName.For(manifest.Name, language, manifest.Uuid),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read an add-on manifest under {Directory}.", packDirectory);
            return null;
        }
    }

    private static async Task CopyTreeAsync(
        string source, string target, CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(target);

        foreach (var file in System.IO.Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);

            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            await using var from = File.OpenRead(file);
            await using var to = File.Create(destination);
            await from.CopyToAsync(to, cancellationToken).ConfigureAwait(false);
        }
    }

    private void TryDelete(string directory)
    {
        try
        {
            if (System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not clear add-on staging at {Directory}.", directory);
        }
    }
}
