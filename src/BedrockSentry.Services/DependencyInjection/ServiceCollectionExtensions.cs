using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Players;
using BedrockSentry.Core.Security;
using BedrockSentry.Core.Updates;
using BedrockSentry.Services.Addons;
using BedrockSentry.Services.Backups;
using BedrockSentry.Services.Commands;
using BedrockSentry.Services.Configuration;
using BedrockSentry.Services.Diagnostics;
using BedrockSentry.Services.Instances;
using BedrockSentry.Services.Packages;
using BedrockSentry.Services.Players;
using BedrockSentry.Services.Security;
using BedrockSentry.Services.Updates;
using BedrockSentry.Services.Sync;
using BedrockSentry.Services.Worlds;
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
    /// <summary>
    /// Every port a throwaway run must leave alone: each instance's, whether it runs or
    /// not, the LAN discovery pair, and every UDP socket open now.
    /// </summary>
    private static IReadOnlySet<int> ThrowawayPortsInUse(InstanceRegistry instances)
    {
        var ports = new HashSet<int> { 19132, 19133 };
        foreach (var supervisor in instances.All)
        {
            ports.UnionWith(PortInspector.PortsOf(supervisor.WorkingDirectory).Select(p => p.Port));
        }

        ports.UnionWith(UdpListeners.All().Select(l => l.Port));
        return ports;
    }

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
        // The concrete type as well: the authenticator step writes auth.json through
        // its one writer, which the interface does not carry (D125).
        services.AddSingleton<AdminCredentialService>();
        services.AddSingleton<IAdminCredentialService>(sp => sp.GetRequiredService<AdminCredentialService>());
        services.AddSingleton<IAdminMfaService, AdminMfaService>();
        services.AddSingleton<SignInThrottle>();
        services.AddSingleton<ServerCertificateProvider>();

        var layout = new InstallLayout();
        services.AddSingleton(layout);
        services.AddSingleton<IInstallLayout>(layout);

        // D45's switch lives beside the install, not in the data root's config: the
        // service account can write the config, and only an administrator the install.
        services.AddSingleton<IInstallPolicy, InstallPolicyFile>();

        // D120: what the panel says, or asks the broker, about a folder outside the data root.
        services.AddSingleton<Broker.OutsideFolders>();

        services.AddSingleton<UpdateLedgerStore>();
        services.AddHttpClient<UpdateClient>();

        // Registered before UpdateService, which takes it: a wrapper update
        // warns and drains every server before the helper is launched (§ 12),
        // rather than leaving them to the service stop's short countdown.
        services.AddSingleton(provider => new ServersToStartAgain(
            provider.GetRequiredService<ISentryPaths>(),
            provider.GetRequiredService<ILogger<ServersToStartAgain>>(),
            layout.ResolveCurrentVersionDirectory()));
        services.AddSingleton<IManagedServerShutdown, ManagedServerShutdown>();
        services.AddSingleton<DrainCountdowns>();

        services.AddSingleton<UpdateSettingsStore>();
        // D46: staging and the helper launch, so UpdateService does not know where they run.
        services.TryAddSingleton<IWrapperUpdateInstaller, LocalWrapperUpdateInstaller>();
        services.AddSingleton<UpdateService>();
        services.AddHostedService(sp => sp.GetRequiredService<UpdateService>());

        // TryAdd so a test or a future caller can substitute a clock without
        // having to reach past this method.
        services.TryAddSingleton(TimeProvider.System);
        // Milestone 18.5 phase H2: every server, the throwaway runs included, starts
        // at low integrity on Windows (D126).
        services.TryAddSingleton(_ => ServerStarters.ForThisMachine());
        services.AddSingleton<IBedrockProcessFactory, BedrockProcessFactory>();
        services.AddSingleton<ServerPackageLog>();
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

        // D47. Named and singleton for PublicAddressService's reason below: the
        // in-flight fetches and the back-off after an unreachable source are fields.
        services.AddHttpClient(PlayerFaces.HttpClientName);
        services.AddSingleton<PlayerFaces>();
        services.AddSingleton<PlayerFacesSettingsStore>();
        services.AddSingleton<ConfigArchive>();

        // docs/design.md § 8. Strategies register against the interface, so milestone 8
        // and 9 each add one line here and touch nothing else.
        // Above the strategy that needs it, so the dependency travels with it.
        services.AddSingleton<CustomCommandPolicy>();
        services.AddSingleton<IBackupStrategy, CustomCommandStrategy>();
        services.AddSingleton<IBackupStrategy, ZipBackupStrategy>();
        services.AddSingleton<IBackupStrategy, FolderBackupStrategy>();

        // Not beside a strategy any more (`D66`): the shadow copy is how the world
        // is ACQUIRED, applying to every strategy, so it belongs to the
        // orchestrator. It used to sit above VssBackupStrategy on the reasoning
        // that a dependency should travel with the thing that needs it, which was
        // right about the principle and wrong about which thing that was.
        services.TryAddSingleton<IShadowCopyService, ShadowCopyService>();
        services.AddSingleton<BackupHistory>();
        services.AddSingleton<BackupArtifactStore>();
        services.AddSingleton<WorldRestorer>();

        // D111. A restore of a whole world in the background, with its steps for the strip.
        services.AddSingleton<WorldRestoreRuns>();
        services.AddSingleton<RestoreAsNewServer>();
        services.AddSingleton<WorldFileImport>();
        services.AddSingleton<BackupImporter>();
        services.AddSingleton<BackupExporter>();
        services.AddSingleton<BackupDeleter>();

        // Milestone 15. The library is install-wide and the per-instance reader
        // holds no state of its own, so both are singletons like the stores
        // above rather than scoped to a page.
        services.AddSingleton<AddonLibrary>();
        services.AddSingleton<InstanceAddons>();
        services.AddSingleton<LibraryPackRemover>();
        services.AddSingleton<OrphanedStorage>();

        // D86 and D103: the pack a server's own /structure load reads.
        services.AddSingleton<StructurePackWriter>();

        // A factory, since the timings constructor is the tests'.
        services.AddSingleton(sp => new StructureConsole(
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<StructureConsole>>()));

        // D103. One instance registered twice, like the seeder below: the panel
        // starts a sync and reads its progress, and every launch asks it to take
        // the structures pack off first. A sync runs in the background, so it
        // outlives the page that started it.
        services.AddSingleton<WorldSync>();

        // Every launch's work with the process down, in order: the sync's structures pack
        // comes off, then the add-on changes queued while it ran are written (D85), then the
        // companion moves to the shipped version (D106), then the instance's other add-ons
        // follow the library when it asks for that (D112).
        services.AddSingleton<CompanionPackUpgrade>();
        services.AddSingleton(sp => new AddonAutoUpdater(
            sp.GetRequiredService<InstanceAddons>(),
            sp.GetRequiredService<AddonLibrary>(),
            sp.GetRequiredService<ServerVersionLog>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<AddonAutoUpdater>>()));
        // D85. Changes made on the Add-ons tab while a server has a process wait here, and are
        // written after it stops and again before each launch, ahead of the companion's move
        // and the auto-update, which then read the lists as the administrator left them.
        services.AddSingleton<AddonQueueStore>();
        services.AddSingleton<AddonChanges>();
        services.AddHostedService(sp => sp.GetRequiredService<AddonChanges>());
        services.AddSingleton<IBeforeLaunch>(sp => new BeforeLaunchSteps(
            [sp.GetRequiredService<WorldSync>(), sp.GetRequiredService<AddonChanges>(),
             sp.GetRequiredService<CompanionPackUpgrade>(), sp.GetRequiredService<AddonAutoUpdater>()],
            sp.GetRequiredService<ILogger<BeforeLaunchSteps>>()));

        // D108. One area put back from a backup, read on a throwaway copy of the server's own
        // build while it runs; from the Backups tab or restorearea in game.
        services.AddSingleton<IBackupAreaReader>(sp => new BackupAreaReader(
            sp.GetRequiredService<ThrowawayServer>(),
            server => sp.GetRequiredService<CompletionCoordinator>().BuildOf(server).Package,
            server => BackupAreaReader.PacksOutsideTheWorld(sp.GetRequiredService<InstanceAddons>().Read(server)),
            sp.GetRequiredService<StructureConsole>(),
            sp.GetRequiredService<TimeProvider>()));
        // D111. Each server's build, so an area is not put into a server older than its backup's.
        services.AddSingleton<IInstanceBuilds>(sp =>
            new InstanceBuilds(server => sp.GetRequiredService<BdsUpdateService>().BuildOf(server).Build));
        services.AddSingleton<AreaRestore>();

        // D106 step 4. syncarea typed in game or on the console, answered from here; restorearea too (D108).
        services.AddHostedService<InGameAreaCommands>();

        // D107. The world's ticking areas, read and changed through the console; one instance
        // registered twice, since the Ticking Areas view calls the object the host started.
        services.AddSingleton<TickingAreas>();
        services.AddHostedService(sp => sp.GetRequiredService<TickingAreas>());

        // Install-wide, because the icon route answers from what any circuit's
        // Add-ons tab found (AddonIcons' own remarks say why a request names no path).
        services.AddSingleton<AddonIcons>();

        // D93. One instance registered twice: the host starts it, and the panel
        // reads what this start's seeding did from the same object.
        services.AddSingleton<CompanionPackSeeder>();
        services.AddHostedService(sp => sp.GetRequiredService<CompanionPackSeeder>());

        services.AddSingleton<ShutdownBackups>();
        services.AddSingleton<BackupRuns>();
        services.AddHostedService<BackupScheduler>();
        services.AddSingleton(sp => new BackupOrchestrator(
            sp.GetServices<IBackupStrategy>(),
            sp.GetRequiredService<BackupHistory>(),
            sp.GetRequiredService<BackupArtifactStore>(),
            sp.GetRequiredService<IShadowCopyService>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<BackupOrchestrator>>(),
            sp.GetRequiredService<IPlayerStore>(),
            runs: sp.GetRequiredService<BackupRuns>()));
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
        services.AddSingleton<FirewallNoticeDismissals>();
        services.AddSingleton<FirewallReadings>();
        services.AddSingleton<ServerAccessControl>();

        // D20. Its own HttpClient: this talks to a third party on a long cache,
        // and giving it a named client keeps that traffic separable from the
        // update checker's in any diagnosis.
        //
        // Named and singleton rather than AddHttpClient<PublicAddressService>(),
        // for the reason spelled out above BdsUpdateService and then written
        // wrongly here: the typed overload registers TRANSIENT, and this service
        // caches the address in its own fields under a header calling that cache a
        // correctness requirement. Transient, every consumer gets an empty one, so
        // the half-hour cache holds only for as long as exactly one singleton is
        // the only caller -- and a component that re-renders would ask a third
        // party's free service once per render. Forget() is the same hazard
        // inverted and has no caller yet: a cache dropped on one copy is not
        // dropped on the next, so the first thing to reach for it would not have
        // worked either (T217).
        services.AddHttpClient(PublicAddressService.HttpClientName);
        services.AddSingleton<PublicAddressService>();

        // D55. TryAdd, like IShadowCopyService: it is the one Windows call in the
        // storage section, and a test host replaces it with a fake.
        services.TryAddSingleton<IVolumeDeduplicationInspector, WmiVolumeDeduplicationInspector>();
        services.TryAddSingleton<IVolumeMediaInspector, WmiVolumeMediaInspector>();
        services.AddSingleton<HostInspector>();

        services.TryAddSingleton<IPowerShellRunner, PowerShellRunner>();

        // D46. The four host fixes, read and write, behind Core interfaces so a
        // caller never knows whether the work runs here or in the privilege
        // broker. TryAdd, like IShadowCopyService, so a test host can replace one.
        services.TryAddSingleton<IFirewallService, FirewallInspector>();
        services.TryAddSingleton<IDefenderService, DefenderInspector>();
        services.TryAddSingleton<IVssServiceControl, ReadinessInspector>();
        services.TryAddSingleton<ILoopbackExemptionService, AppIsolationInspector>();

        // Singleton because CPU is a rate: it keeps the previous processor time
        // per process, and a transient one would have no previous to compare to.
        services.AddSingleton<ProcessSampler>();

        // Registered after UpdateService so instances start after the update
        // check and, more importantly, stop *before* it on shutdown — hosted
        // services stop in reverse order, and a self-update that begins while
        // servers are still draining is the race this ordering avoids.
        services.AddHostedService<InstanceHostedService>();
        services.AddHostedService<RestartScheduler>();

        // D105, console completion. Last, so it stops first: a throwaway server it is
        // running is killed before the servers are drained.
        services.AddHttpClient(VanillaDataClient.HttpClientName);
        services.AddSingleton<VanillaDataClient>();
        services.AddSingleton<CompletionStore>();
        services.AddSingleton(sp => new ThrowawayServer(
            sp.GetRequiredService<ISentryPaths>(),
            sp.GetRequiredService<ServerPackageStore>(),
            (executable, directory) => new BedrockProcess(
                "completion", executable, directory, sp.GetRequiredService<ILoggerFactory>().CreateLogger<BedrockProcess>(),
                sp.GetRequiredService<IServerProcessStarter>()),
            () => ThrowawayPortsInUse(sp.GetRequiredService<InstanceRegistry>()),
            sp.GetRequiredService<ILogger<ThrowawayServer>>()));
        services.AddSingleton<CompletionCapture>();
        services.AddSingleton<CompletionCoordinator>();
        services.AddSingleton<ICompletionRuns>(sp => sp.GetRequiredService<CompletionCoordinator>());
        services.AddHostedService(sp => sp.GetRequiredService<CompletionCoordinator>());
        services.AddSingleton<CompletionData>();

        return services;
    }
}
