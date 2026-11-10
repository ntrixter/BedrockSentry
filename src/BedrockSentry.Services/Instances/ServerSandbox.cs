using System.Security.AccessControl;
using System.Text;
using BedrockSentry.Core.Broker;
using BedrockSentry.Services.Security;

namespace BedrockSentry.Services.Instances;

/// <summary>
/// The token a <c>bedrock_server.exe</c> runs under (milestone 18.5, <c>D126</c>),
/// described without touching Windows so the rules can be tested anywhere.
/// </summary>
/// <param name="FolderSid">
/// The folder's own identity, which names the server's desktop and is granted on its folder.
/// No token holds it since <c>D126</c>; the grant stays for a later clean-up.
/// </param>
/// <param name="IntegritySid">The integrity level the token runs at.</param>
/// <param name="DefaultDaclSids">
/// Who the server's own new objects (its process, threads and pipes) admit, besides the
/// account it runs as.
/// </param>
public sealed record ServerTokenPlan(string FolderSid, string IntegritySid, IReadOnlyList<string> DefaultDaclSids);

/// <summary>Works out a server's token and what its folder needs.</summary>
/// <remarks>
/// <para>The token is the service account's own with every privilege but
/// <c>SeChangeNotifyPrivilege</c> removed, at low integrity. A low-integrity token
/// writes nothing that does not carry a low label, which only the server folders do
/// (<see cref="LowLabel"/>), and reads nothing labelled no-read-up, which the installer
/// sets on the rest of the data root (<see cref="NoReadUpLabel"/>): the certificate and
/// its password, the credential, the setup token, the DataProtection keys, the
/// database, the backups and the update folder stay out of its reach. Servers can read
/// one another's folders, which the operator accepted (<c>D126</c>).</para>
/// <para>An AppContainer (<c>D124</c>) took no connection from another process on its
/// own machine (<c>T409</c>), so a player on the server's PC could not join; a low
/// integrity token outside one signed in to Minecraft services and took a localhost join
/// (2026-10-28). A token restricted by a list of SIDs (<c>D121</c>) failed that sign-in
/// (<c>T385</c> clause 1).</para>
/// </remarks>
public static class ServerSandbox
{
    /// <summary>The low mandatory level a server's token runs at.</summary>
    public const string LowIntegrity = "S-1-16-4096";

