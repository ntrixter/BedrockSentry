using BedrockSentry.Core.Broker;
using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Instances;
using BedrockSentry.Services.Security;

namespace BedrockSentry.Services.Broker;

/// <summary>
/// Grants the main service's account an existing folder outside the data root, as an
/// administrator asked by name (<c>install.ps1 -GrantFolder</c>, <c>D115</c> (b)).
/// </summary>
/// <remarks>
/// <para>The broker grants only a folder it makes or one that is empty, because the
/// folder it is asked for comes from a configuration the service's account writes. A
/// folder that already holds a server's files is handed over here instead: the
/// administrator typing its path is the decision the broker never has to make.</para>
/// <para>The same path rules as the broker's grant, so no administrator hands over a
/// system folder, the install tree or a drive root by mistyping. The grant is written on
/// the folder and inherited by everything in it, so the tree is walked first and refused
/// whole on a link or a file with a second name: either would carry the grant to
/// something outside the folder.</para>
/// <para>The entry <c>D124</c> gave the folder's own server identity
/// (<see cref="ServerSandbox.SidFor"/>) is removed, which a low-integrity server needs
/// (<c>T409</c>), and the folder labelled low; on a backups or packages folder there is
/// no such entry, and a server adopted here can start.</para>
/// </remarks>
public static class AdministratorGrant
{
    /// <summary>Grants <paramref name="folder"/>, creating it when it is missing, and records it.</summary>
    /// <param name="junctionsRefused">
    /// Whether this process refuses a junction a non-administrator made
    /// (<see cref="Security.RedirectionGuard"/>). A server's folder in the instances
    /// folder is one the service's account can write while the grant spreads over it,
    /// so it is granted only then.
    /// </param>
    public static SystemCheckFix Run(
        string folder,
        BrokerPathContext context,
        IReparseProbe probe,
        IFolderPin pin,
        IFolderGrant grant,
        IAccessListWriter tree,
        BrokerRecord record,
        bool junctionsRefused)
    {
        if (!junctionsRefused && UseFor(folder, context) == BrokerPathUse.Exclude)
        {
            return new SystemCheckFix(false,
                $"'{Path.GetFullPath(folder)}' is in the data folder, which the service can change while the grant "
                + "spreads over it, and this Windows cannot refuse a junction the service made there. Nothing "
                + "was granted. Move the server to a new folder outside the data folder and grant that.");
        }

        if (BrokerPathRules.Refuse(folder, UseFor(folder, context), context, probe) is { } refused)
        {
            return new SystemCheckFix(false, refused);
        }

        var full = Path.GetFullPath(folder);
        Directory.CreateDirectory(full);

        // Asked again now the folder exists, as the broker's own grant does.
        if (BrokerPathRules.Refuse(full, UseFor(full, context), context, probe) is { } after)
        {
            return new SystemCheckFix(false, after);
        }

        using var pinned = pin.Open(full, changeAccess: true, out var unopened);

        if (pinned is null)
        {
            return new SystemCheckFix(false, unopened!);
        }

        foreach (var entry in tree.Beneath(pinned.Path))
        {
            if (entry.List is null)
            {
                return new SystemCheckFix(false,
                    $"'{entry.Path}' is a link, has a second name or could not be opened, so the grant on "
                    + $"'{pinned.Path}' might reach past it. Nothing was granted. Remove the link, or copy the folder's contents to a new "
                    + "folder, and run the command again.");
            }
        }

        // The folder already holds a server's files, which take the label one at a time (T419).
        var granted = grant.GrantModify(pinned, ServerSandbox.SidFor(pinned.Path));

        if (granted.Succeeded)
        {
            record.AddGrantedFolder(pinned.Path);
        }

        return granted;
    }

    /// <summary>
    /// A server's folder beneath the data root's instances folder is the one place the
    /// broker's grant refuses that this may grant: the root's grant covers the service
    /// there, but a folder made before its server's identity existed lacks that entry.
    /// </summary>
    private static BrokerPathUse UseFor(string folder, BrokerPathContext context)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var instances = Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.InstancesRoot));

        return ReservedSystemDirectories.IsAtOrUnder(full, instances)
               && !string.Equals(full, instances, StringComparison.OrdinalIgnoreCase)
            ? BrokerPathUse.Exclude
            : BrokerPathUse.Grant;
    }
}
