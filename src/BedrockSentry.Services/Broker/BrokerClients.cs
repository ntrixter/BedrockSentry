using System.Text.Json;
using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Broker;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Updates;
using BedrockSentry.Services.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Broker;

// The main service's side of each seam when the broker does the work (milestone
// 18.5). Each change goes through the channel; a read stays in this process when
// it needs no privilege, and goes through the channel when it does.

/// <summary><see cref="IFirewallService"/> through the broker, the read included.</summary>
/// <remarks>
/// The read too, because <c>Get-NetFirewallRule</c> answers a service's virtual account
/// with access denied (<c>T394</c>).
/// </remarks>
public sealed class BrokerFirewallService(IBrokerChannel broker) : IFirewallService
{
    private readonly SharedRead<BrokerOperation, BrokerResponse> _reads = new();

    public async Task<FirewallRules> ReadAsync(CancellationToken cancellationToken = default)
    {
        var response = await _reads.ReadAsync(
            BrokerOperation.ReadFirewallRules,
            () => broker.SendAsync(new BrokerRequest { Operation = BrokerOperation.ReadFirewallRules }),
            cancellationToken);

        return response.Succeeded
            ? new FirewallRules(true, response.Json ?? string.Empty, string.Empty)
            : new FirewallRules(false, string.Empty, response.Message);
    }

    public async Task<SystemCheckFix> AllowAsync(
        string instanceId,
        string instanceName,
        PortSpan ports,
        PortProtocol protocol,
        CancellationToken cancellationToken = default) =>
        Fix(await ChangeAsync(
            new BrokerRequest
            {
                Operation = BrokerOperation.AllowServerPorts,
                InstanceId = instanceId,
                FirstPort = ports.First,
                LastPort = ports.Last,
                Protocol = protocol,
            },
            cancellationToken));

    public async Task<SystemCheckFix> RemoveServerRulesAsync(
        string instanceId,
        string instanceName,
        CancellationToken cancellationToken = default) =>
        Fix(await ChangeAsync(
            new BrokerRequest { Operation = BrokerOperation.RemoveServerRules, InstanceId = instanceId },
            cancellationToken));

    public async Task<SystemCheckFix> RenameServerRulesAsync(
        string instanceId,
        string instanceName,
        CancellationToken cancellationToken = default) =>
        Fix(await ChangeAsync(
            new BrokerRequest { Operation = BrokerOperation.RenameServerRules, InstanceId = instanceId },
            cancellationToken));

    public async Task<SystemCheckFix> UpdateServerRulesAsync(
        string instanceId,
        string instanceName,
        IReadOnlyList<ReachableSockets> sockets,
        CancellationToken cancellationToken = default) =>
        Fix(await ChangeAsync(
            new BrokerRequest { Operation = BrokerOperation.UpdateServerRules, InstanceId = instanceId },
            cancellationToken));

    internal static SystemCheckFix Fix(BrokerResponse response) => new(response.Succeeded, response.Message);

    private async Task<BrokerResponse> ChangeAsync(BrokerRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await broker.SendAsync(request, cancellationToken);
        }
        finally
        {
            _reads.Changed();
        }
    }
}

/// <summary><see cref="IDefenderService"/> through the broker, the read included.</summary>
/// <remarks>
/// The read too, because Defender shows its exclusion list to administrators only;
/// any other account is told so in a sentence that would read as an empty list.
/// </remarks>
public sealed class BrokerDefenderService(IBrokerChannel broker) : IDefenderService
{
    private readonly SharedRead<BrokerOperation, BrokerResponse> _reads = new();

    public async Task<DefenderExclusions> ReadAsync(CancellationToken cancellationToken = default)
    {
        var response = await _reads.ReadAsync(
            BrokerOperation.ReadDefenderExclusions,
            () => broker.SendAsync(new BrokerRequest { Operation = BrokerOperation.ReadDefenderExclusions }),
            cancellationToken);

        return response.Succeeded
            ? new DefenderExclusions(true, response.Json ?? string.Empty, string.Empty)
            : new DefenderExclusions(false, string.Empty, response.Message);
    }

