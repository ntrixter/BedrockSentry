using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using BedrockSentry.Services.Broker;
using BedrockSentry.Services.Security;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace BedrockSentry.Services.Instances;

/// <summary><see cref="IServerFolderAccess"/> on Windows, as the service's own account.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsServerFolderAccess : IServerFolderAccess
{
    public bool HoldsFolderIdentity(string workingDirectory, string sid) =>
        new DirectoryInfo(workingDirectory)
            .GetAccessControl(AccessControlSections.Access)
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Any(rule => rule.IdentityReference.Value == sid);

    public void RemoveFolderIdentity(string workingDirectory, string sid)
    {
        // Through a handle that refuses a link, so the change lands on the folder named.
        if (PinnedHandle.Open(Path.GetFullPath(workingDirectory), PinnedHandle.ReadControl | PinnedHandle.WriteDac, directory: true, out var refusal)
            is not { } opened)
        {
            throw new IOException(refusal);
        }

        using (opened.Handle)
        {
            if (WindowsFolderGrant.RemoveEntries(opened.Handle, opened.Path, new SecurityIdentifier(sid)) is { } failed)
            {
                throw new UnauthorizedAccessException(failed);
            }
        }
    }

    public bool IsLowLabelled(string workingDirectory) =>
        WindowsFolderGrant.ReadLabel(Path.GetFullPath(workingDirectory)) is { } sddl && ServerSandbox.IsLowLabelled(sddl);
}

