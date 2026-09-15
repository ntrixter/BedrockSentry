using BedrockSentry.Core.Backups;

namespace BedrockSentry.Tests;

/// <summary>
/// The badge style for a <see cref="BackupSeverity"/> (`T158`).
/// </summary>
/// <remarks>
/// This mapping lived as two byte-identical copies in two <c>.razor</c> files the
/// test project cannot reach. They could not disagree yet, which is the only reason
/// it was a latent duplication rather than a defect — and it is the same shape as
/// the defect `T158` was filed about. These tests are what makes the single copy
/// stay single and stay complete.
/// </remarks>
public class BackupBadgeTests
{
    [Theory]
    [InlineData(BackupSeverity.Ok, "state-badge state-running")]
    [InlineData(BackupSeverity.Warning, "state-badge state-warning")]
    [InlineData(BackupSeverity.Failed, "state-badge state-faulted")]
    [InlineData(BackupSeverity.Skipped, "state-badge")]
    public void Each_severity_has_its_own_badge(BackupSeverity severity, string expected) =>
        Assert.Equal(expected, severity.BadgeClass());

    /// <summary>
    /// A severity added later must be styled deliberately rather than falling into
    /// the neutral default.
    /// </summary>
    /// <remarks>
    /// The default arm returns a neutral badge rather than throwing, because a render
    /// path must not fail over an unknown enum member. That is the right behaviour and
    /// it is also how a new member would ship looking like "nothing happened" — so the
    /// guard is here instead of in the switch. <see cref="BackupSeverity.Skipped"/> is
    /// the one member that legitimately wants the bare badge, because nothing happened
    /// and nothing was meant to.
    /// </remarks>
    [Fact]
    public void Only_skipped_falls_through_to_the_bare_badge()
    {
        var bare = Enum.GetValues<BackupSeverity>()
            .Where(severity => severity.BadgeClass() == "state-badge")
            .ToArray();

        Assert.Equal([BackupSeverity.Skipped], bare);
    }

    /// <summary>
    /// Every badge has to name a style the stylesheet actually defines, and the two
    /// pages that render this are not reachable from here — so the class names are
    /// pinned as strings rather than trusted.
    /// </summary>
    [Fact]
    public void Every_badge_starts_from_the_shared_state_badge_class()
    {
        Assert.All(Enum.GetValues<BackupSeverity>(), severity =>
        {
            var badge = severity.BadgeClass();
            Assert.StartsWith("state-badge", badge);
            Assert.DoesNotContain("  ", badge);
            Assert.Equal(badge.Trim(), badge);
        });
    }
}
