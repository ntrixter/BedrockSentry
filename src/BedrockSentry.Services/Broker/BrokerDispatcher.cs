using System.Text.Json;
using BedrockSentry.Core;
using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Broker;
using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Services.Backups;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Broker;

/// <summary>A configured server, as the broker resolves it from an instance id.</summary>
/// <param name="Executable">A bare file name, never a path; <see cref="BrokerDispatcher"/> refuses anything else.</param>
public sealed record BrokerInstance(string Id, string Name, string WorkingDirectory, string Executable);

/// <summary>Resolves an instance id from the configuration, as the broker reads it.</summary>
public interface IBrokerInstances
{
    /// <summary>The instance, or null when no server has that id.</summary>
    BrokerInstance? Find(string instanceId);

    /// <summary>Every configured server, to tell whether a port belongs to another.</summary>
    IReadOnlyList<BrokerInstance> All();

    /// <summary>The backups root, or the server-packages root, as Settings set it.</summary>
    string StorageRoot(BrokerGrantTarget target);
}

/// <summary>
/// The broker's side of every operation: refuse what the rules refuse, resolve the
/// rest from the configuration, and only then act (milestone 18.5, <c>D46</c>).
/// </summary>
/// <remarks>
/// <para>Platform-free on purpose. The pipe, the caller check and the Windows calls sit
/// on either side of it, so every rule here runs in the Linux suite against fakes.</para>
/// <para>The order is the same for every operation: <see cref="BrokerArguments"/> on the
/// request's shape, then the instance from the configuration, then the rules on what
/// that resolves to, then the work. Nothing the caller sent is used except to choose
/// among things the broker resolved itself.</para>
/// </remarks>
public sealed class BrokerDispatcher
{
    private readonly IBrokerInstances _instances;
    private readonly Func<string, IReadOnlyList<ReachableSockets>> _reachable;
    private readonly BrokerPathContext _paths;
    private readonly IReparseProbe _probe;
    private readonly BrokerRecord _record;
    private readonly IFirewallService _firewall;
    private readonly IDefenderService _defender;
    private readonly ILoopbackExemptionService _loopback;
    private readonly IVssServiceControl _vss;
    private readonly IShadowCopyService _shadows;
    private readonly IOrphanInspector _orphans;
    private readonly IFolderPin _pin;
    private readonly IFolderGrant _grant;
    private readonly IBrokerUpdates? _updates;
    private readonly IInstallPolicy? _policy;
    private readonly IDeduplicationReader? _deduplication;
    private readonly ILogger _log;

    public BrokerDispatcher(
        IBrokerInstances instances,
        Func<string, IReadOnlyList<ReachableSockets>> reachable,
        BrokerPathContext paths,
        IReparseProbe probe,
        BrokerRecord record,
        IFirewallService firewall,
        IDefenderService defender,
        ILoopbackExemptionService loopback,
        IVssServiceControl vss,
        IShadowCopyService shadows,
        IOrphanInspector orphans,
        IFolderPin pin,
        IFolderGrant grant,
        ILogger<BrokerDispatcher> log,
        IBrokerUpdates? updates = null,
        IInstallPolicy? policy = null,
        IDeduplicationReader? deduplication = null)
    {
        _deduplication = deduplication;
        _instances = instances;
        _reachable = reachable;
        _paths = paths;
        _probe = probe;
        _record = record;
        _firewall = firewall;
        _defender = defender;
        _loopback = loopback;
        _vss = vss;
        _shadows = shadows;
        _orphans = orphans;
        _pin = pin;
        _grant = grant;
        _updates = updates;
        _policy = policy;
        _log = log;
    }

