using System.Text;
using BedrockSentry.Core.Instances;
using Microsoft.Extensions.Configuration;

namespace BedrockSentry.Tests;

/// <summary>
/// The instance section is hand-edited in a config file until milestone 16 gives
/// it a UI, so the binding is the interface. A renamed property or a TimeSpan
/// that silently fails to parse would show up as a server that ignores its own
/// settings — with no error anywhere.
/// </summary>
public class InstanceOptionsBindingTests
{
    private static InstanceOptions Bind(string json)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        return configuration.GetSection(InstanceOptions.SectionName).Get<InstanceOptions>()
               ?? new InstanceOptions();
    }

    [Fact]
    public void Binds_the_documented_shape()
    {
        var options = Bind("""
        {
          "Sentry": {
            "Instances": {
              "Root": "D:\\bedrock",
              "ServiceStopWarning": "00:00:05",
              "ShutdownDrainBudget": "00:00:45",
              "ConsoleLog": { "MaxFiles": 3, "MaxFileBytes": 1048576 },
              "Servers": {
                "survival": {
                  "Name": "Survival",
                  "AutoStart": true,
                  "Watchdog": { "MaxConsecutiveFailures": 5, "InitialBackoff": "00:00:20" },
                  "RestartSchedule": { "Mode": "DailyAt", "TimeOfDay": "05:30" }
                },
                "creative": {
                  "Name": "Creative",
                  "AutoStart": false,
                  "WorkingDirectory": "E:\\worlds\\creative",
                  "RestartSchedule": { "Mode": "EveryNHours", "IntervalHours": 6 }
                },
                "retired-one": { "Name": "Old", "Retired": true }
              }
            }
          }
        }
        """);

        Assert.Equal("D:\\bedrock", options.Root);
        Assert.Equal(TimeSpan.FromSeconds(5), options.ServiceStopWarning);
        Assert.Equal(TimeSpan.FromSeconds(45), options.ShutdownDrainBudget);
        Assert.Equal(3, options.ConsoleLog.MaxFiles);
        Assert.Equal(1048576, options.ConsoleLog.MaxFileBytes);
        Assert.Equal(3, options.Servers.Count);

        var survival = options.Servers["survival"];
        Assert.Equal("Survival", survival.Name);
        Assert.True(survival.AutoStart);
        Assert.Equal(5, survival.Watchdog.MaxConsecutiveFailures);
        Assert.Equal(TimeSpan.FromSeconds(20), survival.Watchdog.InitialBackoff);
        Assert.Equal(RestartScheduleMode.DailyAt, survival.RestartSchedule.Mode);
        Assert.Equal(new TimeOnly(5, 30), survival.RestartSchedule.TimeOfDay);

        var creative = options.Servers["creative"];
        Assert.False(creative.AutoStart);
        Assert.Equal("E:\\worlds\\creative", creative.WorkingDirectory);
        Assert.Equal(RestartScheduleMode.EveryNHours, creative.RestartSchedule.Mode);
        Assert.Equal(6, creative.RestartSchedule.IntervalHours);

        Assert.True(options.Servers["retired-one"].Retired);
    }

    // Anything not specified has to fall back to the defaults the spec describes,
    // not to zero — a watchdog silently configured with MaxConsecutiveFailures 0
    // would never give up, which is the opposite of D15.
    [Fact]
    public void Unspecified_values_keep_their_defaults()
    {
        var options = Bind("""
        { "Sentry": { "Instances": { "Servers": { "survival": { "Name": "Survival" } } } } }
        """);

        var survival = options.Servers["survival"];
        Assert.Equal(3, survival.Watchdog.MaxConsecutiveFailures);
        Assert.Equal(TimeSpan.FromMinutes(2), survival.Watchdog.StableAfter);
        Assert.Equal("bedrock_server.exe", survival.Executable);
        Assert.True(survival.AutoStart);
        Assert.False(survival.Retired);
        Assert.Equal(RestartScheduleMode.None, survival.RestartSchedule.Mode);

        Assert.Equal(string.Empty, options.Root);
        Assert.Equal(TimeSpan.FromSeconds(5), options.ServiceStopWarning);
        Assert.True(options.ConsoleLog.Enabled);
    }

    [Fact]
    public void A_missing_section_yields_no_instances()
    {
        var options = Bind("""{ "Sentry": { "Tls": { "Port": 8443 } } }""");

        Assert.Empty(options.Servers);
    }

    // The configuration binder appends to a collection that already has items
    // rather than replacing it. A pre-populated default would therefore merge
    // with the administrator's list and hand back warnings they had removed.
    [Fact]
    public void A_configured_warning_list_replaces_the_defaults_rather_than_merging()
    {
        var options = Bind("""
        {
          "Sentry": { "Instances": { "Servers": { "survival": {
            "Name": "Survival",
            "RestartSchedule": { "Warnings": ["00:15:00", "00:01:00", "00:00:10"] }
          } } } }
        }
        """);

        Assert.Equal(
            [TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10)],
            options.Servers["survival"].RestartSchedule.WarningsOrDefault);
    }

    [Fact]
    public void An_unconfigured_warning_list_uses_the_defaults()
    {
        var options = Bind("""
        { "Sentry": { "Instances": { "Servers": { "survival": { "Name": "Survival" } } } } }
        """);

        Assert.Equal(
            RestartSchedule.DefaultWarnings,
            options.Servers["survival"].RestartSchedule.WarningsOrDefault);
    }

    // The configuration system produces no keys for an empty JSON array, so it
    // cannot be told apart from the property being absent. Asserting the real
    // behaviour rather than the behaviour that would be nicer: the alternative
    // is a config file that silently does the opposite of what it reads like.
    [Fact]
    public void An_empty_json_array_falls_back_to_the_defaults()
    {
        var options = Bind("""
        {
          "Sentry": { "Instances": { "Servers": { "survival": {
            "Name": "Survival",
            "RestartSchedule": { "Warnings": [] }
          } } } }
        }
        """);

        Assert.Equal(
            RestartSchedule.DefaultWarnings,
            options.Servers["survival"].RestartSchedule.WarningsOrDefault);
    }

    // So this is the supported way to restart without warning anyone: a single
    // zero, which the countdown planner filters out.
    [Fact]
    public void A_single_zero_warning_means_restart_without_warning_anyone()
    {
        var options = Bind("""
        {
          "Sentry": { "Instances": { "Servers": { "survival": {
            "Name": "Survival",
            "RestartSchedule": { "Warnings": ["00:00:00"] }
          } } } }
        }
        """);

        var request = GracefulStopRequest.Scheduled(
            options.Servers["survival"].RestartSchedule, TimeSpan.FromSeconds(45));

        Assert.Empty(request.EffectiveWarnings());
        Assert.Equal(TimeSpan.Zero, request.WarningLead);
    }
}
