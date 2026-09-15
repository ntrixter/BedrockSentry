using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Diagnostics;

namespace BedrockSentry.Tests;

/// <summary>
/// The port gate's path match (<c>T175</c>).
/// </summary>
/// <remarks>
/// Every row below was observed against the real listener on build.84, where the
/// gate used <c>PathString.Equals</c>: the exact paths returned 404 and the
/// trailing-slash forms returned 200 with the per-check detail, off-box, with no
/// authentication. These are that table, turned into the guard a comment could not
/// be — the comment recommending the exact compare was present and correct-looking
/// the whole time.
/// </remarks>
public class LoopbackOnlyPathTests
{
    [Theory]
    // The exact paths. These were always refused; they are what a reviewer checks,
    // which is why the defect survived inspection.
    [InlineData("/healthz")]
    [InlineData("/readyz")]
    [InlineData("/prepare-shutdown")]
    // The forms that were served. One trailing slash is what endpoint routing
    // tolerates, so it is what the gate has to tolerate too.
    [InlineData("/healthz/")]
    [InlineData("/readyz/")]
    [InlineData("/prepare-shutdown/")]
    // Case was handled before and stays handled.
    [InlineData("/READYZ")]
    [InlineData("/ReadyZ/")]
    [InlineData("/HEALTHZ/")]
    // Routing answered /readyz// with a redirect rather than the body, so this one
    // was never exposed — it is gated anyway, because a path that cannot be served
    // costs nothing to refuse and the next framework version need not agree.
    [InlineData("/readyz//")]
    // Nothing is mapped beneath these roots and nothing should be. Gating the
    // subtree is the cheap direction: it forecloses the next variant of T175
    // instead of waiting to be surprised by it again.
    [InlineData("/readyz/detail")]
    [InlineData("/healthz/anything/deeper")]
    public void A_loopback_only_path_is_recognised(string path) =>
        Assert.True(LoopbackOnlyPaths.Matches(path), path);

    [Theory]
    // The boundary that makes this a segment match rather than a prefix match. If
    // these were gated, a future /readyzone page would 404 on the UI listener for
    // reasons nobody would find.
    [InlineData("/readyzebra")]
    [InlineData("/healthzone")]
    [InlineData("/prepare-shutdown-now")]
    // Shorter, and a prefix of a root rather than the other way round.
    [InlineData("/ready")]
    [InlineData("/health")]
    // Not rooted at all, and the ordinary pages.
    [InlineData("readyz")]
    [InlineData("/")]
    [InlineData("/instances")]
    [InlineData("")]
    [InlineData(null)]
    public void An_ordinary_path_is_not(string? path) =>
        Assert.False(LoopbackOnlyPaths.Matches(path), path ?? "<null>");

    /// <summary>
    /// The list is the enforcement, so its contents are asserted rather than
    /// assumed: a loopback-only endpoint added to <c>Program.cs</c> and not to
    /// <see cref="LoopbackOnlyPaths.All"/> is served on the listener that binds
    /// every interface, which is the whole failure this guards.
    /// </summary>
    [Fact]
    public void Every_loopback_only_path_is_in_the_list()
    {
        // ToArray is not noise: ImmutableArray<T>'s default equality is reference
        // equality on the underlying array, so comparing two of them directly
        // asserts identity and fails on contents that match exactly.
        Assert.Equal(
            new[] { UpdateOptions.HealthPath, UpdateOptions.DrainPath, HealthContract.ReadyPath },
            LoopbackOnlyPaths.All.ToArray());

        // A root that is not rooted, or carries its own trailing slash, would make
        // the segment comparison mean something other than it says.
        Assert.All(LoopbackOnlyPaths.All, root =>
        {
            Assert.StartsWith("/", root);
            Assert.DoesNotContain('/', root[1..]);
        });
    }
}
