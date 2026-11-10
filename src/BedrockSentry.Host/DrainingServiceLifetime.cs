using System.Runtime.Versioning;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace BedrockSentry.Host;

/// <summary>
/// The framework's Windows service lifetime, except that Windows is told the service
/// stopped only once the host has, every server's drain included (<c>T167</c>,
/// <c>D32</c>). <see cref="ServiceStopHold"/> holds the rule and its tests.
/// </summary>
/// <remarks>
/// <c>OnShutdown</c> is left as the framework has it: when Windows itself shuts down it
/// bounds the wait, whatever a service reports.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class DrainingServiceLifetime : WindowsServiceLifetime
{
    private readonly IHostApplicationLifetime _application;
    private readonly TimeSpan _shutdownTimeout;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    public DrainingServiceLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime application,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> hostOptions,
        IOptions<WindowsServiceLifetimeOptions> serviceOptions,
        TimeProvider time)
        : base(environment, application, loggerFactory, hostOptions, serviceOptions)
    {
        _application = application;
        _shutdownTimeout = hostOptions.Value.ShutdownTimeout;
        _time = time;
        _log = loggerFactory.CreateLogger<DrainingServiceLifetime>();
    }

    protected override void OnStop() =>
        ServiceStopHold.Run(
            _shutdownTimeout,
            () => base.OnStop(),
            timeout => _application.ApplicationStopped.WaitHandle.WaitOne(timeout),
            asked => RequestAdditionalTime((int)Math.Clamp(asked.TotalMilliseconds, 0, int.MaxValue)),
            _time,
            _log);
}
