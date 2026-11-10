using BedrockSentry.Core.Broker;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Instances;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace BedrockSentry.Services.Broker;

/// <summary>
/// <see cref="IBrokerInstances"/> read from the configuration the main service writes,
/// resolved by the same rules <c>InstanceRegistry</c> uses.
/// </summary>
/// <remarks>
/// Read fresh on every call: the broker answers for the configuration as it is now,
/// and an instance created a moment ago is the one a grant is usually for. The file's
/// watcher reloads it some time after a write, which is after the grant the write was
/// for (<c>T382</c> clause 4 judged a moved server's old folder), so each call reloads
/// the configuration itself first.
/// </remarks>
public sealed class ConfiguredBrokerInstances : IBrokerInstances
{
    private readonly IOptionsMonitor<InstanceOptions> _instances;
    private readonly IOptionsMonitor<StorageOptions> _storage;
    private readonly ISentryPaths _paths;
    private readonly IConfigurationRoot? _configuration;
    private readonly object _reload = new();

    /// <param name="configuration">
    /// Reloaded before each answer; the monitors follow it as soon as it reloads. Null
    /// leaves them to the file's watcher.
    /// </param>
    public ConfiguredBrokerInstances(
        IOptionsMonitor<InstanceOptions> instances, IOptionsMonitor<StorageOptions> storage, ISentryPaths paths,
        IConfiguration? configuration = null)
    {
        _instances = instances;
        _storage = storage;
        _paths = paths;
        _configuration = configuration as IConfigurationRoot;
    }

    private InstanceOptions Instances
    {
        get
        {
            Reload();
            return _instances.CurrentValue;
        }
    }

    private void Reload()
    {
        if (_configuration is null)
        {
            return;
        }

        lock (_reload)
        {
            _configuration.Reload();
        }
    }

    private string Root
    {
        get
        {
            var root = _instances.CurrentValue.Root;
            return string.IsNullOrWhiteSpace(root) ? _paths.Instances : Path.GetFullPath(root);
        }
    }

    public BrokerInstance? Find(string instanceId) =>
        Instances.Servers.TryGetValue(instanceId, out var definition)
            ? Describe(instanceId, definition)
            : null;

    public IReadOnlyList<BrokerInstance> All() =>
        [.. Instances.Servers.Select(pair => Describe(pair.Key, pair.Value))];

    public string StorageRoot(BrokerGrantTarget target)
    {
        Reload();
        var options = _storage.CurrentValue;

        return target switch
        {
            BrokerGrantTarget.BackupsRoot => string.IsNullOrWhiteSpace(options.BackupsRoot)
                ? Path.Combine(_paths.DataRoot, "backups")
                : options.BackupsRoot,
            BrokerGrantTarget.ServerPackagesRoot => string.IsNullOrWhiteSpace(options.ServerPackagesRoot)
                ? Path.Combine(_paths.DataRoot, "server-packages")
                : options.ServerPackagesRoot,
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Not a storage root."),
        };
    }

    private BrokerInstance Describe(string id, InstanceDefinition definition) =>
        new(id, definition.Name, InstanceLayout.ResolveWorkingDirectory(Root, id, definition), definition.Executable);
}
