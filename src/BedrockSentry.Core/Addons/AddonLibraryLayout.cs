namespace BedrockSentry.Core.Addons;

/// <summary>Why an archive's contents cannot be taken into the library.</summary>
public enum PackDiscoveryVerdict
{
    /// <summary>At least one pack was found and each is usable.</summary>
    Accept,

    /// <summary>Nothing in it carries a <c>manifest.json</c>.</summary>
    NoManifest,

    /// <summary>
    /// One pack's folder contains another's. Which files belong to which is then
    /// a guess, and guessing wrong copies half a pack into a server.
    /// </summary>
    NestedPacks,
}

/// <summary>One pack found inside an archive.</summary>
/// <param name="Root">
/// The folder holding its <c>manifest.json</c>, relative to the archive, or the
/// empty string when the manifest is at the archive's top level.
/// </param>
/// <param name="ManifestPath">The manifest's own path within the archive.</param>
public readonly record struct DiscoveredPack(string Root, string ManifestPath);

/// <summary>What an archive turned out to hold.</summary>
public readonly record struct PackDiscovery(
    PackDiscoveryVerdict Verdict, IReadOnlyList<DiscoveredPack> Packs, string? Reason);

/// <summary>
/// Where a pack lives in the library, and what an archive is allowed to become.
/// </summary>
/// <remarks>
/// <para>Pure and separate from the filesystem, because both rules here are
/// guards on <strong>untrusted strings that become paths</strong> and the
/// hostile cases should be unit tests rather than archives somebody has to
/// build.</para>
///
/// <para><strong>The uuid is the sharp one.</strong> It is the library's folder
/// name and it comes out of a <c>manifest.json</c> the wrapper did not write, so
/// a pack claiming <c>"uuid": "../../../config"</c> would place its files
/// wherever it liked — a path escape through the manifest rather than through
/// the archive, which <see cref="PackArchiveRules"/> cannot see because it only
/// inspects entry names.</para>
/// </remarks>
public static class AddonLibraryLayout
{
    /// <summary>The manifest file every pack folder carries.</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>
    /// A pack's own language file, relative to the pack folder, separated by
    /// <c>/</c> as an archive spells it.
    /// </summary>
    /// <remarks>
    /// Here rather than beside either reader because both of them need it: a
    /// pack on disk and a pack still inside an uploaded archive carry the same
    /// file in the same place, and two spellings of that is how the shelf and
    /// the import come to disagree about what one pack is called.
    /// </remarks>
    public const string LanguageFilePath = "texts/en_US.lang";

    /// <summary>
    /// What to call the thing a pack came out of, for a message about a pack
    /// that has no usable name of its own.
    /// </summary>
    /// <remarks>
    /// <para>The pack's folder inside the archive when it has one — an
    /// <c>.mcaddon</c> holds two packs and naming the archive would not say
    /// which — and the uploaded file's own name when it does not, which is the
    /// ordinary <c>.mcpack</c>.</para>
    ///
    /// <para><strong>Never the uuid, which is the shelf's fallback.</strong> The
    /// import refuses a pack precisely because its uuid is unusable, so the one
    /// string a rejection cannot fall back on is the one the shelf uses.</para>
    /// </remarks>
    public static string SourceName(string root, string? archiveName)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (root.Length > 0)
        {
            return root;
        }

