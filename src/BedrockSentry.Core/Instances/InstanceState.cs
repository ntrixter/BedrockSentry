namespace BedrockSentry.Core.Instances;

/// <summary>
/// Runtime state of one managed instance.
///
/// <see cref="Backoff"/> and <see cref="Faulted"/> are separate on purpose:
/// "crashed and will try again in 40 seconds" and "crashed three times and has
/// given up" are the same to a naive status field, and they call for opposite
/// responses from whoever is looking at the page.
///
/// Retirement is not here. A retired instance (<c>D14</c>) is a property of its
/// definition, not a state its process can be in — it has no process.
/// </summary>
public enum InstanceState
{
    /// <summary>Not running, and not trying to.</summary>
    Stopped,

    /// <summary>Process launched; waiting for BDS to report <c>Server started.</c></summary>
    Starting,

    /// <summary>Up and accepting commands.</summary>
    Running,

    /// <summary>A graceful stop is in progress.</summary>
    Stopping,

    /// <summary>Exited unexpectedly; a restart is scheduled. See <see cref="InstanceStatus.NextRetryUtc"/>.</summary>
    Backoff,

    /// <summary>
    /// Not running and will not try again on its own. Either it gave up after too
    /// many consecutive failed starts (<c>D15</c>), or it refused to start at all
    /// because another process already holds its port (<c>T149</c>) — the
    /// second is deliberately the same state, because every surface already
    /// treats this one as "needs a human", and that is what both mean.
    /// <see cref="InstanceStatus.FaultReason"/> says which to a reader and
    /// <see cref="InstanceStatus.FaultKind"/> says which to code.
    /// </summary>
    Faulted,
}

/// <summary>
/// Which kind of fault an instance is in, while its state is
/// <see cref="InstanceState.Faulted"/>.
/// </summary>
/// <remarks>
/// <para>The state above is deliberately shared between the two, because every
/// surface treats both as "needs a human". What was missing is something a
/// <em>machine</em> can decide on. Until <c>T167</c> the only thing that said
/// which kind it was is <see cref="InstanceStatus.FaultReason"/> — a sentence
/// written for a person to read — and the boot retry has to re-check a refusal
/// and must never re-check a give-up. Deciding that by matching on prose is how
/// a reworded sentence silently turns one into the other.</para>
///
/// <para><c>T176</c>'s rule pointing the other way: a field with too few states
/// gets read by pattern-matching the field beside it.</para>
///
/// <para><strong>Not a reason to rewrite the two callers that read
/// <c>Faulted</c> plus any reason</strong> immediately after <c>StartAsync</c>
/// returns (<c>BdsUpdateInstaller</c>, <c>WorldRestorer</c>). Those are asking
/// <em>did it start</em>, which the state alone answers correctly; moving them
/// onto this question would be <c>T151</c> clause 6 again — two callers sharing
/// an answer only one of them wanted.</para>
/// </remarks>
public enum InstanceFault
{
    /// <summary>Not faulted. The reason is absent too.</summary>
    None,

    /// <summary>
    /// Gave up after too many consecutive failed starts (<c>D15</c>). Something
    /// about this server does not work, and trying again on a timer is the crash
    /// loop the give-up exists to end.
    /// </summary>
    GaveUp,

    /// <summary>
    /// Refused to start, so nothing was launched: a process is already on this
    /// world, or something holds the port (<c>T149</c>, <c>T151</c>).
    /// </summary>
    /// <remarks>
    /// This one is worth re-checking, and the reason is structural rather than
    /// optimistic: the guard runs <em>before</em> anything is launched, so a
    /// re-check that still finds the conflict refuses again and costs a log line.
    /// Re-checking is free; only launching into a conflict is destructive. The
    /// commonest holder is the wrapper's own predecessor, still draining
    /// (<c>T167</c>), and it is gone within seconds.
    /// </remarks>
    RefusedToStart,
}

/// <summary>
/// An immutable snapshot of one instance, safe to hand to the UI.
/// </summary>
/// <param name="StartedUtc">When the process was launched, not when it became ready.</param>
/// <param name="ReadyUtc">
/// When BDS reported <c>Server started.</c> Distinct from
/// <paramref name="StartedUtc"/> because the gap between them is the honest
/// answer to "is it up yet", and the spike measured it at 1.6 s on an existing
/// world — long enough for a status page to be wrong about.
/// </param>
/// <param name="ConsecutiveFailures">
/// Reset by a start that stays up, not by a start that merely happens; a server
/// that crashes two seconds in must not reset its own give-up counter.
/// </param>
/// <param name="FaultReason">
/// Why it is <see cref="InstanceState.Faulted"/>: either why it gave up, or why it
/// refused to start. Written on entering that state and cleared by the next launch
/// that actually happens; a stop in between carries it along unread, so every
/// renderer shows it only while the state is <c>Faulted</c>.
/// </param>
/// <param name="FaultKind">
/// Which of those two it is, for code rather than for a reader. Moves with
/// <paramref name="FaultReason"/> and must never move without it — the invariant
/// is that exactly one of "both set" and "both absent" holds, which is what stops
/// a sentence and a decision drifting apart.
/// </param>
/// <param name="ServerVersion">
/// The version BDS announced at startup. Read from the server's own output
/// rather than from the package it was installed from — those disagree the
/// moment anyone updates the files by hand, and the running server is the one
/// telling the truth.
/// </param>
/// <param name="ClaimedFor">
/// The job that alone may start and stop the server now, such as a sync or an
/// update, as <see cref="ClaimText"/> names it; null when nothing claims it.
/// </param>
public sealed record InstanceStatus(
    string InstanceId,
    string Name,
    InstanceState State,
    int? ProcessId,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? ReadyUtc,
    int? LastExitCode,
    DateTimeOffset? LastExitUtc,
    int ConsecutiveFailures,
    DateTimeOffset? NextRetryUtc,
    string? FaultReason,
    string? ServerVersion = null,
    InstanceFault FaultKind = InstanceFault.None,
    string? ClaimedFor = null)
{
    /// <summary>
    /// How long the process has been up, or null if it is not. Derived rather
    /// than stored, the same rule player playtime follows: a counter that is
    /// written down drifts the first time an update is missed.
    /// </summary>
    public TimeSpan? UptimeAt(DateTimeOffset now) =>
        StartedUtc is { } started && State is InstanceState.Starting or InstanceState.Running or InstanceState.Stopping
            ? now - started
            : null;

    public static InstanceStatus Stopped(string instanceId, string name) =>
        new(instanceId, name, InstanceState.Stopped, null, null, null, null, null, 0, null, null);
}
