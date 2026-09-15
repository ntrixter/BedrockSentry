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

    public HostInspector(
        PublicAddressService publicAddress,
        IOptionsMonitor<HostDetailsOptions> options,
        ISentryPaths paths,
        TimeProvider time,
        IVolumeDeduplicationInspector deduplication)
    {
        _publicAddress = publicAddress;
        _options = options;
        _paths = paths;
        _time = time;
        _deduplication = deduplication;
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
        var reasons = RebootReasons();

        return new HostSystem(
            Environment.OSVersion.VersionString,
            Environment.ProcessorCount,
            memory.TotalAvailableMemoryBytes,
            Math.Max(0, memory.TotalAvailableMemoryBytes - memory.MemoryLoadBytes),
            TimeSpan.FromMilliseconds(Environment.TickCount64),
            _time.GetLocalNow(),
            TimeZoneInfo.Local.DisplayName,
            reasons);
    }

    /// <summary>
    /// Space on each volume the wrapper keeps something on, one entry per volume
    /// even when several of its folders share one.
    /// </summary>
    public IReadOnlyList<VolumeUsage> Volumes(IEnumerable<string> instanceDirectories)
    {
        var interesting = new List<(string Name, string Path)>
        {
            ("BedrockSentry data", _paths.DataRoot),
            ("Server packages", _paths.ServerPackages),
            ("Instances", _paths.Instances),
        };

        interesting.AddRange(instanceDirectories.Select(d => ("Instance", d)));

        var byRoot = new Dictionary<string, (string Name, DriveInfo Drive)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, path) in interesting)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
                if (!drive.IsReady || byRoot.ContainsKey(drive.Name))
                {
                    continue;
                }

                byRoot[drive.Name] = (name, drive);
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

        return byRoot
            .Select(entry => new VolumeUsage(
                entry.Value.Name,
                entry.Key,
                entry.Value.Drive.TotalSize,
                entry.Value.Drive.AvailableFreeSpace,
                deduplication.TryGetValue(entry.Key, out var state)
                    ? state
                    : VolumeDeduplication.Unknown("The deduplication service did not answer for this volume.")))
            .ToList();
    }

    /// <summary>
    /// What Windows has pending, and which of it can restart the machine.
    /// </summary>
    /// <remarks>
    /// <para>Worth surfacing because the first two signals mean Windows will
    /// eventually take every server down without asking, and the administrator would
    /// rather choose the moment.</para>
    ///
    /// <para><strong>That justification does not cover the third, and this method
    /// used to be named and written as though it did</strong> (<c>T188</c>).
    /// <c>PendingFileRenameOperations</c> is a passive queue Windows drains if and
    /// when a restart happens — it cannot cause one. It was called "why Windows
    /// thinks it needs restarting", which is not what a pending file rename is: it
    /// is Windows noting work to do <em>if</em> restarted. Each signal now carries
    /// whether it can restart the machine, and the headline follows the strongest
    /// one rather than giving all three the most severe.</para>
    /// </remarks>
    private static IReadOnlyList<RebootSignal> RebootReasons()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var reasons = new List<RebootSignal>();

        try
        {
            if (KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
            {
                reasons.Add(new RebootSignal(
                    "a component install is waiting on a restart", CanCauseARestart: true));
            }

            if (KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
            {
                reasons.Add(new RebootSignal(
                    "Windows Update is waiting on a restart", CanCauseARestart: true));
            }

            using var sessionManager = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager");

            if (sessionManager?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 })
            {
                // "queued to be replaced" overstated it: on a healthy Windows install
                // every observed entry had an empty destination, which is
                // delete-on-boot. The distinction
                // matters — a queued replacement suggests a half-finished install, a
                // queued deletion suggests something that finished and is tidying up.
                reasons.Add(new RebootSignal(
                    "files are queued to be removed or replaced on the next restart",
                    CanCauseARestart: false));
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            // Reported as "not pending" rather than as an error: this is a hint,
            // not something to break a page over.
        }

        return reasons;
    }

    private static bool KeyExists(string path)
    {
        using var key = Registry.LocalMachine.OpenSubKey(path);
        return key is not null;
    }
}
