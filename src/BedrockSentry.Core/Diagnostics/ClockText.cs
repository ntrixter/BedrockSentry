using System.Globalization;

namespace BedrockSentry.Core.Diagnostics;

/// <summary>
/// A moment as the panel shows it: the twelve-hour clock, e.g. <c>2:05 PM</c>.
/// </summary>
/// <remarks>
/// <para><strong>Every time a page shows goes through here</strong>, and every one
/// is on the twelve-hour clock, which is the operator's preference (2026-09-24,
/// <c>T304</c>). <c>PanelMarkupTests</c> fails on a page that formats a time with
/// <c>HH</c> or with a culture's default pattern. The two <c>HH</c> left in the
/// markup are not times a person reads: an <c>&lt;input type="time"&gt;</c> value,
/// which HTML defines as 24-hour and the browser shows in its own locale, and the
/// stamp in a downloaded file's name.</para>
///
/// <para><strong>Callers pass a local time</strong>, apart from <see cref="Utc"/>.
/// Converting here would tie the answer to the machine's time zone in a place a
/// test cannot set, so the conversion stays at the call site, where it is
/// visible.</para>
///
/// <para><strong>The invariant culture, on purpose.</strong> A culture decides the
/// AM/PM text and the month names, so formatting with the service's own culture
/// would make the same moment read differently from one machine to the next —
/// and a culture with no designators would print a twelve-hour time with no way
/// to tell morning from evening. The panel is written in English throughout.
/// <c>ClockTextTests</c> pins this under a culture whose designators differ.</para>
/// </remarks>
public static class ClockText
{
    /// <summary>The time of day, e.g. <c>2:05 PM</c>.</summary>
    public static string Time(DateTimeOffset local) =>
        local.ToString("h:mm tt", CultureInfo.InvariantCulture);

    /// <summary>The time and the day, e.g. <c>2:05 PM on 22 Sep</c>.</summary>
    public static string TimeOnDay(DateTimeOffset local) =>
        local.ToString("h:mm tt 'on' d MMM", CultureInfo.InvariantCulture);

    /// <summary>The time of day to the second, e.g. <c>2:05:09 PM</c>.</summary>
    public static string TimeWithSeconds(DateTimeOffset local) =>
        local.ToString("h:mm:ss tt", CultureInfo.InvariantCulture);

    /// <summary>A schedule's time of day, e.g. <c>4:30 AM</c>.</summary>
    public static string TimeOfDay(TimeOnly time) =>
        time.ToString("h:mm tt", CultureInfo.InvariantCulture);

    /// <summary>The date alone, e.g. <c>2026-09-22</c>.</summary>
    public static string Date(DateTimeOffset local) =>
        local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The day in words, e.g. <c>22 Sep 2026</c>.</summary>
    public static string Day(DateTimeOffset local) =>
        local.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>The date and the time, e.g. <c>2026-09-22 2:05 PM</c>.</summary>
    public static string DateAndTime(DateTimeOffset local) => $"{Date(local)} {Time(local)}";

    /// <summary>The date and the time to the second, e.g. <c>2026-09-22 2:05:09 PM</c>.</summary>
    public static string DateAndTimeWithSeconds(DateTimeOffset local) =>
        $"{Date(local)} {TimeWithSeconds(local)}";

    /// <summary>A moment inside a sentence, e.g. <c>22 Sep 2026, 2:05 PM</c>.</summary>
    public static string DayAndTime(DateTimeOffset local) => $"{Day(local)}, {Time(local)}";

    /// <summary>
    /// A moment in UTC to the second, for a tooltip that has to be exact, e.g.
    /// <c>2026-09-22 2:05:09 PM UTC</c>.
    /// </summary>
    /// <remarks>
    /// The one member that converts, because UTC is the same on every machine.
    /// </remarks>
    public static string Utc(DateTimeOffset moment) =>
        $"{DateAndTimeWithSeconds(moment.ToUniversalTime())} UTC";
}
