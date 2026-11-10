using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace BedrockSentry.Services.Security;

/// <summary>
/// Opens a file or folder without following a link, and only when it is a plain
/// object at exactly the path asked for.
/// </summary>
/// <remarks>
/// <para><c>FILE_FLAG_OPEN_REPARSE_POINT</c> opens a junction or symbolic link itself
/// rather than what it points to, so a link is seen as one and refused. A file Data
/// Deduplication optimized is a reparse point too, but its own storage, and is held
/// like any other file (<see cref="ReparseTags"/>).</para>
/// <para><c>GetFinalPathNameByHandle</c> answers with the object's real name. A path
/// that reached it some other way (a short 8.3 name, a trailing dot Windows trimmed, a
/// junction or mount point higher up the path, an object-manager link) answers with a
/// different name and is refused, so what was checked is what the handle holds.</para>
/// <para>A file with a second hard link is refused: the other name could be anywhere
/// on the volume, and a list written to one name is written to both.</para>
/// <para>Shared for reading and writing, never for deleting, so nothing can remove or
/// rename the object while it is held. Act on it through the handle, never the path.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class PinnedHandle
{
    public const uint ReadControl = 0x0002_0000;
    public const uint WriteDac = 0x0004_0000;
    public const uint WriteOwner = 0x0008_0000;

    /// <param name="full">The full path, in drive-letter form.</param>
    /// <param name="access">What the handle must be able to do beyond reading attributes.</param>
    /// <param name="directory">Whether a folder is expected; a file is expected otherwise.</param>
    /// <param name="refusal">Why it was not opened, when it was not.</param>
    /// <returns>The open handle and the path it resolves to, or null.</returns>
    public static (SafeFileHandle Handle, string Path)? Open(string full, uint access, bool directory, out string? refusal)
    {
        var handle = CreateFile(
            full, FileReadAttributes | access, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            refusal = $"'{full}' could not be opened: {new Win32Exception().Message}";
            handle.Dispose();
            return null;
        }

        refusal = Refuse(full, handle, directory, out var final);

        if (refusal is not null)
        {
            handle.Dispose();
            return null;
        }

        return (handle, final!);
    }

    private static string? Refuse(string full, SafeFileHandle handle, bool directory, out string? final)
    {
        final = null;

        if (!GetFileInformationByHandle(handle, out var information))
        {
            return $"'{full}' could not be read: {new Win32Exception().Message}";
        }

        // A deduplicated file is a reparse point that is the file's own storage, not a
        // way to somewhere else, so it is held as a plain file (ReparseTags).
        if (ReparseTags.Why(handle, information.FileAttributes) is { } link)
        {
            return $"'{full}' is {link}, a link to somewhere else.";
        }

        var isDirectory = (information.FileAttributes & FileAttributeDirectory) != 0;

        if (isDirectory != directory)
        {
            return directory ? $"'{full}' is a file, not a folder." : $"'{full}' is a folder, not a file.";
        }

        if (!directory && information.NumberOfLinks != 1)
        {
            return $"'{full}' has {information.NumberOfLinks} names on the volume, so it is not only this file.";
        }

        if (FinalPathOf(handle) is not { } resolved)
        {
            return $"'{full}' could not be resolved: {new Win32Exception().Message}";
        }

        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(resolved),
                Path.TrimEndingDirectorySeparator(full),
                StringComparison.OrdinalIgnoreCase))
        {
            return $"'{full}' is really '{resolved}'. Name it by its own path.";
        }

        final = resolved;
        return null;
    }

    /// <summary>The handle's path in drive-letter form, or null for anything else (a share, a volume GUID).</summary>
    private static string? FinalPathOf(SafeFileHandle handle)
    {
        var buffer = new char[32_768];
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, FileNameNormalized | VolumeNameDos);

        if (length == 0 || length >= buffer.Length)
        {
            return null;
        }

        var path = new string(buffer, 0, (int)length);
        const string Local = @"\\?\";

        // \\?\C:\... is a local drive; \\?\UNC\... is a share, which no rule here covers.
        return path.StartsWith(Local, StringComparison.Ordinal) && path.Length > Local.Length + 2 && path[Local.Length + 1] == ':'
            ? path[Local.Length..]
            : null;
    }

    private const uint FileReadAttributes = 0x0080;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x0200_0000;
    private const uint FileFlagOpenReparsePoint = 0x0020_0000;
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileNameNormalized = 0x0;
    private const uint VolumeNameDos = 0x0;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] path, uint length, uint flags);
}
