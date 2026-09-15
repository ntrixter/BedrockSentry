using BedrockSentry.Core.Configuration;

namespace BedrockSentry.Tests;

/// <summary>
/// An <see cref="ISentryPaths"/> rooted at a temporary directory.
/// </summary>
/// <remarks>
/// One copy, because there were three — <c>WorldRestorerTests.Paths</c>,
/// <c>BackupSchedulingTests.Paths</c> and <c>BackupOrchestratorTests.TempPaths</c>
/// were the same fourteen mechanical lines, and a fourth was about to be written
/// for `T162`. The suite is not exempt from the rule the rest of this codebase is
/// held to: a layout stated four times is enforced once, and the one that drifts
/// is whichever was not updated when <see cref="ISentryPaths"/> grew a member.
///
/// Every path is computed rather than created. <see cref="EnsureCreated"/> makes
/// the root only, which is what the real implementation's callers rely on —
/// anything that needs a subdirectory creates it, and a test that assumes
/// otherwise is testing the stub.
/// </remarks>
public sealed class TestPaths(string root) : ISentryPaths
{
    public string DataRoot => root;
    public string Config => Path.Combine(root, "config");
    public string Certificates => Path.Combine(root, "certs");
    public string DataProtectionKeys => Path.Combine(root, "keys");
    public string Data => Path.Combine(root, "data");
    public string Backups => Path.Combine(root, "backups");
    public string Update => Path.Combine(root, "update");
    public string Logs => Path.Combine(root, "logs");
    public string InstanceLogs => Path.Combine(Logs, "instances");
    public string Instances => Path.Combine(root, "instances");
    public string ServerPackages => Path.Combine(root, "server-packages");

    public void EnsureCreated() => Directory.CreateDirectory(root);
}
