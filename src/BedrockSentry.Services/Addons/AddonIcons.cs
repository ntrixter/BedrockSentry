using System.Collections.Concurrent;

namespace BedrockSentry.Services.Addons;

/// <summary>
/// Which icon file the Add-ons tab found for each add-on on each instance, so a
/// request for one can be answered without a path in it.
/// </summary>
/// <remarks>
/// <para><strong>A request names an instance and a uuid, never a file.</strong>
/// The only paths this hands out are ones <see cref="AddonsListing.Build"/> found
/// in a pack folder or the library while reading an instance, so a crafted request
/// can reach nothing else on the disk.</para>
///
/// <para><strong>Served rather than inlined.</strong> A pack's icon is often
/// 256 pixels square and tens of kilobytes; written into the page as data it
/// would travel in every render the circuit sends. As a URL the browser fetches it
/// once and caches it, and the version stamp in the URL changes when the file
/// does.</para>
///
/// <para><strong>The library page has its own route</strong>, keyed by uuid alone,
/// because the shelf belongs to no instance; it serves the newest version's icon, which
/// is the version the page names first.</para>
/// </remarks>
public sealed class AddonIcons
{
    /// <summary>The route the Host serves icons on.</summary>
    public const string Route = "/addon-icon/{instanceId}/{uuid}";

    /// <summary>The route the Host serves the library page's icons on.</summary>
    public const string LibraryRoute = "/library-icon/{uuid}";

    private readonly ConcurrentDictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _library = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Remember a listing's icons, and return each row's icon URL by uuid.</summary>
    /// <param name="stamp">When a file last changed. A seam, so the rules are tested without a disk.</param>
    public IReadOnlyDictionary<string, string> Remember(
        string instanceId, AddonsListing listing, Func<string, DateTime>? stamp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(listing);

        stamp ??= File.GetLastWriteTimeUtc;

        var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in listing.Rows)
        {
            var key = Key(instanceId, row.Uuid);

            if (row.IconFile is not { } file)
            {
                _files.TryRemove(key, out _);
                continue;
            }

            _files[key] = file;
            urls[row.Uuid] = $"addon-icon/{Uri.EscapeDataString(instanceId)}/{Uri.EscapeDataString(row.Uuid)}"
                + $"?v={stamp(file).Ticks}";
        }

        return urls;
    }

    /// <summary>The icon file remembered for this add-on on this instance, or null.</summary>
    public string? Find(string instanceId, string uuid) =>
        _files.GetValueOrDefault(Key(instanceId, uuid));

    private static string Key(string instanceId, string uuid) =>
        $"{instanceId}\n{uuid.ToLowerInvariant()}";

    /// <summary>Remember the shelf's icons, and return each pack's icon URL by uuid.</summary>
    /// <param name="packs">What the library page lists.</param>
    /// <param name="exists">Whether a file exists. A seam, so the rules are tested without a disk.</param>
    /// <param name="stamp">When a file last changed. A seam, as above.</param>
    /// <remarks>A pack no longer listed, or whose newest version has no icon, is forgotten.</remarks>
    public IReadOnlyDictionary<string, string> RememberLibrary(
        IReadOnlyList<LibraryPack> packs, Func<string, bool>? exists = null, Func<string, DateTime>? stamp = null)
    {
        ArgumentNullException.ThrowIfNull(packs);

        exists ??= File.Exists;
        stamp ??= File.GetLastWriteTimeUtc;

        var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pack in packs)
        {
            var file = Path.Combine(pack.Newest.Directory, AddonsListing.IconFileName);

            if (!exists(file))
            {
                continue;
            }

            _library[pack.Uuid] = file;
            urls[pack.Uuid] = $"library-icon/{Uri.EscapeDataString(pack.Uuid)}?v={stamp(file).Ticks}";
        }

        foreach (var uuid in _library.Keys.Where(k => !urls.ContainsKey(k)))
        {
            _library.TryRemove(uuid, out _);
        }

        return urls;
    }

    /// <summary>The icon file remembered for this pack on the shelf, or null.</summary>
    public string? FindInLibrary(string uuid) => _library.GetValueOrDefault(uuid);
}
