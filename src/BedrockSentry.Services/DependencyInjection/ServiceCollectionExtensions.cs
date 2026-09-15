using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Players;
using BedrockSentry.Core.Security;
using BedrockSentry.Core.Updates;
using BedrockSentry.Services.Backups;
using BedrockSentry.Services.Configuration;
using BedrockSentry.Services.Diagnostics;
using BedrockSentry.Services.Instances;
using BedrockSentry.Services.Packages;
using BedrockSentry.Services.Players;
using BedrockSentry.Services.Security;
using BedrockSentry.Services.Updates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.DependencyInjection;

/// <summary>Registers the BedrockSentry service layer.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds path resolution, credential storage, first-run state, and TLS certificate
    /// provisioning.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="paths">
    /// The already-constructed paths instance. The host builds this before the container
    /// exists, because configuration files have to be located before they can be loaded.
    /// </param>
    public static IServiceCollection AddBedrockSentryServices(
        this IServiceCollection services,
        ISentryPaths paths)
    {
        services.AddSingleton(paths);

        // The concrete type as well: StorageLocations applies folder overrides
        // live, which is a mutation the interface deliberately does not carry —
        // every test double would otherwise have to pretend to support it.
        if (paths is SentryPaths concrete)
        {
            services.AddSingleton(concrete);
            services.AddSingleton<StorageLocations>();
        }

        // Singleton so the start time is captured once, at first resolution, rather
        // than being re-read per request.
        services.AddSingleton<ServiceUptime>();

        services.AddSingleton<SetupState>();
        services.AddSingleton<IAdminCredentialService, AdminCredentialService>();
        services.AddSingleton<ServerCertificateProvider>();

        var layout = new InstallLayout();
        services.AddSingleton(layout);
        services.AddSingleton<IInstallLayout>(layout);

        services.AddSingleton<UpdateLedgerStore>();
        services.AddHttpClient<UpdateClient>();

        // Registered before UpdateService, which takes it: a wrapper update
        // warns and drains every server before the helper is launched (§ 12),
        // rather than leaving them to the service stop's short countdown.
        services.AddSingleton<IManagedServerShutdown, ManagedServerShutdown>();

        services.AddSingleton<UpdateSettingsStore>();
        services.AddSingleton<UpdateService>();
        services.AddHostedService(sp => sp.GetRequiredService<UpdateService>());

        // TryAdd so a test or a future caller can substitute a clock without
        // having to reach past this method.
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IBedrockProcessFactory, BedrockProcessFactory>();
        services.AddSingleton<ServerPackageStore>();

        // Its own named HttpClient, like the public-address lookup: this talks
        // to Mojang on a schedule and downloads large files, and keeping that
        // traffic separable from the wrapper's own update checker matters the
        // moment somebody is diagnosing which one is misbehaving.
        //
        // Named and singleton rather than AddHttpClient<BdsUpdateService>(),
        // which registers the typed client TRANSIENT. The service caches what
        // Mojang is offering in its own fields, so a transient one meant the
        // background checker refreshed a copy no page could see and every page
        // load began from "nothing has been seen yet" — reported as an update
        // panel that reset itself whenever you left it.
        services.AddHttpClient(BdsUpdateService.HttpClientName);
        services.AddSingleton<BdsUpdateService>();
        services.AddHostedService<BdsUpdateChecker>();
        services.AddSingleton<ServerVersionLog>();
        services.AddSingleton<BdsUpdateInstaller>();
        services.AddSingleton<BdsUpdateSettingsStore>();

        // Singleton and hosted service both: the pages read the pending update
        // and cancel it, and the same object has to be the one counting down.
        services.AddSingleton<BdsAutoUpdater>();
        services.AddHostedService(sp => sp.GetRequiredService<BdsAutoUpdater>());
        // docs/design.md § 7.2. One store for the whole install, satisfying both the
        // player API and the narrow seam instance removal has been calling since
        // milestone 4 — the same rows answer both, so registering two objects
        // would mean two connections to one file for no gain.
        services.AddSingleton<PlayerDatabase>();
        services.AddSingleton<SqlitePlayerStore>();
        services.AddSingleton<PlayerArchive>();
        services.AddSingleton<ConfigArchive>();

        // docs/design.md § 8. Strategies register against the interface, so milestone 8
        // and 9 each add one line here and touch nothing else.
        // Above the strategy that needs it, so the dependency travels with it.
        services.AddSingleton<CustomCommandPolicy>();
        services.AddSingleton<IBackupStrategy, CustomCommandStrategy>();
        services.AddSingleton<IBackupStrategy, ZipBackupStrategy>();
        services.TryAddSingleton<IShadowCopyService, ShadowCopyService>();
        services.AddSingleton<IBackupStrategy, VssBackupStrategy>();
        services.AddSingleton<BackupHistory>();
        services.AddSingleton<WorldRestorer>();
        services.AddSingleton<ShutdownBackups>();
        services.AddHostedService<BackupScheduler>();
        services.AddSingleton(sp => new BackupOrchestrator(
            sp.GetServices<IBackupStrategy>(),
            sp.GetRequiredService<BackupHistory>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<BackupOrchestrator>>(),
            sp.GetRequiredService<IPlayerStore>()));
        services.TryAddSingleton<IPlayerStore>(sp => sp.GetRequiredService<SqlitePlayerStore>());
        services.TryAddSingleton<IInstancePlayerData>(sp => sp.GetRequiredService<SqlitePlayerStore>());

        // Ahead of the registry, which takes it: one guard for the whole install,
        // since the ports it arbitrates are the machine's, not an instance's.
        services.AddSingleton<LanDiscoveryPortGuard>();
        services.AddSingleton<InstanceRegistry>();
        services.AddSingleton<PortInspector>();

        // The start path's pre-flight (T149) is the inspector seen through an
        // interface, so InstanceHostedService does not name the concrete type and
        // a test can push a fake through the same wiring. Same instance, not a
        // second one.
        services.AddSingleton<IStartPreflight>(sp => sp.GetRequiredService<PortInspector>());

        // D56. The only component that ends a process the wrapper did not start,
        // behind an interface for the reason IShadowCopyService is: so a build
        // machine can fake it, and so 18.5's broker has one thing to re-implement.
        services.TryAddSingleton<IOrphanInspector, ProcessOrphanInspector>();
        services.AddSingleton<OrphanCleanup>();
        services.AddSingleton<ServerAccessControl>();

        // D20. Its own HttpClient: this talks to a third party on a long cache,
        // and giving it a named client keeps that traffic separable from the
        // update checker's in any diagnosis.
        services.AddHttpClient<PublicAddressService>();

        // D55. TryAdd, like IShadowCopyService: it is the one Windows call in the
        // storage section, and a test host replaces it with a fake.
        services.TryAddSingleton<IVolumeDeduplicationInspector, WmiVolumeDeduplicationInspector>();
        services.AddSingleton<HostInspector>();

        services.TryAddSingleton<IPowerShellRunner, PowerShellRunner>();
        services.AddSingleton<FirewallInspector>();
        services.AddSingleton<DefenderInspector>();
        services.AddSingleton<ReadinessInspector>();

        // Singleton because CPU is a rate: it keeps the previous processor time
        // per process, and a transient one would have no previous to compare to.
        services.AddSingleton<ProcessSampler>();
        services.AddSingleton<AppIsolationInspector>();

        // Registered after UpdateService so instances start after the update
        // check and, more importantly, stop *before* it on shutdown — hosted
        // services stop in reverse order, and a self-update that begins while
        // servers are still draining is the race this ordering avoids.
        services.AddHostedService<InstanceHostedService>();
        services.AddHostedService<RestartScheduler>();

        return services;
    }
}
