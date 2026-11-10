using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace BedrockSentry.Services.Security;

/// <summary>
/// Locks files and directories down to SYSTEM, the local Administrators group and,
/// where <see cref="AccessPlan"/> says so, the account the main service runs as.
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

    /// <summary>
    /// Writes <paramref name="planned"/> to a directory as its whole, protected list;
    /// inheritable entries reach what already exists beneath it, except where
    /// inheritance is broken.
    /// </summary>
    public static void Apply(DirectoryInfo directory, IReadOnlyList<AccessEntry> planned)
    {
        var security = directory.GetAccessControl();
        Replace(security, planned);
        directory.SetAccessControl(security);
    }

    /// <summary>Writes <paramref name="planned"/> to a file as its whole, protected list.</summary>
    public static void Apply(FileInfo file, IReadOnlyList<AccessEntry> planned)
    {
        var security = file.GetAccessControl();
        Replace(security, planned);
        file.SetAccessControl(security);
    }

    /// <summary>
    /// Restricts a secret file to LocalSystem, Administrators and the account this
    /// process runs as, unless it already carries exactly that list.
    /// </summary>
    /// <remarks>
    /// The check comes first because the account cannot rewrite the list of a file it
    /// does not own (<see cref="AccessPlan.Matches"/>).
    /// </remarks>
    public static void SecureSecretFile(string path)
    {
        var file = new FileInfo(path);
        using var identity = WindowsIdentity.GetCurrent();
        var planned = AccessPlan.ForSecretFile(identity.User!.Value);
        var (isProtected, current) = Read(file);

        if (!AccessPlan.Matches(isProtected, current, planned))
        {
            Apply(file, planned);
        }
    }

    /// <summary>A folder's owner and list as <see cref="AccessPlan"/> reads them.</summary>
    public static DirectoryList Read(DirectoryInfo directory)
    {
        var security = directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);

        return new DirectoryList(
            security.GetOwner(typeof(SecurityIdentifier))?.Value,
            security.AreAccessRulesProtected,
            Rules(security));
    }

    private static List<AccessRuleRead> Rules(FileSystemSecurity security) =>
        security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => new AccessRuleRead(
                rule.IdentityReference.Value,
                rule.FileSystemRights,
                rule.AccessControlType == AccessControlType.Allow,
                rule.IsInherited,
                AccessPlan.ScopeOf(rule.InheritanceFlags, rule.PropagationFlags)))
            .ToList();

    /// <summary>A file's list as <see cref="AccessPlan.Matches"/> reads it.</summary>
    public static (bool IsProtected, IReadOnlyCollection<AccessRuleRead> Rules) Read(FileInfo file)
    {
        var security = file.GetAccessControl();

        return (security.AreAccessRulesProtected, Rules(security));
    }

    /// <summary>
    /// Writes <paramref name="planned"/> as the whole, protected list of the object an
    /// open handle holds, and its owner when <paramref name="ownerSid"/> is given.
    /// </summary>
    /// <remarks>
    /// Through the handle rather than by path, so it lands on the object that was
    /// checked when the handle was opened (<see cref="PinnedHandle"/>), whatever has
    /// been renamed or linked since.
    /// </remarks>
    /// <exception cref="IOException">Windows refused the write.</exception>
    public static void Apply(SafeFileHandle handle, bool isDirectory, IReadOnlyList<AccessEntry> planned, string? ownerSid)
    {
        var security = isDirectory ? (FileSystemSecurity)new DirectorySecurity() : new FileSecurity();
        Replace(security, planned);

        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        var dacl = new byte[descriptor.DiscretionaryAcl!.BinaryLength];
        descriptor.DiscretionaryAcl.GetBinaryForm(dacl, 0);

        byte[]? owner = null;

        if (ownerSid is not null)
        {
            var sid = new SecurityIdentifier(ownerSid);
            owner = new byte[sid.BinaryLength];
            sid.GetBinaryForm(owner, 0);
        }

        var pinnedDacl = GCHandle.Alloc(dacl, GCHandleType.Pinned);
        var pinnedOwner = owner is null ? default : GCHandle.Alloc(owner, GCHandleType.Pinned);

        try
        {
            var written = SetSecurityInfo(
                handle,
                SeFileObject,
                DaclSecurityInformation | ProtectedDaclSecurityInformation | (owner is null ? 0 : OwnerSecurityInformation),
                owner is null ? IntPtr.Zero : pinnedOwner.AddrOfPinnedObject(),
                IntPtr.Zero,
                pinnedDacl.AddrOfPinnedObject(),
                IntPtr.Zero);

            if (written != 0)
            {
                throw new IOException(new Win32Exception(written).Message);
            }
        }
        finally
        {
            pinnedDacl.Free();

            if (owner is not null)
            {
                pinnedOwner.Free();
            }
        }
    }

    /// <summary>
    /// Creates a folder with <paramref name="planned"/> as its protected list and
    /// <paramref name="ownerSid"/> as its owner from the moment it exists, so nothing
    /// its parent lets others do reaches it first.
    /// </summary>
    /// <exception cref="IOException">It already exists, or Windows refused it.</exception>
    public static void Create(string path, IReadOnlyList<AccessEntry> planned, string ownerSid)
    {
        var security = new DirectorySecurity();
        Replace(security, planned);
        security.SetOwner(new SecurityIdentifier(ownerSid));

        if (!CreateDirectory(path, security))
        {
            throw new IOException($"'{path}' could not be created: {new Win32Exception().Message}");
        }
    }

    /// <summary>
    /// <c>CreateDirectory</c> with a security descriptor, which fails when the name
    /// exists in any form, a link included, rather than opening what is there.
    /// </summary>
    private static bool CreateDirectory(string path, DirectorySecurity security)
    {
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);

        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = pinned.AddrOfPinnedObject(),
            };

            return CreateDirectoryW(path, ref attributes);
        }
        finally
        {
            pinned.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, ref SecurityAttributes attributes);

    private static void Replace(FileSystemSecurity security, IReadOnlyList<AccessEntry> planned)
    {
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        RemoveExistingRules(security);

        foreach (var entry in planned)
        {
            var (inheritance, propagation) = AccessPlan.FlagsOf(entry.Scope);

            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(entry.Sid), entry.Rights, inheritance, propagation, AccessControlType.Allow));
        }
    }

    private const int SeFileObject = 1;
    private const uint OwnerSecurityInformation = 0x1;
    private const uint DaclSecurityInformation = 0x4;
    private const uint ProtectedDaclSecurityInformation = 0x8000_0000;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int SetSecurityInfo(
        SafeFileHandle handle, int objectType, uint securityInfo, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

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
