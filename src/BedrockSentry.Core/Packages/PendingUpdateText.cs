using BedrockSentry.Core.Instances;

namespace BedrockSentry.Core.Packages;

/// <summary>
/// Where a scheduled Bedrock update that has not failed stands (<c>T407</c>).
/// </summary>
public enum PendingUpdateStage
{
    /// <summary>The window is still open; Apply now can bring it forward.</summary>
    CountingDown,

    /// <summary>
    /// The window has closed and the install has not begun: the next sweep, a
    /// download or another job's claim stands in front of it.
    /// </summary>
    Due,

    /// <summary>
    /// The installer has it: warning players, stopping, swapping the files and
    /// waiting for the new build to start. Nothing on the tab can change that.
    /// </summary>
    Installing,
}

/// <summary>
/// What the Configuration tab says and offers under a scheduled Bedrock update that
/// has not failed.
/// </summary>
/// <remarks>
/// Chosen from the pending update's own state rather than set by the button that
/// asked for it (<c>T287</c>). <strong>Apply now</strong> only brings the update's
/// moment forward and returns; the apply runs on the updater's next sweep, and can
/// fail. A sentence set by the button said <em>Applying now</em> under the failure.
/// Said from here it lasts while the update is due and unfailed, and goes when the
/// update does.
///
/// Three stages, not two (<c>T407</c>). The update stays pending until its install
/// returns, which on a running server includes the health gate's wait for the new
/// build, so the tab showed <strong>Apply now</strong> and <strong>Cancel this
/// update</strong> live for minutes over a server already running the new build.
/// </remarks>
public static class PendingUpdateText
{
    /// <param name="remaining">Time left before it is applied; zero or less is due.</param>
    /// <param name="installing">True while the updater is applying it.</param>
    public static PendingUpdateStage Stage(TimeSpan remaining, bool installing) =>
        installing ? PendingUpdateStage.Installing
        : remaining > TimeSpan.Zero ? PendingUpdateStage.CountingDown
        : PendingUpdateStage.Due;

    /// <summary>The bold line naming the build and when.</summary>
    public static string Headline(string version, PendingUpdateStage stage, TimeSpan remaining) => stage switch
    {
        PendingUpdateStage.CountingDown =>
            $"Updating to Minecraft {version} in {CountdownMessage.Describe(remaining)}",
        PendingUpdateStage.Due => $"Updating to Minecraft {version} now",
        PendingUpdateStage.Installing => $"Installing Minecraft {version}",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null),
    };

    /// <summary>What the update is doing, for one that has not failed.</summary>
    public static string Detail(PendingUpdateStage stage) => stage switch
    {
        PendingUpdateStage.CountingDown =>
            "Anyone online has been told, and is told again as the window closes. Cancelling "
            + "leaves this server where it is and does not offer this build again until "
            + "BedrockSentry restarts or a newer one appears.",
        PendingUpdateStage.Due =>
            "Applying now. The server will warn anyone online, stop and come back on the new build.",
        PendingUpdateStage.Installing =>
            "The server warns anyone online, stops and comes back on the new build, then has a "
            + "few minutes to show it runs before the old one is put back. This goes when it is done.",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null),
    };

    /// <summary>
    /// Only while the window is open. Once it has closed there is no moment left to
    /// bring forward, and a live button that does nothing reads as a broken one.
    /// </summary>
    public static bool OffersApplyNow(PendingUpdateStage stage) => stage is PendingUpdateStage.CountingDown;

    /// <summary>
    /// Until the installer has it. Calling it off then would tell players it was
    /// cancelled while the server stops under them.
    /// </summary>
    public static bool OffersCancel(PendingUpdateStage stage) => stage is not PendingUpdateStage.Installing;
}
