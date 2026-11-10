using System.Runtime.Versioning;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Services.Instances;

namespace BedrockSentry.Services.Security;

/// <summary>A folder's owner and list, as <see cref="AccessPlan"/> reads them.</summary>
public sealed record DirectoryList(string? OwnerSid, bool IsProtected, IReadOnlyCollection<AccessRuleRead> Rules);

/// <summary>
/// One folder beneath a data root, with its owner and list, or a link or a file with a
/// second name (<see cref="List"/> null): the root's list would reach what either names.
/// </summary>
public sealed record TreeEntry(string Path, DirectoryList? List);

/// <summary>One folder or file beneath a data root to label, or one refused because it is a link or has a second name.</summary>
public sealed record LabelTarget(string Path, bool Directory, bool Refused);

/// <summary>Reads and writes a planned access list on a folder or a file.</summary>
/// <remarks>Each refuses, by throwing <see cref="IOException"/>, a link or anything not at exactly the path given.</remarks>
public interface IAccessListWriter
{
    DirectoryList ReadDirectory(string path);

    /// <summary>
    /// Every folder beneath <paramref name="root"/>, every link and every file with a
    /// second hard link, without following a link.
    /// </summary>
    IEnumerable<TreeEntry> Beneath(string root);

    /// <summary>Creates a folder that carries <paramref name="planned"/> and <paramref name="ownerSid"/> from the start.</summary>
    /// <exception cref="IOException">Something already has the name.</exception>
    void Create(string path, IReadOnlyList<AccessEntry> planned, string ownerSid);

    /// <param name="ownerSid">The owner to give it, or null to leave the owner as it is.</param>
    void Directory(string path, IReadOnlyList<AccessEntry> planned, string? ownerSid);

    void File(string path, IReadOnlyList<AccessEntry> planned);

    /// <summary>
    /// Removes a folder's own entries for <paramref name="sid"/> and the copies beneath,
    /// keeping the rest of its list and its inheritance as they are.
    /// </summary>
    void RemoveEntries(string path, string sid);

    /// <summary>
    /// Gives a folder <see cref="Instances.ServerSandbox.LowLabel"/>, which what is made in
    /// it later inherits, and each folder and file already in it its own, one object at a
    /// time, so a server at low integrity can write in its folder (<c>D124</c>, <c>D126</c>, <c>T422</c>).
    /// </summary>
    void LabelLow(string path);

    /// <summary>
    /// Every folder and file beneath <paramref name="root"/> but <paramref name="except"/>
    /// and what is beneath it, without following a link.
    /// </summary>
    IEnumerable<LabelTarget> ToHide(string root, string except);

    /// <summary>
    /// Gives one folder <see cref="Instances.ServerSandbox.NoReadUpLabel"/> or one file
    /// <see cref="Instances.ServerSandbox.NoReadUpFileLabel"/>, carried down to nothing
    /// already beneath it (<c>D126</c>).
    /// </summary>
    void HideFromServers(string path, bool directory);
}

/// <summary>Grants the main service's account the data root and its secret files.</summary>
/// <remarks>
/// <para>Run elevated by the installer, never by the service: the service's account
/// holds <see cref="System.Security.AccessControl.FileSystemRights.Modify"/>, which
/// cannot write an access list it does not own.</para>
/// <para>Every write goes through a handle opened without following links
/// (<see cref="PinnedHandle"/>). The service's account can rename and replace
/// anything beneath the root, so a path it prepared could lead an elevated write to
/// any file on the machine; a link, a second hard link or a path that resolves
/// elsewhere is refused instead, and nothing further is written.</para>
/// <para>The root's grant reaches every file and folder beneath it that inherits,
/// which is all of them but <see cref="SecretFiles"/>: those break inheritance, so a
/// data root that survives a reinstall keeps lists naming only the account that wrote
/// them. Each that exists is written again here, or the service could not read its
/// own credential and certificate, and would fail at start or make new ones.</para>
/// <para>That reach is a walk by name, as an administrator, over everything beneath
/// the root. So the root's list is written over an existing root only when it, and
/// every folder beneath it, is owned by LocalSystem or Administrators and only they
/// could write there (<see cref="AccessPlan.WhyNotSpreadOver"/>), with no link
/// anywhere in it; then nobody else can change the tree while it is walked. It is
/// left alone when it already is the plan, which is every upgrade after the first. A
/// tree the service's account, or anyone else, could already write is refused rather
/// than walked. A new root is created carrying the plan, so no one gets in between
/// its creation and its list: <c>%ProgramData%</c> lets any user create in a new
/// folder beneath it.</para>
/// </remarks>
public static class DataRootPreparation
{
    /// <summary>What was written, one line each, for the installer to print.</summary>
    public static IReadOnlyList<string> Run(ISentryPaths paths, string serviceSid, IAccessListWriter writer)
    {
        List<string> done = [];

        var planned = AccessPlan.ForDataRoot(serviceSid);

        if (!System.IO.Directory.Exists(paths.DataRoot))
        {
            // Its parents as they come; only the root's own list is this installer's.
            if (Path.GetDirectoryName(Path.GetFullPath(paths.DataRoot)) is { } parent)
            {
                System.IO.Directory.CreateDirectory(parent);
            }

            writer.Create(paths.DataRoot, planned, AccessPlan.Administrators);
            done.Add(paths.DataRoot);
            LabelInstances(paths, writer, done);
            HideFromServers(paths, writer, done);
            return done;
        }

        var current = writer.ReadDirectory(paths.DataRoot);

        if (!AccessPlan.Matches(current.IsProtected, current.Rules, planned))
        {
            if (WhyNotWritten(paths.DataRoot, current, writer) is { } why)
            {
                throw new IOException(
                    $"The data root {paths.DataRoot} already exists and {why}, so its access list is not "
                    + "written over it. Give it and everything in it to LocalSystem and Administrators alone, "
                    + "owned by Administrators, or name a data root that does not exist yet, and run the "
                    + "installer again. A root granted to another account is refused the same way: to change "
                    + "the account the service runs as, uninstall, then install with the new one.");
            }

            // Administrators as the owner, so no earlier owner keeps the right to
            // rewrite the list written here.
            writer.Directory(paths.DataRoot, planned, AccessPlan.Administrators);
            done.Add(paths.DataRoot);

            LabelInstances(paths, writer, done);
        }

        foreach (var secret in SecretFiles.All(paths))
        {
            if (System.IO.File.Exists(secret))
            {
                writer.File(secret, AccessPlan.ForSecretFile(serviceSid));
                done.Add(secret);
            }
        }

        ShedFolderIdentities(paths, writer, done);
        HideFromServers(paths, writer, done);
        return done;
    }

