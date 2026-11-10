using BedrockSentry.Core.Addons;

namespace BedrockSentry.Services.Addons;

/// <summary>What the Add-ons tab's Remove says before and after it deletes (<c>T363</c>).</summary>
/// <remarks>
/// Here rather than in the markup, so the sentence that decides whether somebody deletes
/// the only copy of a version is checked by the suite.
/// </remarks>
public static class ServerPackRemovalText
{
    /// <summary>The confirmation's heading.</summary>
    public static string Heading(AddonRow row) => $"Remove {row.Name}";

    /// <summary>What Remove does to this server, and whether the library can put it back.</summary>
    /// <param name="row">The pack's row on this server.</param>
    /// <param name="server">The server's name.</param>
    /// <param name="shelved">The library's copy of the same uuid, or null when it holds none.</param>
    /// <remarks>
    /// The library is named by version because a uuid on the shelf is not this copy:
    /// a library holding 1.2.0 cannot put back 1.1.0, which is the case Switch off
    /// keeps a folder for (<c>T277</c>).
    /// </remarks>
    public static string Confirm(AddonRow row, string server, LibraryPack? shelved)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentException.ThrowIfNullOrWhiteSpace(server);

        var deletes = $"This deletes every copy of {row.Name} in {server}'s folders and every entry its world "
            + "keeps for it.";

        if (shelved is null || shelved.Versions.Count == 0)
        {
            return $"{deletes} The add-on library holds no copy of it, so it cannot be put back from there."
                + Keep(row);
        }

        if (row.Parsed is { } version && shelved.Versions.Any(v => v.Version == version))
        {
            return $"{deletes} The add-on library keeps {version}, so it can be switched on here again.";
        }

        var held = Join(shelved.Versions.Select(v => v.Version.ToString()).ToList());

        return $"{deletes} The add-on library holds {held}, not {row.Version}, so {row.Version} cannot be put "
            + $"back from there. Once it is gone, {shelved.Newest.Version} can be switched on here from the "
            + "library." + Keep(row);
    }

    /// <summary>The way to keep this copy, when the row offers one (<c>D78</c> (a)).</summary>
    private static string Keep(AddonRow row) =>
        row.CanCopyToLibrary ? " Copy to library keeps a copy there first." : "";

    /// <summary>What the tab says once the pack is gone.</summary>
    public static string Done(AddonRow row, string server)
    {
        ArgumentNullException.ThrowIfNull(row);

        return $"Removed {row.Name} from {server}.";
    }

    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        1 => items[0],
        _ => $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}",
    };
}
