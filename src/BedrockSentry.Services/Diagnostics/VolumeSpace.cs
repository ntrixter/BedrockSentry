using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BedrockSentry.Services.Diagnostics;

/// <summary>
/// The volume a folder is on, and that volume's size and free space, for the
/// Storage table.
/// </summary>
/// <remarks>
/// <see cref="Path.GetPathRoot(string)"/> and <see cref="DriveInfo"/> know drive
/// letters only, so a volume mounted in a folder (<c>C:\Mounts\Server1</c>) read as
/// <c>C:</c> and showed C:'s free space. On Windows the root comes from
/// <c>GetVolumePathNameW</c>, which returns the mount folder for a mounted volume,
/// and the space from <c>GetDiskFreeSpaceExW</c>, which takes any path (T430 clause 5).
/// </remarks>
public static class VolumeSpace
{
    /// <summary>The volume's root, with a trailing separator, or null when it cannot be read.</summary>
    public static string? RootOf(string path)
    {
        var full = Path.GetFullPath(path);

        if (!OperatingSystem.IsWindows())
        {
            return Path.GetPathRoot(full);
        }

        var buffer = new char[Math.Max(full.Length + 2, 261)];

        return GetVolumePathNameW(full, buffer, (uint)buffer.Length)
            ? new string(buffer, 0, Array.IndexOf(buffer, '\0'))
            : null;
    }

    /// <summary>Total and free bytes, free as the caller can use it; null when the volume is not ready.</summary>
    public static (long Total, long Free)? Read(string root)
    {
        if (!OperatingSystem.IsWindows())
        {
            var drive = new DriveInfo(root);
            return drive.IsReady ? (drive.TotalSize, drive.AvailableFreeSpace) : null;
        }

        return GetDiskFreeSpaceExW(root, out var free, out var total, out _)
            ? ((long)Math.Min(total, long.MaxValue), (long)Math.Min(free, long.MaxValue))
            : null;
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumePathNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string fileName, [Out] char[] volumePathName, uint bufferLength);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetDiskFreeSpaceExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(
        string directoryName, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);
}
