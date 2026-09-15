namespace BedrockSentry.Core.Instances;

/// <summary>
/// The retry sequence a <see cref="WatchdogOptions"/> actually produces, in
/// words, for the Configuration tab (<c>T140</c>).
/// </summary>
/// <remarks>
/// Lives in <c>Core</c> rather than in the page for the reason <c>T126</c> and
/// <c>T136</c> both record: the test project does not reference the host, so a
/// rule left in a <c>.razor</c> file is verified on a real page or not at all.
/// This one was wrong in two independent ways and neither was reachable by a
/// test.
///
/// <para><b>It used to round like a player announcement.</b> The ladder called
/// <see cref="CountdownMessage.Describe"/>, which collapses anything at or above
/// a minute to whole minutes — right for a <c>say</c> line to a player, wrong
/// for an engineering value the console prints exactly. A real 135-second rung
/// displayed as "2 minutes" while the console said <c>retrying in 135s</c>, and
/// on the shipped defaults a 4m30s wait displayed as "4 minutes", understating
/// it by half a minute. One helper cannot serve both audiences, so this one
/// formats exactly and <see cref="CountdownMessage"/> keeps rounding.</para>
///
/// <para><b>It used to advertise rungs that cannot happen.</b> The page always
/// printed four. But the supervisor's give-up branch returns *before*
/// <see cref="WatchdogOptions.BackoffFor"/> is called, so with <b>give up
/// after</b> = N only retries 1..N-1 ever occur — at the shipped default of 3
/// the panel promised two waits that were unreachable.</para>
/// </remarks>
public static class WatchdogLadder
{
    /// <summary>How many rungs to spell out before summarising the rest.</summary>
    private const int Shown = 4;

    /// <summary>
    /// The whole claim as one sentence, lead-in included — so the degenerate
    /// cases can contradict the normal one. A page that supplies "it is
    /// restarted after..." itself cannot then render "it is never restarted".
    /// </summary>
    public static string Describe(WatchdogOptions watchdog)
    {
        // Zero or less does NOT disable the watchdog — it removes the limit
        // (`T145`). `InstanceSupervisor` gates its fault branch on
        // `MaxConsecutiveFailures > 0`, so at zero the branch never fires and the
        // instance is retried for as long as it keeps failing.
        //
        // This said the opposite until 2026-09-04, and said it three lines from
        // the page's own help text, which had it right. It was written from
        // `WatchdogOptions.MaxConsecutiveFailures`'s doc comment — which also said
        // "zero disables the watchdog entirely" and was itself wrong. The comment
        // is corrected too; getting only this line right would have left the next
        // reader to make the same mistake from the same source.
        var unlimited = watchdog.MaxConsecutiveFailures <= 0;

        // Counts *failures*, and the last one faults rather than retrying, so the
        // number of retries is one fewer — unless there is no limit at all.
        var retries = unlimited ? int.MaxValue : watchdog.MaxConsecutiveFailures - 1;

        if (retries <= 0)
        {
            return "It gives up on the first failure, so it is never restarted.";
        }

        var rungs = new List<string>();
        var ceilingReached = false;

        for (var failure = 1; failure <= retries && rungs.Count < Shown; failure++)
        {
            var backoff = watchdog.BackoffFor(failure);
            rungs.Add(Exactly(backoff));

            // Every rung past the ceiling is the same wait, so listing them
            // would pad the sentence with a repeated number.
            if (backoff >= watchdog.MaxBackoff)
            {
                ceilingReached = true;
                break;
            }
        }

        var sentence = "It is restarted after " + string.Join(", then ", rungs);
        var remaining = retries - rungs.Count;

        // Said plainly rather than left to be inferred from a missing limit. An
        // operator who set zero meaning "leave it down" has done the opposite,
        // and the sentence is the only place that will tell them.
        if (unlimited)
        {
            return $"{sentence}, and {Exactly(watchdog.MaxBackoff)} for every retry "
                + "after that. It never gives up: a server that cannot start is "
                + "restarted for as long as it keeps failing.";
        }

        if (remaining <= 0)
        {
            return sentence + ".";
        }

        return ceilingReached
            ? $"{sentence}, and {Exactly(watchdog.MaxBackoff)} for each retry after that."
            : $"{sentence}, and so on up to {Exactly(watchdog.MaxBackoff)}.";
    }

    /// <summary>
    /// The one-line form for the Status card's <b>Watchdog</b> field (<c>T145</c>).
    /// </summary>
    /// <remarks>
    /// Added because the first <c>T145</c> fix moved the rule into
    /// <see cref="Describe"/> and left <c>StatusTab.razor</c> rendering its own
    /// copy — "off" at zero — one tab away from the Configuration tab saying "it
    /// never gives up". The fifth instance of the rule-stated-twice class, and the
    /// sharpest: the fix for the fourth reproduced it, because the caller the test
    /// project cannot see kept the old sentence. Every surface that says what
    /// zero means now reads it from here.
    /// </remarks>
    public static string Summary(WatchdogOptions watchdog) => watchdog.MaxConsecutiveFailures switch
    {
        <= 0 => "never gives up",
        1 => "gives up on the first failure",
        var limit => $"gives up after {limit} failures",
    };

    /// <summary>
    /// The console's <c>(failure 2 of 3)</c> tag on an unexpected exit. At no
    /// limit the old template read <c>failure 1 of 0</c>, which is the same wrong
    /// claim as "off" in a different font.
    /// </summary>
    public static string FailureCount(int failure, WatchdogOptions watchdog) =>
        watchdog.MaxConsecutiveFailures <= 0
            ? $"failure {failure}, no limit"
            : $"failure {failure} of {watchdog.MaxConsecutiveFailures}";

    /// <summary>
    /// A duration the way the console prints it, not the way a countdown
    /// announces it: exact, with no component rounded away.
    /// </summary>
    private static string Exactly(TimeSpan value)
    {
        var total = (long)Math.Round(Math.Max(0, value.TotalSeconds));

        if (total == 0)
        {
            return "no wait";
        }

        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var seconds = total % 60;

        var parts = new List<string>(3);

        if (hours > 0)
        {
            parts.Add(Plural(hours, "hour"));
        }

        if (minutes > 0)
        {
            parts.Add(Plural(minutes, "minute"));
        }

        if (seconds > 0)
        {
            parts.Add(Plural(seconds, "second"));
        }

        return string.Join(' ', parts);
    }

    private static string Plural(long count, string unit) =>
        count == 1 ? $"1 {unit}" : $"{count} {unit}s";
}
