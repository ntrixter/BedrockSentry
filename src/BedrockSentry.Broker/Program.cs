using System.Runtime.Versioning;
using System.Security.Principal;
using BedrockSentry.Core;
using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Broker;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Updates;
using BedrockSentry.Services.Backups;
using BedrockSentry.Services.Broker;
using BedrockSentry.Services.Configuration;
using BedrockSentry.Services.Diagnostics;
using BedrockSentry.Services.Instances;
using BedrockSentry.Services.Security;
using BedrockSentry.Services.Updates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

[assembly: SupportedOSPlatform("windows")]

// The same data root the main service uses, from the same switch in its own
// ImagePath, so both read one configuration.
var paths = new SentryPaths(SentryPaths.DataRootArgument(args));

// Before any file is opened, as the main service does: a junction the main service's
// account or a server made is not followed by anything this process opens.
var redirectionGuard = RedirectionGuard.Enforce();
var mainService = BrokerNames.MainServiceArgument(args) ?? BrokerNames.DefaultMainService;
// The broker runs from current\broker, its own folder beside the main service's, so
// the build it belongs to is the folder above it.
var layout = new InstallLayout(Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)));

// Run once by the elevated installer, not as the service: grant the main service's
// account the data root and re-grant its secret files, then exit.
if (args.Contains(BrokerProgram.PrepareDataRootSwitch, StringComparer.OrdinalIgnoreCase))
{
    return BrokerProgram.PrepareDataRoot(paths, mainService, BrokerProgram.ArgumentAfter(args, "--account"));
}

