using System.Collections.Immutable;

namespace BedrockSentry.Core.Instances;

/// <summary>
/// When the wrapper re-checks an instance that refused to start while the service
/// was coming up, and when it stops (<c>T167</c>).
/// </summary>
/// <remarks>
/// <para><strong>The defect this exists for.</strong>
/// <c>WindowsServiceLifetime.OnStop</c> waits the host's shutdown timeout — 65
/// seconds, derived in <c>Program.cs</c> — and then reports <c>SERVICE_STOPPED</c>
/// whether the host has finished or not. So the host outlives the SCM's belief
/// that it stopped, measured on a real box at between 0.03 and 8.8 seconds. A
/// back-to-back restart then starts a new service while the old one's
/// <c>bedrock_server.exe</c> children are still saving, every auto-start is
/// correctly refused because something is already on the world, and the box comes
/// back with **nothing running**. Before this, nothing re-checked: the auto-start
/// loop in <c>InstanceHostedService</c> is a single pass, so a refusal there was
/// permanent for the lifetime of the process.</para>
///
/// <para><strong>Why re-checking is safe, which is the whole argument.</strong>
/// The refusal is produced by a guard that runs <em>before</em> anything is
/// launched. A re-check that still finds the conflict refuses again and costs one
/// log line; it cannot open a world twice. That is what makes this different from
/// retrying a <see cref="InstanceFault.GaveUp"/> fault, where the thing being
/// repeated is a launch that has already proven it does not work — the crash loop
/// the give-up exists to end. <see cref="StillWorthRetrying"/> is the line between
/// them, and it reads <see cref="InstanceStatus.FaultKind"/> rather than the
/// reason sentence on purpose.</para>
///
/// <para><strong>Why it is bounded.</strong> An instance whose port is held by
/// something permanent — another server, a service that is not ours — must end up
/// faulted and quiet, not logging an error every thirty seconds forever. The
/// schedule covers the measured overlap with room to spare and then stops, leaving
/// the fault exactly as it found it.</para>
///
/// <para>Here in <c>Core</c> rather than beside the loop that runs it, so the
/// schedule and the eligibility rule are both reachable from a test. The loop
/// itself is in <c>InstanceHostedService</c>, which the test project can also
/// reach; what it cannot reach is a real service restart, which is what the
/// <c>T167</c> clauses are for.</para>
/// </remarks>
public static class RefusedStartRetry
{
    /// <summary>
    /// How long to wait before each re-check, in order. Four attempts, the last
    /// two minutes after the auto-start pass.
    /// </summary>
    /// <remarks>
    /// The first delay alone covers the whole measured range of a predecessor
    /// outliving its own <c>SERVICE_STOPPED</c> (0.03 s to 8.8 s), so the common
    /// case is expected to clear on attempt one. The rest are for a slower box or
    /// a server mid-save: generous, because a re-check is cheap, and finite,
    /// because a conflict that is not transient has to be allowed to settle into a
    /// fault somebody reads.
    /// </remarks>
    public static readonly ImmutableArray<TimeSpan> Schedule =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    ];

    /// <summary>How long the whole schedule spans, for the messages.</summary>
    public static TimeSpan Window
    {
        get
        {
            var total = TimeSpan.Zero;
            foreach (var delay in Schedule)
            {
                total += delay;
            }

            return total;
        }
    }

    /// <summary>
    /// Whether <paramref name="status"/> is still the refusal this retry was
    /// started for.
    /// </summary>
    /// <remarks>
    /// <para>Asked again before <em>every</em> attempt, not only at the start, and
    /// that re-asking is the point. Anything that moves the instance out of a
    /// refused fault is a decision this must not override:</para>
    /// <list type="bullet">
    /// <item>somebody pressed <strong>Start</strong> and it worked — the state is
    /// no longer <c>Faulted</c>;</item>
    /// <item>somebody pressed <strong>Stop</strong> on the faulted instance, which
    /// moves it to <c>Stopped</c> — read as "I know, leave it down". <strong>That
    /// gesture was unreachable until <c>T184</c></strong>: <c>InstanceControls</c>
    /// disabled Stop while the state was <c>Faulted</c>, and it is the only Stop
    /// affordance in the app, so this rule described something nobody could do and
    /// the only cancel inside the window was the auto-start toggle. The button is
    /// enabled on <c>Faulted</c> now, which is what makes the sentence true;</item>
    /// <item>it crashed its way to a give-up in the meantime, so the fault kind
    /// has changed and a relaunch is exactly what must not happen;</item>
    /// <item>it was retired (<c>D14</c>) or had auto-start turned off while the
    /// schedule was running.</item>
    /// </list>
    /// </remarks>
    public static bool StillWorthRetrying(InstanceStatus status, bool autoStart, bool retired) =>
        autoStart
        && !retired
        && status is { State: InstanceState.Faulted, FaultKind: InstanceFault.RefusedToStart };

    /// <summary>
    /// What to say when the schedule runs out and the instance is still refused.
    /// </summary>
    /// <remarks>
    /// It has to name the count and the span, because the operator's question at
    /// that point is whether anything tried at all — the whole defect was a box
    /// that looked healthy with nothing on it. The reason itself is logged with
    /// this, and is left on the instance unchanged: it is still true, and it names
    /// the process holding the world.
    /// </remarks>
    public static string GaveUpMessage(int attempts, TimeSpan window) =>
        $"still refusing to start after {attempts} re-check(s) over "
        + $"{(int)Math.Round(window.TotalMinutes)} minute(s); it will stay down until somebody "
        + "clears what is holding it and starts it";
}
