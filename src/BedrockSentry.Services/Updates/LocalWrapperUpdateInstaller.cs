using System.Text.Json;
using BedrockSentry.Core;
using BedrockSentry.Core.Broker;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Updates;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Updates;

/// <summary>
/// <see cref="IWrapperUpdateInstaller"/> run in this process, against the install
/// tree <see cref="InstallLayout"/> describes.
/// </summary>
public sealed class LocalWrapperUpdateInstaller : IWrapperUpdateInstaller
{
    private readonly UpdateClient _client;
    private readonly InstallLayout _layout;
    private readonly ISentryPaths _paths;
    private readonly ILogger _log;

    private readonly Func<string?> _unavailable;
    private readonly InstalledService _service;

    private string? _stagedIdentity;

    public LocalWrapperUpdateInstaller(
        UpdateClient client, InstallLayout layout, ISentryPaths paths, ILogger<LocalWrapperUpdateInstaller> log,
        InstalledService? service = null)
        : this(client, layout, paths, (ILogger)log, service: service)
    {
    }

    /// <param name="unavailable">
    /// Why this process may not change the install tree, or null when it may; null
    /// asks Windows (<see cref="AccountCannotWriteInstall"/>).
    /// </param>
    internal LocalWrapperUpdateInstaller(
        UpdateClient client, InstallLayout layout, ISentryPaths paths, ILogger log, Func<string?>? unavailable = null,
        InstalledService? service = null)
    {
        _service = service ?? InstalledService.Default;
        _client = client;
        _layout = layout;
        _paths = paths;
        _log = log;
        _unavailable = unavailable ?? AccountCannotWriteInstall;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// In this process the update writes the install tree and stops and starts the
    /// service, which needs LocalSystem or an administrator. Under its own service
    /// account (milestone 18.5) only the privilege broker can do either.
    /// </remarks>
    public string? Unavailable => _unavailable();

    /// <inheritdoc/>
    public string ResultPath => Path.Combine(_paths.Update, "result.json");

    /// <summary>Why the account this runs as cannot apply an update itself, or null when it can.</summary>
    public static string? AccountCannotWriteInstall()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();

        if (identity.IsSystem
            || new System.Security.Principal.WindowsPrincipal(identity).IsInRole(
                System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            return null;
        }

        return $"Self-update is unavailable: this service runs as {identity.Name}, which cannot change "
            + "the install folder or restart the service, and this install leaves out the privilege broker. "
            + "Run install.ps1 again without -NoBroker to turn self-update on.";
    }

    private string PlanPath => Path.Combine(_paths.Update, "plan.json");

    /// <inheritdoc/>
    public async Task<UpdateStaging> StageAsync(
        UpdateManifest manifest, UpdateOptions options, CancellationToken ct = default)
    {
        _stagedIdentity = null;

        var target = await _client.StageAsync(manifest, ct);

        if (target is null)
        {
            return new UpdateStaging(UpdateStagingState.NotDownloaded, "The build could not be downloaded or verified.");
        }

        var previous = _layout.ResolveCurrentVersionDirectory();

        if (previous is null)
        {
            _log.LogError("Could not resolve which version directory 'current' points at; not updating.");
            return new UpdateStaging(UpdateStagingState.NotReady, "'current' does not resolve to a version directory.");
        }

        if (FindStagedHelper() is null)
        {
            _log.LogError("The updater helper is not staged; not updating.");
            return new UpdateStaging(UpdateStagingState.NotReady, "The updater helper is not staged.");
        }

        var plan = new UpdatePlan
        {
            ServiceName = _service.Name,
            InstallRoot = _layout.InstallRoot,
            PreviousVersionDirectory = previous,
            TargetVersionDirectory = target,
            TargetIdentity = manifest.Identity,
            PreviousIdentity = BuildInfo.Identity,
            HealthUrl = options.BuildHealthUrl(),
            HealthTimeoutSeconds = (int)options.HealthTimeout.TotalSeconds,
            StopTimeoutSeconds = (int)options.StopTimeout.TotalSeconds,
            LogPath = Path.Combine(_paths.Logs, "update.log"),
            ResultPath = ResultPath,
        };

        Directory.CreateDirectory(_paths.Update);
        await File.WriteAllTextAsync(
            PlanPath,
            JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }),
            ct);

        _stagedIdentity = manifest.Identity;

        return new UpdateStaging(UpdateStagingState.Staged, string.Empty, plan.HealthTimeoutSeconds);
    }

    /// <inheritdoc/>
    public Task<int> LaunchAsync(string identity, CancellationToken ct = default)
    {
        if (!string.Equals(identity, _stagedIdentity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{identity} is not the build staged last.");
        }

        var helper = FindStagedHelper()
            ?? throw new InvalidOperationException("The updater helper is not staged.");

        return Task.FromResult(DetachedProcess.Start(helper, $"\"{PlanPath}\"", Path.GetDirectoryName(helper)!));
    }

    /// <summary>
    /// Copies the helper out of the install tree so it can replace that tree.
    /// </summary>
    /// <remarks>
    /// A running executable holds a lock on its own image, so a helper running from
    /// inside <c>current\</c> would block the rename it exists to perform. It refuses
    /// to run from there at all; this is what makes sure a usable copy exists
    /// elsewhere before it is needed.
    /// </remarks>
    public void PrepareHelper()
    {
        try
        {
            var source = Path.Combine(_layout.CurrentLink, "updater");

            if (!Directory.Exists(source))
            {
                _log.LogWarning("No updater helper found at {Source}; self-update will not run.", source);
                return;
            }

            var destination = Path.Combine(_paths.Update, "helper");

            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }

            Directory.CreateDirectory(destination);

            foreach (var file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
            }

            _log.LogInformation("Updater helper staged at {Destination}.", destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "Could not stage the updater helper.");
        }
    }

    private string? FindStagedHelper()
    {
        var path = Path.Combine(_paths.Update, "helper", "BedrockSentry.Updater.exe");
        return File.Exists(path) ? path : null;
    }
}
