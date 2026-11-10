using System.Collections.Immutable;
using BedrockSentry.Core.Instances;

namespace BedrockSentry.Core.Broker;

/// <summary>
/// Whether the broker may act on a folder: grant the service account
/// <c>Modify</c> on it, exclude it from Defender, or snapshot its volume.
/// </summary>
/// <param name="InstallRoot">The install tree, which the service account must never write.</param>
/// <param name="DataRoot">The data root, whose ACL the installer owns.</param>
/// <param name="InstancesRoot">The default home of a server's working directory, beneath <paramref name="DataRoot"/>.</param>
/// <param name="Reserved">The <c>D43</c> rules, <see cref="ReservedSystemDirectories.All"/> on a real machine.</param>
public sealed record BrokerPathContext(
    string InstallRoot,
    string DataRoot,
    string InstancesRoot,
    ImmutableArray<ReservedSystemDirectory> Reserved);

/// <summary>What the broker is about to do to a folder.</summary>
public enum BrokerPathUse
{
    /// <summary><c>Modify</c> for the service account (<see cref="BrokerOperation.GrantFolder"/>).</summary>
    Grant = 1,

    /// <summary>A Defender exclusion (<see cref="BrokerOperation.ExcludeServerFolder"/>).</summary>
    Exclude = 2,

    /// <summary>A shadow copy of its volume (<see cref="BrokerOperation.CreateShadowCopy"/>).</summary>
    Snapshot = 3,
}

/// <summary>Answers whether a folder, or any folder above it, is a reparse point.</summary>
/// <remarks>
/// A seam because the answer comes from the file system, and the rule that reads it
/// must be testable with a junction on a machine that has none.
/// </remarks>
public interface IReparseProbe
{
    /// <summary>
    /// The first existing folder from the volume root down to <paramref name="full"/>,
    /// inclusive, that is a reparse point, or null when none is.
    /// </summary>
    string? FirstReparsePoint(string full);
}

/// <summary>The probe that asks the file system.</summary>
public sealed class FileSystemReparseProbe : IReparseProbe
{
    public string? FirstReparsePoint(string full)
    {
        var root = Path.GetPathRoot(full);
        var path = full;
        string? first = null;

        // Walked upwards and kept the topmost, so the message names the junction
        // somebody planted rather than a folder beneath it.
        while (!string.IsNullOrEmpty(path) && !string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            var info = new DirectoryInfo(path);

            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                first = path;
            }

            path = Path.GetDirectoryName(path);
        }

        return first;
    }
}

