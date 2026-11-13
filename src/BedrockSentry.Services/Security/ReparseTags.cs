using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace BedrockSentry.Services.Security;

/// <summary>
/// Which reparse points lead somewhere else, and which are only how a file stores its
/// own contents.
/// </summary>
/// <remarks>
/// <para>Every check that refuses a link reads <see cref="FileAttributes.ReparsePoint"/>,
/// and that attribute is also set on every file Windows Data Deduplication has
/// optimized: the file's stream is replaced by a reparse point that the dedup filter
/// reads back from the volume's chunk store. Such a file has its own name, its own
/// security descriptor and its own contents, so a grant, a label or a read stops at it
/// exactly as at a plain file. Refusing it refused every server folder, backups folder
/// and packages folder on a deduplicated volume.</para>
/// <para>Only <see cref="Dedup"/> on a file is let through. A symbolic link, a junction,
/// a mount point and every other tag, the ones not known here included, are refused as
/// before: an unknown tag is a filter whose behaviour nothing here has read, and a
/// folder never carries the dedup tag.</para>
/// </remarks>
public static class ReparseTags
{
    /// <summary><c>IO_REPARSE_TAG_DEDUP</c>: a file Data Deduplication has optimized.</summary>
    public const uint Dedup = 0x8000_0013;

    /// <summary><c>IO_REPARSE_TAG_MOUNT_POINT</c>: a junction or a volume mount point.</summary>
    public const uint MountPoint = 0xA000_0003;

    /// <summary><c>IO_REPARSE_TAG_SYMLINK</c>.</summary>
    public const uint SymbolicLink = 0xA000_000C;

    /// <summary>
    /// Whether a reparse point with <paramref name="tag"/> is a file's own storage rather
    /// than a way to somewhere else: a deduplicated file, and nothing else.
    /// </summary>
    public static bool IsOwnStorage(uint tag, bool directory) => !directory && tag == Dedup;

    /// <summary>What a reparse point with <paramref name="tag"/> is, naming the tag, for a refusal.</summary>
    /// <param name="tag">The tag, or null when it could not be read.</param>
    public static string Describe(uint? tag) => tag switch
    {
        null => "a reparse point whose tag could not be read",
        MountPoint => "a junction or mount point (reparse tag 0xA0000003)",
        SymbolicLink => "a symbolic link (reparse tag 0xA000000C)",
        Dedup => "a folder carrying the deduplication tag (reparse tag 0x80000013), which no deduplicated volume makes",
        { } other => "a reparse point with tag 0x" + other.ToString("X8", CultureInfo.InvariantCulture)
                     + ", which is not a deduplicated file",
    };

    /// <summary>
    /// Null when <paramref name="entry"/> is no link: no reparse point, or a deduplicated
    /// file; otherwise what it is (<see cref="Describe"/>).
    /// </summary>
    /// <remarks>
    /// Read by name, so it is a reading of that moment. Anything that then acts on the
    /// object opens it through <see cref="PinnedHandle"/> or <c>ServerFileReader</c>,
    /// which ask the same of the open handle.
    /// </remarks>
    public static string? Why(FileSystemInfo entry)
    {
        var attributes = entry.Attributes;

        if (!attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            return "a symbolic link";
        }

        var tag = TagOf(entry.FullName);
        return tag is { } known && IsOwnStorage(known, attributes.HasFlag(FileAttributes.Directory)) ? null : Describe(tag);
    }

    /// <summary>
    /// The nearest folder above <paramref name="full"/> that is a link, and what it is with
    /// its tag; null when none is or one cannot be read (<c>T443</c>). What names the cause
    /// when a path resolves somewhere other than where it was named.
    /// </summary>
    public static (string Path, string What)? LinkAbove(string full)
    {
        for (var folder = Path.GetDirectoryName(full); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            try
            {
                if (Why(folder) is { } what)
                {
                    return (folder, what);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="entry"/> is a link (<see cref="Why(FileSystemInfo)"/>).</summary>
    public static bool IsLink(FileSystemInfo entry) => Why(entry) is not null;

    /// <summary><see cref="Why(FileSystemInfo)"/> for a path, folder or file.</summary>
    /// <exception cref="FileNotFoundException">Nothing is there.</exception>
    /// <exception cref="DirectoryNotFoundException">Nothing is there, nor its folder.</exception>
    public static string? Why(string path)
    {
        // Read first so a missing path throws: a FileInfo for one reads every attribute set.
        _ = File.GetAttributes(path);
        return Why(new FileInfo(path));
    }

    /// <summary>The reparse tag of the object at <paramref name="path"/>, not following it; null when unread.</summary>
    [SupportedOSPlatform("windows")]
    public static uint? TagOf(string path)
    {
        var full = Path.GetFullPath(path);

        if (!full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            full = @"\\?\" + full;
        }

        // FindFirstFile names the object itself and reports its tag in dwReserved0,
        // without opening it, so a file a running server holds is read too.
        using var find = FindFirstFileExW(full, FindExInfoBasic, out var data, FindExSearchNameMatch, IntPtr.Zero, 0);

        return find.IsInvalid || (data.FileAttributes & FileAttributeReparsePoint) == 0 ? null : data.Reserved0;
    }

    /// <summary>The reparse tag of the object behind <paramref name="handle"/>; null when unread.</summary>
    [SupportedOSPlatform("windows")]
    internal static uint? TagOf(SafeFileHandle handle) =>
        GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, out var info, (uint)Marshal.SizeOf<FileAttributeTagInfo>())
            ? info.ReparseTag
            : null;

    /// <summary>
    /// Null when the object behind <paramref name="handle"/>, with <paramref name="attributes"/>,
    /// is no link; otherwise why it is refused, naming the tag.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static string? Why(SafeFileHandle handle, uint attributes)
    {
        if ((attributes & FileAttributeReparsePoint) == 0)
        {
            return null;
        }

        var tag = TagOf(handle);

        if (tag is null)
        {
            return Describe(null) + ": " + new Win32Exception().Message;
        }

        return IsOwnStorage(tag.Value, (attributes & FileAttributeDirectory) != 0) ? null : Describe(tag);
    }

    private const uint FileAttributeReparsePoint = 0x400;
    private const uint FileAttributeDirectory = 0x10;
    private const int FindExInfoBasic = 1;
    private const int FindExSearchNameMatch = 0;
    private const int FileAttributeTagInfoClass = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindData
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint Reserved0;
        public uint Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string FileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string AlternateFileName;
    }

    [SupportedOSPlatform("windows")]
    private sealed class FindHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => FindClose(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern FindHandle FindFirstFileExW(
        string fileName, int infoLevel, out Win32FindData data, int searchOp, IntPtr filter, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FindClose(IntPtr find);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file, int infoClass, out FileAttributeTagInfo info, uint size);
}
