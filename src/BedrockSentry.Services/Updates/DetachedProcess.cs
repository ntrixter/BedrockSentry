using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace BedrockSentry.Services.Updates;

/// <summary>
/// Launches a process that must outlive the process launching it.
/// </summary>
/// <remarks>
/// <para>
/// The updater helper's first act is to stop the BedrockSentry service — the very
/// process that started it. <see cref="System.Diagnostics.Process"/> cannot express
/// the creation flags that make that survivable, which is why this drops to
/// <c>CreateProcessW</c> directly.
/// </para>
/// <para>
/// <c>CREATE_BREAKAWAY_FROM_JOB</c> is the important one. If the service happens to
/// be running inside a job object, terminating the job takes every process in it —
/// including the helper, mid-swap, leaving the install pointing at nothing. Not every
/// job permits breakaway, so a refusal falls back to an ordinary detached launch
/// rather than failing the update outright.
/// </para>
/// <para>
/// <strong>Compile-verified only.</strong> This has never been executed against a
/// real Windows service; it is on the milestone 3.5 disaster-drill list.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class DetachedProcess
{
    private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    private const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
    private const uint DETACHED_PROCESS = 0x00000008;
    private const int ERROR_ACCESS_DENIED = 5;

    /// <summary>Starts <paramref name="executablePath"/> fully detached.</summary>
    /// <returns>The new process id.</returns>
    public static int Start(string executablePath, string arguments, string workingDirectory)
    {
        var commandLine = new StringBuilder($"\"{executablePath}\" {arguments}");

        var startupInfo = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>(),
        };

        const uint baseFlags = DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP;

        // inheritHandles: false, and no redirected streams. An inherited handle would
        // keep a pipe alive against a process that is about to disappear.
        var created = CreateProcessW(
            null,
            commandLine,
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            baseFlags | CREATE_BREAKAWAY_FROM_JOB,
            IntPtr.Zero,
            workingDirectory,
            ref startupInfo,
            out var info);

        if (!created && Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED)
        {
            // The job forbids breakaway. Launching without it is still better than
            // not launching at all — most services are not in a job to begin with.
            created = CreateProcessW(
                null,
                new StringBuilder($"\"{executablePath}\" {arguments}"),
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                baseFlags,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out info);
        }

        if (!created)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not start '{executablePath}'.");
        }

        // Closing these does not affect the running process; leaking them would keep
        // kernel objects alive for the lifetime of this service.
        CloseHandle(info.hProcess);
        CloseHandle(info.hThread);

        return info.dwProcessId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }
}
