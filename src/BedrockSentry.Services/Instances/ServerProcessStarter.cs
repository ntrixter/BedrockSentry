using System.Diagnostics;
using System.Text;
using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Backups;

namespace BedrockSentry.Services.Instances;

/// <summary>A started <c>bedrock_server.exe</c> and the three ends of its console.</summary>
/// <param name="Process">The process, for its id, its exit and a kill.</param>
/// <param name="Input">Its standard input, UTF-8 without a byte-order mark.</param>
/// <param name="Output">Its standard output, read as UTF-8.</param>
/// <param name="Error">Its standard error, read as UTF-8.</param>
/// <param name="Held">Held until disposed, beside <paramref name="Process"/>; null where nothing else is.</param>
public sealed record StartedServer(
    Process Process, StreamWriter Input, StreamReader Output, StreamReader Error, IDisposable? Held = null) : IDisposable
{
    public void Dispose()
    {
        Input.Dispose();
        Output.Dispose();
        Error.Dispose();
        Held?.Dispose();
        Process.Dispose();
    }
}

/// <summary>Starts a server's process with its console redirected.</summary>
/// <remarks>
/// One place for it, so every server, the throwaway runs included, starts the same
/// way: at low integrity on Windows (<see cref="RestrictedServerStarter"/>),
/// and as a plain child elsewhere.
/// </remarks>
public interface IServerProcessStarter
{
    /// <exception cref="ServerFolderNotGrantedException">The folder could not be given to the server's token.</exception>
    StartedServer Start(string executablePath, string workingDirectory);
}

/// <summary>
/// The encodings the console uses. BDS writes UTF-8, and left to its default .NET
/// decodes with the console's code page, which mangles every non-ASCII player name;
/// <c>say</c> and <c>kick</c> carry player-written text back the other way.
/// </summary>
public static class ServerConsole
{
    public static readonly UTF8Encoding Encoding = new(encoderShouldEmitUTF8Identifier: false);
}

/// <summary>A plain child process: off Windows, where there is no integrity level.</summary>
public sealed class PlainServerStarter : IServerProcessStarter
{
    public StartedServer Start(string executablePath, string workingDirectory)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = ServerConsole.Encoding,
                StandardErrorEncoding = ServerConsole.Encoding,
                StandardInputEncoding = ServerConsole.Encoding,
            },
            EnableRaisingEvents = true,
        };

        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        return new StartedServer(process, process.StandardInput, process.StandardOutput, process.StandardError);
    }
}

/// <summary>Whether a server's folder still carries the entry <c>D124</c> wrote for its identity, and its removal.</summary>
public interface IServerFolderAccess
{
    /// <summary>Whether the folder's own list holds an entry for <paramref name="sid"/>.</summary>
    bool HoldsFolderIdentity(string workingDirectory, string sid);

    /// <summary>Removes it, and the copies beneath, as the account this service runs as.</summary>
    /// <exception cref="UnauthorizedAccessException">This account does not own the folder.</exception>
    /// <exception cref="IOException">The folder is a link, or the list could not be written.</exception>
    void RemoveFolderIdentity(string workingDirectory, string sid);

    /// <summary>
    /// Whether the folder carries a low mandatory label its contents inherit
    /// (<see cref="ServerSandbox.IsLowLabelled"/>), which a server at low integrity
    /// needs to write there. The service cannot add one: that takes <c>WRITE_OWNER</c>,
    /// which its account does not hold.
    /// </summary>
    bool IsLowLabelled(string workingDirectory);
}

/// <summary>Creates a server's process under the token <see cref="ServerTokenPlan"/> describes.</summary>
public interface IRestrictedProcessCreator
{
    StartedServer Create(string executablePath, string workingDirectory, ServerTokenPlan plan);
}

/// <summary>
/// Starts a server under the token <see cref="ServerSandbox"/> describes, removing
/// first the entry <c>D124</c> gave its folder's AppContainer identity.
/// </summary>
/// <remarks>
/// <para>An entry for an AppContainer SID in a folder's list refuses a low-integrity token
/// outside an AppContainer everything beneath it, the folder's own listing included,
/// though nothing in the token matches it (<c>T409</c>, 2026-10-29: the same folder
/// without it read). A server's executable inherits its folder's low label, so the
/// process runs at low integrity whatever level its token asks for.</para>
/// <para>The removal is made as the service's own account, which may change the list of
/// a folder it created and of no other. A folder the service did not create sheds it by
/// the installer (the data root's server folders) or by the broker's grant; one neither
/// reached refuses the start with <see cref="ServerFolderNotGrantedException"/>. So does
/// a folder without the low label: the installer labels the instances folder, whose
/// server folders inherit it, and the broker labels a folder it grants.</para>
/// </remarks>
public sealed class RestrictedServerStarter(IServerFolderAccess access, IRestrictedProcessCreator creator) : IServerProcessStarter
{
    public StartedServer Start(string executablePath, string workingDirectory)
    {
        var plan = ServerSandbox.TokenFor(workingDirectory);

        if (access.HoldsFolderIdentity(workingDirectory, plan.FolderSid))
        {
            try
            {
                access.RemoveFolderIdentity(workingDirectory, plan.FolderSid);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                throw new ServerFolderNotGrantedException(workingDirectory, ex.Message, ex);
            }
        }

        if (!access.IsLowLabelled(workingDirectory))
        {
            throw new ServerFolderNotGrantedException(
                workingDirectory,
                "it carries no low integrity label, and a server runs at low integrity, so it could write nothing there");
        }

        ServerProfileFolders.Prepare(workingDirectory);

        return creator.Create(executablePath, workingDirectory, plan);
    }
}

/// <summary>Makes the profile folders a server is pointed at (<c>D123</c>).</summary>
public static class ServerProfileFolders
{
    /// <summary>Creates each of <see cref="ServerSandbox.ProfileEnvironment"/>'s folders that is missing.</summary>
    /// <remarks>
    /// Made by the service, under the server's folder, so each inherits the folder's
    /// grant. A link on the way is refused rather than followed: the server could have
    /// left one there while it ran.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A link is on the way to one of them; the message names it.</exception>
    public static void Prepare(string workingDirectory)
    {
        var root = ServerSandbox.Canonical(workingDirectory);

        foreach (var folder in ServerSandbox.ProfileEnvironment(workingDirectory).Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ServerTree.LinkOnPath(root, folder) is { } link)
            {
                throw new InvalidOperationException(ServerTree.Refusal(link));
            }

            Directory.CreateDirectory(folder);
        }
    }
}

/// <summary>The starter for this machine.</summary>
public static class ServerStarters
{
    public static IServerProcessStarter ForThisMachine() =>
        OperatingSystem.IsWindows()
            ? new RestrictedServerStarter(new WindowsServerFolderAccess(), new WindowsRestrictedProcessCreator())
            : new PlainServerStarter();
}