    /// <summary>Answers one request. Never throws: a failure is an answer too.</summary>
    public async Task<BrokerResponse> DispatchAsync(BrokerRequest request, CancellationToken ct = default)
    {
        if (BrokerArguments.Refuse(request) is { } shape)
        {
            return Refuse(request, shape);
        }

        try
        {
            return request.Operation switch
            {
                BrokerOperation.AllowServerPorts => await AllowServerPortsAsync(request, ct),
                BrokerOperation.ExcludeServerFolder => await ExcludeServerFolderAsync(request, ct),
                BrokerOperation.RemoveServerRules => await RemoveServerRulesAsync(request, ct),
                BrokerOperation.RenameServerRules => await RenameServerRulesAsync(request, ct),
                BrokerOperation.UpdateServerRules => await UpdateServerRulesAsync(request, ct),
                BrokerOperation.ReadDefenderExclusions => Read(await _defender.ReadAsync(ct)),
                BrokerOperation.ReadFirewallRules => Read(await _firewall.ReadAsync(ct)),
                BrokerOperation.ReadLoopbackExemptions => Read(await _loopback.ReadAsync(ct)),
                BrokerOperation.ReadDeduplication when _deduplication is not null => Read(_deduplication.Read()),
                BrokerOperation.ReadDeduplication => Refuse(request, "This broker has no deduplication reader."),
                BrokerOperation.ExemptLoopback => Fix(await _loopback.ExemptAsync(request.PackageFamily!, ct)),
                BrokerOperation.EnableVss => Fix(await _vss.EnableVssAsync(ct)),
                BrokerOperation.CreateShadowCopy => await CreateShadowCopyAsync(request, ct),
                BrokerOperation.ReleaseShadowCopy => await ReleaseShadowCopyAsync(request, ct),
                BrokerOperation.FindOrphans => await FindOrphansAsync(request, ct),
                BrokerOperation.TerminateOrphan => await TerminateOrphanAsync(request, ct),
                BrokerOperation.GrantFolder => GrantFolder(request),
                BrokerOperation.Status => new BrokerResponse(true, BuildInfo.Identity),

                // Only the identity crosses; BrokerUpdates finds everything else itself.
                BrokerOperation.StageUpdate when _updates is not null => await _updates.StageAsync(request.BuildIdentity!, ct),
                BrokerOperation.ApplyUpdate when _updates is not null => _updates.Apply(request.BuildIdentity!),
                BrokerOperation.StageUpdate or BrokerOperation.ApplyUpdate =>
                    Refuse(request, "This broker has no install tree to update."),
                _ => Refuse(request, $"{request.Operation} is not a broker operation."),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Broker operation {Operation} failed", request.Operation);
            return new BrokerResponse(false, $"{request.Operation} failed: {ex.Message}");
        }
    }

    private BrokerResponse Refuse(BrokerRequest request, string why)
    {
        _log.LogWarning("Refused {Operation}: {Why}", request.Operation, why);
        return BrokerResponse.Refusal(why);
    }

    private static BrokerResponse Fix(SystemCheckFix fix) => new(fix.Succeeded, fix.Message);

    private static BrokerResponse Read(DefenderExclusions read) => Read(read.Read, read.Json, read.Error);

    private static BrokerResponse Read(FirewallRules read) => Read(read.Read, read.Json, read.Error);

    private static BrokerResponse Read(LoopbackExemptions read) => Read(read.Read, read.Json, read.Error);

    private static BrokerResponse Read(bool read, string json, string error) => new(read, error, Json: json);

    /// <summary>Succeeded whatever the reading says: not installed and unreadable are answers too, and cross as such.</summary>
    private static BrokerResponse Read(DeduplicationReading read) =>
        new(true, read.Error ?? string.Empty, Json: System.Text.Json.JsonSerializer.Serialize(read));

    /// <summary>The instance, or the refusal that names why there is none to act on.</summary>
    private (BrokerInstance? Instance, string? Refusal) Resolve(string instanceId)
    {
        if (_instances.Find(instanceId) is not { } instance)
        {
            return (null, $"No server has the id '{instanceId}'.");
        }

        if (instance.Executable?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) != true
            || instance.Executable.IndexOfAny(['\\', '/', ':']) >= 0)
        {
            return (null, $"'{instance.Name}' names an executable that is not a plain .exe file name.");
        }

        return (instance, null);
    }

    /// <summary>
    /// The rules named for the instance the configuration knows by this id; the name
    /// is the broker's own reading, so a request cannot reach another server's rules.
    /// </summary>
    private async Task<BrokerResponse> RemoveServerRulesAsync(BrokerRequest request, CancellationToken ct)
    {
        if (_instances.Find(request.InstanceId!) is not { } instance)
        {
            return Refuse(request, $"No server has the id '{request.InstanceId}'.");
        }

        return Fix(await _firewall.RemoveServerRulesAsync(instance.Id, instance.Name, ct));
    }

    /// <summary>
    /// The instance's rules relabelled with the name the configuration gives it now
    /// (<c>T411</c>); as with removal, the name is the broker's own reading.
    /// </summary>
    private async Task<BrokerResponse> RenameServerRulesAsync(BrokerRequest request, CancellationToken ct)
    {
        if (_instances.Find(request.InstanceId!) is not { } instance)
        {
            return Refuse(request, $"No server has the id '{request.InstanceId}'.");
        }

        return Fix(await _firewall.RenameServerRulesAsync(instance.Id, instance.Name, ct));
    }

