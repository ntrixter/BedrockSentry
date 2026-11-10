using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Services.Security;
using Microsoft.Win32.SafeHandles;

namespace BedrockSentry.Services.Broker;

/// <summary>A folder held open while the broker acts on it.</summary>
/// <remarks>
/// While it is held the folder cannot be renamed, deleted or swapped for a link, so
/// what was checked is what is acted on. <see cref="Path"/> is the path the open
/// handle resolves to, not the one that was asked for.
/// </remarks>
public sealed class PinnedFolder(string path, SafeFileHandle? handle) : IDisposable
{
    public string Path { get; } = path;

    /// <summary>The open handle, or null where the platform has none to hold.</summary>
    public SafeFileHandle? Handle { get; } = handle;

    public void Dispose() => Handle?.Dispose();
}

/// <summary>Opens a folder for the broker to act on, or says why not.</summary>
public interface IFolderPin
{
    /// <param name="full">A full path the path rules have already passed.</param>
    /// <param name="changeAccess">Whether the handle must be able to change the folder's access list.</param>
    /// <param name="refusal">Why the folder was not opened, when it was not.</param>
    PinnedFolder? Open(string full, bool changeAccess, out string? refusal);
}

/// <summary>Gives the main service's account <c>Modify</c> on a pinned folder.</summary>
/// <remarks>A seam because the grant is a Windows ACL and the rules around it are not.</remarks>
public interface IFolderGrant
{
    /// <summary>Grants through the folder's handle, or says why not.</summary>
    /// <param name="folder">The folder, held open.</param>
    /// <param name="serverSid">
    /// A server's folder identity (<see cref="Instances.ServerSandbox.SidFor"/>), for a folder
    /// a server runs in: the entry <c>D124</c> wrote for it is removed and the folder labelled
    /// low (<c>D126</c>); null for any other folder.
    /// </param>
    SystemCheckFix GrantModify(PinnedFolder folder, string? serverSid = null);
}

/// <summary>
/// <see cref="IFolderPin"/> on Windows: <see cref="PinnedHandle"/> for a folder, so a
/// link is refused and the folder held is the one at exactly the path asked for.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFolderPin : IFolderPin
{
    public PinnedFolder? Open(string full, bool changeAccess, out string? refusal) =>
        // WRITE_OWNER too, which setting a server folder's label takes (D124).
        PinnedHandle.Open(
                full, changeAccess ? PinnedHandle.ReadControl | PinnedHandle.WriteDac | PinnedHandle.WriteOwner : 0, directory: true, out refusal)
            is { } opened
            ? new PinnedFolder(opened.Path, opened.Handle)
            : null;
}

