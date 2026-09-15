using System.Security.AccessControl;
using System.Security.Principal;
using BedrockSentry.Services.Configuration;

namespace BedrockSentry.Tests;

/// <summary>
/// Guards the rule that every mutable path hangs off one root.
/// </summary>
/// <remarks>
/// Milestone 3.5 replaces the install directory wholesale on every self-update. Any
/// component that computes its own directory instead of asking for one here is a
/// data-loss bug waiting for the second update — so the properties being derived
/// from <see cref="ISentryPaths.DataRoot"/>, and only from it, is worth asserting.
/// </remarks>
public sealed class SentryPathsTests
{
    [Fact]
    public void Every_path_lives_under_the_data_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "bedrocksentry-paths-test");
        var paths = new SentryPaths(root);

        var all = new[]
        {
            paths.Config,
            paths.Certificates,
            paths.DataProtectionKeys,
            paths.Data,
            paths.Update,
            paths.Logs,
        };

        Assert.All(all, path => Assert.StartsWith(paths.DataRoot, path, StringComparison.Ordinal));
    }

    [Fact]
    public void Paths_are_distinct()
    {
        var paths = new SentryPaths(Path.Combine(Path.GetTempPath(), "bedrocksentry-distinct-test"));

        var all = new[]
        {
            paths.Config,
            paths.Certificates,
            paths.DataProtectionKeys,
            paths.Data,
            paths.Update,
            paths.Logs,
        };

        Assert.Equal(all.Length, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void An_explicit_root_is_normalised_to_an_absolute_path()
    {
        var paths = new SentryPaths(Path.Combine(Path.GetTempPath(), "a", "..", "b"));

        Assert.True(Path.IsPathFullyQualified(paths.DataRoot));
        Assert.DoesNotContain("..", paths.DataRoot, StringComparison.Ordinal);
    }

    [ElevatedFact]
    public void EnsureCreated_creates_every_directory()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"bedrocksentry-ensure-{Guid.NewGuid():N}");

        try
        {
            var paths = new SentryPaths(root);
            paths.EnsureCreated();

            Assert.True(Directory.Exists(paths.Config));
            Assert.True(Directory.Exists(paths.Certificates));
            Assert.True(Directory.Exists(paths.DataProtectionKeys));
            Assert.True(Directory.Exists(paths.Data));
            Assert.True(Directory.Exists(paths.Update));
            Assert.True(Directory.Exists(paths.Logs));
        }
        finally
        {
            DeleteAfterEnsureCreated(root);
        }
    }

    [ElevatedFact]
    public void EnsureCreated_is_safe_to_call_twice()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"bedrocksentry-twice-{Guid.NewGuid():N}");

        try
        {
            var paths = new SentryPaths(root);
            paths.EnsureCreated();
            paths.EnsureCreated();

            Assert.True(Directory.Exists(paths.Config));
        }
        finally
        {
            DeleteAfterEnsureCreated(root);
        }
    }

    /// <summary>A fact that reports itself skipped rather than failed without elevation.</summary>
    /// <remarks>
    /// <see cref="SentryPaths.EnsureCreated"/> locks the data root to SYSTEM and
    /// Administrators and only then creates the directories underneath it, so the call
    /// needs the privileges it just wrote in. Owning the root is not enough: an owner
    /// keeps <c>READ_CONTROL</c> and <c>WRITE_DAC</c> whatever the DACL says, but not
    /// the <c>FILE_ADD_SUBDIRECTORY</c> that creating the children takes.
    /// <para>
    /// Linux skips the hardening altogether, which is why CI has never had an opinion
    /// about this and why the condition is Windows-only. The service runs as
    /// LocalSystem, where the privilege always holds — so an unelevated developer run
    /// is the only place this bites, and it should say so rather than fail.
    /// </para>
    /// </remarks>
    private sealed class ElevatedFactAttribute : FactAttribute
    {
        public ElevatedFactAttribute()
        {
            if (OperatingSystem.IsWindows()
                && !new WindowsPrincipal(WindowsIdentity.GetCurrent())
                    .IsInRole(WindowsBuiltInRole.Administrator))
            {
                Skip = "EnsureCreated locks the data root down before it creates "
                    + "anything underneath, so this needs an elevated run on Windows.";
            }
        }
    }

    /// <summary>
    /// Removes a root that <see cref="SentryPaths.EnsureCreated"/> has locked down.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On Windows <c>EnsureCreated</c> restricts the root to SYSTEM and the local
    /// Administrators group and breaks inheritance — which is the entire point of it,
    /// and which leaves a non-elevated test run unable to delete the directory it just
    /// created. Linux skips that branch, so CI never saw this.
    /// </para>
    /// <para>
    /// The test process owns the directory, and an owner keeps <c>WRITE_DAC</c>
    /// whatever the DACL says, so it can grant itself back in without elevation. The
    /// write has to name <see cref="AccessControlSections.Access"/> explicitly though:
    /// carrying the SACL along needs <c>SeSecurityPrivilege</c>, which an ordinary user
    /// does not hold, and the call then fails on the audit section rather than on the
    /// permissions it was actually trying to change.
    /// </para>
    /// </remarks>
    private static void DeleteAfterEnsureCreated(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            var info = new DirectoryInfo(root);
            var security = new DirectorySecurity(root, AccessControlSections.Access);

            // Inheritable, so the directories EnsureCreated made underneath the
            // locked-down root come back within reach too.
            security.AddAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            info.SetAccessControl(security);
        }

        Directory.Delete(root, recursive: true);
    }
}
