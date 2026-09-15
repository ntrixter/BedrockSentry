using BedrockSentry.Services.Configuration;
using Microsoft.AspNetCore.Authorization;

namespace BedrockSentry.Host.Middleware;

/// <summary>
/// Sends every request to the first-run wizard until an administrator account exists.
/// </summary>
/// <remarks>
/// <para>
/// Placed <em>before</em> authentication on purpose. An unconfigured install has no
/// credential to authenticate against, so letting the auth middleware run first would
/// redirect to a login form that nobody can possibly satisfy.
/// </para>
/// <para>
/// The exemption list is derived from endpoint metadata rather than hard-coded paths.
/// Routing is inserted ahead of the user pipeline automatically, so the endpoint — and
/// therefore its <see cref="IAllowAnonymous"/> metadata — is already resolved by the
/// time this runs. One rule then covers the health endpoint, the wizard itself, the
/// login page, and the error pages, and a page marked anonymous later is covered
/// without anyone remembering to update a list here.
/// </para>
/// </remarks>
public sealed class FirstRunGateMiddleware
{
    private const string SetupPath = "/setup";

    private readonly RequestDelegate _next;

    public FirstRunGateMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, SetupState setupState)
    {
        if (setupState.IsConfigured)
        {
            await _next(context);
            return;
        }

        if (context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            await _next(context);
            return;
        }

        if (context.Request.Path.Equals(SetupPath, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        context.Response.Redirect(SetupPath);
    }
}

/// <summary>Pipeline registration for <see cref="FirstRunGateMiddleware"/>.</summary>
public static class FirstRunGateMiddlewareExtensions
{
    public static IApplicationBuilder UseFirstRunGate(this IApplicationBuilder app) =>
        app.UseMiddleware<FirstRunGateMiddleware>();
}
