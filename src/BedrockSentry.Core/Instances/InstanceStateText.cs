namespace BedrockSentry.Core.Instances;

/// <summary>
/// What the panel calls an instance's state, and the style it is shown in (<c>T456</c>).
/// </summary>
/// <remarks>
/// <para><see cref="InstanceState.Faulted"/> covers a server that gave up after failed
/// starts and one that refused to start, where nothing was launched. Code treats both
/// as "needs a human"; a reader does not, since <em>Faulted</em> beside a clean refusal
/// reads as a crash. The refusal is shown as <see cref="NotStarted"/>, in the warning
/// style, and its reason is still printed beside it.</para>
/// <para>Display only: <see cref="InstanceStatus.State"/> and
/// <see cref="InstanceStatus.FaultKind"/> are unchanged, so every rule that reads them
/// answers as before.</para>
/// </remarks>
public static class InstanceStateText
{
    /// <summary>The label for a refused start.</summary>
    public const string NotStarted = "Not started";

    /// <summary>The state's name as the panel prints it.</summary>
    public static string Label(InstanceStatus status) =>
        Refused(status) ? NotStarted : status.State.ToString();

    /// <summary>
    /// The suffix of the <c>state-</c> class the badge and the dot take:
    /// <c>backoff</c>, the warning style, for a refused start, and the state's own
    /// name in lower case otherwise.
    /// </summary>
    public static string Style(InstanceStatus status) =>
        Refused(status) ? "backoff" : status.State.ToString().ToLowerInvariant();

    private static bool Refused(InstanceStatus status) =>
        status is { State: InstanceState.Faulted, FaultKind: InstanceFault.RefusedToStart };
}
