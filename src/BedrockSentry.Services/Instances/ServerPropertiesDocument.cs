using System.Text.RegularExpressions;

using BedrockSentry.Core.Configuration;

namespace BedrockSentry.Services.Instances;

/// <summary>
/// A parsed <c>server.properties</c> — every setting, its documentation, and
/// enough of the original file to write it back unchanged apart from the edits.
/// </summary>
/// <remarks>
/// <para>
/// The file Mojang ships is laid out in blocks: a setting, the comment lines
/// documenting it, then a blank line. Settings that are off by default appear
/// with the whole line commented out. Both shapes are read here, so a setting
/// nobody has turned on is still listed — with its documented default — rather
/// than being invisible until somebody knows to type it in.
/// </para>
/// <para>
/// Editing works line by line rather than by regenerating the file from a
/// dictionary. That documentation is the most valuable thing in there, and
/// rewriting would throw all of it away the first time the wrapper changed a
/// port number.
/// </para>
/// </remarks>
public sealed partial class ServerPropertiesDocument
{
    /// <summary>A setting line: <c>key=value</c>, with the value allowed to be empty.</summary>
    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9._-]*)\s*=(.*)$")]
    private static partial Regex SettingLine();

    private readonly List<string> _lines;

    /// <summary>Where each setting's line sits in <see cref="_lines"/>.</summary>
    private readonly Dictionary<string, int> _lineOf = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<ServerProperty> _properties = [];

    private ServerPropertiesDocument(List<string> lines)
    {
        _lines = lines;
        Parse();
    }

    public static ServerPropertiesDocument Parse(IEnumerable<string> lines) => new([.. lines]);

    /// <summary>
    /// Reads the file, or yields an empty document when there is not one yet.
    /// A missing file is a legitimate state — an instance whose server has not
    /// been installed — not an error worth failing a page render over.
    /// </summary>
    public static async Task<ServerPropertiesDocument> LoadAsync(
        string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return new ServerPropertiesDocument([]);
        }

        return new ServerPropertiesDocument(
            [.. await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false)]);
    }

    /// <summary>Every setting, in the order the file lists them.</summary>
    /// <remarks>
    /// A copy, not the live list. Callers iterate this while calling
    /// <see cref="Set"/> — that is the whole shape of an editor applying a set
    /// of edits — and handing out the backing list makes the obvious loop throw
    /// "Collection was modified" partway through. Sixty-six records is not worth
    /// making every caller remember to snapshot first.
    /// </remarks>
    public IReadOnlyList<ServerProperty> Properties => [.. _properties];

    public ServerProperty? Find(string key) =>
        _properties.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public string? ValueOf(string key) => Find(key)?.Value;

    /// <summary>
    /// The value of <paramref name="key"/> only when it is actually set; null
    /// when the line is commented out.
    /// </summary>
    /// <remarks>
    /// The distinction matters for anything that acts on the value. A commented
    /// <c>#server-port=19132</c> documents a default the server will use, but
    /// reading it as configuration and then, say, opening a firewall rule for it
    /// would be acting on a guess.
    /// </remarks>
    public string? SetValueOf(string key) => Find(key) is { IsSet: true } property ? property.Value : null;

    private void Parse()
    {
        // False at the start of the file and after every blank line. It is what
        // stops the commented example inside force-gamemode's own description
        // ("# force-gamemode=false (or force-gamemode is not defined ...)") from
        // being read as a second, commented-out setting.
        var inBlock = false;
        ServerProperty? current = null;
        var currentIndex = -1;
        var prose = new List<string>();
        var allowed = new List<string>();
        var documented = new List<string>();
        var section = Section.Prose;

        void Flush()
        {
            if (current is null)
            {
                return;
            }

            var allowedText = Join(allowed);
            var descriptionText = Join(prose);

            // The description is passed in because the two halves of Mojang's
            // documentation can disagree about the range — see Interpret.
            var (editor, choices, minimum, maximum, isInteger) =
                AllowedValues.Interpret(allowedText, current.Value, descriptionText);

            _properties.Add(current with
            {
                Description = descriptionText,
                AllowedValues = allowedText,
                DocumentedDefault = Join(documented),
                Editor = editor,
                Choices = choices,
                Minimum = minimum,
                Maximum = maximum,
                IsInteger = isInteger,
            });

            _lineOf[current.Key] = currentIndex;
            current = null;
            prose.Clear();
            allowed.Clear();
            documented.Clear();
            section = Section.Prose;
        }

        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                Flush();
                inBlock = false;
                continue;
            }

            if (trimmed[0] != '#')
            {
                if (SettingLine().Match(trimmed) is { Success: true } match)
                {
                    // Always starts a new block, blank line or not: settings the
                    // wrapper appended when creating the instance sit flush
                    // against each other with no separator.
                    Flush();
                    current = Blank(match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim(), isSet: true);
                    currentIndex = i;
                    inBlock = true;
                }

                continue;
            }

            var body = trimmed.TrimStart('#').Trim();

            if (!inBlock && SettingLine().Match(body) is { Success: true } commented)
            {
                Flush();
                current = Blank(
                    commented.Groups[1].Value.Trim(), commented.Groups[2].Value.Trim(), isSet: false);
                currentIndex = i;
                inBlock = true;
                continue;
            }

            if (current is null)
            {
                continue;
            }

            // A heading with its content on the same line is finished; anything
            // after it is prose again. Only a bare "# Allowed values:" keeps
            // reading, because that is how the file writes a list long enough to
            // need several lines. Without the distinction, chat-restriction's
            // list swallows the four lines of explanation that follow it and
            // stops looking like a list at all.
            if (StartsSection(body, "Allowed values", out var rest))
            {
                Add(allowed, rest);
                section = rest.Length == 0 ? Section.Allowed : Section.Prose;
            }
            else if (StartsSection(body, "Default", out rest))
            {
                Add(documented, rest);
                section = rest.Length == 0 ? Section.Default : Section.Prose;
            }
            else
            {
                Add(
                    section switch
                    {
                        Section.Allowed => allowed,
                        Section.Default => documented,
                        _ => prose,
                    },
                    body);
            }
        }

        Flush();
    }

    private enum Section
    {
        Prose,
        Allowed,
        Default,
    }

    private static ServerProperty Blank(string key, string value, bool isSet) =>
        new(key, value, isSet, null, null, null, ServerPropertyEditor.Text, [], null, null, false);

    private static void Add(List<string> target, string text)
    {
        if (text.Length != 0)
        {
            target.Add(text);
        }
    }

    private static string? Join(List<string> lines) =>
        lines.Count == 0 ? null : string.Join(' ', lines);

    private static bool StartsSection(string body, string heading, out string rest)
    {
        rest = string.Empty;

        if (!body.StartsWith(heading, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var after = body[heading.Length..].TrimStart();
        if (after.Length == 0 || after[0] != ':')
        {
            return false;
        }

        rest = after[1..].Trim();
        return true;
    }

    /// <summary>
    /// Sets <paramref name="key"/>, uncommenting its line if it was commented
    /// out and appending it if the file never mentioned it.
    /// </summary>
    /// <remarks>
    /// Uncommenting in place rather than appending keeps the setting next to the
    /// documentation that explains it. Appending would leave the file with the
    /// same setting twice — commented above, live at the bottom — which reads as
    /// a contradiction even though the server resolves it fine.
    /// </remarks>
    public void Set(string key, string value)
    {
        var normalised = value.ReplaceLineEndings(string.Empty);

        if (_lineOf.TryGetValue(key, out var index))
        {
            // The key as the file spells it, so turning a commented setting on
            // does not also silently change its capitalisation.
            var existing = _properties.First(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            _lines[index] = $"{existing.Key}={normalised}";

            for (var i = 0; i < _properties.Count; i++)
            {
                if (_properties[i].Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    _properties[i] = _properties[i] with { Value = normalised, IsSet = true };
                }
            }

            return;
        }

        if (_lines.Count > 0 && _lines[^1].Trim().Length != 0)
        {
            _lines.Add(string.Empty);
        }

        _lines.Add($"{key}={normalised}");
        _lineOf[key] = _lines.Count - 1;

        var (editor, choices, minimum, maximum, isInteger) = AllowedValues.Interpret(null, normalised);
        _properties.Add(new ServerProperty(
            key, normalised, IsSet: true, null, null, null, editor, choices, minimum, maximum, isInteger));
    }

    public IReadOnlyList<string> Lines => _lines;

    /// <summary>
    /// Writes the file, via a temporary file in the same folder so a crash
    /// mid-write cannot leave a server with a truncated configuration.
    /// </summary>
    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        await AtomicFile
            .WriteAllTextAsync(
                path,
                string.Join(Environment.NewLine, _lines) + Environment.NewLine,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
