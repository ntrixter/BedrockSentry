using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Diagnostics;
using Microsoft.Extensions.Options;
using System.Security;
using Microsoft.Win32;

namespace BedrockSentry.Services.Diagnostics;

/// <summary>
/// What the status page can find out about the machine.
/// </summary>
/// <remarks>
/// Most questions an administrator arrives with are about the host rather than
/// the wrapper: what address do I give people, is there room for another backup,
/// why did every server restart last night.
/// </remarks>
public sealed class HostInspector
{
    private readonly PublicAddressService _publicAddress;
    private readonly IOptionsMonitor<HostDetailsOptions> _options;
    private readonly ISentryPaths _paths;
    private readonly TimeProvider _time;
    private readonly IVolumeDeduplicationInspector _deduplication;
    private readonly IVolumeMediaInspector _media;

    public HostInspector(
        PublicAddressService publicAddress,
        IOptionsMonitor<HostDetailsOptions> options,
        ISentryPaths paths,
        TimeProvider time,
        IVolumeDeduplicationInspector deduplication,
        IVolumeMediaInspector media)
    {
        _publicAddress = publicAddress;
        _options = options;
        _paths = paths;
        _time = time;
        _deduplication = deduplication;
        _media = media;
    }

    /// <summary>
    /// The addresses that can be answered without leaving the machine.
    /// </summary>
    /// <remarks>
    /// Split from <see cref="AddressesAsync"/> so the status page has something
    /// true to show on its first paint. The hostname and the adapter list are
    /// local reads that cost nothing; the public address is a request to a
    /// third party, and making the whole card wait on it meant an operator saw
    /// nothing at all until it came back.
    /// </remarks>
    public static HostAddresses Local() => new(Dns.GetHostName(), LocalAddresses(), null, null);

    public async Task<HostAddresses> AddressesAsync(CancellationToken cancellationToken = default)
    {
        var (address, note) = await _publicAddress.GetAsync(cancellationToken).ConfigureAwait(false);

        return new HostAddresses(Dns.GetHostName(), LocalAddresses(), address, note);
    }