/// <summary>
/// <see cref="IRestrictedProcessCreator"/> on Windows: this process's own token with its
/// privileges removed and its integrity lowered, then <c>CreateProcessAsUserW</c> with
/// the console on three anonymous pipes (<c>D126</c>).
/// </summary>
/// <remarks>
/// <para>A restricted copy of the caller's own token is one Windows lets the caller
/// assign without <c>SeAssignPrimaryTokenPrivilege</c>, which the service's account
/// does not hold.</para>
/// <para>Only the three pipe ends are inherited (<c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>),
/// so the server holds no other handle of this process's. The process starts
/// suspended and is resumed once <see cref="Process.GetProcessById(int)"/> has it, so
/// it cannot exit before there is an object to report the exit; the process handle
/// is held until the server is disposed, so its id cannot be reused meanwhile.</para>
/// <para>The server starts on the service's own window station and desktop. A desktop
/// made for each server, which <c>D124</c>'s AppContainer needed, kills a low-integrity
/// server outside one with 0xC0000142 before its first line, while the service's own
/// starts it (<c>T409</c>, 2026-10-28); the server shows no window.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsRestrictedProcessCreator : IRestrictedProcessCreator
{
    public StartedServer Create(string executablePath, string workingDirectory, ServerTokenPlan plan)
    {
        using var token = RestrictedToken(plan);

        SafeFileHandle? stdinRead = null, stdinWrite = null, stdoutRead = null, stdoutWrite = null, stderrRead = null, stderrWrite = null;
        var attributes = IntPtr.Zero;
        var handleList = IntPtr.Zero;
        var environment = IntPtr.Zero;
        SafeProcessHandle? process = null;

        try
        {
            CreatePipe(out stdinRead, out stdinWrite);
            CreatePipe(out stdoutRead, out stdoutWrite);
            CreatePipe(out stderrRead, out stderrWrite);

            // The child's ends are inherited, and only from here until the finally
            // below closes them; ours never are.
            SetInheritable(stdinRead, true);
            SetInheritable(stdoutWrite, true);
            SetInheritable(stderrWrite, true);

            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);

            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
            {
                throw new Win32Exception();
            }

            handleList = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(handleList, 0, stdinRead.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, IntPtr.Size, stdoutWrite.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, IntPtr.Size * 2, stderrWrite.DangerousGetHandle());

            if (!UpdateProcThreadAttribute(
                    attributes, 0, ProcThreadAttributeHandleList, handleList, IntPtr.Size * 3, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception();
            }

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    cb = Marshal.SizeOf<StartupInfoEx>(),
                    dwFlags = StartfUseStdHandles,
                    hStdInput = stdinRead.DangerousGetHandle(),
                    hStdOutput = stdoutWrite.DangerousGetHandle(),
                    hStdError = stderrWrite.DangerousGetHandle(),
                },
                lpAttributeList = attributes,
            };

            // The service's own variables, with the profile folders moved into the
            // server's folder (D123).
            var inherited = Environment.GetEnvironmentVariables()
                .Cast<System.Collections.DictionaryEntry>()
                .Select(e => new KeyValuePair<string, string>((string)e.Key, (string?)e.Value ?? ""));
            environment = Marshal.StringToHGlobalUni(
                ServerSandbox.EnvironmentBlock(inherited, ServerSandbox.ProfileEnvironment(workingDirectory)));

            // A buffer, not a string: CreateProcessAsUserW may write to it.
            var commandLine = new StringBuilder($"\"{executablePath}\"");

            if (!CreateProcessAsUserW(
                    token, executablePath, commandLine, IntPtr.Zero, IntPtr.Zero, bInheritHandles: true,
                    ExtendedStartupInfoPresent | CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment,
                    environment, workingDirectory, ref startup, out var info))
            {
                var refused = new Win32Exception();
                throw new Win32Exception(
                    refused.NativeErrorCode,
                    $"The server could not be started at low integrity: {refused.Message}");
            }

            process = new SafeProcessHandle(info.hProcess, ownsHandle: true);
            using var thread = new SafeProcessHandle(info.hThread, ownsHandle: true);

            Process managed;

            try
            {
                managed = Process.GetProcessById(info.dwProcessId);
            }
            catch
            {
                TerminateProcess(process, 1);
                throw;
            }

            managed.EnableRaisingEvents = true;

            if (ResumeThread(thread) == uint.MaxValue)
            {
                var error = new Win32Exception();
                TerminateProcess(process, 1);
                managed.Dispose();
                throw error;
            }

            var server = new StartedServer(
                managed,
                new StreamWriter(new FileStream(stdinWrite, FileAccess.Write, 4096, isAsync: false), ServerConsole.Encoding),
                new StreamReader(new FileStream(stdoutRead, FileAccess.Read, 4096, isAsync: false), ServerConsole.Encoding, detectEncodingFromByteOrderMarks: false),
                new StreamReader(new FileStream(stderrRead, FileAccess.Read, 4096, isAsync: false), ServerConsole.Encoding, detectEncodingFromByteOrderMarks: false),
                process);

            process = null;
            return server;
        }
        catch
        {
            process?.Dispose();
            stdinWrite?.Dispose();
            stdoutRead?.Dispose();
            stderrRead?.Dispose();
            throw;
        }
        finally
        {
            // The child holds its own copies now, and ours would keep its output open.
            stdinRead?.Dispose();
            stdoutWrite?.Dispose();
            stderrWrite?.Dispose();

            if (attributes != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }

            if (handleList != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(handleList);
            }

            if (environment != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environment);
            }

        }
    }

    /// <summary>
    /// This process's token with every privilege but <c>SeChangeNotifyPrivilege</c> removed,
    /// at <see cref="ServerTokenPlan.IntegritySid"/>, its default list set so the server
    /// can open its own objects.
    /// </summary>
    /// <remarks>
    /// No restricting SIDs (<c>D121</c>): the integrity level and the labels are the
    /// boundary, and with them the sign-in to Minecraft services failed (<c>T385</c> clause 1).
    /// </remarks>
    private static SafeAccessTokenHandle RestrictedToken(ServerTokenPlan plan)
    {
        using var current = Process.GetCurrentProcess();

        if (!OpenProcessToken(
                current.Handle,
                TokenDuplicate | TokenAssignPrimary | TokenQuery | TokenAdjustDefault,
                out var own))
        {
            throw new Win32Exception();
        }

        using (own)
        {
            if (!CreateRestrictedToken(own, DisableMaxPrivilege, 0, null, 0, null, 0, null, out var restricted))
            {
                throw new Win32Exception();
            }

            try
            {
                SetDefaultDacl(restricted, plan);
                SetIntegrity(restricted, plan);
            }
            catch
            {
                restricted.Dispose();
                throw;
            }

            return restricted;
        }
    }

    private static void SetDefaultDacl(SafeAccessTokenHandle token, ServerTokenPlan plan)
    {
        using var self = WindowsIdentity.GetCurrent();

        var owners = new[] { self.User! }.Concat(plan.DefaultDaclSids.Select(s => new SecurityIdentifier(s))).Distinct();
        var acl = new RawAcl(GenericAcl.AclRevision, 4);

        foreach (var sid in owners)
        {
            acl.InsertAce(acl.Count, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, GenericAll, sid, false, null));
        }

        var bytes = new byte[acl.BinaryLength];
        acl.GetBinaryForm(bytes, 0);
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        var holder = Marshal.AllocHGlobal(IntPtr.Size);

        try
        {
            Marshal.WriteIntPtr(holder, pinned.AddrOfPinnedObject());

            if (!SetTokenInformation(token, TokenDefaultDacl, holder, IntPtr.Size))
            {
                throw new Win32Exception();
            }
        }
        finally
        {
            Marshal.FreeHGlobal(holder);
            pinned.Free();
        }
    }

    private static void SetIntegrity(SafeAccessTokenHandle token, ServerTokenPlan plan)
    {
        var sid = Binary(new SecurityIdentifier(plan.IntegritySid));
        var pinned = GCHandle.Alloc(sid, GCHandleType.Pinned);
        var label = Marshal.AllocHGlobal(Marshal.SizeOf<SidAndAttributes>());

        try
        {
            Marshal.StructureToPtr(
                new SidAndAttributes { Sid = pinned.AddrOfPinnedObject(), Attributes = SeGroupIntegrity },
                label,
                fDeleteOld: false);

            if (!SetTokenInformation(token, TokenIntegrityLevel, label, Marshal.SizeOf<SidAndAttributes>() + sid.Length))
            {
                throw new Win32Exception();
            }
        }
        finally
        {
            Marshal.FreeHGlobal(label);
            pinned.Free();
        }
    }

    private static byte[] Binary(SecurityIdentifier sid)
    {
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private static void CreatePipe(out SafeFileHandle read, out SafeFileHandle write)
    {
        if (!CreatePipe(out read, out write, IntPtr.Zero, 0))
        {
            throw new Win32Exception();
        }
    }

    private static void SetInheritable(SafeFileHandle handle, bool inheritable)
    {
        if (!SetHandleInformation(handle, HandleFlagInherit, inheritable ? HandleFlagInherit : 0))
        {
            throw new Win32Exception();
        }
    }

    private const uint DisableMaxPrivilege = 0x1;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustDefault = 0x0080;
    private const int TokenDefaultDacl = 6;
    private const int GenericAll = 0x10000000;
    private const uint HandleFlagInherit = 0x1;
    private const int StartfUseStdHandles = 0x100;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private static readonly IntPtr ProcThreadAttributeHandleList = 0x20002;
    private const int TokenIntegrityLevel = 25;
    private const uint SeGroupIntegrity = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
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
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CreateRestrictedToken(
        SafeAccessTokenHandle existing, uint flags, int disableCount, SidAndAttributes[]? sidsToDisable,
        int deleteCount, IntPtr[]? privilegesToDelete, int restrictCount, SidAndAttributes[]? sidsToRestrict,
        out SafeAccessTokenHandle newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(SafeAccessTokenHandle token, int infoClass, IntPtr info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr list, uint flags, IntPtr attribute, IntPtr value, nint size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUserW(
        SafeAccessTokenHandle token, string application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool bInheritHandles, uint creationFlags, IntPtr environment,
        string currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeProcessHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
}
