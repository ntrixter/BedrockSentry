using System.Text.Json;
using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BedrockSentry.Tests;

/// <summary>
/// The JSON the health endpoints answer, and the rules behind the detailed one
/// (<c>T165</c>).
/// </summary>
/// <remarks>
/// The field names are a cross-version contract: <c>UpdateRunner</c> reads
/// <c>status</c> and <c>version</c> by name out of the build that replaced it and
/// rolls back if either is wrong. Until this file existed nothing asserted them —
/// the shape was an anonymous object inside a lambda in <c>Program.cs</c>, which
/// this project cannot reference — so a rename would have been found by an update
/// failing in the field, looking exactly like a broken build.
/// </remarks>
public class HealthContractTests
{
    private static readonly HealthBody Liveness = new()
    {
        Status = "Healthy",
        Version = "0.3.0-build.82+74f24ab",
        BuildChannel = "build",
    };

    [Fact]
    public void The_liveness_body_carries_exactly_the_three_fields_the_updater_expects()
    {
        using var document = JsonDocument.Parse(HealthContract.Serialize(Liveness));
        var root = document.RootElement;

        // By name, because that is how the updater reads them.
        Assert.Equal("Healthy", root.GetProperty("status").GetString());
        Assert.Equal("0.3.0-build.82+74f24ab", root.GetProperty("version").GetString());
        Assert.Equal("build", root.GetProperty("buildChannel").GetString());

        // `detail` is absent rather than null on this endpoint, so the response is
        // byte-identical in shape to what every build before T165 answered.
        Assert.False(root.TryGetProperty("detail", out _));

        Assert.Equal(3, root.EnumerateObject().Count());
    }

    [Fact]
    public void The_status_the_updater_accepts_is_spelled_one_way_in_one_place()
    {
        // UpdateRunner compares against this exact string. HealthCheckStatus.Healthy
        // must keep rendering to it, and the constant must keep matching, or the
        // comparison silently never succeeds.
        Assert.Equal(HealthContract.HealthyStatus, HealthStatus.Healthy.ToString());
    }

    [Fact]
    public void The_detailed_path_is_a_single_rooted_segment_and_is_not_the_liveness_one()
    {
        // Single segment for the same reason HealthPath is: the loopback port gate
        // matches with PathString.Equals, not StartsWithSegments.
        Assert.StartsWith("/", HealthContract.ReadyPath, StringComparison.Ordinal);
        Assert.Equal(1, HealthContract.ReadyPath.Count(c => c == '/'));

        Assert.NotEqual(
            BedrockSentry.Core.Configuration.UpdateOptions.HealthPath,
            HealthContract.ReadyPath);
    }

    [Fact]
    public void The_detailed_body_names_each_check_and_its_own_status()
    {
        var body = Liveness with
        {
            Status = "Degraded",
            Detail =
            [
                new HealthDetail { Name = "instances", Status = "Degraded", Description = "Survival: held" },
                new HealthDetail { Name = "data-root", Status = "Healthy" },
            ],
        };

        using var document = JsonDocument.Parse(HealthContract.Serialize(body));
        var detail = document.RootElement.GetProperty("detail");

        Assert.Equal(2, detail.GetArrayLength());
        Assert.Equal("instances", detail[0].GetProperty("name").GetString());
        Assert.Equal("Survival: held", detail[0].GetProperty("description").GetString());

        // A check with nothing to say omits the field rather than sending null.
        Assert.False(detail[1].TryGetProperty("description", out _));
    }

    // ---- the instance rule -------------------------------------------------

    private static InstanceStatus Status(string name, InstanceState state, string? fault = null) =>
        new("id-" + name, name, state, null, null, null, null, null, 0, null, fault);

    [Fact]
    public void Nothing_configured_is_healthy_and_says_so()
    {
        var result = InstanceStateHealthCheck.Evaluate([]);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("No servers are configured", result.Description);
    }

    [Theory]
    [InlineData(InstanceState.Running)]
    [InlineData(InstanceState.Stopped)]
    [InlineData(InstanceState.Starting)]
    [InlineData(InstanceState.Stopping)]
    // Backoff is healthy on purpose: a scheduled retry is the watchdog working,
    // and flagging it would make every ordinary crash-and-recover into something
    // an operator has to dismiss.
    [InlineData(InstanceState.Backoff)]
    public void Every_state_but_faulted_is_healthy(InstanceState state)
    {
        Assert.Equal(
            HealthStatus.Healthy,
            InstanceStateHealthCheck.Evaluate([Status("Survival", state)]).Status);
    }

    // THE one that matters. A start refused because something already holds the
    // port (T151) lands in Faulted, and that is the wrapper doing its job. If this
    // ever returns Unhealthy and the check is mapped on the liveness endpoint, the
    // updater throws away a replacement build that was never broken.
    [Fact]
    public void A_faulted_server_is_degraded_and_never_unhealthy()
    {
        var result = InstanceStateHealthCheck.Evaluate(
            [Status("Survival", InstanceState.Faulted, "not started: Port 19132 is already open.")]);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.NotEqual(HealthStatus.Unhealthy, result.Status);

        Assert.Contains("Survival", result.Description);
        Assert.Contains("Port 19132", result.Description);
    }

    [Fact]
    public void A_faulted_server_with_no_reason_recorded_still_names_itself()
    {
        var result = InstanceStateHealthCheck.Evaluate([Status("Survival", InstanceState.Faulted)]);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("Survival", result.Description);
        Assert.Contains("no reason recorded", result.Description);
    }

    // FaultReason is written on entering Faulted and carried along unread through a
    // later Stopped, so reading it unconditionally would attach a stale explanation
    // to a server somebody stopped on purpose.
    [Fact]
    public void A_stopped_server_carrying_a_stale_fault_reason_is_not_reported()
    {
        var result = InstanceStateHealthCheck.Evaluate(
            [Status("Survival", InstanceState.Stopped, "not started: Port 19132 is already open.")]);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.DoesNotContain("19132", result.Description);
    }

    [Fact]
    public void One_faulted_server_among_several_is_counted_rather_than_generalised()
    {
        var result = InstanceStateHealthCheck.Evaluate(
        [
            Status("Survival", InstanceState.Running),
            Status("Creative", InstanceState.Faulted, "gave up after 3 consecutive failures"),
            Status("Test", InstanceState.Stopped),
        ]);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("1 of 3", result.Description);
        Assert.Contains("Creative", result.Description);
        Assert.DoesNotContain("Survival", result.Description);
    }

    // ---- the data root rule ------------------------------------------------

    [Fact]
    public void A_writable_data_root_is_healthy_and_leaves_nothing_behind()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bs-health-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            Assert.Equal(HealthStatus.Healthy, DataRootHealthCheck.Evaluate(directory).Status);

            // The probe file is the point of the check; a leftover would accumulate
            // one per poll.
            Assert.Empty(Directory.GetFileSystemEntries(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_data_root_that_is_not_there_is_unhealthy_rather_than_throwing()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"bs-absent-{Guid.NewGuid():N}", "deeper");

        var result = DataRootHealthCheck.Evaluate(missing);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("cannot be written to", result.Description);
    }

    [Fact]
    public void An_unconfigured_data_root_is_unhealthy_and_says_which_problem_it_is()
    {
        var result = DataRootHealthCheck.Evaluate("   ");

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("not configured", result.Description);
    }
}
