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
/// <summary>Whether a <c>server.properties</c> was read, and why not if it was not.</summary>
public enum PropertiesReadState
{
    /// <summary>The file was opened and parsed. Every value is what it says.</summary>
    Read,

    /// <summary>There is no file. Every setting is at BDS's own default.</summary>
    Missing,

    /// <summary>
    /// There is a file and it could not be opened — locked, half-written, or
    /// denied by an ACL. Nothing is known about any setting.
    /// </summary>
    Unreadable,
}

/// <summary>A <c>server.properties</c> read, with the state it was read in.</summary>
/// <param name="Values">Empty unless <paramref name="State"/> is <c>Read</c>.</param>
/// <param name="Why">Windows's own reason, when the file could not be opened.</param>
/// <remarks>
/// <see cref="Known"/> is the question a page asks: not *is this setting on* but
/// *may I say anything about it at all*. Missing counts as known, because BDS's
/// defaults are a fact about a server with no file — which is the distinction
/// `T231` turned on.
/// </remarks>
public sealed record PropertiesFile(
    PropertiesReadState State,
    IReadOnlyDictionary<string, string> Values,
    string? Why)
{
    /// <summary>Whether anything may be said about this server's settings.</summary>
    public bool Known => State is not PropertiesReadState.Unreadable;
}

public static class ServerProperties
{
    public const string FileName = "server.properties";

    /// <summary>
    /// The console notice after the panel writes this file. One sentence for every
    /// writer, because BDS reads the file only when it starts and each writer has
    /// to say so the same way.
    /// </summary>
    public const string UpdatedNotice =
        FileName + " updated from the web interface; restart the server to apply it";

    public static string PathFor(string instanceDirectory) =>
        Path.Combine(instanceDirectory, FileName);

    /// <summary>
    /// Every setting in the file. Missing or unreadable yields an empty map
    /// rather than throwing: callers are asking "what is this set to", and
    /// "there is no file yet" is a legitimate answer to that.
    /// </summary>
    /// <remarks>
    /// <para><strong>It cannot say WHICH of those two happened, and a caller that
    /// renders a value needs to know</strong> (<c>T231</c>). An empty map from a
    /// missing file means every key is at BDS's default, which is a fact; an empty
    /// map from an ACL-denied file means nothing is known, and rendering the
    /// defaults for it tells the operator their allowlist is off when the wrapper
    /// could not look. Use <see cref="ReadFile"/> where the answer reaches a
    /// screen; this stays for callers that only want a value and treat absent and
    /// unknown alike.</para>
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Read(string path) => ReadFile(path).Values;

    /// <summary>
    /// The file's settings <em>and</em> whether they were read at all.
    /// </summary>
    /// <remarks>
    /// Three states, because the world has three (<c>T176</c>): the file was read,
    /// there is no file, or there is one and it could not be opened. The third is
    /// the one <see cref="Read"/> cannot express, and `T210` settled the same
    /// question for the port model — absent carries a measurement, unreadable
    /// carries none.
    /// </remarks>
    public static PropertiesFile ReadFile(string path) => ReadFile(path, File.ReadAllLines);

