namespace BedrockSentry.Core.Instances;

/// <summary>
/// What the "new instance" form collects.
/// </summary>
/// <param name="Name">Display name, and the folder name unless overridden.</param>
/// <param name="PackageFileName">
/// A package from the store to install into it, or null to leave the directory
/// alone — for adopting a server that is already unpacked somewhere.
/// </param>
/// <param name="Port">
/// The IPv4 port; the next number is written as the IPv6 port, and
/// <see cref="NewServerPorts"/> suggests one where both are free. Null leaves
/// whatever the package shipped, which for a second instance on one machine
/// means colliding with the first.
/// </param>
/// <param name="World">
/// The handful of <c>server.properties</c> settings that are painful to change
/// later. Null leaves every one of them as the package shipped it.
/// </param>
/// <param name="AllowExistingFiles">
/// Skips the empty-folder guard. Only <c>AdoptAsync</c> sets this — that is the
/// one caller that deliberately wants a folder already holding a server; every
/// other path (package or not) must refuse one, or a folder somebody else's
/// server already lives in gets silently reconfigured out from under it.
/// </param>
/// <param name="Transport">
/// Which transport the new server speaks (<c>D75</c>). Only written when a
/// package was extracted, because that is the one file the wrapper made; an
/// adopted folder keeps whatever it already said.
/// </param>
/// <param name="CompanionPack">
/// Switch the companion pack on before the first start (<c>D93</c>): its world
/// folder is made and seeded with the pack, which BDS generates the world into
/// and keeps, and script output is turned on. Only with a package; adopting and
/// restoring never set it, since a restored copy is a faithful one (<c>D42</c>).
/// </param>
/// <param name="UdpPorts">
/// The <c>server-udp-ports</c> line for a NetherNet server, as the form showed it and the
/// operator left it (<c>ON1</c>). Written only with a package and under NetherNet, the
/// transport that reads it; null or blank writes nothing.
/// </param>
public sealed record NewInstanceRequest(
    string Name,
    string? PackageFileName = null,
    int? Port = null,
    bool AutoStart = true,
    string? WorkingDirectory = null,
    bool StartNow = false,
    NewWorldSettings? World = null,
    bool AllowExistingFiles = false,
    ServerTransport Transport = ServerTransports.Default,
    RestoredFrom? RestoredFrom = null,
    bool CompanionPack = false,
    string? UdpPorts = null);

/// <summary>
/// World settings chosen when an instance is created.
/// </summary>
/// <remarks>
/// Everything in <c>server.properties</c> is editable afterwards on the
/// instance's own tab, so this is not about which settings exist — it is about
/// which ones are painful to change once the world has been generated.
/// <see cref="Seed"/> is the extreme case: it decides the terrain and does
/// nothing at all after the first launch. The rest are here because they are
/// the answers somebody has already decided before they click Create, and
/// making them go and find the properties tab afterwards is a worse first five
/// minutes than one form with six fields.
///
/// Defaults are Bedrock's own except where the operator's likely intent is
/// clearly otherwise — <see cref="Difficulty"/> is <c>hard</c> because a server
/// somebody stood up deliberately is rarely meant to be <c>easy</c>.
/// </remarks>
public sealed record NewWorldSettings
{
    /// <summary>Blank asks Bedrock for a random world.</summary>
    public string Seed { get; init; } = string.Empty;

    /// <summary><c>survival</c>, <c>creative</c> or <c>adventure</c>.</summary>
    public string Gamemode { get; init; } = "survival";

    /// <summary><c>peaceful</c>, <c>easy</c>, <c>normal</c> or <c>hard</c>.</summary>
    public string Difficulty { get; init; } = "hard";

    /// <summary>
    /// Bedrock's <c>allow-cheats</c>. Off, because on means every operator
    /// command is available to anyone the permissions file promotes.
    /// </summary>
    public bool AllowCheats { get; init; }

    /// <summary>
    /// Enforce <c>allowlist.json</c>. Off is Bedrock's default and means anyone
    /// who can reach the port can join.
    /// </summary>
    public bool AllowList { get; init; }

    /// <summary>
    /// Send Mojang the telemetry it asks for on startup. BDS ships no line for
    /// this at all, so the wrapper writes one either way — which is what stops
    /// the "TELEMETRY MESSAGE" notice appearing in the console on every launch.
    /// </summary>
    public bool EmitTelemetry { get; init; } = true;

    /// <summary>The settings as <c>server.properties</c> keys.</summary>
    public IReadOnlyDictionary<string, string> ToProperties()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["gamemode"] = Gamemode,
            ["difficulty"] = Difficulty,
            ["allow-cheats"] = AllowCheats ? "true" : "false",
            ["allow-list"] = AllowList ? "true" : "false",
            ["emit-server-telemetry"] = EmitTelemetry ? "true" : "false",
        };

        // Only when given. Writing an empty level-seed is the same as omitting
        // it, but it reads in the file as though a choice was made.
        if (Seed.Trim() is { Length: > 0 } seed)
        {
            values["level-seed"] = seed;
        }

        return values;
    }
}

/// <summary>
/// Turns a display name into a stable instance id.
/// </summary>
public static class InstanceId
{
    /// <summary>
    /// Lower-case, ASCII, hyphen-separated. The id ends up in folder names, log
    /// directory names and URLs, so it is deliberately narrower than the display
    /// name it comes from — the name can be renamed later, the id cannot.
    /// </summary>
    public static string FromName(string name)
    {
        var slug = new string(name
            .Trim()
            .ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
            .ToArray());

        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        slug = slug.Trim('-');

        // A name of nothing but punctuation, or a non-Latin script, slugs to
        // empty. Falling back keeps the instance creatable rather than rejecting
        // a name that is perfectly good for a person to read.
        return slug.Length > 0 ? slug : "instance";
    }

    /// <summary>
    /// Appends a counter until the id is not already taken.
    /// </summary>
    public static string MakeUnique(string candidate, Func<string, bool> isTaken)
    {
        if (!isTaken(candidate))
        {
            return candidate;
        }

        for (var n = 2; n < 1000; n++)
        {
            var next = $"{candidate}-{n}";
            if (!isTaken(next))
            {
                return next;
            }
        }

        throw new InvalidOperationException($"Could not find a free instance id based on '{candidate}'.");
    }
}
