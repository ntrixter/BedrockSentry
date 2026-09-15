namespace BedrockSentry.Core.Packages;

/// <summary>
/// The notices players get between a Bedrock build appearing and this server
/// taking it (<c>docs/design.md</c> § 11.3).
/// </summary>
/// <remarks>
/// Derived from the delay rather than configured beside it. A separate list of
/// warning points would let somebody set a five-minute delay with a
/// fifteen-minute warning in it, which cannot be honoured and would have to be
/// silently dropped — and the countdown for a scheduled restart is already a
/// second place where exactly that can be misconfigured. One number to set, and
/// the notices follow from it.
/// </remarks>
public static class BdsUpdateCountdown
{
    /// <summary>
    /// When to speak up, as time remaining, longest first.
    /// </summary>
    /// <remarks>
    /// The shared ladder — a Bedrock build's window and a wrapper update's are
    /// the same kind of countdown, and two copies would drift the first time
    /// somebody tuned one. Deliberately without
    /// <see cref="Instances.CountdownLadder.LastCall"/>: this countdown is
    /// spoken from a sweep that runs every fifteen seconds, so a ten-second
    /// point would be missed or announced with five seconds left.
    /// </remarks>
    public static IReadOnlyList<TimeSpan> PointsFor(TimeSpan delay) =>
        Instances.CountdownLadder.PointsFor(delay);

    /// <summary>The <c>say</c> line for one point in the countdown.</summary>
    /// <remarks>
    /// Names the version. "Server restarting in 15 minutes" is what a scheduled
    /// restart says and is all it can say; here the wrapper knows why, and a
    /// player who has seen the same line three times is better served by one
    /// that says what is actually changing.
    /// </remarks>
    public static string Notice(string version, TimeSpan remaining) =>
        $"say Server updating to Minecraft {version} in {Instances.CountdownMessage.Describe(remaining)}";

    /// <summary>The line for an update that was called off.</summary>
    public static string Cancelled(string version) =>
        $"say The update to Minecraft {version} has been called off";
}
