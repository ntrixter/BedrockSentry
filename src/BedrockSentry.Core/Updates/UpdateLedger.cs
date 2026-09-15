using System.Text.Json.Serialization;

namespace BedrockSentry.Core.Updates;

/// <summary>
/// Persistent record of what the wrapper has tried to install and how it went.
/// </summary>
/// <remarks>
/// <para>
/// This is what stops an update loop. A build that fails its health check gets rolled
/// back, and without a record of that the very next check would see the same newer
/// build on the channel and try again — indefinitely, several minutes of downtime per
/// cycle.
/// </para>
/// <para>
/// Three layers, because each catches something the others miss.
/// <see cref="BlockedIdentities"/> stops retrying one known-bad build.
/// <see cref="CooldownUntilUtc"/> stops thrashing immediately after any rollback.
/// <see cref="ConsecutiveFailures"/> catches a CI stream shipping several bad builds
/// in a row, which a per-build block list alone would happily follow off a cliff.
/// </para>
/// <para>
/// Underneath all three sits a structural safety net: downloading and staging happen
/// in the old, working build, so a build that cannot start is never in a position to
/// trigger the next update.
/// </para>
/// </remarks>
public sealed class UpdateLedger
{
    /// <summary>Build identities that failed and must not be retried.</summary>
    [JsonPropertyName("blockedIdentities")]
    public List<string> BlockedIdentities { get; set; } = [];

    /// <summary>No update is attempted before this time.</summary>
    [JsonPropertyName("cooldownUntilUtc")]
    public DateTimeOffset? CooldownUntilUtc { get; set; }

    [JsonPropertyName("consecutiveFailures")]
    public int ConsecutiveFailures { get; set; }

    /// <summary>Set once too many updates have failed in a row; requires a human.</summary>
    [JsonPropertyName("autoUpdateDisabled")]
    public bool AutoUpdateDisabled { get; set; }

    [JsonPropertyName("lastAttemptedIdentity")]
    public string? LastAttemptedIdentity { get; set; }

    [JsonPropertyName("lastAttemptUtc")]
    public DateTimeOffset? LastAttemptUtc { get; set; }

    /// <summary>
    /// Why the last update failed, in words an operator can act on.
    /// </summary>
    /// <remarks>
    /// Surfaced on the settings page. Without it a rolled-back update looks exactly
    /// like an update that never happened, which is the hardest kind of problem to
    /// notice.
    /// </remarks>
    [JsonPropertyName("lastFailureReason")]
    public string? LastFailureReason { get; set; }

    [JsonPropertyName("lastSuccessIdentity")]
    public string? LastSuccessIdentity { get; set; }

    [JsonPropertyName("lastSuccessUtc")]
    public DateTimeOffset? LastSuccessUtc { get; set; }

    /// <summary>Whether an update to <paramref name="identity"/> may be attempted now.</summary>
    public bool CanAttempt(string identity, int maxConsecutiveFailures, DateTimeOffset now, out string? reason)
    {
        if (AutoUpdateDisabled)
        {
            reason = $"Automatic updates were disabled after {ConsecutiveFailures} consecutive failures.";
            return false;
        }

        if (ConsecutiveFailures >= maxConsecutiveFailures)
        {
            reason = $"{ConsecutiveFailures} consecutive update failures.";
            return false;
        }

        if (BlockedIdentities.Contains(identity, StringComparer.OrdinalIgnoreCase))
        {
            reason = $"Build {identity} already failed on this machine.";
            return false;
        }

        if (CooldownUntilUtc is { } until && now < until)
        {
            reason = $"Cooling down after a failed update until {until:u}.";
            return false;
        }

        reason = null;
        return true;
    }
}