    /// <summary>
    /// The addresses a player on the same network would actually use.
    /// </summary>
    /// <remarks>
    /// Filtered rather than listed wholesale. A Windows box with Hyper-V, WSL or
    /// a VPN client reports a dozen addresses, most of which are useless to a
    /// player, and a status page that prints all of them answers "what do I give
    /// my friends" with a puzzle.
    /// </remarks>
    public static IReadOnlyList<string> LocalAddresses()
    {
        var addresses = new List<string>();

        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up
                    || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback
                        or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;

                    if (address.AddressFamily != AddressFamily.InterNetwork
                        || IPAddress.IsLoopback(address))
                    {
                        continue;
                    }

                    // 169.254.x.x means DHCP never answered. Showing it as "your
                    // address" would send somebody chasing a firewall problem
                    // when the real answer is that the adapter has no network.
                    if (address.GetAddressBytes() is [169, 254, ..])
                    {
                        continue;
                    }

                    addresses.Add(address.ToString());
                }
            }
        }
        catch (NetworkInformationException)
        {
            // A status page renders with what it has.
        }

        return addresses;
    }

    /// <summary>
    /// Resolves the configured domain and compares it against the public address.
    /// </summary>
    public async Task<DomainCheck?> CheckDomainAsync(CancellationToken cancellationToken = default)
    {
        var domain = _options.CurrentValue.Domain?.Trim() ?? string.Empty;
        if (domain.Length == 0)
        {
            return null;
        }

        var (publicAddress, _) = await _publicAddress.GetAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<string> resolved;
        try
        {
            resolved =
            [
                .. (await Dns.GetHostAddressesAsync(domain, cancellationToken).ConfigureAwait(false))
                    .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    .Select(a => a.ToString()),
            ];
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return new DomainCheck(
                domain, [], publicAddress, DomainCheckResult.NotFound,
                $"{domain} does not resolve.");
        }

        if (resolved.Count == 0)
        {
            return new DomainCheck(
                domain, resolved, publicAddress, DomainCheckResult.NotFound,
                $"{domain} resolved to no addresses.");
        }

        if (publicAddress is null)
        {
            return new DomainCheck(
                domain, resolved, null, DomainCheckResult.Unknown,
                "There is no public address to compare it against.");
        }

        return resolved.Contains(publicAddress, StringComparer.OrdinalIgnoreCase)
            ? new DomainCheck(domain, resolved, publicAddress, DomainCheckResult.Matches, null)
            : new DomainCheck(
                domain, resolved, publicAddress, DomainCheckResult.Mismatch,
                $"{domain} points at {string.Join(", ", resolved)}, but this machine's public "
                + $"address is {publicAddress}. Players following that name are reaching "
                + "somewhere else — usually a DNS record left behind by a changed address.");
    }

    public HostSystem System()
    {
        var memory = GC.GetGCMemoryInfo();
        var (reasons, unreadable) = RebootReasons();

        return new HostSystem(
            Environment.OSVersion.VersionString,
            Environment.ProcessorCount,
            memory.TotalAvailableMemoryBytes,
            Math.Max(0, memory.TotalAvailableMemoryBytes - memory.MemoryLoadBytes),
            TimeSpan.FromMilliseconds(Environment.TickCount64),
            _time.GetLocalNow(),
            TimeZoneInfo.Local.DisplayName,
            reasons,
            unreadable);
    }

    /// <summary>
    /// The folders whose volumes the Storage table lists, in the order it lists them.
    /// </summary>
    /// <remarks>
    /// The system volume first, because Windows, its updates and its page file live
    /// there and fill it whatever the wrapper does. Then every folder the wrapper
    /// writes to: the data root, the backups folder, the server packages folder,
    /// the new servers folder and each server's own folder. The backups folder is
    /// the one most often moved to a drive of its own (T430 clause 4).
    /// </remarks>
    public static IReadOnlyList<(string Name, string Path)> VolumeFolders(
        ISentryPaths paths,
        string? newServersRoot,
        IEnumerable<string> instanceDirectories,
        string? windowsDirectory)
    {
        var folders = new List<(string Name, string Path)>
        {
            ("Windows", windowsDirectory ?? ""),
            ("BedrockSentry data", paths.DataRoot),
            ("Backups", paths.Backups),
            ("Server packages", paths.ServerPackages),
            ("New servers", newServersRoot ?? ""),
            ("Instances", paths.Instances),
        };

        folders.AddRange(instanceDirectories.Select(d => ("Instance", d)));

        return folders;
    }

    /// <summary>
    /// The folders grouped by the volume each is on, first name per volume, in the
    /// order given. A volume mounted in a folder is its own volume, not the drive
    /// letter the folder sits under (T430 clause 5).
    /// </summary>
    /// <param name="rootOf">The volume root of a path, or null when it cannot be read.</param>
    public static IReadOnlyList<(string Root, string Name)> ByVolume(
        IEnumerable<(string Name, string Path)> folders, Func<string, string?> rootOf)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(string Root, string Name)>();

        foreach (var (name, path) in folders)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string? root;

            try
            {
                root = rootOf(path);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(root) && seen.Add(root))
            {
                result.Add((root, name));
            }
        }

        return result;
    }

    /// <summary>
    /// Space on the system volume and on each volume the wrapper keeps something
    /// on, one entry per volume even when several of its folders share one.
    /// </summary>
    /// <param name="instanceDirectories">Each registered server's folder.</param>
    /// <param name="newServersRoot">Where new servers are created (Settings → Storage).</param>
    public IReadOnlyList<VolumeUsage> Volumes(IEnumerable<string> instanceDirectories, string? newServersRoot = null)
    {
        var interesting = VolumeFolders(
            _paths, newServersRoot, instanceDirectories, Environment.GetFolderPath(Environment.SpecialFolder.Windows));

        var byRoot = new Dictionary<string, (string Name, long Total, long Free)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (root, name) in ByVolume(interesting, VolumeSpace.RootOf))
        {
            try
            {
                if (VolumeSpace.Read(root) is { } space)
                {
                    byRoot[root] = (name, space.Total, space.Free);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // A volume that cannot be read is not worth failing the page for.
            }
        }

        // One question to the deduplication service for every volume at once,
        // rather than one per row: it is a CIM query, and the answer for a
        // machine without the role is the same for all of them (`D55`).
        var deduplication = _deduplication.Inspect([.. byRoot.Keys]);
        var media = _media.Inspect([.. byRoot.Keys]);

        return byRoot
            .Select(entry => new VolumeUsage(
                entry.Value.Name,
                entry.Key,
                entry.Value.Total,
                entry.Value.Free,
                deduplication.TryGetValue(entry.Key, out var state)
                    ? state
                    : VolumeDeduplication.Unknown("The deduplication service did not answer for this volume."),
                media.TryGetValue(entry.Key, out var kind) ? kind : DriveMedia.Unknown))
            .ToList();
    }

    /// <summary>
    /// What Windows has pending that can restart the machine by itself
    /// (<see cref="RebootNotice.Signals"/>).
    /// </summary>
    /// <remarks>
    /// Worth surfacing because Windows will take every server down without asking, and
    /// the administrator would rather choose the moment. The file-rename queue is not
    /// read: it cannot cause a restart (<c>T188</c>, <c>D64</c>).
    /// </remarks>
    private static (IReadOnlyList<string> Reasons, string? Unreadable) RebootReasons() =>
        OperatingSystem.IsWindows() ? RebootNotice.Read(KeyExists) : ([], null);

    private static bool KeyExists(string path)
    {
        using var key = Registry.LocalMachine.OpenSubKey(path);
        return key is not null;
    }
}