    /// <summary>
    /// The path the container's name is worked out from: full, without a trailing separator.
    /// </summary>
    public static string Canonical(string workingDirectory) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));

    /// <summary>The name a server folder's identity is derived from, as an AppContainer's was (<c>D124</c>).</summary>
    /// <remarks>
    /// A hash of the upper-cased path, since an AppContainer name holds at most 64
    /// characters and no path separator, and Windows paths ignore case.
    /// </remarks>
    public static string ContainerNameFor(string workingDirectory)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(Canonical(workingDirectory).ToUpperInvariant()));

        return "BedrockSentry.Server." + Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    /// <summary>The SID Windows derives for an AppContainer name.</summary>
    /// <remarks>
    /// What <c>DeriveAppContainerSidFromAppContainerName</c> answers: <c>S-1-15-2-</c>
    /// and the first 28 bytes of the SHA-256 of the lower-cased name in UTF-16LE, as
    /// seven little-endian sub-authorities.
    /// </remarks>
    public static string ContainerSid(string containerName)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.Unicode.GetBytes(containerName.ToLowerInvariant()));
        var parts = Enumerable.Range(0, 7).Select(i => BitConverter.ToUInt32(hash, i * 4).ToString(System.Globalization.CultureInfo.InvariantCulture));

        return "S-1-15-2-" + string.Join('-', parts);
    }

    /// <summary>The identity a server's folder is granted to: its container's SID.</summary>
    public static string SidFor(string workingDirectory) => ContainerSid(ContainerNameFor(workingDirectory));

    public static ServerTokenPlan TokenFor(string workingDirectory)
    {
        var folder = SidFor(workingDirectory);

        return new ServerTokenPlan(folder, LowIntegrity, [AccessPlan.LocalSystem]);
    }

    /// <summary>
    /// The mandatory label a server's folder carries, inherited by everything in it:
    /// low, refusing writes from below low only.
    /// </summary>
    /// <remarks>
    /// The server's token runs at low integrity, and an object with no label reads as
    /// medium, which refuses it every write whatever its access list grants.
    /// </remarks>
    public const string LowLabel = "S:(ML;OICI;NW;;;LW)";

    /// <summary>The same label on a file, which has nothing to pass on (<c>T419</c>).</summary>
    public const string LowFileLabel = "S:(ML;;NW;;;LW)";

    /// <summary>
    /// The label the data root and every folder beneath it but the instances folder
    /// carry: medium, refusing reads and writes from below, inherited by what is made there.
    /// </summary>
    public const string NoReadUpLabel = "S:(ML;OICI;NRNW;;;ME)";

    /// <summary>The same label on a file, which has nothing to pass on.</summary>
    public const string NoReadUpFileLabel = "S:(ML;;NRNW;;;ME)";

    /// <summary>
    /// Whether a label part in SDDL, as <c>LABEL_SECURITY_INFORMATION</c> reads it,
    /// lets a low-integrity token write: a low or untrusted label, set or inherited,
    /// that a folder's children inherit too.
    /// </summary>
    public static bool IsLowLabelled(string sddl)
    {
        var index = sddl.IndexOf("S:", StringComparison.Ordinal);

        if (index < 0)
        {
            return false;
        }

        foreach (var ace in sddl[(index + 2)..].Split('(', StringSplitOptions.RemoveEmptyEntries).Select(a => a.TrimEnd(')')))
        {
            var fields = ace.Split(';');

            if (fields.Length >= 6
                && fields[0] == "ML"
                && fields[1].Contains("OI", StringComparison.Ordinal)
                && fields[1].Contains("CI", StringComparison.Ordinal)
                && !fields[1].Contains("IO", StringComparison.Ordinal)
                && fields[5] is "LW" or "S-1-16-4096" or "S-1-16-0")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The folder in a server's own folder that stands in for a user profile (<c>D123</c>).</summary>
    public const string ProfileFolderName = "sentry-profile";

    /// <summary>
    /// The variables that point a server's profile folders into its own folder (<c>D123</c>).
    /// </summary>
    /// <remarks>
    /// Bedrock creates <c>Minecraft Bedrock Server</c> under the roaming application-data
    /// folder and <c>minecraftpe</c> under the temporary folder before it signs in to
    /// Minecraft services. Inherited from the service, those are the service account's
    /// own profile, which the server's token is kept out of (<c>T385</c> clause 1). Under
    /// the server's own folder they are covered by its folder grant and its label, and
    /// one server's files are not another's. <c>USERPROFILE</c> is set too, because the profile's
    /// known folders are stored as paths under it.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> ProfileEnvironment(string workingDirectory)
    {
        var profile = Path.Combine(Canonical(workingDirectory), ProfileFolderName);
        var roaming = Path.Combine(profile, "AppData", "Roaming");
        var local = Path.Combine(profile, "AppData", "Local");
        var temp = Path.Combine(local, "Temp");

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["USERPROFILE"] = profile,
            ["APPDATA"] = roaming,
            ["LOCALAPPDATA"] = local,
            ["TEMP"] = temp,
            ["TMP"] = temp,
        };
    }

    /// <summary>
    /// A Unicode environment block: <paramref name="inherited"/> with
    /// <paramref name="overrides"/> in its place, sorted by name without regard to case,
    /// each entry ending in a null and the block in a second.
    /// </summary>
    public static string EnvironmentBlock(
        IEnumerable<KeyValuePair<string, string>> inherited, IReadOnlyDictionary<string, string> overrides)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in inherited)
        {
            // A name holding '=' or a null cannot be written into the block and read back.
            if (name.Length > 0 && !name.Contains('=') && !name.Contains('\0') && !value.Contains('\0'))
            {
                merged[name] = value;
            }
        }

        foreach (var (name, value) in overrides)
        {
            // Removed first, so the block spells the name as the override does.
            merged.Remove(name);
            merged[name] = value;
        }

        var block = new StringBuilder();

        foreach (var (name, value) in merged.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
        {
            block.Append(name).Append('=').Append(value).Append('\0');
        }

        return block.Append('\0').ToString();
    }
}
