namespace BedrockSentry.Services.Instances;

/// <summary>
/// Reads and edits a BDS <c>server.properties</c> file.
///
/// Line-oriented on purpose. The file Mojang ships is mostly documentation —
/// every setting has a comment above it explaining the allowed values — and
/// rewriting it from a parsed dictionary would throw all of that away the first
/// time the wrapper touched a single port number. So edits replace values in
/// place and append only what was genuinely missing.
/// </summary>
public static class ServerProperties
{
    public const string FileName = "server.properties";

    public static string PathFor(string instanceDirectory) =>
        Path.Combine(instanceDirectory, FileName);

    /// <summary>
    /// Every setting in the file. Missing or unreadable yields an empty map
    /// rather than throwing: callers are asking "what is this set to", and
    /// "there is no file yet" is a legitimate answer to that.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Read(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(path))
        {
            return values;
        }

        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.TrimStart();
                if (trimmed.Length == 0 || trimmed[0] == '#')
                {
                    continue;
                }

                var separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }
        catch (IOException)
        {
            // A locked or half-written file is not worth failing a page render
            // over; the caller sees "unset" and can try again.
        }

        return values;
    }

    /// <summary>
    /// Whether BDS is enforcing an allowlist, from the properties it was given
    /// (<c>T136</c>).
    /// </summary>
    /// <remarks>
    /// Lives here rather than in the page that asks, for the reason <c>T126</c>
    /// records: a rule stated in a <c>.razor</c> file is verified on a real page
    /// or not at all, because the test project does not reference the host.
    ///
    /// Three rules, and the first two are where the badge went wrong. An
    /// **absent** key means **off** — that is BDS's own default, and the page
    /// used to read a missing key as *enabled*, so a file with no
    /// <c>allow-list</c> line at all showed a green "Allowlist enabled" badge
    /// while the Properties tab showed no such setting. Second, <c>white-list</c>
    /// is the pre-1.16 name Bedrock still honours and is read as a fallback;
    /// <see cref="Backups.ServerConfigFiles"/> already knows the legacy
    /// <c>whitelist.json</c> filename one layer up, so knowing only half the old
    /// naming was the actual gap. Third, only a literal <c>true</c> counts as on,
    /// where the old test counted everything that was not the literal
    /// <c>false</c> — which made an empty or misspelt value mean *enforced*.
    ///
    /// Display-only today: nothing gates access on this, so getting it wrong
    /// misreported rather than let anyone in.
    /// </remarks>
    public static bool AllowlistEnforced(IReadOnlyDictionary<string, string> properties)
    {
        if (properties.TryGetValue("allow-list", out var modern))
        {
            return IsTrue(modern);
        }

        return properties.TryGetValue("white-list", out var legacy) && IsTrue(legacy);
    }

    /// <summary>Whether cheats are allowed on this server.</summary>
    /// <remarks>
    /// Beside <see cref="AllowlistEnforced"/> and reading the same way for the
    /// same reason: an absent key means off, which is BDS's default, and only a
    /// literal <c>true</c> counts as on. Written as a method rather than a
    /// <c>TryGetValue</c> at the call site so that the two flags shown side by
    /// side on the Status tab cannot come to disagree about what a blank or
    /// misspelt value means — which is exactly how the allowlist badge went wrong
    /// (<c>T136</c>). Display-only: nothing gates anything on this.
    /// </remarks>
    public static bool CheatsAllowed(IReadOnlyDictionary<string, string> properties) =>
        properties.TryGetValue("allow-cheats", out var value) && IsTrue(value);

    private static bool IsTrue(string value) =>
        string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Applies <paramref name="values"/>, keeping comments, ordering and every
    /// untouched setting exactly as they were.
    /// </summary>
    /// <remarks>
    /// A thin wrapper over <see cref="ServerPropertiesDocument"/> so that every
    /// write to a properties file goes through one implementation. Two of them
    /// would eventually disagree about something that matters — whether a
    /// commented-out setting is uncommented in place or duplicated at the
    /// bottom, say.
    /// </remarks>
    public static async Task SetAsync(
        string path,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default)
    {
        var document = await ServerPropertiesDocument.LoadAsync(path, cancellationToken)
            .ConfigureAwait(false);

        foreach (var (key, value) in values)
        {
            document.Set(key, value);
        }

        await document.SaveAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The IPv4 port this instance listens on, or null when unset.</summary>
    public static int? PortOf(string instanceDirectory) =>
        Read(PathFor(instanceDirectory)).TryGetValue("server-port", out var text)
        && int.TryParse(text, out var port)
            ? port
            : null;
}