    public async Task<SystemCheckFix> ExcludeAsync(
        string instanceId, string directory, CancellationToken cancellationToken = default)
    {
        try
        {
            return BrokerFirewallService.Fix(await broker.SendAsync(
                new BrokerRequest { Operation = BrokerOperation.ExcludeServerFolder, InstanceId = instanceId },
                cancellationToken));
        }
        finally
        {
            _reads.Changed();
        }
    }
}

/// <summary><see cref="ILoopbackExemptionService"/> through the broker, the read included.</summary>
/// <remarks>
/// The read too, because <c>Get-AppxPackage -AllUsers</c> needs an administrator, and the
/// Minecraft a player uses is installed for that player, not for the service (<c>T395</c>).
/// </remarks>
public sealed class BrokerLoopbackExemptionService(IBrokerChannel broker) : ILoopbackExemptionService
{
    private readonly SharedRead<BrokerOperation, BrokerResponse> _reads = new();

    public async Task<LoopbackExemptions> ReadAsync(CancellationToken cancellationToken = default)
    {
        var response = await _reads.ReadAsync(
            BrokerOperation.ReadLoopbackExemptions,
            () => broker.SendAsync(new BrokerRequest { Operation = BrokerOperation.ReadLoopbackExemptions }),
            cancellationToken);

        return response.Succeeded
            ? new LoopbackExemptions(true, response.Json ?? string.Empty, string.Empty)
            : new LoopbackExemptions(false, string.Empty, response.Message);
    }

    public async Task<SystemCheckFix> ExemptAsync(string family, CancellationToken cancellationToken = default)
    {
        try
        {
            return BrokerFirewallService.Fix(await broker.SendAsync(
                new BrokerRequest { Operation = BrokerOperation.ExemptLoopback, PackageFamily = family },
                cancellationToken));
        }
        finally
        {
            _reads.Changed();
        }
    }
}

/// <summary><see cref="IVssServiceControl"/> with the start type set by the broker.</summary>
public sealed class BrokerVssServiceControl(IBrokerChannel broker, ReadinessInspector local) : IVssServiceControl
{
    public Task<VssState> ReadVssAsync(CancellationToken cancellationToken = default) =>
        local.ReadVssAsync(cancellationToken);

    public async Task<SystemCheckFix> EnableVssAsync(CancellationToken cancellationToken = default) =>
        BrokerFirewallService.Fix(await broker.SendAsync(
            new BrokerRequest { Operation = BrokerOperation.EnableVss }, cancellationToken));
}

/// <summary><see cref="IShadowCopyService"/> with the snapshot taken and released by the broker.</summary>
/// <remarks>The copy is read in this process, through the device path the broker answers with.</remarks>
public sealed class BrokerShadowCopyService(IBrokerChannel broker, ILogger<BrokerShadowCopyService> log)
    : IShadowCopyService
{
    public async Task<ShadowCopy> CreateAsync(
        string instanceId, string volumeRoot, CancellationToken cancellationToken = default)
    {
        var response = await broker.SendAsync(
            new BrokerRequest { Operation = BrokerOperation.CreateShadowCopy, InstanceId = instanceId },
            cancellationToken);

        if (!response.Succeeded
            || string.IsNullOrEmpty(response.ShadowId)
            || string.IsNullOrEmpty(response.DeviceObject))
        {
            throw new InvalidOperationException(response.Succeeded
                ? "The broker took a shadow copy but did not say where it is."
                : response.Message);
        }

        return new ShadowCopy(response.ShadowId, response.DeviceObject);
    }

    public async Task ReleaseAsync(ShadowCopy shadow, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await broker.SendAsync(
                new BrokerRequest { Operation = BrokerOperation.ReleaseShadowCopy, ShadowId = shadow.ShadowId },
                cancellationToken);

            if (!response.Succeeded)
            {
                log.LogWarning("Shadow copy {ShadowId} was not released: {Why}", shadow.ShadowId, response.Message);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The interface's promise: a release runs in a finally and never throws.
            log.LogWarning(ex, "Shadow copy {ShadowId} was not released", shadow.ShadowId);
        }
    }
}

/// <summary><see cref="IOrphanInspector"/> with the process table read and the kill done by the broker.</summary>
/// <remarks>
/// The find too: a process another account started does not show this one its
/// executable path, so a server left by a wrapper that ran as LocalSystem would be
/// invisible from here.
/// </remarks>
public sealed class BrokerOrphanInspector(IBrokerChannel broker, ILogger<BrokerOrphanInspector> log) : IOrphanInspector
{
    private readonly SharedRead<string, BrokerResponse> _finds = new();

