namespace BedrockSentry.Core.Instances;

public enum RestartScheduleMode
{
    /// <summary>No scheduled restarts. The default.</summary>
    None,

    /// <summary>Every N hours, measured from the instance's last start.</summary>
    EveryNHours,

    /// <summary>Once a day at a wall-clock time, in the server's local time zone.</summary>
    DailyAt,

    /// <summary>
    /// Every N minutes, measured the same way <see cref="EveryNHours"/> is.
    /// </summary>
    /// <remarks>
    /// Its own mode with its own field rather than a unit selector beside a
    /// single number, so a config file always says which value is in force —
    /// and so every schedule written before this existed keeps working with
    /// nothing to migrate. It exists for backups, where "every twenty minutes"
    /// is a reasonable appetite; a restart that often is expressible and is
    /// nobody's plan.
    /// </remarks>
    EveryNMinutes,
}

/// <summary>
/// When an instance restarts itself.
///
/// Deliberately not cron (<c>docs/design.md</c> § 5.3). These two shapes cover what a
/// game server actually needs, and both stay readable in a config file — which is
/// how they were edited until milestone 16 put them on the Configuration tab, and
/// is still where anything the panel does not model has to go. Cron's extra power
/// would be paid for on every edit.
/// </summary>
public sealed class RestartSchedule
{
    public RestartScheduleMode Mode { get; set; } = RestartScheduleMode.None;

    /// <summary>Interval for <see cref="RestartScheduleMode.EveryNHours"/>.</summary>
    public int IntervalHours { get; set; } = 12;

    /// <summary>Interval for <see cref="RestartScheduleMode.EveryNMinutes"/>.</summary>
    public int IntervalMinutes { get; set; } = 30;

    /// <summary>
    /// The interval the current mode actually uses, or null when this schedule
    /// is not an interval one — so callers ask once instead of branching on the
    /// mode to pick a field.
    /// </summary>
    public TimeSpan? Interval => Mode switch
    {
        RestartScheduleMode.EveryNHours when IntervalHours > 0 => TimeSpan.FromHours(IntervalHours),
        RestartScheduleMode.EveryNMinutes when IntervalMinutes > 0 => TimeSpan.FromMinutes(IntervalMinutes),
        _ => null,
    };

    /// <summary>Time of day for <see cref="RestartScheduleMode.DailyAt"/>, local time.</summary>
    public TimeOnly TimeOfDay { get; set; } = new(5, 0);

    /// <summary>Warning points used when the administrator has not chosen any.</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultWarnings =
    [
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromSeconds(10),
    ];

    /// <summary>
    /// When to warn players before a scheduled restart. This is the long
    /// countdown of <c>docs/design.md</c> § 5.2 — the one a service stop deliberately
    /// does not get, because Windows is waiting.
    /// </summary>
    /// <remarks>
    /// Nullable, and defaulted through <see cref="WarningsOrDefault"/> rather
    /// than by an initialiser here. The configuration binder *appends* to a
    /// collection that already has items instead of replacing it, so a
    /// pre-populated default would silently merge with whatever the
    /// administrator wrote — they would get their warnings plus the ones they
    /// deliberately removed, with no error to explain it.
    /// <para>
    /// Null means "not configured" and yields <see cref="DefaultWarnings"/>. An
    /// empty JSON array cannot be used to mean "no warnings": the configuration
    /// system produces no keys for one, so it is indistinguishable from the
    /// property being absent. To restart without warning anybody, configure a
    /// single <c>"00:00:00"</c> — zero-length warnings are filtered out when the
    /// countdown is planned, which leaves no warnings and no lead time.
    /// </para>
    /// </remarks>
    public List<TimeSpan>? Warnings { get; set; }

    /// <summary>The warning points actually in force.</summary>
    public IReadOnlyList<TimeSpan> WarningsOrDefault => Warnings ?? DefaultWarnings;