    /// <summary>
    /// The instance's rules moved to the sockets its configuration names (<c>T411</c>),
    /// under the rules <see cref="AllowServerPortsAsync"/> applies to one socket: none
    /// another server is configured for, none <see cref="BrokerPortRules"/> refuses.
    /// </summary>
    private async Task<BrokerResponse> UpdateServerRulesAsync(BrokerRequest request, CancellationToken ct)
    {
        var (instance, refusal) = Resolve(request.InstanceId!);

        if (instance is null)
        {
            return Refuse(request, refusal!);
        }

        var sockets = _reachable(instance.WorkingDirectory);

        foreach (var socket in sockets)
        {
            if (BrokerPortRules.RefuseSpan(socket.Ports.First, socket.Ports.Last) is { } port)
            {
                return Refuse(request, port);
            }
        }

        foreach (var other in _instances.All())
        {
            if (string.Equals(other.Id, instance.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var theirs = _reachable(other.WorkingDirectory);

            var shared = sockets.Where(s => theirs.Any(t => t.Protocol == s.Protocol && Overlap(t.Ports, s.Ports))).ToList();

            if (shared.Count > 0)
            {
                return Refuse(request, $"{shared[0].Protocol} {shared[0].Ports} is also configured for '{other.Name}'.");
            }
        }

        return Fix(await _firewall.UpdateServerRulesAsync(instance.Id, instance.Name, sockets, ct));
    }

    private async Task<BrokerResponse> AllowServerPortsAsync(BrokerRequest request, CancellationToken ct)
    {
        var (instance, refusal) = Resolve(request.InstanceId!);

        if (instance is null)
        {
            return Refuse(request, refusal!);
        }

        var asked = new ReachableSockets(new PortSpan(request.FirstPort!.Value, request.LastPort!.Value), request.Protocol!.Value);

        // The request chooses among the sockets the server's own configuration
        // names; it cannot add one.
        if (!_reachable(instance.WorkingDirectory).Contains(asked))
        {
            return Refuse(request, $"{asked.Protocol} {asked.Ports} is not a port '{instance.Name}' is configured to use.");
        }

        foreach (var other in _instances.All())
        {
            if (string.Equals(other.Id, instance.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_reachable(other.WorkingDirectory).Any(s => s.Protocol == asked.Protocol && Overlap(s.Ports, asked.Ports)))
            {
                return Refuse(request, $"{asked.Protocol} {asked.Ports} is also configured for '{other.Name}'.");
            }
        }

        return Fix(await _firewall.AllowAsync(instance.Id, instance.Name, asked.Ports, asked.Protocol, ct));
    }

    private static bool Overlap(PortSpan a, PortSpan b) => a.First <= b.Last && b.First <= a.Last;

    private async Task<BrokerResponse> ExcludeServerFolderAsync(BrokerRequest request, CancellationToken ct)
    {
        var (instance, refusal) = Resolve(request.InstanceId!);

        if (instance is null)
        {
            return Refuse(request, refusal!);
        }

        if (BrokerPathRules.Refuse(instance.WorkingDirectory, BrokerPathUse.Exclude, _paths, _probe) is { } path)
        {
            return Refuse(request, path);
        }

        using var folder = _pin.Open(Path.GetFullPath(instance.WorkingDirectory), changeAccess: false, out var unopened);

        if (folder is null)
        {
            return Refuse(request, unopened!);
        }

        // D115 (b): a folder the installer's grant covers, or one the broker granted
        // itself. Any other folder is somebody else's, and excluding it from scanning
        // is not the main service's to ask.
        if (!ReservedSystemDirectories.IsAtOrUnder(folder.Path, _paths.InstancesRoot) && !_record.Granted(folder.Path))
        {
            return Refuse(request,
                $"'{folder.Path}' is not a folder the broker granted. An administrator grants it with install.ps1 -GrantFolder \"{folder.Path}\".");
        }

        return Fix(await _defender.ExcludeAsync(instance.Id, folder.Path, ct));
    }

    private async Task<BrokerResponse> CreateShadowCopyAsync(BrokerRequest request, CancellationToken ct)
    {
        var (instance, refusal) = Resolve(request.InstanceId!);

        if (instance is null)
        {
            return Refuse(request, refusal!);
        }

        if (BrokerPathRules.Refuse(instance.WorkingDirectory, BrokerPathUse.Snapshot, _paths, _probe) is { } path)
        {
            return Refuse(request, path);
        }

        using var folder = _pin.Open(Path.GetFullPath(instance.WorkingDirectory), changeAccess: false, out var unopened);

        if (folder is null)
        {
            return Refuse(request, unopened!);
        }

        var shadow = await _shadows.CreateAsync(instance.Id, ShadowCopyPaths.VolumeRootOf(folder.Path), ct);

        // The id Windows answered with, never one from a request: this is the only
        // place the record learns a copy is the broker's.
        _record.AddShadowCopy(shadow.ShadowId);

        return new BrokerResponse(true, "Shadow copy taken.", ShadowId: shadow.ShadowId, DeviceObject: shadow.DeviceObject);
    }

    private async Task<BrokerResponse> ReleaseShadowCopyAsync(BrokerRequest request, CancellationToken ct)
    {
        var id = request.ShadowId!;

        if (!_record.OwnsShadowCopy(id))
        {
            return Refuse(request, "That shadow copy was not taken by the broker, so it is not the broker's to release.");
        }

        await _shadows.ReleaseAsync(new ShadowCopy(id, string.Empty), ct);
        _record.RemoveShadowCopy(id);

        return new BrokerResponse(true, "Shadow copy released.");
    }

    private async Task<BrokerResponse> FindOrphansAsync(BrokerRequest request, CancellationToken ct)
    {
        var (instance, refusal) = Resolve(request.InstanceId!);

        if (instance is null)
        {
            return Refuse(request, refusal!);
        }

        // Every Bedrock server running from the folder; the caller leaves out the
        // ones it started, which only it knows.
        var found = await _orphans.FindAsync(instance.Id, instance.WorkingDirectory, [], ct);

        return new BrokerResponse(true, $"{found.Count} found.", Json: JsonSerializer.Serialize(found));
    }

    private async Task<BrokerResponse> TerminateOrphanAsync(BrokerRequest request, CancellationToken ct)
    {
        var (instance, refusal) = Resolve(request.InstanceId!);

        if (instance is null)
        {
            return Refuse(request, refusal!);
        }

        // IOrphanInspector.Terminate proves the process is a Bedrock server running
        // from this folder, against the live process, before it ends it.
        var ended = await _orphans.TerminateAsync(instance.Id, request.ProcessId!.Value, instance.WorkingDirectory, ct);

        return new BrokerResponse(ended.Succeeded, ended.Detail);
    }

    private BrokerResponse GrantFolder(BrokerRequest request)
    {
        // D120: by default only an administrator grants a folder outside the data root,
        // with install.ps1 -GrantFolder; -AllowPanelFolderGrants lets the service ask.
        // A broker given no policy refuses, as a missing file does.
        if (_policy is not { AllowPanelFolderGrants: true })
        {
            return Refuse(request,
                "This install grants folders outside the data root only through install.ps1 -GrantFolder, run as "
                + "administrator; install.ps1 -AllowPanelFolderGrants lets the panel ask instead.");
        }

        string folder;

        if (request.GrantTarget == BrokerGrantTarget.InstanceWorkingDirectory)
        {
            var (instance, refusal) = Resolve(request.InstanceId!);

            if (instance is null)
            {
                return Refuse(request, refusal!);
            }

            folder = instance.WorkingDirectory;
        }
        else
        {
            folder = _instances.StorageRoot(request.GrantTarget!.Value);
        }

        if (BrokerPathRules.Refuse(folder, BrokerPathUse.Grant, _paths, _probe) is { } path)
        {
            return Refuse(request, path);
        }

        var full = Path.GetFullPath(folder);

        // D115 (b): the broker grants a folder it makes, or one with nothing in it.
        // A folder that already holds files belongs to something, and only an
        // administrator may hand it to the service.
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
        {
            return Refuse(request,
                $"'{full}' already holds files. An administrator grants it with install.ps1 -GrantFolder \"{full}\".");
        }

        Directory.CreateDirectory(full);

        // Asked again now the folder exists: a link created in the moment between
        // the check and the creation is still a link.
        if (BrokerPathRules.Refuse(full, BrokerPathUse.Grant, _paths, _probe) is { } after)
        {
            return Refuse(request, after);
        }

        using var pinned = _pin.Open(full, changeAccess: true, out var unopened);

        if (pinned is null)
        {
            return Refuse(request, unopened!);
        }

        // A server's own folder sheds the entry D124 gave its folder identity and takes the low
        // label (T409, D126); the service could not change either on a folder it does not own.
        var granted = _grant.GrantModify(
            pinned,
            request.GrantTarget == BrokerGrantTarget.InstanceWorkingDirectory ? ServerSandbox.SidFor(full) : null);

        if (granted.Succeeded)
        {
            _record.AddGrantedFolder(pinned.Path);
        }

        return Fix(granted);
    }
}
