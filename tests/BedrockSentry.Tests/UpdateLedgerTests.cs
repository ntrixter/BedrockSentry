using BedrockSentry.Core.Updates;

namespace BedrockSentry.Tests;

/// <summary>
/// Covers the three guards that stop a failing update from looping.
/// </summary>
/// <remarks>
/// Each one exists because the others miss something. Without all three, a build that
/// crashes on startup gets downloaded, installed, health-checked, rolled back, and
/// then found again on the very next poll — several minutes of downtime per cycle,
/// indefinitely, with nothing in the UI to say why.
/// </remarks>
public sealed class UpdateLedgerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private const int MaxFailures = 3;

    [Fact]
    public void A_fresh_ledger_allows_an_attempt()
    {
        var ledger = new UpdateLedger();

        Assert.True(ledger.CanAttempt("0.3.0+abc1234", MaxFailures, Now, out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void A_build_that_already_failed_is_not_retried()
    {
        var ledger = new UpdateLedger { BlockedIdentities = ["0.3.0+abc1234"] };

        Assert.False(ledger.CanAttempt("0.3.0+abc1234", MaxFailures, Now, out var reason));
        Assert.Contains("already failed", reason);
    }

    [Fact]
    public void A_different_build_is_still_allowed_after_one_failure()
    {
        // The block list is per build, not a blanket stop — a genuine fix published
        // after a bad build must still be able to install.
        var ledger = new UpdateLedger { BlockedIdentities = ["0.3.0+abc1234"] };

        Assert.True(ledger.CanAttempt("0.3.1+def5678", MaxFailures, Now, out _));
    }

    [Fact]
    public void The_cooldown_blocks_everything_while_it_is_running()
    {
        var ledger = new UpdateLedger { CooldownUntilUtc = Now.AddHours(6) };

        Assert.False(ledger.CanAttempt("0.3.1+def5678", MaxFailures, Now, out var reason));
        Assert.Contains("Cooling down", reason);
    }

    [Fact]
    public void The_cooldown_stops_blocking_once_it_expires()
    {
        var ledger = new UpdateLedger { CooldownUntilUtc = Now.AddHours(-1) };

        Assert.True(ledger.CanAttempt("0.3.1+def5678", MaxFailures, Now, out _));
    }

    [Fact]
    public void Consecutive_failures_stop_a_stream_of_different_bad_builds()
    {
        // This is the case the per-build block list cannot catch: every identity is
        // new, so nothing is blocked, and the cooldown only delays the next attempt.
        var ledger = new UpdateLedger { ConsecutiveFailures = MaxFailures };

        Assert.False(ledger.CanAttempt("0.3.9+999aaaa", MaxFailures, Now, out var reason));
        Assert.Contains("consecutive", reason);
    }

    [Fact]
    public void An_explicit_disable_outranks_everything()
    {
        var ledger = new UpdateLedger { AutoUpdateDisabled = true };

        Assert.False(ledger.CanAttempt("0.3.1+def5678", MaxFailures, Now, out var reason));
        Assert.Contains("disabled", reason);
    }

    [Fact]
    public void Blocking_is_case_insensitive_on_the_commit()
    {
        // Git tooling is inconsistent about hex casing; the same build must not slip
        // through simply because a manifest spelled its SHA differently.
        var ledger = new UpdateLedger { BlockedIdentities = ["0.3.0+ABC1234"] };

        Assert.False(ledger.CanAttempt("0.3.0+abc1234", MaxFailures, Now, out _));
    }
}