    /// <summary>
    /// Each server's folder sheds the entry <c>D124</c> gave its AppContainer identity, which
    /// refuses a low-integrity server everything in it (<c>T409</c>), on every run.
    /// </summary>
    /// <remarks>
    /// Removing an allow entry only narrows what the list grants, so it is safe over a tree
    /// the service can write, unlike a grant spread there; a folder that refuses is noted
    /// and the service removes the entry from a folder it owns at the server's next start.
    /// </remarks>
    private static void ShedFolderIdentities(ISentryPaths paths, IAccessListWriter writer, List<string> done)
    {
        if (!System.IO.Directory.Exists(paths.Instances))
        {
            return;
        }

        foreach (var server in System.IO.Directory.GetDirectories(paths.Instances))
        {
            try
            {
                writer.RemoveEntries(server, ServerSandbox.SidFor(server));
                done.Add(server);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                done.Add($"{server} still holds the entry for its AppContainer identity: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The data root and everything in it but the instances folder labelled no-read-up,
    /// which a server's low-integrity token cannot read through (<c>D126</c>).
    /// </summary>
    /// <remarks>
    /// On every run, since every install before <c>D126</c> lacks it. Each object is
    /// labelled alone through a handle that refuses a link, so nothing is reached by name
    /// over a tree the service's account can change; a link or a file with a second name
    /// is left unlabelled and named. The root's label is inherited by what is made there
    /// later; the instances folder's own low label is protected from it.
    /// </remarks>
    private static void HideFromServers(ISentryPaths paths, IAccessListWriter writer, List<string> done)
    {
        writer.HideFromServers(paths.DataRoot, directory: true);
        var labelled = 1;

        foreach (var target in writer.ToHide(paths.DataRoot, paths.Instances))
        {
            if (target.Refused)
            {
                done.Add($"{target.Path} is a link or has a second name, so it is not labelled no-read-up");
                continue;
            }

            try
            {
                writer.HideFromServers(target.Path, target.Directory);
                labelled++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                done.Add($"{target.Path} could not be labelled no-read-up: {ex.Message}");
            }
        }

        done.Add($"{labelled} folders and files in {paths.DataRoot} labelled no-read-up for the servers");
    }

    /// <summary>
    /// The instances folder with the low label each server's folder inherits (<c>D124</c>),
    /// created here when it is missing so the label is there before any server is.
    /// </summary>
    /// <remarks>
    /// Only over a root just created or just found to be one only LocalSystem and
    /// Administrators could write: what is already there is labelled one object at a
    /// time. A root that already matched the plan was labelled when it was first written.
    /// </remarks>
    private static void LabelInstances(ISentryPaths paths, IAccessListWriter writer, List<string> done)
    {
        System.IO.Directory.CreateDirectory(paths.Instances);
        writer.LabelLow(paths.Instances);
        done.Add(paths.Instances);
    }

    /// <summary>The first reason the tree is not one only LocalSystem and Administrators could change, or null.</summary>
    private static string? WhyNotWritten(string root, DirectoryList current, IAccessListWriter writer)
    {
        if (AccessPlan.WhyNotSpreadOver(current.OwnerSid, current.Rules) is { } why)
        {
            return why;
        }

        foreach (var entry in writer.Beneath(root))
        {
            if (entry.List is not { } list)
            {
                return $"{entry.Path} is a link or has a second name";
            }

            if (AccessPlan.WhyNotSpreadOver(list.OwnerSid, list.Rules) is { } beneath)
            {
                return $"at {entry.Path}, {beneath}";
            }
        }

        return null;
    }
}

/// <summary><see cref="IAccessListWriter"/> through <see cref="PinnedHandle"/> and <see cref="WindowsAcl"/>.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAccessListWriter : IAccessListWriter
{
    public DirectoryList ReadDirectory(string path)
    {
        // Opened first only to refuse a link: the read below goes by the same path.
        if (PinnedHandle.Open(Path.GetFullPath(path), PinnedHandle.ReadControl, directory: true, out var refusal) is not { } opened)
        {
            throw new IOException(refusal);
        }

        opened.Handle.Dispose();
        return WindowsAcl.Read(new DirectoryInfo(path));
    }

    public IEnumerable<TreeEntry> Beneath(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
        };

        // .NET does not recurse into a link; each is answered as one, and the first ends
        // the walk.
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", options))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                yield return new TreeEntry(entry.FullName, null);
            }
            else if (entry is FileInfo)
            {
                // Refused when it has more than one name, among other things; a file
                // that cannot be pinned is not one to walk a list over either.
                if (PinnedHandle.Open(entry.FullName, 0, directory: false, out _) is { } pinned)
                {
                    pinned.Handle.Dispose();
                }
                else
                {
                    yield return new TreeEntry(entry.FullName, null);
                }
            }
            else if (entry is DirectoryInfo directory)
            {
                yield return new TreeEntry(entry.FullName, WindowsAcl.Read(directory));
            }
        }
    }

    public void Create(string path, IReadOnlyList<AccessEntry> planned, string ownerSid) =>
        WindowsAcl.Create(Path.GetFullPath(path), planned, ownerSid);

    public void Directory(string path, IReadOnlyList<AccessEntry> planned, string? ownerSid) =>
        Write(path, directory: true, planned, ownerSid);

    public void File(string path, IReadOnlyList<AccessEntry> planned) =>
        Write(path, directory: false, planned, ownerSid: null);

    public void RemoveEntries(string path, string sid)
    {
        if (PinnedHandle.Open(Path.GetFullPath(path), PinnedHandle.ReadControl | PinnedHandle.WriteDac, directory: true, out var refusal)
            is not { } opened)
        {
            throw new IOException(refusal);
        }

        using (opened.Handle)
        {
            if (Broker.WindowsFolderGrant.RemoveEntries(opened.Handle, opened.Path, new System.Security.Principal.SecurityIdentifier(sid)) is { } failed)
            {
                throw new IOException(failed);
            }
        }
    }

    public IEnumerable<LabelTarget> ToHide(string root, string except)
    {
        var skip = Path.TrimEndingDirectorySeparator(Path.GetFullPath(except));
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));