/// <summary>
/// <see cref="IFolderGrant"/> on Windows: one inheritable <c>Modify</c> entry for the
/// main service's SID, written through the pinned handle.
/// </summary>
/// <remarks>
/// <c>Modify</c>, never <c>FullControl</c>, so the account cannot rewrite the list it
/// was given. Written through the handle rather than by path, so it lands on the
/// folder that was checked.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsFolderGrant : IFolderGrant
{
    private readonly SecurityIdentifier _service;

    /// <exception cref="IdentityNotMappedException">No service of that name is installed.</exception>
    public WindowsFolderGrant(string mainServiceName) =>
        _service = (SecurityIdentifier)new NTAccount("NT SERVICE", mainServiceName).Translate(typeof(SecurityIdentifier));

    public SystemCheckFix GrantModify(PinnedFolder folder, string? serverSid = null)
    {
        if (folder.Handle is not { IsInvalid: false } handle)
        {
            return new SystemCheckFix(false, $"'{folder.Path}' is not held open, so nothing was granted.");
        }

        if (AddModify(handle, folder.Path, [_service]) is { } failed)
        {
            return new SystemCheckFix(false, failed);
        }

        // An entry for the folder's AppContainer identity refuses a low-integrity token
        // outside one everything beneath it (T409), so a server's folder sheds it.
        if (serverSid is not null && RemoveEntries(handle, folder.Path, new SecurityIdentifier(serverSid)) is { } kept)
        {
            return new SystemCheckFix(false, kept);
        }

        // A server's folder also takes the low label its token needs to write there (D124, D126).
        if (serverSid is not null
            && LabelLow(handle, folder.Path) is { } unlabelled)
        {
            return new SystemCheckFix(false, unlabelled);
        }

        return new SystemCheckFix(true, $"The service account can now change files in '{folder.Path}'.");
    }

    /// <summary>
    /// Gives a folder <see cref="Instances.ServerSandbox.LowLabel"/>, which what is made in
    /// it later inherits, and each folder and file already beneath it its own low label,
    /// one object at a time; null when every one was written, or the first that was not and why.
    /// </summary>
    /// <remarks>
    /// <para>One <c>SetSecurityInfo</c> write with the SACL protected, which carries the label
    /// down the tree, was refused with "Access is denied" over a folder of a server's files
    /// (<c>T419</c>) and over an empty instances folder the installer had just made
    /// (<c>T422</c>). One object at a time through <c>SetKernelObjectSecurity</c> takes only
    /// <c>WRITE_OWNER</c> on each, and a refusal names the object.</para>
    /// <para>Each is opened through <see cref="PinnedHandle"/>, so a link is refused rather
    /// than followed. The folder's own label is explicit, so a parent's inheritable label
    /// written afterwards by <see cref="LabelOne"/>, such as the data root's no-read-up
    /// label (<c>D126</c>), is not carried onto it.</para>
    /// </remarks>
    internal static string? LabelLow(SafeFileHandle handle, string path)
    {
        if (LabelOne(handle, path, Instances.ServerSandbox.LowLabel) is { } root)
        {
            return root;
        }

        var labelled = 0;
        var pending = new Stack<string>();
        pending.Push(path);

        while (pending.Count > 0)
        {
            foreach (var entry in new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos())
            {
                var directory = entry is DirectoryInfo;

                if (PinnedHandle.Open(entry.FullName, PinnedHandle.ReadControl | PinnedHandle.WriteOwner, directory, out var refusal)
                    is not { } opened)
                {
                    return $"'{entry.FullName}' could not be opened to label it low, after {labelled} others were: {refusal}";
                }

                using (opened.Handle)
                {
                    var label = directory ? Instances.ServerSandbox.LowLabel : Instances.ServerSandbox.LowFileLabel;

                    if (LabelOne(opened.Handle, opened.Path, label) is { } failed)
                    {
                        return $"{failed} ({labelled} others beneath '{path}' were labelled).";
                    }
                }

                labelled++;

                if (directory)
                {
                    pending.Push(entry.FullName);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Sets <paramref name="sddl"/>, a mandatory label, on the one object behind
    /// <paramref name="handle"/>, carrying nothing down to what is already beneath it;
    /// null when written, or why not.
    /// </summary>
    /// <remarks>
    /// <c>SetKernelObjectSecurity</c> rather than <c>SetSecurityInfo</c>, which would walk
    /// the tree by name as an administrator over folders the service's account can change.
    /// </remarks>
    /// <param name="handle">Opened with <c>WRITE_OWNER</c>, which setting a label takes.</param>
    internal static string? LabelOne(SafeFileHandle handle, string path, string sddl)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var descriptor, IntPtr.Zero))
        {
            return $"The label for '{path}' could not be made: {new Win32Exception().Message}";
        }

        try
        {
            return SetKernelObjectSecurity(handle, LabelSecurityInformation, descriptor)
                ? null
                : $"'{path}' could not be labelled: {new Win32Exception().Message}";
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    /// <summary>A path's mandatory label in SDDL, or null when it could not be read.</summary>
    internal static string? ReadLabel(string path)
    {
        if (GetNamedSecurityInfoW(
                path, SeFileObject, LabelSecurityInformation, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var descriptor) != 0)
        {
            return null;
        }

        try
        {
            return ConvertSecurityDescriptorToStringSecurityDescriptorW(descriptor, 1, LabelSecurityInformation, out var sddl, out _)
                ? ConsumeString(sddl)
                : null;
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private static string? ConsumeString(IntPtr text)
    {
        try
        {
            return Marshal.PtrToStringUni(text);
        }
        finally
        {
            LocalFree(text);
        }
    }

    /// <summary>
    /// What <see cref="AddModify"/> grants: <c>Modify</c> with <c>SYNCHRONIZE</c>, which is
    /// what <c>icacls</c> shows as <c>(M)</c>.
    /// </summary>
    /// <remarks>
    /// A raw entry gets only the bits it is given, unlike a <see cref="FileSystemAccessRule"/>,
    /// which adds <c>SYNCHRONIZE</c> to every allow. Without it a file opened for
    /// synchronous I/O is refused, and Bedrock under the restricted token stopped at
    /// "Error opening file: server.properties" (<c>T385</c> clause 1, 2026-10-22).
    /// </remarks>
    internal const FileSystemRights Granted = FileSystemRights.Modify | FileSystemRights.Synchronize;

    /// <summary>
    /// Adds one inheritable <see cref="Granted"/> entry per SID to a folder's list through
    /// its handle, keeping everything already there; null when written, or why not.
    /// </summary>
    /// <param name="handle">Opened with <c>READ_CONTROL</c> and <c>WRITE_DAC</c>.</param>
    internal static string? AddModify(SafeFileHandle handle, string path, IReadOnlyList<SecurityIdentifier> sids)
    {
        var read = GetSecurityInfo(
            handle, SeFileObject, DaclSecurityInformation, IntPtr.Zero, IntPtr.Zero, out _, IntPtr.Zero, out var descriptor);

        if (read != 0)
        {
            return $"The access list of '{path}' could not be read: {new Win32Exception(read).Message}";
        }

        byte[] dacl;

        try
        {
            var bytes = new byte[GetSecurityDescriptorLength(descriptor)];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);

            var security = new CommonSecurityDescriptor(isContainer: true, isDS: false, new RawSecurityDescriptor(bytes, 0));

            // No list at all grants everyone everything; one entry added to it would
            // take that away from everyone else, which is not what was asked.
            if (security.DiscretionaryAcl is null)
            {
                return $"'{path}' has no access list, so nothing was granted.";
            }

            foreach (var sid in sids)
            {
                security.DiscretionaryAcl.AddAccess(
                    AccessControlType.Allow,
                    sid,
                    (int)Granted,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None);
            }

            dacl = new byte[security.DiscretionaryAcl.BinaryLength];
            security.DiscretionaryAcl.GetBinaryForm(dacl, 0);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // A list not in canonical order, which only a hand edit makes. Left as
            // it is rather than reordered: reordering changes what it grants.
            return $"The access list of '{path}' could not be extended: {ex.Message}";
        }
        finally
        {
            LocalFree(descriptor);
        }

        var pinned = GCHandle.Alloc(dacl, GCHandleType.Pinned);

        try
        {
            var written = SetSecurityInfo(
                handle, SeFileObject, DaclSecurityInformation, IntPtr.Zero, IntPtr.Zero, pinned.AddrOfPinnedObject(), IntPtr.Zero);

            return written == 0
                ? null
                : $"'{path}' could not be granted: {new Win32Exception(written).Message}";
        }
        finally
        {
            pinned.Free();
        }
    }

    /// <summary>
    /// Removes every entry the folder's own list holds for <paramref name="sid"/>, and with
    /// them the copies its contents inherited; null when removed or there were none, or why not.
    /// </summary>
    /// <param name="handle">Opened with <c>READ_CONTROL</c> and <c>WRITE_DAC</c>.</param>
    internal static string? RemoveEntries(SafeFileHandle handle, string path, SecurityIdentifier sid)
    {
        var read = GetSecurityInfo(
            handle, SeFileObject, DaclSecurityInformation, IntPtr.Zero, IntPtr.Zero, out _, IntPtr.Zero, out var descriptor);

        if (read != 0)
        {
            return $"The access list of '{path}' could not be read: {new Win32Exception(read).Message}";
        }

        byte[] dacl;

        try
        {
            var bytes = new byte[GetSecurityDescriptorLength(descriptor)];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);

            var security = new CommonSecurityDescriptor(isContainer: true, isDS: false, new RawSecurityDescriptor(bytes, 0));

            if (security.DiscretionaryAcl is not { } list
                || !list.Cast<GenericAce>().OfType<KnownAce>().Any(ace => ace.SecurityIdentifier == sid && !ace.IsInherited))
            {
                return null;
            }

            list.Purge(sid);
            dacl = new byte[list.BinaryLength];
            list.GetBinaryForm(dacl, 0);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return $"The access list of '{path}' could not be changed: {ex.Message}";
        }
        finally
        {
            LocalFree(descriptor);
        }

        var pinned = GCHandle.Alloc(dacl, GCHandleType.Pinned);

        try
        {
            // SetSecurityInfo carries the change to the inherited copies beneath.
            var written = SetSecurityInfo(
                handle, SeFileObject, DaclSecurityInformation, IntPtr.Zero, IntPtr.Zero, pinned.AddrOfPinnedObject(), IntPtr.Zero);

            return written == 0
                ? null
                : $"The entry for {sid.Value} could not be removed from '{path}': {new Win32Exception(written).Message}";
        }
        finally
        {
            pinned.Free();
        }
    }

    private const int SeFileObject = 1;
    private const uint DaclSecurityInformation = 0x4;
    private const uint LabelSecurityInformation = 0x10;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl, uint revision, out IntPtr descriptor, IntPtr length);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(
        IntPtr descriptor, uint revision, uint securityInformation, out IntPtr sddl, out uint length);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetNamedSecurityInfoW(
        string path, int objectType, uint securityInfo, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr descriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int GetSecurityInfo(
        SafeFileHandle handle, int objectType, uint securityInfo, IntPtr owner, IntPtr group,
        out IntPtr dacl, IntPtr sacl, out IntPtr securityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int SetSecurityInfo(
        SafeFileHandle handle, int objectType, uint securityInfo, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(IntPtr securityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetKernelObjectSecurity(SafeFileHandle handle, uint information, IntPtr descriptor);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