// Run by install.ps1 -GrantFolder, elevated: hand an existing folder outside the data
// root to the main service (D115 (b)), then exit.
if (args.Contains(BrokerProgram.GrantFolderSwitch, StringComparer.OrdinalIgnoreCase))
{
    return BrokerProgram.GrantFolder(
        paths, layout, mainService, BrokerProgram.ArgumentAfter(args, BrokerProgram.GrantFolderSwitch), redirectionGuard is null);
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,

    // The service control manager starts services in System32; the broker's own
    // folder is where its appsettings would be, as for the main service.
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.AddWindowsService(options => options.ServiceName = BrokerNames.Service(mainService));

// The main service's configuration, read and never written. Its account can change
// this file, so nothing in it is trusted: every folder it names passes the path
// rules, and every port must be one the server's own files name.
builder.Configuration.AddJsonFile(Path.Combine(paths.Config, "bedrocksentry.json"), optional: true, reloadOnChange: true);
builder.Services.AddOptions<InstanceOptions>().Bind(builder.Configuration.GetSection(InstanceOptions.SectionName));
builder.Services.AddOptions<StorageOptions>().Bind(builder.Configuration.GetSection(StorageOptions.SectionName));

builder.Services.AddSingleton<ISentryPaths>(paths);
builder.Services.AddSingleton<IPowerShellRunner, PowerShellRunner>();
builder.Services.AddSingleton<IFirewallService, FirewallInspector>();
builder.Services.AddSingleton<IDefenderService, DefenderInspector>();
builder.Services.AddSingleton<ILoopbackExemptionService, AppIsolationInspector>();
builder.Services.AddSingleton<IVssServiceControl, ReadinessInspector>();
builder.Services.AddSingleton<IShadowCopyService, ShadowCopyService>();
builder.Services.AddSingleton<IOrphanInspector, ProcessOrphanInspector>();
builder.Services.AddSingleton<IDeduplicationReader, WmiVolumeDeduplicationInspector>();
builder.Services.AddSingleton<IBrokerInstances, ConfiguredBrokerInstances>();
builder.Services.AddSingleton<IReparseProbe, FileSystemReparseProbe>();
builder.Services.AddSingleton<IFolderPin, WindowsFolderPin>();
builder.Services.AddSingleton<IFolderGrant>(_ => new WindowsFolderGrant(mainService));
builder.Services.AddSingleton(new BrokerPathContext(
    layout.InstallRoot, paths.DataRoot, paths.Instances, ReservedSystemDirectories.All));
builder.Services.AddSingleton(_ => BrokerProgram.LoadRecord(layout.InstallRoot));
builder.Services.AddSingleton<Func<string, IReadOnlyList<ReachableSockets>>>(PortInspector.ReachableOn);
// Self-update (milestone 18.5). Only the channel, the health port and the timeouts
// are read from the main service's configuration; the feed is the install policy's.
builder.Services.AddOptions<UpdateOptions>().Bind(builder.Configuration.GetSection(UpdateOptions.SectionName));
builder.Services.AddSingleton(layout);
builder.Services.AddSingleton<IInstallLayout>(layout);
builder.Services.AddSingleton<IInstallPolicy, InstallPolicyFile>();
builder.Services.AddHttpClient<UpdateClient>();
builder.Services.AddSingleton<Func<UpdateOptions>>(services =>
{
    var monitor = services.GetRequiredService<IOptionsMonitor<UpdateOptions>>();
    return () => BrokerProgram.ReadOrDefault(monitor);
});
builder.Services.AddSingleton<IBrokerUpdateSource>(services => new BrokerUpdateSource(
    services.GetRequiredService<UpdateClient>(),
    services.GetRequiredService<IInstallPolicy>(),
    services.GetRequiredService<Func<UpdateOptions>>(),
    Path.Combine(BrokerUpdates.FolderIn(layout.InstallRoot), "downloads")));
builder.Services.AddSingleton<IHelperLauncher, DetachedHelperLauncher>();
builder.Services.AddSingleton<IBrokerUpdates>(services => new BrokerUpdates(
    services.GetRequiredService<IBrokerUpdateSource>(),
    services.GetRequiredService<IHelperLauncher>(),
    layout,
    services.GetRequiredService<Func<UpdateOptions>>(),
    mainService,
    BrokerNames.Service(mainService),
    BuildInfo.InstallEpoch,
    services.GetRequiredService<ILogger<BrokerUpdates>>()));
builder.Services.AddSingleton<BrokerDispatcher>();

builder.Services.AddSingleton<IBrokerPipeFactory>(_ => new WindowsBrokerPipeFactory(BrokerNames.Pipe(mainService), mainService));
builder.Services.AddSingleton<IBrokerCallerCheck>(_ => new ServiceProcessCallerCheck(mainService));
builder.Services.AddHostedService<BrokerPipeServer>();

builder.Logging.AddEventLog(settings => settings.SourceName = BrokerNames.Service(mainService));

var host = builder.Build();

if (redirectionGuard is { } notGuarded)
{
    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("BedrockSentry.Broker")
        .LogWarning("{Warning}", RedirectionGuard.NotOn(notGuarded));
}

host.Run();

return 0;

internal static class BrokerProgram
{
    /// <summary>The value after <paramref name="name"/>, or null when it is absent or blank.</summary>
    public static string? ArgumentAfter(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    public const string PrepareDataRootSwitch = "--prepare-data-root";

    public const string GrantFolderSwitch = "--grant-folder";

    /// <summary>
    /// The main service's update settings, or the defaults when its file does not bind:
    /// that file is the service account's to write, and an update must not fail on it.
    /// </summary>
    public static UpdateOptions ReadOrDefault(IOptionsMonitor<UpdateOptions> monitor)
    {
        try
        {
            return monitor.CurrentValue;
        }
        catch (InvalidOperationException)
        {
            return new UpdateOptions();
        }
    }

    /// <summary>
    /// <see cref="DataRootPreparation"/> for the account the main service logs on as:
    /// <c>--account</c> when given, <c>NT SERVICE\&lt;main service&gt;</c> otherwise.
    /// </summary>
    /// <remarks>
    /// The service names the user SID of its own token in each secret file's list, so
    /// the account granted here must be that one: the virtual account's SID for a
    /// virtual account, the named account's for a named one.
    /// </remarks>
    /// <returns>0 when every list was written; 1, with the reason on standard error, otherwise.</returns>
    public static int PrepareDataRoot(SentryPaths paths, string mainService, string? account)
    {
        try
        {
            // A virtual account's SID is worked out from the name, so this can run
            // before the installer registers the service; a named account must exist.
            var name = account ?? $"NT SERVICE\\{mainService}";
            var sid = account is null
                ? ServiceSid.For(mainService)
                : new NTAccount(account).Translate(typeof(SecurityIdentifier)).Value;

            foreach (var path in DataRootPreparation.Run(paths, sid, new WindowsAccessListWriter()))
            {
                Console.WriteLine($"Granted {name}: {path}");
            }

            return 0;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"The data root could not be prepared for {mainService}: {ex.Message}");
            return 1;
        }
    }

    /// <summary><see cref="AdministratorGrant"/> for one folder, printing what happened.</summary>
    /// <returns>0 when the folder was granted; 1, with the reason on standard error, otherwise.</returns>
    public static int GrantFolder(SentryPaths paths, InstallLayout layout, string mainService, string? folder, bool guarded)
    {
        if (folder is null)
        {
            Console.Error.WriteLine($"{GrantFolderSwitch} needs the folder to grant.");
            return 1;
        }

        try
        {
            var granted = AdministratorGrant.Run(
                folder,
                new BrokerPathContext(layout.InstallRoot, paths.DataRoot, paths.Instances, ReservedSystemDirectories.All),
                new FileSystemReparseProbe(),
                new WindowsFolderPin(),
                new WindowsFolderGrant(mainService),
                new WindowsAccessListWriter(),
                LoadRecord(layout.InstallRoot),
                junctionsRefused: guarded);

            (granted.Succeeded ? Console.Out : Console.Error).WriteLine(granted.Message);
            return granted.Succeeded ? 0 : 1;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"'{folder}' could not be granted to {mainService}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// The broker's record, in a folder under the install root locked to LocalSystem and
    /// Administrators, which the main service's account cannot write.
    /// </summary>
    public static BrokerRecord LoadRecord(string installRoot)
    {
        var folder = Path.Combine(installRoot, "broker-state");
        Directory.CreateDirectory(folder);
        WindowsAcl.RestrictToSystemAndAdministrators(folder);

        return BrokerRecord.Load(Path.Combine(folder, "record.json"));
    }
}
