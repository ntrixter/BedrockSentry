using BedrockSentry.Core.Configuration;

namespace BedrockSentry.Core.Updates;

/// <summary>How far <see cref="IWrapperUpdateInstaller.StageAsync"/> got.</summary>
public enum UpdateStagingState
{
    /// <summary>The build is staged, the plan written and the helper ready to launch.</summary>
    Staged,

    /// <summary>The build could not be downloaded or verified; a failure for the ledger.</summary>
    NotDownloaded,

    /// <summary>The build is staged but the install cannot hand off to it; nothing to retry.</summary>
    NotReady,
}

/// <summary>What staging a wrapper update produced.</summary>
/// <param name="Reason">Empty when <see cref="State"/> is <see cref="UpdateStagingState.Staged"/>.</param>
/// <param name="HealthTimeoutSeconds">How long the helper waits for the new build to report healthy.</param>
public sealed record UpdateStaging(UpdateStagingState State, string Reason, int HealthTimeoutSeconds = 0);

/// <summary>
/// The parts of a wrapper self-update that touch the install tree: staging a build
/// beside the running one and launching the helper that swaps them.
/// </summary>
/// <remarks>
/// The <c>D46</c> shape. <c>UpdateService</c> decides whether and when to update and
/// drains the servers between the two calls; it does not know whether the staging
/// and the launch run in this process or in the privilege broker (milestone 18.5).
/// </remarks>
public interface IWrapperUpdateInstaller
{
    /// <summary>
    /// Why this install cannot update itself through this installer, or null when it can.
    /// </summary>
    /// <remarks>
    /// Separate from a non-versioned install, which has nowhere to put a build: this is
    /// an install that has the place but not the right to change it.
    /// </remarks>
    string? Unavailable { get; }

    /// <summary>Where the helper leaves the outcome for the service to read at its next start.</summary>
    string ResultPath { get; }

    /// <summary>
    /// The feed this installer stages from, when it is not the configured
    /// <see cref="UpdateOptions.FeedUrlTemplate"/>; null for the configured one.
    /// </summary>
    /// <remarks>
    /// The service checks the same feed the build will be staged from, or it would
    /// offer builds the installer then refuses as not on offer.
    /// </remarks>
    string? FeedUrlTemplate => null;

    /// <summary>
    /// Makes a copy of the updater helper that can run while the install tree is
    /// replaced. Called once at start; never throws.
    /// </summary>
    void PrepareHelper();

    /// <summary>
    /// Downloads, verifies and unpacks <paramref name="manifest"/>'s build and writes
    /// the plan the helper follows. Stops no server.
    /// </summary>
    Task<UpdateStaging> StageAsync(UpdateManifest manifest, UpdateOptions options, CancellationToken ct = default);

    /// <summary>
    /// Launches the helper for <paramref name="identity"/>, which must be the build
    /// <see cref="StageAsync"/> staged last.
    /// </summary>
    /// <returns>The helper's process id.</returns>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="identity"/> is not the build staged last.
    /// </exception>
    /// <exception cref="Exception">The helper could not be started.</exception>
    Task<int> LaunchAsync(string identity, CancellationToken ct = default);
}