        // Last resort, and it still says something: an empty name renders as an
        // empty space where the pack's name goes, which reads as the page being
        // broken rather than as the pack being unnameable.
        return archiveName is { Length: > 0 } ? archiveName : "the archive";
    }

    /// <summary>
    /// Is this uuid usable as a folder name, and as an identity?
    /// </summary>
    /// <remarks>
    /// <para>A Bedrock pack's uuid is a GUID, so this requires one: 32 hex
    /// digits in the five dashed groups, and nothing else. That refuses every
    /// traversal, every separator, every reserved device name and every casing
    /// trap in one rule, rather than enumerating them.</para>
    ///
    /// <para><strong>It is a real refusal and not only a sanitiser</strong>, and
    /// that is deliberate. Sanitising would map two different uuids onto one
    /// folder, so two packs could overwrite each other; and a pack whose uuid is
    /// not a GUID is one the game resolves by a rule nothing here has measured.
    /// Over-gating costs an import that says plainly why; under-gating costs a
    /// write outside the library.</para>
    /// </remarks>
    public static bool IsUsableIdentity(string? uuid) =>
        uuid is not null && Guid.TryParseExact(uuid, "D", out _);

    /// <summary>The library folder for a pack, lower-cased so two casings are one pack.</summary>
    public static string FolderName(string uuid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uuid);

        if (!IsUsableIdentity(uuid))
        {
            throw new ArgumentException(
                "A pack uuid that is not a GUID never names a folder.", nameof(uuid));
        }

        return uuid.ToLowerInvariant();
    }

    /// <summary>The folder a particular version sits in, under its uuid's.</summary>
    /// <remarks>
    /// Composed from the version's own components, which are digits and dots by
    /// construction — so unlike the uuid this needs no guard, and saying so here
    /// is cheaper than a reader wondering whether one was forgotten.
    /// </remarks>
    public static string VersionFolderName(PackVersion version) => version.ToString();

    /// <summary>
    /// Find every pack inside an archive, from the paths a plan accepted.
    /// </summary>
    /// <remarks>
    /// An <c>.mcaddon</c> usually holds two — a behaviour pack and a resource
    /// pack — each in its own folder with its own manifest, so this returns a
    /// list rather than one pack. Taking only the first would import half an
    /// add-on and leave the other half to fail quietly at enable time, which is
    /// <c>D80</c>'s first silent failure arriving at the door.
    /// </remarks>
    public static PackDiscovery FindPacks(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var packs = paths
            .Where(p => p.EndsWith('/' + ManifestFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p, ManifestFileName, StringComparison.OrdinalIgnoreCase))
            .Select(p => new DiscoveredPack(RootOf(p), p))
            .OrderBy(p => p.Root, StringComparer.Ordinal)
            .ToList();

        if (packs.Count == 0)
        {
            return new PackDiscovery(
                PackDiscoveryVerdict.NoManifest, [],
                $"Nothing in this archive carries a {ManifestFileName}, so it holds no add-on.");
        }

        // Sorted by root, so a pack nested inside another is always immediately
        // after the one containing it — which makes this one pass rather than a
        // comparison of every pair.
        for (var i = 1; i < packs.Count; i++)
        {
            if (IsInside(packs[i].Root, packs[i - 1].Root))
            {
                return new PackDiscovery(
                    PackDiscoveryVerdict.NestedPacks, [],
                    $"'{packs[i].ManifestPath}' sits inside another pack in the same archive, "
                    + "so which files belong to which cannot be told apart.");
            }
        }

        return new PackDiscovery(PackDiscoveryVerdict.Accept, packs, null);
    }

    /// <summary>Is this file one of the given pack's, rather than a sibling's?</summary>
    /// <remarks>
    /// The empty root means the archive IS the pack, so everything belongs to it.
    /// Matched on a trailing separator so <c>pack2/x</c> is not read as part of
    /// <c>pack</c> — the prefix trap that would copy one pack's files into
    /// another's folder.
    /// </remarks>
    public static bool Contains(string root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);

        return root.Length == 0
            || path.StartsWith(root + '/', StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The path of a pack's file relative to the pack, not the archive.</summary>
    public static string RelativeToPack(string root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);

        return root.Length == 0 ? path : path[(root.Length + 1)..];
    }

    private static string RootOf(string manifestPath)
    {
        var slash = manifestPath.LastIndexOf('/');
        return slash < 0 ? string.Empty : manifestPath[..slash];
    }

    private static bool IsInside(string candidate, string outer) =>
        outer.Length == 0
            ? candidate.Length > 0
            : candidate.StartsWith(outer + '/', StringComparison.OrdinalIgnoreCase);
}
