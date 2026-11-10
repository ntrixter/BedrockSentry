using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BedrockSentry.Services.Security;

/// <summary>
/// Turns on Windows' RedirectionGuard for this process: it then refuses to follow a
/// junction that an account other than an administrator created.
/// </summary>
/// <remarks>
/// <para>Each server runs at low integrity and can write only its own folder
/// (milestone 18.5, <c>D126</c>), and so can make a junction there. This service reads
/// and writes inside that folder with more rights than the server has: a restore, an
/// add-on copy, <c>server.properties</c>, the allowlist. A junction the server made
/// would carry any of those to a file of the service's own, the credential or the
/// certificate. With the guard on, Windows refuses the open instead, for every path
/// the service opens, so no caller can forget to check. A symbolic link needs a
/// privilege the server's token lacks, and a hard link needs write access to the
/// file it names.</para>
/// <para>A junction an administrator made, such as the install's <c>current</c>, is
/// still followed. One a non-administrator made on the way to a server's folder is
/// not, and that server's folder is then named by its full path instead.</para>
/// <para>Not on every Windows build. Where it is missing the call fails and the
/// reason is logged; the restore and the add-on copy still check their own trees
/// (<see cref="Backups.ServerTree"/>).</para>
/// </remarks>
public static class RedirectionGuard
{
    /// <summary>Turns the guard on; null when it is on, or why it is not.</summary>
    [SupportedOSPlatform("windows")]
    public static string? Enforce()
    {
        var policy = EnforceRedirectionTrust;

        return SetProcessMitigationPolicy(ProcessRedirectionTrustPolicy, ref policy, sizeof(uint))
            ? null
            : new Win32Exception(Marshal.GetLastWin32Error()).Message;
    }

    /// <summary>The sentence logged when <see cref="Enforce"/> could not turn it on.</summary>
    public static string NotOn(string reason) =>
        $"RedirectionGuard could not be turned on ({reason}), so this process follows a junction "
        + "an account other than an administrator made. The restore and the add-on copy still check "
        + "a server's folder for one themselves. Older Windows builds lack it.";

    private const int ProcessRedirectionTrustPolicy = 16;
    private const uint EnforceRedirectionTrust = 0x1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessMitigationPolicy(int policy, ref uint buffer, nuint length);
}
