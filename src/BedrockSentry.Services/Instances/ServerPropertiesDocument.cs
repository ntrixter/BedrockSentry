using System.Text.RegularExpressions;

using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Instances;

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

    /// <summary>
    /// Where each setting's lines sit in <see cref="_lines"/>, in file order.
    /// </summary>
    /// <remarks>
    /// <para><strong>A list rather than one index, and that is `D72`.</strong> This
    /// held a single index and kept the <em>last</em> occurrence, while
    /// <see cref="_properties"/> kept one record per occurrence and
    /// <see cref="Find"/> answered with the <em>first</em>. Two different
    /// occurrences answering two different questions is how <see cref="Set"/>
    /// came to rewrite one line and claim it had rewritten all of them.</para>
    ///
    /// <para><strong>A repeated key is not a malformed file.</strong>
    /// <c>server-udp-ports</c> may appear on several lines and BDS
    /// <em>accumulates</em> them — see <see cref="AccumulatingKey"/> — so a
    /// value left behind on an earlier line stays in force beside the new one.
    /// That is what made a silent merge possible rather than a visible
    /// contradiction.</para>
    /// </remarks>
    private readonly Dictionary<string, List<int>> _lineOf = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The one key in <c>server.properties</c> that may legitimately appear on
    /// more than one line, with every line taking effect.
    /// </summary>
    /// <remarks>
    /// BDS's own documentation for it says the key "may also appear on more than
    /// one line to accumulate entries". Nothing else in the file behaves that
    /// way, so a duplicate of any other key is a mistake somebody made by hand —
    /// but the editing rules below cannot tell the two apart from the text, and
    /// do not try to: collapsing duplicates on a write is the right answer for
    /// both. It is named here because a <em>reader</em> of this key must use
    /// <see cref="ValuesOf"/> rather than <see cref="ValueOf"/>, and a reader
    /// that does not is reading a fraction of the configuration.
    /// <para>The string itself comes from <see cref="ServerUdpPorts.Key"/>, where
    /// the type that understands the value lives. Spelling it again here would be
    /// one key named in two places, which is how the two come to disagree.</para>
    /// </remarks>
    public const string AccumulatingKey = ServerUdpPorts.Key;

    private readonly List<ServerProperty> _properties = [];

    private ServerPropertiesDocument(
        List<string> lines,
        PropertiesReadState state = PropertiesReadState.Read,
        string? why = null)
    {
        _lines = lines;
        State = state;
        Why = why;
        Parse();
    }

    /// <summary>Which of the three states the file was in when this was read.</summary>
    public PropertiesReadState State { get; }

    /// <summary>
    /// Windows's own reason, when <see cref="State"/> is
    /// <see cref="PropertiesReadState.Unreadable"/>. Null otherwise.
    /// </summary>
    public string? Why { get; }

    /// <summary>
    /// Whether this document is a reading of the file rather than a stand-in for
    /// one nobody could open.
    /// </summary>
    /// <remarks>
    /// A MISSING file counts as known, and that is the distinction an empty
    /// document could not carry: a server that has never started really is at every
    /// default, while a file something else has open says nothing at all.
    /// <c>ServerProperties.ReadFile</c> settled the same question one type along
    /// and this is deliberately the same three states under the same names.
    /// </remarks>
    public bool Known => State is not PropertiesReadState.Unreadable;

    /// <summary>
    /// Whether a page holding this document should read the file again on its tick
    /// (<c>T239</c>).
    /// </summary>
    /// <remarks>
    /// <para><strong>Here rather than in the tab, because the test project cannot
    /// reference the panel at all</strong> — a rule left in a <c>.razor</c> is verified
    /// on a real page or not at all, which is a reason to move it rather than to shrug.
    /// The same move <c>T136</c> and <c>T145</c> both ended in.</para>
    ///
    /// <para>The caching rule a properties page needs — read once, not once a second,
    /// or the read fights an edit in progress — is <strong>about a document that was
    /// read</strong>. An unreadable one renders no editor, so there is no edit to fight
    /// with, and holding it cached means an administrator who has just repaired the file
    /// keeps being shown the failure. The justification did not cover the arm it was
    /// applied to (<c>T188</c>), and the staleness check that looked like cover sits in
    /// the other branch and compares a timestamp an ACL change does not move
    /// (<c>T187</c>).</para>
    ///
    /// <para><strong>It deliberately does NOT answer for a null document</strong>, and
    /// the first draft of it did. A page that has read nothing yet must load
    /// unconditionally, because there is no document to render the failure with; a page
    /// holding an unreadable one must load only when the file can be read again, or it
    /// replaces its own message every second. Two different actions, so folding them
    /// into one predicate would have made the next caller pick whichever arm it
    /// happened to write.</para>
    /// </remarks>
    public bool ShouldRetryRead => !Known;

    public static ServerPropertiesDocument Parse(IEnumerable<string> lines) => new([.. lines]);

    /// <summary>
    /// Reads the file, in the three states it can be in (<c>T234</c>).
    /// </summary>
    /// <remarks>
    /// <para>A missing file is a legitimate state — an instance whose server has
    /// not been installed — and yields an empty document, which is what it has
    /// always done.</para>
    ///
    /// <para><strong>A file that cannot be READ used to escape as an exception</strong>,
    /// through <c>OnParametersSetAsync</c>, past a Host with no <c>ErrorBoundary</c>,
    /// and out as an error page: an ACL-denied <c>server.properties</c> made the
    /// Server properties tab unopenable rather than informative. The guard tested
    /// <see cref="File.Exists"/> only, which answers a different question from
    /// "may I open this".</para>
    ///
    /// <para><strong>The asymmetry between readers and writers is deliberate and is
    /// the whole reason this returns a state rather than an empty document.</strong>
    /// A reader may degrade — render the state and say why. A writer may NOT: an
    /// empty document handed to <c>SetAsync</c> would be saved over the file nobody
    /// could read, replacing a configuration with the handful of keys being set.
    /// <c>ServerProperties.SetAsync</c> refuses on this state for that reason.</para>
    ///
    /// <para>This type and <c>ServerProperties.ReadFile</c> read the same file and
    /// only one of them was made three-state, in one commit — which is <c>T203</c>'s
    /// own recorded lesson, that the second copy is found by grepping for the
    /// READER rather than for the symptom, not applied to its own follow-up.</para>
    /// </remarks>
    public static Task<ServerPropertiesDocument> LoadAsync(
        string path, CancellationToken cancellationToken = default) =>
        LoadAsync(path, File.ReadAllLinesAsync, cancellationToken);

    /// <summary>
    /// <see cref="LoadAsync(string, CancellationToken)"/> with the read injected.
    /// </summary>
    /// <remarks>
    /// The refusal arm is unreproducible on the platform this is written on: the
    /// build runs as root, so a file made unreadable and then read SUCCEEDS, and
    /// the arm that renders an error page would have stayed uncovered exactly where
    /// the code lives (<c>T176</c>). Only that arm takes the seam — <em>missing</em>
    /// and <em>read</em> use the real filesystem, because both reproduce anywhere.
    /// </remarks>
    internal static async Task<ServerPropertiesDocument> LoadAsync(
        string path,
        Func<string, CancellationToken, Task<string[]>> read,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return new ServerPropertiesDocument([], PropertiesReadState.Missing);
        }

        try
        {
            return new ServerPropertiesDocument(
                [.. await read(path, cancellationToken).ConfigureAwait(false)]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ServerPropertiesDocument([], PropertiesReadState.Unreadable, ex.Message);
        }
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

    /// <summary>
    /// The first occurrence's value, whether or not the line is commented out.
    /// </summary>
    /// <remarks>
    /// <strong>The first, which is only the whole answer for a key that appears
    /// once.</strong> For <see cref="AccumulatingKey"/> every set line takes
    /// effect, so this returns a fraction of the configuration and
    /// <see cref="ValuesOf"/> is the read that does not (`D72`).
    /// </remarks>
    public string? ValueOf(string key) => Find(key)?.Value;

    /// <summary>
    /// Every <em>set</em> value for <paramref name="key"/>, in file order.
    /// Empty when the key is absent or every occurrence is commented out.
    /// </summary>
    /// <remarks>
    /// The honest read for a key that repeats. One occurrence gives a
    /// single-element list, so a caller never has to know in advance whether the
    /// key it is asking about is <see cref="AccumulatingKey"/> — which is the
    /// point, because the one that does have to know is the one that gets it
    /// wrong.
    /// </remarks>
    public IReadOnlyList<string> ValuesOf(string key) =>
        [.. _properties
            .Where(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && p.IsSet)
            .Select(p => p.Value)];

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

            // Appended, not assigned. Assigning kept the last occurrence and
            // silently discarded every earlier one (`D72`).
            if (!_lineOf.TryGetValue(current.Key, out var lines))
            {
                lines = [];
                _lineOf[current.Key] = lines;
            }

            lines.Add(currentIndex);
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
    /// <para>Uncommenting in place rather than appending keeps the setting next to
    /// the documentation that explains it. Appending would leave the file with the
    /// same setting twice — commented above, live at the bottom — which reads as
    /// a contradiction even though the server resolves it fine.</para>
    ///
    /// <para><strong>Setting a key that appears more than once replaces all of
    /// them</strong> (`D72`): the first occurrence carries the new value and every
    /// later one is removed. That is the only reading of <em>set</em> that is true
    /// of <see cref="AccumulatingKey"/>, where leaving an earlier line in place
    /// keeps its value in force beside the new one — so an administrator who
    /// retyped their NAT mapping in the panel would have got a union of the old
    /// and the new rather than a replacement, with the file and this model
    /// disagreeing about which.</para>
    ///
    /// <para>The stated limit, because somebody will meet it: a panel field holds
    /// one value, so the panel cannot <em>author</em> several accumulating lines.
    /// Writing more than one stays a hand edit, and this reads them all back
    /// through <see cref="ValuesOf"/>. What it will not do is quietly keep half of
    /// an edit somebody thought they had made.</para>
    ///
    /// <para>A cosmetic consequence, said here rather than left to surprise
    /// somebody reading their file afterwards: removing a duplicate takes its
    /// line and nothing else, so a blank line that separated it can end up
    /// doubled, and any comment written under it is left attached to whatever
    /// follows. Both re-read correctly — <c>Replacing_a_duplicate_leaves_the_settings_around_it_editable</c>
    /// pins that the settings on either side stay editable — and tidying them
    /// would mean deciding which blank lines and comments belonged to the
    /// removed line, which is a guess about somebody else's formatting.</para>
    /// </remarks>
    /// <summary>
    /// What somebody typed or pasted into one setting's box, without a leading
    /// <c>key=</c>: a line copied whole from a suggestion was written as
    /// <c>server-udp-ports=server-udp-ports=…</c> (<c>T417</c>).
    /// </summary>
    public static string ValueTyped(string key, string? typed)
    {
        var value = typed ?? string.Empty;

        // Anything else is kept as typed, spaces included: a server-name may want them.
        while (value.TrimStart().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
        {
            value = value.TrimStart()[(key.Length + 1)..].Trim();
        }

        return value;
    }

    public void Set(string key, string value)
    {
        var normalised = value.ReplaceLineEndings(string.Empty);

        if (_lineOf.TryGetValue(key, out var indices) && indices.Count > 0)
        {
            // The key as the file spells it, so turning a commented setting on
            // does not also silently change its capitalisation.
            var existing = _properties.First(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            _lines[indices[0]] = $"{existing.Key}={normalised}";

            if (indices.Count > 1)
            {
                // Descending, so removing one does not move the next one's index
                // out from under the loop.
                for (var i = indices.Count - 1; i >= 1; i--)
                {
                    _lines.RemoveAt(indices[i]);
                }

                // Every index after a removed line has moved, so the maps are
                // rebuilt from the lines rather than patched. Reusing the parser
                // is what keeps a rare path from growing its own idea of what a
                // setting line looks like - and it collapses the duplicates in
                // `_properties` at the same time, for free.
                Reparse();
                return;
            }

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
        _lineOf[key] = [_lines.Count - 1];

        var (editor, choices, minimum, maximum, isInteger) = AllowedValues.Interpret(null, normalised);
        _properties.Add(new ServerProperty(
            key, normalised, IsSet: true, null, null, null, editor, choices, minimum, maximum, isInteger));
    }

    /// <summary>
    /// Makes <paramref name="values"/> the key's set lines, one each, in order, where its
    /// first line is; none comments every line out (<see cref="Unset"/>).
    /// </summary>
    /// <remarks>
    /// For <see cref="AccumulatingKey"/>, whose several lines all take effect (<c>D72</c>):
    /// a server's own <c>server-udp-ports</c> put back over a restored file is every line it
    /// had, not the first (<c>D127</c>).
    /// </remarks>
    public void SetAll(string key, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            Unset(key);
            return;
        }

        Set(key, values[0]);

        if (values.Count == 1)
        {
            return;
        }

        var at = _lineOf[key].First(i => !_lines[i].TrimStart().StartsWith('#'));
        var spelled = _lines[at][.._lines[at].IndexOf('=')];

        _lines.InsertRange(
            at + 1, values.Skip(1).Select(v => $"{spelled}={v.ReplaceLineEndings(string.Empty)}"));
        Reparse();
    }

    /// <summary>
    /// Comments out every set line of <paramref name="key"/>, keeping each in place beside
    /// its documentation; a key that is not set is left as it is.
    /// </summary>
    /// <remarks>
    /// Every line, since <see cref="AccumulatingKey"/> keeps each set line in force
    /// (<c>D72</c>). Commented rather than removed, so <see cref="Set"/> turns the same
    /// line back on where it was.
    /// </remarks>
    public void Unset(string key)
    {
        if (!_lineOf.TryGetValue(key, out var indices))
        {
            return;
        }

        var changed = false;

        foreach (var index in indices)
        {
            if (!_lines[index].TrimStart().StartsWith('#'))
            {
                _lines[index] = "# " + _lines[index].Trim();
                changed = true;
            }
        }

        if (changed)
        {
            Reparse();
        }
    }

    /// <summary>
    /// Rebuilds the settings and the line map from <see cref="_lines"/>.
    /// </summary>
    /// <remarks>
    /// Only reached when <see cref="Set"/> has removed a duplicate line, which
    /// moves every index after it. Clearing first matters: <see cref="Parse"/>
    /// appends, so re-running it over populated collections would double them.
    /// </remarks>
    private void Reparse()
    {
        _properties.Clear();
        _lineOf.Clear();
        Parse();
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
