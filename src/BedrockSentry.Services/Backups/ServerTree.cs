namespace BedrockSentry.Services.Backups;

/// <summary>
/// Finds a link in a folder a server writes, before this service reads from it or
/// writes into it as data.
/// </summary>
/// <remarks>
/// <para>At low integrity (milestone 18.5, <c>D126</c>) a server can make a junction in
/// its own folder. A copy out of that folder would then read whatever the junction
/// names, a credential or a certificate, and a write into it would land there. So a
/// tree is refused whole when anything in it is a link; nothing is followed to find
/// out. <see cref="Security.RedirectionGuard"/> refuses such a junction for every open
/// where Windows has it; this is the check that holds where it does not.</para>
/// <para>A walk by name, so a server that is running can still make one after it. The
/// callers walk a server they have stopped, or a folder only this service writes.</para>
/// <para>A file Data Deduplication optimized is a reparse point but no link
/// (<see cref="Security.ReparseTags"/>), so a server on a deduplicated volume is walked,
/// and its files are then read through <see cref="ServerFileReader"/>.</para>
/// </remarks>
public static class ServerTree
{
    /// <summary>The first link at or beneath <paramref name="root"/>, or null when there is none.</summary>
    public static string? FirstLink(string root)
    {
        var top = new DirectoryInfo(root);

        if (!top.Exists)
        {
            return null;
        }

        if (Security.ReparseTags.IsLink(top))
        {
            return top.FullName;
        }

        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
        var pending = new Stack<DirectoryInfo>();
        pending.Push(top);

        // One folder at a time, never into a link: a recursive enumeration opened a
        // junction RedirectionGuard refuses before the link was seen, and threw "untrusted
        // mount point" in place of naming it (T392 clauses 2 and 4).
        while (pending.TryPop(out var folder))
        {
            foreach (var entry in folder.EnumerateFileSystemInfos("*", options))
            {
                if (Security.ReparseTags.IsLink(entry))
                {
                    return entry.FullName;
                }

                if (entry is DirectoryInfo directory)
                {
                    pending.Push(directory);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// <paramref name="root"/> when it is a link, or the first file directly in it that
    /// is one: where a server's own files sit beside the folders an administrator may
    /// link on purpose, such as a development pack folder.
    /// </summary>
    public static string? LinkedFileIn(string root)
    {
        var top = new DirectoryInfo(root);

        if (!top.Exists)
        {
            return null;
        }

        if (Security.ReparseTags.IsLink(top))
        {
            return top.FullName;
        }

        return top.EnumerateFiles("*", new EnumerationOptions { AttributesToSkip = 0 })
            .FirstOrDefault(Security.ReparseTags.IsLink)
            ?.FullName;
    }

    /// <summary>
    /// The first link among the folders from <paramref name="root"/> down to
    /// <paramref name="path"/> that exist, both included, or null.
    /// </summary>
    public static string? LinkOnPath(string root, string path)
    {
        var top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        List<string> chain = [];

        // Each parent is shorter, so the walk ends at the root.
        for (string? folder = current; folder is not null && folder.Length >= top.Length; folder = Path.GetDirectoryName(folder))
        {
            chain.Add(folder);
        }

        chain.Reverse();

        return chain.FirstOrDefault(IsLink);
    }

    private static bool IsLink(string path)
    {
        try
        {
            return Security.ReparseTags.Why(path) is not null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Why a tree holding <paramref name="link"/> is not read or written, naming its reparse tag (<c>T443</c>).</summary>
    public static string Refusal(string link) =>
        $"'{link}' is {What(link)}, so nothing in the server's folder was read or written through it. "
        + "A server does not make links; remove it with the server stopped and try again.";

    /// <summary>What <paramref name="link"/> is, with its tag (<see cref="Security.ReparseTags.Describe"/>); "a link" when it is gone.</summary>
    public static string What(string link)
    {
        try
        {
            return Security.ReparseTags.Why(link) ?? "a link";
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return "a link";
        }
    }
}