        while (pending.Count > 0)
        {
            foreach (var entry in new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos())
            {
                if (string.Equals(Path.TrimEndingDirectorySeparator(entry.FullName), skip, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    yield return new LabelTarget(entry.FullName, entry is DirectoryInfo, Refused: true);
                }
                else if (entry is DirectoryInfo)
                {
                    yield return new LabelTarget(entry.FullName, Directory: true, Refused: false);
                    pending.Push(entry.FullName);
                }
                else
                {
                    yield return new LabelTarget(entry.FullName, Directory: false, Refused: false);
                }
            }
        }
    }

    public void HideFromServers(string path, bool directory)
    {
        // Refuses a link and a file with a second name, as the walk above cannot.
        if (PinnedHandle.Open(Path.GetFullPath(path), PinnedHandle.ReadControl | PinnedHandle.WriteOwner, directory, out var refusal)
            is not { } opened)
        {
            throw new IOException(refusal);
        }

        using (opened.Handle)
        {
            var label = directory ? ServerSandbox.NoReadUpLabel : ServerSandbox.NoReadUpFileLabel;

            if (Broker.WindowsFolderGrant.LabelOne(opened.Handle, opened.Path, label) is { } failed)
            {
                throw new IOException(failed);
            }
        }
    }

    public void LabelLow(string path)
    {
        if (PinnedHandle.Open(Path.GetFullPath(path), PinnedHandle.ReadControl | PinnedHandle.WriteOwner, directory: true, out var refusal)
            is not { } opened)
        {
            throw new IOException(refusal);
        }

        using (opened.Handle)
        {
            if (Broker.WindowsFolderGrant.LabelLow(opened.Handle, opened.Path) is { } failed)
            {
                throw new IOException(failed);
            }
        }
    }

    private static void Write(string path, bool directory, IReadOnlyList<AccessEntry> planned, string? ownerSid)
    {
        var access = PinnedHandle.ReadControl | PinnedHandle.WriteDac | (ownerSid is null ? 0 : PinnedHandle.WriteOwner);

        if (PinnedHandle.Open(Path.GetFullPath(path), access, directory, out var refusal) is not { } opened)
        {
            throw new IOException(refusal);
        }

        using (opened.Handle)
        {
            WindowsAcl.Apply(opened.Handle, directory, planned, ownerSid);
        }
    }
}
