using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace BedrockSentry.Services.Web;

/// <summary>
/// Answers every request that arrives after the host has begun to stop with a 503,
/// so the web server's own stop has nothing new to wait for (<c>T112</c>).
/// </summary>
/// <remarks>
/// <para>SignalR closes the connections it holds once, when
/// <see cref="IHostApplicationLifetime.ApplicationStopping"/> fires, and a panel tab
/// reconnects straight away. A reconnect that lands before the web server stops
/// listening opens a connection nothing closes, and the web server waits for it until
/// <c>HostOptions.ShutdownTimeout</c>. Measured on Linux with that window widened by
/// two seconds and one stand-in server running: the tab reconnected 35 ms after the
/// stop began and the host took 71.4 s to stop, against 6.3 s with this gate.</para>
///
/// <para>Only new requests. One already running when the stop began is left to finish:
/// the drain endpoint's answer is what <c>install.ps1</c> reads.</para>
/// </remarks>
public sealed class StoppingRequestGate(RequestDelegate next, IHostApplicationLifetime lifetime)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (!lifetime.ApplicationStopping.IsCancellationRequested)
        {
            return next(context);
        }

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return Task.CompletedTask;
    }
}

/// <summary>Pipeline registration for <see cref="StoppingRequestGate"/>.</summary>
public static class StoppingRequestGateExtensions
{
    /// <summary>First in the pipeline, so nothing after it runs for a refused request.</summary>
    public static IApplicationBuilder UseStoppingRequestGate(this IApplicationBuilder app) =>
        app.UseMiddleware<StoppingRequestGate>();
}