    /// <summary>
    /// <see cref="ReadFile(string)"/> with the read itself injectable.
    /// </summary>
    /// <remarks>
    /// A seam, for the reason <c>PortInspector.ModelOf</c> has one: the
    /// <see cref="PropertiesReadState.Unreadable"/> arm cannot be produced where
    /// this is written. The build runs as root, so a rig that makes a file
    /// unreadable and then reads it <em>succeeds</em>, and the arm that renders a
    /// claim about access control would stay uncovered exactly where the code is
    /// (<c>T176</c>).
    /// </remarks>
    internal static PropertiesFile ReadFile(string path, Func<string, string[]> read)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(path))
        {
            return new PropertiesFile(PropertiesReadState.Missing, values, null);
        }

        try
        {
            Parse(read(path), values);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked or half-written file is not worth failing a page render
            // over; the caller sees "unset" and can try again. BOTH, because the
            // summary above promises that an unreadable file yields an empty map
            // and an ACL-denied one raises the second -- a guarantee stated in a
            // doc comment and not provided by the code beneath it is `T145`.
            //
            // Windows's own reason travels with the state (`T231`): a row that
            // says only "unknown" sends somebody looking for the cause the
            // exception was already holding.
            return new PropertiesFile(PropertiesReadState.Unreadable, values, ex.Message);
        }

        return new PropertiesFile(PropertiesReadState.Read, values, null);
    }

    /// <summary>The settings in some lines of a <c>server.properties</c>, such as one inside an archive.</summary>
    internal static IReadOnlyDictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Parse(lines, values);
        return values;
    }

    private static void Parse(IEnumerable<string> lines, Dictionary<string, string> values)
    {
        foreach (var line in lines)
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

    /// <summary>
    /// What the panel says wherever cheats can be turned on: Bedrock marks a world
    /// that has run with cheats on, and that world never earns achievements again,
    /// even with cheats turned back off (<c>T428</c>).
    /// </summary>
    public const string CheatsLockAchievements =
        "Once a world runs with cheats on, it can never earn achievements again.";

    /// <summary>
    /// <see cref="CheatsLockAchievements"/> when <paramref name="key"/> is
    /// <c>allow-cheats</c> and <paramref name="value"/> turns it on, read as
    /// <see cref="CheatsAllowed"/> reads it; otherwise null.
    /// </summary>
    public static string? CheatsNote(string key, string value) =>
        key.Equals("allow-cheats", StringComparison.OrdinalIgnoreCase) && IsTrue(value)
            ? CheatsLockAchievements
            : null;

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
    public static Task SetAsync(
        string path,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default) =>
        SetAsync(path, values, File.ReadAllLinesAsync, cancellationToken);

    /// <summary>
    /// <see cref="SetAsync(string, IReadOnlyDictionary{string, string}, CancellationToken)"/>,
    /// and each of <paramref name="unset"/> commented out in the same write
    /// (<see cref="ServerPropertiesDocument.Unset"/>).
    /// </summary>
    public static Task SetAsync(
        string path,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyCollection<string> unset,
        CancellationToken cancellationToken = default) =>
        SetAsync(path, values, File.ReadAllLinesAsync, cancellationToken, unset);

    /// <summary>
    /// <see cref="SetAsync(string, IReadOnlyDictionary{string, string}, CancellationToken)"/>
    /// with the read injected, for the refusal arm (<c>T234</c>, <c>T176</c>).
    /// </summary>
    internal static async Task SetAsync(
        string path,
        IReadOnlyDictionary<string, string> values,
        Func<string, CancellationToken, Task<string[]>> read,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? unset = null)
    {
        var document = await ServerPropertiesDocument.LoadAsync(path, read, cancellationToken)
            .ConfigureAwait(false);

        // `T234`. A reader may degrade and a writer may not. An unreadable document
        // carries no properties, so saving it would write a file holding only the
        // keys being set -- replacing a configuration nobody could read with a
        // fragment of one. The refusal names the file and Windows's own reason,
        // which is the half somebody can act on.
        if (!document.Known)
        {
            throw new IOException(
                $"'{Path.GetFileName(path)}' could not be read, so it cannot be edited: "
                + $"{document.Why} Nothing has been written.");
        }

        foreach (var (key, value) in values)
        {
            document.Set(key, value);
        }

        foreach (var key in unset ?? [])
        {
            document.Unset(key);
        }

        await document.SaveAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes each key's set lines exactly the values given, an empty list commenting the key
    /// out (<see cref="ServerPropertiesDocument.SetAll"/>); refuses an unreadable file as
    /// <see cref="SetAsync(string, IReadOnlyDictionary{string, string}, CancellationToken)"/> does.
    /// </summary>
    public static async Task SetAllAsync(
        string path,
        IReadOnlyDictionary<string, IReadOnlyList<string>> values,
        CancellationToken cancellationToken = default)
    {
        var document = await ServerPropertiesDocument.LoadAsync(path, cancellationToken).ConfigureAwait(false);

        if (!document.Known)
        {
            throw new IOException(
                $"'{Path.GetFileName(path)}' could not be read, so it cannot be edited: "
                + $"{document.Why} Nothing has been written.");
        }

        foreach (var (key, lines) in values)
        {
            document.SetAll(key, lines);
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