    /// <summary>
    /// This schedule in words — "every 12 hours", "every 30 minutes", "daily at
    /// 05:00", "none". A fragment rather than a sentence, so a caller can put its
    /// own subject in front of it.
    /// </summary>
    /// <remarks>
    /// Here rather than in each page that shows a schedule, because it was in two
    /// pages and one of them had never heard of
    /// <see cref="RestartScheduleMode.EveryNMinutes"/>: the Configuration tab
    /// rendered such a schedule correctly while the Status card beside it called
    /// the same schedule "none" (`T126`). A switch over a mode that lives in more
    /// than one place will be updated in one of them, and a read-only summary
    /// saying "nothing is scheduled" when something is, is the one wrong answer it
    /// must not give. One place to be right, and it is testable from
    /// <c>BedrockSentry.Tests</c>, which a <c>.razor</c> file is not.
    /// </remarks>
    public string Describe() => Mode switch
    {
        RestartScheduleMode.DailyAt => $"daily at {TimeOfDay:HH\\:mm}",
        RestartScheduleMode.EveryNHours =>
            IntervalHours == 1 ? "every hour" : $"every {IntervalHours} hours",
        RestartScheduleMode.EveryNMinutes =>
            IntervalMinutes == 1 ? "every minute" : $"every {IntervalMinutes} minutes",
        _ => "none",
    };

    /// <summary>
    /// Computes the next restart instant after <paramref name="after"/>, or null
    /// when nothing is scheduled.
    /// </summary>
    /// <param name="lastStartUtc">
    /// Used by <see cref="RestartScheduleMode.EveryNHours"/>, which counts from
    /// the last start rather than from a fixed epoch — otherwise a server that
    /// was just restarted by hand gets restarted again minutes later.
    /// </param>
    /// <param name="timeZone">
    /// The zone <see cref="TimeOfDay"/> is expressed in. Injected so the
    /// daylight-saving behaviour below can actually be tested; production passes
    /// <see cref="TimeZoneInfo.Local"/>.
    /// </param>
    public DateTimeOffset? NextOccurrence(
        DateTimeOffset after,
        DateTimeOffset? lastStartUtc = null,
        TimeZoneInfo? timeZone = null)
    {
        switch (Mode)
        {
            case RestartScheduleMode.EveryNHours:
            case RestartScheduleMode.EveryNMinutes:
            {
                if (Interval is not { } interval)
                {
                    return null;
                }

                var anchor = lastStartUtc ?? after;

                // A long-running instance whose anchor is many intervals back
                // must not produce a time in the past, and must not be walked
                // to forward one interval at a time either.
                if (anchor > after)
                {
                    return anchor + interval;
                }

                var elapsed = after - anchor;
                var periods = Math.Floor(elapsed / interval) + 1;
                return anchor + (interval * periods);
            }

            case RestartScheduleMode.DailyAt:
            {
                var zone = timeZone ?? TimeZoneInfo.Local;
                var localNow = TimeZoneInfo.ConvertTime(after, zone);

                for (var dayOffset = 0; dayOffset <= 2; dayOffset++)
                {
                    var date = localNow.Date.AddDays(dayOffset);
                    var candidate = ResolveLocal(date + TimeOfDay.ToTimeSpan(), zone);
                    if (candidate > after)
                    {
                        return candidate;
                    }
                }

                return null;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Turns a wall-clock local time into a real instant, coping with the two
    /// days a year when a given wall-clock time is not a single instant
    /// (<c>D18</c>, <c>docs/design.md</c> § 5.3).
    /// </summary>
    private static DateTimeOffset ResolveLocal(DateTime localWallClock, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(localWallClock, DateTimeKind.Unspecified);

        // Spring forward: the chosen time does not exist. Run at the moment the
        // gap closes rather than skipping the day — a daily restart that
        // silently does not happen is a worse surprise than one that is an hour
        // late, and the skip would only ever be noticed by its consequences.
        if (zone.IsInvalidTime(unspecified))
        {
            var probe = unspecified;
            while (zone.IsInvalidTime(probe))
            {
                probe = probe.AddMinutes(1);
            }

            return new DateTimeOffset(probe, zone.GetUtcOffset(probe));
        }

        // Fall back: the time happens twice. Take the earlier instant, so the
        // restart runs once rather than being eligible twice in one night.
        if (zone.IsAmbiguousTime(unspecified))
        {
            var offsets = zone.GetAmbiguousTimeOffsets(unspecified);
            return new DateTimeOffset(unspecified, offsets.Max());
        }

        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }
}
