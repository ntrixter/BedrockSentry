using System.Net;
using System.Text.Json;

namespace BedrockSentry.Updater;

/// <summary>One read of the new build's health endpoint, and the client that reads it.</summary>
/// <remarks>
/// Apart from <see cref="UpdateRunner"/> because it needs nothing Windows-only, so the
/// suite compiles this file on its own: the Updater's self-contained single-file build
/// is not a project a test project can reference.
/// </remarks>
internal static class HealthProbe
{
    /// <summary>What one read found.</summary>
    internal enum Verdict
    {
        /// <summary>The new build answered healthy and is the one installed.</summary>
        Healthy,

        /// <summary>An answer no amount of waiting will change.</summary>
        Failed,

        /// <summary>Not healthy yet; worth asking again.</summary>
        NotYet,
    }

    /// <summary>A client that reports a redirect rather than following it (<c>T327</c>).</summary>
    /// <remarks>
    /// The default handler follows redirects, so the check for one in
    /// <see cref="ReadAsync"/> never saw it: a redirect to the login page came back as
    /// HTML that failed to parse, an HTTPS redirect failed TLS against the self-signed
    /// certificate, and both were recorded as a timeout.
    /// </remarks>
    internal static HttpClient CreateClient() =>
        new(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(5),
        };

    /// <summary>Reads the endpoint once. A request that fails throws, and the caller asks again.</summary>
    internal static async Task<(Verdict Verdict, string Detail)> ReadAsync(
        HttpClient http, string healthUrl, string targetIdentity)
    {
        using var response = await http.GetAsync(healthUrl);

        if (response.StatusCode is HttpStatusCode.Found
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.MovedPermanently)
        {
            // Very specific message on purpose. A redirect here means the health
            // endpoint is behind authentication or behind HTTPS redirection, not
            // that the build is broken — and every update would fail identically
            // and be misdiagnosed as a bad build.
            return (Verdict.Failed,
                $"the health endpoint redirected to '{response.Headers.Location}' instead of answering. " +
                "It must be anonymous and exempt from HTTPS redirection");
        }

        if (!response.IsSuccessStatusCode)
        {
            return (Verdict.NotYet, $"HTTP {(int)response.StatusCode}");
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
        var version = root.TryGetProperty("version", out var v) ? v.GetString() : null;

        if (!string.Equals(status, "Healthy", StringComparison.OrdinalIgnoreCase))
        {
            return (Verdict.NotYet, $"status was '{status}'");
        }

        if (!string.Equals(version, targetIdentity, StringComparison.OrdinalIgnoreCase))
        {
            // Healthy, but not the build we installed. That means the stop or
            // the junction flip did not take effect and the OLD build is still
            // serving — which would otherwise be recorded as a successful
            // update, and the previous version pruned out from under it.
            return (Verdict.Failed,
                $"the running build reports '{version}' but '{targetIdentity}' was installed — " +
                "the service stop or the version switch did not take effect");
        }

        return (Verdict.Healthy, "healthy");
    }
}
