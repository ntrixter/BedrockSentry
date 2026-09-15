using BedrockSentry.Core.Instances;

namespace BedrockSentry.Tests;

/// <summary>
/// What <see cref="ReservedSystemDirectories.Resolve"/> reads off <em>this</em>
/// machine, as distinct from how the rules match.
/// </summary>
/// <remarks>
/// <para><strong>Which arm runs where, said out loud so nobody assumes both did.</strong>
/// CI is <c>windows-latest</c>, so the off-Windows test below never runs there, and the
/// Windows-only ones never run on the Linux container this code is written on. Every
/// test in this file is one-sided by construction.</para>
/// </remarks>
public class ReservedSystemDirectoryResolutionTests
{
    /// <summary>
    /// Off Windows there are no rules, rather than empty rules.
    /// </summary>
    /// <remarks>
    /// The difference matters more than it looks: an empty rule path matched the
    /// obvious way refuses every path on the machine. This is the outermost of the
    /// three guards against that, and the only one a Linux run can exercise.
    /// </remarks>
    [Fact]
    public void Nothing_is_resolved_off_windows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Empty(ReservedSystemDirectories.Resolve());
        Assert.Empty(ReservedSystemDirectories.All);
    }

    [Fact]
    public void Every_resolved_rule_is_a_rooted_path_with_a_phrase()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var rules = ReservedSystemDirectories.Resolve();

        Assert.NotEmpty(rules);
        Assert.All(rules, rule =>
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Path));
            Assert.False(string.IsNullOrWhiteSpace(rule.Rule));
            Assert.True(Path.IsPathRooted(rule.Path), $"'{rule.Path}' is not rooted");
        });
    }

    /// <summary>
    /// The anti-staleness guarantee, as an assertion rather than a comment.
    /// </summary>
    /// <remarks>
    /// The decision requires these to be resolved rather than written down, because
    /// the system drive is not always <c>C:</c> and Program Files is redirectable. A
    /// literal would pass every other test in this file.
    /// </remarks>
    [Fact]
    public void The_windows_directory_is_resolved_rather_than_assumed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var rules = ReservedSystemDirectories.Resolve();

        Assert.Contains(rules, r => r.Path.Equals(windows, StringComparison.OrdinalIgnoreCase));

        // And the system-drive rules are on the drive Windows is on, not on C:.
        var root = Path.GetPathRoot(windows)!;
        Assert.All(
            rules.Where(r => r.Path.EndsWith("PerfLogs", StringComparison.OrdinalIgnoreCase)
                || r.Path.EndsWith("Windows.old", StringComparison.OrdinalIgnoreCase)),
            r => Assert.StartsWith(root, r.Path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The users container must not resolve inside the Windows directory.
    /// </summary>
    /// <remarks>
    /// This is the trap the implementation exists to avoid. Under LocalSystem,
    /// <c>SpecialFolder.UserProfile</c> is
    /// <c>C:\Windows\System32\config\systemprofile</c>, so resolving the container from
    /// it gives a rule that looks right, is already covered by the Windows rule, and
    /// leaves <c>C:\Users</c> guarded by nothing at all.
    /// </remarks>
    [Fact]
    public void The_users_container_is_not_inside_the_windows_directory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var container = ReservedSystemDirectories.Resolve()
            .FirstOrDefault(r => r.Scope == ReservedScope.FolderAndItsImmediateChildren);

        if (container is null)
        {
            // Absent is a valid answer — better than a C:\Users literal.
            return;
        }

        Assert.False(
            container.Path.StartsWith(windows, StringComparison.OrdinalIgnoreCase),
            $"the users container resolved to '{container.Path}', inside the Windows directory");
    }

    [Fact]
    public void The_two_program_files_folders_are_not_listed_twice()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = ReservedSystemDirectories.Resolve()
            .Select(r => Path.TrimEndingDirectorySeparator(r.Path))
            .ToList();

        Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <remarks>
    /// Catches a <c>DriveInfo.GetDrives()</c> creeping in later and making the rules
    /// depend on machine state that changes when a drive is attached.
    /// </remarks>
    [Fact]
    public void Resolving_twice_gives_the_same_rules()
    {
        var first = ReservedSystemDirectories.Resolve();
        var second = ReservedSystemDirectories.Resolve();

        Assert.Equal(
            first.Select(r => $"{r.Path}|{r.Scope}").ToArray(),
            second.Select(r => $"{r.Path}|{r.Scope}").ToArray());
    }
}
