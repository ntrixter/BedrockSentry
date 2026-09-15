using System.Text.Json;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Packages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BedrockSentry.Tests;

/// <summary>
/// docs/design.md § 11. When the wrapper asks Mojang is install-wide and settable, and
/// it reuses <see cref="RestartSchedule"/> rather than growing a third way of
/// writing "every so often".
/// </summary>
public class BdsCheckScheduleTests
{
    [Fact]
    public void An_unconfigured_install_checks_every_six_hours()
    {
        var schedule = new BdsUpdateOptions().ScheduleOrDefault;

        Assert.Equal(RestartScheduleMode.EveryNHours, schedule.Mode);
        Assert.Equal(6, schedule.IntervalHours);
    }

    // The pre-CheckSchedule spelling. Nothing writes it, but somebody may have
    // hand-edited it, and a config key that quietly stops working is worse than
    // one that was never offered.
    [Fact]
    public void The_legacy_interval_key_is_still_honoured()
    {
        var schedule = new BdsUpdateOptions { CheckInterval = TimeSpan.FromHours(12) }.ScheduleOrDefault;

        Assert.Equal(RestartScheduleMode.EveryNHours, schedule.Mode);
        Assert.Equal(12, schedule.IntervalHours);
    }

    [Fact]
    public void An_explicit_schedule_supersedes_the_legacy_key()
    {
        var options = new BdsUpdateOptions
        {
            CheckInterval = TimeSpan.FromHours(12),
            CheckSchedule = new RestartSchedule
            {
                Mode = RestartScheduleMode.DailyAt,
                TimeOfDay = new TimeOnly(4, 30),
            },
        };

        Assert.Equal(RestartScheduleMode.DailyAt, options.ScheduleOrDefault.Mode);
        Assert.Equal(new TimeOnly(4, 30), options.ScheduleOrDefault.TimeOfDay);
    }

    // A zero or negative legacy interval would otherwise produce a schedule of
    // "every 0 hours", which RestartSchedule reads as no interval at all — so an
    // install with a typo in it would silently stop checking.
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void A_nonsensical_legacy_interval_falls_back_rather_than_disabling_checks(int hours)
    {
        var schedule = new BdsUpdateOptions
        {
            CheckInterval = TimeSpan.FromHours(hours),
        }.ScheduleOrDefault;

        Assert.Equal(6, schedule.IntervalHours);
        Assert.NotNull(schedule.Interval);
    }

    [Fact]
    public void The_schedule_binds_from_configuration_the_way_the_page_writes_it()
    {
        // Exactly the shape BdsUpdateSettingsStore.SetScheduleAsync produces,
        // including TimeOfDay as "HH:mm" — the binder has to accept what the
        // writer emits, and nothing else checks that the two agree.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sentry:BdsUpdate:CheckForUpdates"] = "true",
                ["Sentry:BdsUpdate:CheckSchedule:Mode"] = "DailyAt",
                ["Sentry:BdsUpdate:CheckSchedule:IntervalHours"] = "6",
                ["Sentry:BdsUpdate:CheckSchedule:TimeOfDay"] = "04:30",
            })
            .Build();

        var options = new BdsUpdateOptions();
        configuration.GetSection(BdsUpdateOptions.SectionName).Bind(options);

        Assert.True(options.CheckForUpdates);
        Assert.Equal(RestartScheduleMode.DailyAt, options.ScheduleOrDefault.Mode);
        Assert.Equal(new TimeOnly(4, 30), options.ScheduleOrDefault.TimeOfDay);
    }

    [Theory]
    [InlineData(RestartScheduleMode.EveryNHours, 6, "every 6 hours")]
    [InlineData(RestartScheduleMode.EveryNHours, 1, "every hour")]
    [InlineData(RestartScheduleMode.DailyAt, 6, "daily at 04:30")]
    [InlineData(RestartScheduleMode.None, 6, "only on startup and when asked")]
    public void The_cadence_is_described_in_the_administrators_own_terms(
        RestartScheduleMode mode, int hours, string expected)
    {
        var described = BdsUpdateSettingsStore.Describe(new RestartSchedule
        {
            Mode = mode,
            IntervalHours = hours,
            TimeOfDay = new TimeOnly(4, 30),
        });

        Assert.Equal(expected, described);
    }
}

public sealed class BdsUpdateSettingsStoreTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("bedrocksentry-bdscfg-").FullName;

    private readonly BdsUpdateSettingsStore _store;

    public BdsUpdateSettingsStoreTests()
    {
        var paths = new Services.Configuration.SentryPaths(_root);
        Directory.CreateDirectory(paths.Config);
        _store = new BdsUpdateSettingsStore(paths, NullLogger<BdsUpdateSettingsStore>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string ConfigPath => Path.Combine(_root, "config", "bedrocksentry.json");

    /// <summary>Binds the written file back, which is the only assertion that matters.</summary>
    private BdsUpdateOptions ReadBack()
    {
        var options = new BdsUpdateOptions();

        new ConfigurationBuilder()
            .AddJsonFile(ConfigPath)
            .Build()
            .GetSection(BdsUpdateOptions.SectionName)
            .Bind(options);

        return options;
    }

    [Fact]
    public async Task A_saved_schedule_binds_back_to_what_was_saved()
    {
        await _store.SetScheduleAsync(
            new RestartSchedule { Mode = RestartScheduleMode.DailyAt, TimeOfDay = new TimeOnly(4, 30) },
            checkForUpdates: true);

        var options = ReadBack();

        Assert.True(options.CheckForUpdates);
        Assert.Equal(RestartScheduleMode.DailyAt, options.ScheduleOrDefault.Mode);
        Assert.Equal(new TimeOnly(4, 30), options.ScheduleOrDefault.TimeOfDay);
    }

    [Fact]
    public async Task Turning_checking_off_is_saved_as_such()
    {
        await _store.SetScheduleAsync(new RestartSchedule(), checkForUpdates: false);

        Assert.False(ReadBack().CheckForUpdates);
    }

    // Leaving the old key behind would make the file state two different answers
    // to one question, and the louder of the two would be the one that no longer
    // applies.
    [Fact]
    public async Task Saving_clears_the_legacy_interval_key()
    {
        await File.WriteAllTextAsync(ConfigPath, """
            {
              "Sentry": {
                "BdsUpdate": { "CheckInterval": "12:00:00", "LinksUrl": "https://example.invalid/feed" }
              }
            }
            """);

        await _store.SetScheduleAsync(
            new RestartSchedule { Mode = RestartScheduleMode.EveryNHours, IntervalHours = 3 },
            checkForUpdates: true);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath));
        var section = document.RootElement.GetProperty("Sentry").GetProperty("BdsUpdate");

        Assert.False(section.TryGetProperty("CheckInterval", out _));

        // And the hand-edited feed address it was never asked about survives.
        Assert.Equal("https://example.invalid/feed", section.GetProperty("LinksUrl").GetString());
        Assert.Equal(3, ReadBack().ScheduleOrDefault.IntervalHours);
    }

    [Fact]
    public async Task An_untouched_setting_elsewhere_in_the_file_survives()
    {
        await File.WriteAllTextAsync(ConfigPath, """
            {
              "Sentry": {
                "Admin": { "Username": "steve" },
                "BdsUpdate": { "CheckForUpdates": true }
              }
            }
            """);

        await _store.SetScheduleAsync(new RestartSchedule(), checkForUpdates: true);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigPath));

        Assert.Equal(
            "steve",
            document.RootElement.GetProperty("Sentry").GetProperty("Admin")
                .GetProperty("Username").GetString());
    }
}