/// <summary>
/// The folders the broker refuses to act on, whatever the configuration says.
/// </summary>
/// <remarks>
/// <para>The folder comes from the configuration, which the service account writes,
/// so a compromised main service chooses it. These refusals bound what that buys:
/// never the operating system's folders (<c>D43</c>), a drive root, Program Files,
/// the install tree, the data root's own folders or anything above them, and never a
/// path through a reparse point, which would let a folder that passed these checks
/// lead somewhere that does not.</para>
/// <para>Each rule is a reason in a sentence rather than a bool, and the first one
/// that applies is the answer, so a refusal says which rule refused it.</para>
/// </remarks>
public static class BrokerPathRules
{
    /// <summary>Why the broker refuses to act on <paramref name="requested"/>, or null.</summary>
    public static string? Refuse(
        string? requested, BrokerPathUse use, BrokerPathContext context, IReparseProbe probe)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return "No folder was given.";
        }

        if (ReservedSystemDirectories.RefuseAnUnsupportedPathForm(requested) is { } form)
        {
            return form;
        }

        if (RefuseSpelling(requested.Trim()) is { } spelling)
        {
            return spelling;
        }

        // Asked of the path as given and again once it is resolved: the first catches
        // what reads as a system folder whatever this platform makes of it, the
        // second what only becomes one, such as 'C:\Servers\..\Windows'.
        if (Fixed(requested.Trim(), context) is { } given)
        {
            return given;
        }

        if (!Path.IsPathFullyQualified(requested))
        {
            return $"'{requested}' is not a full path.";
        }

        var full = Path.GetFullPath(requested);

        if (Fixed(full, context) is { } resolved)
        {
            return resolved;
        }

        if (Overlaps(full, context.InstallRoot))
        {
            return $"'{full}' overlaps the install folder '{context.InstallRoot}', which the service must not write.";
        }

        // A server in the default place, beneath the data root's instances folder:
        // the installer's grant on the data root already covers it, so a grant is
        // refused as unneeded, while an exclusion or a snapshot of it is the
        // ordinary case. Anywhere else in the data root holds the credentials, the
        // certificate and the keys, and no use reaches those.
        if (IsBeneath(full, context.InstancesRoot))
        {
            if (use == BrokerPathUse.Grant)
            {
                return $"'{full}' is in the data folder, which the installer already grants.";
            }
        }
        else if (Overlaps(full, context.DataRoot))
        {
            return $"'{full}' overlaps the data folder '{context.DataRoot}', whose permissions the installer sets.";
        }

        if (probe.FirstReparsePoint(full) is { } link)
        {
            return $"'{link}' is a junction or symbolic link, so '{full}' may not be where it appears to be.";
        }

        return null;
    }

    /// <summary>
    /// Spellings that name a folder the comparisons below would not recognise as the
    /// same one, refused outright because no server folder needs them.
    /// </summary>
    /// <remarks>
    /// <para>A leading backslash covers <c>\??\</c>, which .NET treats as extended and
    /// leaves unresolved, and every share or device form: a server folder is a drive
    /// letter and a path.</para>
    /// <para>A colon after the drive's names a stream: <c>C:\Windows::$INDEX_ALLOCATION</c>
    /// opens the folder itself, while the comparisons see a different name.</para>
    /// <para>A segment ending in a dot or a space is stripped by Windows and kept by every
    /// comparison here, and a <c>~</c> followed by a digit reads as an 8.3 short name
    /// (<c>PROGRA~1</c>), which Windows expands and <c>GetFullPath</c> does not.</para>
    /// </remarks>
    private static string? RefuseSpelling(string path)
    {
        // A leading '/' is not fully qualified on Windows and is refused below; on
        // any other platform it is the only way a full path starts.
        if (path.Length > 0 && path[0] == '\\')
        {
            return $"'{path}' does not start with a drive letter.";
        }

        var colon = path.IndexOf(':');

        if (colon >= 0 && (colon != 1 || path.IndexOf(':', colon + 1) >= 0))
        {
            return $"'{path}' names a stream or holds a colon after the drive.";
        }

        foreach (var segment in path.Split('\\', '/'))
        {
            if (segment is "." or ".." || segment.Length == 0)
            {
                continue;
            }

            if (segment[^1] is '.' or ' ')
            {
                return $"'{path}' has a folder name ending in a dot or a space, which Windows strips.";
            }

            var tilde = segment.IndexOf('~');

            if (tilde >= 0 && tilde + 1 < segment.Length && char.IsAsciiDigit(segment[tilde + 1]))
            {
                return $"'{path}' looks like a short (8.3) name; give the folder's full name.";
            }
        }

        return null;
    }

    /// <summary>The refusals that hold wherever the product is installed.</summary>
    private static string? Fixed(string path, BrokerPathContext context) =>
        ReservedSystemDirectories.RefuseAVolumeRoot(path)
        ?? ReservedSystemDirectories.RefuseAVolumeFolder(path)
        ?? ReservedSystemDirectories.Refuse(path, context.Reserved);

    private static bool IsBeneath(string full, string folder) =>
        ReservedSystemDirectories.IsAtOrUnder(full, folder)
        && !ReservedSystemDirectories.IsAtOrUnder(folder, full);

    /// <summary>Either folder is at or beneath the other.</summary>
    private static bool Overlaps(string full, string folder) =>
        ReservedSystemDirectories.IsAtOrUnder(full, folder)
        || ReservedSystemDirectories.IsAtOrUnder(folder, full);
}
