using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BedrockSentry.Services.Security;
using Microsoft.Win32.SafeHandles;

namespace BedrockSentry.Services.Backups;

/// <summary>
/// Opens a file in a server's folder for reading beside the running server, refusing
/// one that is not the plain file at exactly the path given.
/// </summary>
/// <remarks>
/// <para>At low integrity (milestone 18.5, <c>D126</c>) the server writes its folder and
/// the service reads it with more rights than the server has. A link the server made
/// in its folder (a junction higher up the path, a symbolic link, a second hard link)
/// would lead that read to the credential or the certificate and into a backup. So the
/// file is opened without following a link, and refused unless the handle's own name
/// is the path asked for and the file has one name. Checked on the open handle, so
/// nothing can be swapped in between the check and the read.</para>
/// <para>A file Data Deduplication optimized is a reparse point that is its own storage,
/// not a link (<see cref="ReparseTags"/>). It is read through a second handle opened
/// the ordinary way, so the deduplication filter returns its contents, and only when
/// that handle holds the same file as the one checked.</para>
/// <para>Shared for reading, writing and deleting, as a running server needs: LevelDB
/// appends to its current log while saves are held, and compacts and deletes the rest.</para>
/// </remarks>
internal static class ServerFileReader
{
    /// <exception cref="FileNotFoundException">Nothing is there.</exception>
    /// <exception cref="IOException">It is a link, has a second name, or is not at that path.</exception>
    public static FileStream Open(string path)
    {
        var full = Path.GetFullPath(path);

        if (!OperatingSystem.IsWindows())
        {
            var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            if (File.ResolveLinkTarget($"/proc/self/fd/{stream.SafeFileHandle.DangerousGetHandle()}", false)?.FullName is { } real
                && real != full)
            {
                stream.Dispose();
                throw new IOException($"'{full}' is really '{real}', so it is not copied.");
            }

            return stream;
        }

        return OpenOnWindows(full);
    }

    [SupportedOSPlatform("windows")]
    private static FileStream OpenOnWindows(string full)
    {
        var handle = CreateFile(
            full, GenericRead, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting,
            FileFlagOpenReparsePoint, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();

            throw error is ErrorFileNotFound or ErrorPathNotFound
                ? new FileNotFoundException($"'{full}' is not there.", full)
                : new IOException($"'{full}' could not be opened: {new Win32Exception(error).Message}");
        }

        try
        {
            if (Refuse(full, handle, out var deduplicated) is { } why)
            {
                throw new IOException(why);
            }

            if (deduplicated)
            {
                var contents = ReopenThroughTheFilter(full, handle);
                handle.Dispose();
                return new FileStream(contents, FileAccess.Read);
            }

            return new FileStream(handle, FileAccess.Read);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A deduplicated file opened again without <c>FILE_FLAG_OPEN_REPARSE_POINT</c>, so its
    /// reads go through the deduplication filter and return the file's contents rather
    /// than its reparse point, refused unless it is the very file <paramref name="checkedHandle"/> holds.
    /// </summary>
    /// <remarks>
    /// The second open follows a link, so the file is compared by volume and file index
    /// with the handle already checked, which is still open: a link swapped in between
    /// names another file and is refused.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static SafeFileHandle ReopenThroughTheFilter(string full, SafeFileHandle checkedHandle)
    {
        var handle = CreateFile(
            full, GenericRead, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"'{full}' is a deduplicated file and could not be opened to read it: {new Win32Exception(error).Message}");
        }

        if (GetFileInformationByHandle(checkedHandle, out var expected)
            && GetFileInformationByHandle(handle, out var opened)
            && opened.VolumeSerialNumber == expected.VolumeSerialNumber
            && opened.FileIndexHigh == expected.FileIndexHigh
            && opened.FileIndexLow == expected.FileIndexLow)
        {
            return handle;
        }

        handle.Dispose();
        throw new IOException($"'{full}' changed while it was opened, so it is not copied.");
    }

    [SupportedOSPlatform("windows")]
    private static string? Refuse(string full, SafeFileHandle handle, out bool deduplicated)
    {
        deduplicated = false;

        if (!GetFileInformationByHandle(handle, out var information))
        {
            return $"'{full}' could not be read: {new Win32Exception().Message}";
        }

        // A deduplicated file is a reparse point that is the file's own storage, not a
        // way to somewhere else (ReparseTags); every other reparse point is refused.
        if (ReparseTags.Why(handle, information.FileAttributes) is { } link)
        {
            return $"'{full}' is {link}, so it is not copied.";
        }

        deduplicated = (information.FileAttributes & FileAttributeReparsePoint) != 0;

        if (information.NumberOfLinks != 1)
        {
            return $"'{full}' is a hard link with {information.NumberOfLinks} names on the volume, so it is not copied.";
        }

        // A shadow copy is named by its device, which has no drive letter.
        const string Device = @"\\?\GLOBALROOT";
        var shadow = full.StartsWith(Device, StringComparison.OrdinalIgnoreCase);
        var expected = shadow ? full[Device.Length..] : full;

        var buffer = new char[32_768];
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, shadow ? VolumeNameNt : VolumeNameDos);

        if (length == 0 || length >= buffer.Length)
        {
            return $"'{full}' could not be resolved: {new Win32Exception().Message}";
        }

        var resolved = new string(buffer, 0, (int)length);

        if (!shadow && resolved.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            resolved = resolved[4..];
        }

        if (string.Equals(resolved, expected, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // T443. A junction on the way is the likelier cause, and its own sentence names it
        // and its tag; the short-name hint only fits when no folder on the way is a link.
        return ReparseTags.LinkAbove(full) is { } above
            ? $"'{full}' is reached through '{above.Path}', {above.What}, so it is not copied."
            : $"'{full}' is really '{resolved}', so it is not copied. If the server's folder is named "
              + "through a short name, a subst drive or a mapped drive, name it by its full path instead.";
    }


    private const uint GenericRead = 0x8000_0000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint FileShareDelete = 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x0020_0000;
    private const uint FileAttributeReparsePoint = 0x400;
    private const uint VolumeNameDos = 0x0;
    private const uint VolumeNameNt = 0x2;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, char[] buffer, uint length, uint flags);
}
