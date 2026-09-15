using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace BedrockSentry.Services.Security;

/// <summary>
/// Locks files and directories down to SYSTEM and the local Administrators group.
/// </summary>
/// <remarks>
/// <para>
/// Used for anything holding a password hash, a private key, or the setup token.
/// </para>
/// <para>
/// The important detail is that granting access is not enough — inheritance has to
/// be broken first. <c>%ProgramData%</c> grants <c>BUILTIN\Users</c> read and
/// execute by default, and a newly created subdirectory inherits it. Calling
/// <c>AddAccessRule</c> without <c>SetAccessRuleProtection</c> therefore produces
/// an ACL that looks restrictive in code and is world-readable on disk.
/// </para>
/// <para>
/// Identities are resolved from well-known SIDs rather than names: "Administrators"
/// is "Administradores" on a Spanish install, and a name lookup would throw there.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class WindowsAcl
{
    /// <summary>Restricts a directory, and everything created inside it, to SYSTEM + Administrators.</summary>
    public static void RestrictToSystemAndAdministrators(string directoryPath)
    {
        var info = new DirectoryInfo(directoryPath);
        var security = info.GetAccessControl();

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        RemoveExistingRules(security);

        foreach (var sid in ProtectedIdentities())
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        info.SetAccessControl(security);
    }

    /// <summary>Restricts a single file to SYSTEM + Administrators.</summary>
    public static void RestrictToSystemAndAdministrators(FileInfo file)
    {
        var security = file.GetAccessControl();

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        RemoveExistingRules(security);

        foreach (var sid in ProtectedIdentities())
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        }

        file.SetAccessControl(security);
    }

    private static IEnumerable<SecurityIdentifier> ProtectedIdentities()
    {
        yield return new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        yield return new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
    }

    private static void RemoveExistingRules(FileSystemSecurity security)
    {
        // After protection is turned on the previously-inherited rules are copied
        // in as explicit ones, so they have to be cleared rather than left behind.
        var rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            targetType: typeof(SecurityIdentifier));

        foreach (FileSystemAccessRule rule in rules)
        {
            security.RemoveAccessRule(rule);
        }
    }
}
