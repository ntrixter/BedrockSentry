using BedrockSentry.Core.Configuration;

namespace BedrockSentry.Tests;

/// <summary>
/// Guards the health-check contract that crosses a version boundary.
/// </summary>
/// <remarks>
/// <para>
/// The update plan is written by the build being replaced and consumed against the
/// build replacing it. If the two disagree about where the health endpoint lives, the
/// probe hits nothing, reports unhealthy, and every update rolls back — while looking
/// exactly like a genuinely broken build.
/// </para>
/// <para>
/// The IPv4 assertion is not cosmetic. An earlier version registered the endpoint with
/// <c>RequireHost("[::1]:47324")</c>; ASP.NET Core's host matcher cannot parse a
/// bracketed IPv6 literal with a port and throws while building the routing table.
/// Because that table is built on the first request rather than at startup, the
/// service started cleanly, generated its certificate, wrote its setup token — and
/// then returned 500 for every route in the application.
/// </para>
/// </remarks>
public sealed class UpdateHealthContractTests
{
    [Fact]
    public void The_health_url_uses_an_IPv4_loopback_literal()
    {
        var url = new UpdateOptions().BuildHealthUrl();

        Assert.StartsWith("http://127.0.0.1:", url, StringComparison.Ordinal);

        // No brackets anywhere: they are the specific thing the host matcher chokes on.
        Assert.DoesNotContain('[', url);
        Assert.DoesNotContain(']', url);
    }

    [Fact]
    public void The_health_url_is_plaintext_http()
    {
        // Never HTTPS. The updater would then have to tolerate certificate errors —
        // and a broken certificate is exactly the class of failure it exists to catch.
        Assert.StartsWith("http://", new UpdateOptions().BuildHealthUrl(), StringComparison.Ordinal);
        Assert.DoesNotContain("https://", new UpdateOptions().BuildHealthUrl(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_health_url_follows_the_configured_port()
    {
        var url = new UpdateOptions { HealthPort = 51234 }.BuildHealthUrl();

        Assert.Equal("http://127.0.0.1:51234/healthz", url);
    }

    [Fact]
    public void The_health_url_ends_with_the_shared_path_constant()
    {
        // The host maps the endpoint from this same constant, so they cannot drift.
        Assert.EndsWith(UpdateOptions.HealthPath, new UpdateOptions().BuildHealthUrl(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_health_path_is_a_single_rooted_segment()
    {
        // The first-run gate matches this path with Equals rather than a prefix,
        // because StartsWithSegments("/health") is False against "/healthz".
        Assert.StartsWith("/", UpdateOptions.HealthPath, StringComparison.Ordinal);
        Assert.Equal(1, UpdateOptions.HealthPath.Count(c => c == '/'));
    }

    [Fact]
    public void The_default_health_port_is_outside_the_ephemeral_range()
    {
        // Windows allocates dynamic ports from 49152 upward. A default inside that
        // range would intermittently collide with whatever grabbed it first.
        Assert.InRange(new UpdateOptions().HealthPort, 1024, 49151);
    }
}