    public async Task<IReadOnlyList<OrphanedServer>> FindAsync(
        string instanceId,
        string workingDirectory,
        IReadOnlyCollection<int> ours,
        CancellationToken cancellationToken = default)
    {
        // Shared per server: the broker's answer leaves out nothing this caller started,
        // so callers with different `ours` can wait on one find.
        var response = await _finds.ReadAsync(
            instanceId,
            () => broker.SendAsync(new BrokerRequest { Operation = BrokerOperation.FindOrphans, InstanceId = instanceId }),
            cancellationToken);

        if (!response.Succeeded || response.Json is null)
        {
            // The interface's promise: empty rather than throwing when the question
            // cannot be asked. The start's port check still stands in front.
            log.LogWarning("Could not look for orphaned servers of {InstanceId}: {Why}", instanceId, response.Message);
            return [];
        }

        List<OrphanedServer> found;

        try
        {
            found = JsonSerializer.Deserialize<List<OrphanedServer>>(response.Json) ?? [];
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "The broker's list of orphaned servers for {InstanceId} could not be read", instanceId);
            return [];
        }

        // The broker cannot know which process this service started; that list is here.
        return [.. found.Where(orphan => !ours.Contains(orphan.ProcessId))];
    }

    public async Task<OrphanTermination> TerminateAsync(
        string instanceId, int processId, string workingDirectory, CancellationToken cancellationToken = default)
    {
        BrokerResponse response;

        try
        {
            response = await broker.SendAsync(
                new BrokerRequest { Operation = BrokerOperation.TerminateOrphan, InstanceId = instanceId, ProcessId = processId },
                cancellationToken);
        }
        finally
        {
            _finds.Changed();
        }

        return new OrphanTermination(response.Succeeded, response.Message);
    }
}

/// <summary>
/// <see cref="IWrapperUpdateInstaller"/> through the broker: the main service names
/// the build and the broker does the rest (milestone 18.5, `D46`).
/// </summary>
/// <remarks>
/// Nothing is staged in this process and no helper is copied here: the broker copies
/// the running build's updater into the install tree when it applies, so LocalSystem
/// never runs a file this account could have changed.
/// </remarks>
public sealed class BrokerWrapperUpdateInstaller(IBrokerChannel broker, IInstallLayout layout, IInstallPolicy? policy = null)
    : IWrapperUpdateInstaller
{
    /// <inheritdoc/>
    /// <remarks>The broker's: the install policy's feed, or the built-in one, never this account's configuration.</remarks>
    public string? FeedUrlTemplate => BrokerUpdates.FeedFor(policy);

    /// <inheritdoc/>
    /// <remarks>Never: an unreachable broker is a failed staging with its reason, not a missing ability.</remarks>
    public string? Unavailable => null;

    /// <inheritdoc/>
    public string ResultPath => BrokerUpdates.ResultPathIn(layout.InstallRoot);

    public void PrepareHelper()
    {
    }

    public async Task<UpdateStaging> StageAsync(UpdateManifest manifest, UpdateOptions options, CancellationToken ct = default)
    {
        var response = await broker.SendAsync(
            new BrokerRequest { Operation = BrokerOperation.StageUpdate, BuildIdentity = manifest.Identity }, ct);

        return response switch
        {
            { Succeeded: true } => new UpdateStaging(
                UpdateStagingState.Staged, string.Empty, BrokerUpdates.HealthTimeoutSeconds(options)),
            { Refused: true } => new UpdateStaging(UpdateStagingState.NotReady, response.Message),
            _ => new UpdateStaging(UpdateStagingState.NotDownloaded, response.Message),
        };
    }

    public async Task<int> LaunchAsync(string identity, CancellationToken ct = default)
    {
        var response = await broker.SendAsync(
            new BrokerRequest { Operation = BrokerOperation.ApplyUpdate, BuildIdentity = identity }, ct);

        return response is { Succeeded: true, ProcessId: { } pid }
            ? pid
            : throw new InvalidOperationException(response.Message);
    }
}
