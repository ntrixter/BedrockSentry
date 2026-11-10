using BedrockSentry.Core.Backups;
using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Updates;
using BedrockSentry.Services.Broker;
using BedrockSentry.Services.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BedrockSentry.Services.DependencyInjection;

public static class BrokerServiceCollectionExtensions
{
    /// <summary>
    /// Points the eight privileged seams at the broker, through <paramref name="channel"/>.
    /// </summary>
    /// <remarks>
    /// Removes whatever was registered for each seam first, so it holds whether it runs
    /// before or after <see cref="ServiceCollectionExtensions.AddBedrockSentryServices"/>.
    /// </remarks>
    public static IServiceCollection AddBrokerClients(
        this IServiceCollection services, Func<IServiceProvider, IBrokerChannel> channel)
    {
        services.RemoveAll<IBrokerChannel>();
        services.AddSingleton(channel);

        services.TryAddSingleton<ReadinessInspector>();

        Point<IFirewallService, BrokerFirewallService>(services);
        Point<IDefenderService, BrokerDefenderService>(services);
        Point<ILoopbackExemptionService, BrokerLoopbackExemptionService>(services);
        Point<IVssServiceControl, BrokerVssServiceControl>(services);
        Point<IShadowCopyService, BrokerShadowCopyService>(services);
        Point<IOrphanInspector, BrokerOrphanInspector>(services);
        Point<IWrapperUpdateInstaller, BrokerWrapperUpdateInstaller>(services);
        Point<IVolumeDeduplicationInspector, BrokerVolumeDeduplicationInspector>(services);

        return services;
    }

    private static void Point<TSeam, TClient>(IServiceCollection services)
        where TSeam : class
        where TClient : class, TSeam
    {
        services.RemoveAll<TSeam>();
        services.AddSingleton<TSeam, TClient>();
    }
}
