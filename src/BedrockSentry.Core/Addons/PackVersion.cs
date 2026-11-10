using System.Text.Json;

namespace BedrockSentry.Core.Addons;

/// <summary>How a version field read.</summary>
/// <remarks>
/// Three states rather than a version-or-zero, because the two failures are
/// different and neither is a version. A field nobody wrote and a field written
/// in a spelling this cannot read would both compare as <c>0.0.0</c> — below
/// every real version — so the pack would present as older than everything and
/// an update check would offer to replace it with anything at all.
/// </remarks>
public enum PackVersionState
{
    /// <summary>The key is not there.</summary>
    Absent,

    /// <summary>It is there and this cannot read it. The text is kept to show.</summary>
    Malformed,

    /// <summary>Read.</summary>
    Read,
}

/// <summary>A version field's three states, and the version when there is one.</summary>
public readonly record struct PackVersionRead(
    PackVersionState State, PackVersion? Version, string? Raw)
{
    public static PackVersionRead OfAbsent() => new(PackVersionState.Absent, null, null);

    public static PackVersionRead OfMalformed(string raw) =>
        new(PackVersionState.Malformed, null, raw);

    public static PackVersionRead Of(PackVersion version) =>
        new(PackVersionState.Read, version, version.ToString());
}

/// <summary>
/// A pack's version, in either of the two spellings Bedrock packs use.
/// </summary>
/// <remarks>
/// <para><strong>Two shapes, and a reader written against one silently
/// mishandles the other.</strong> At <c>format_version: 3</c> a manifest writes
/// a semver <em>string</em> — <c>"1.3.0"</c> — and older packs write an
/// <em>array</em> — <c>[1, 3, 0]</c>. <c>min_engine_version</c> has the same
/// two shapes. Both appear in packs in the wild, so both are read here and
/// neither is preferred.</para>
///
/// <para><strong>The components are kept exactly as they were written, and the
/// count with them.</strong> Comparison pads the shorter side with zeros, so
/// <c>1.3</c> and <c>1.3.0</c> compare equal — but a version read as two
/// components is written back as two. Normalising on read would quietly change
/// a pack's stated identity in a file the game matches on, which is the one
/// thing a manager must never do to somebody else's pack.</para>
///
/// <para><strong>Four components are accepted although the schema says three.</strong>
/// Being strict here rests on having read two real manifests, and <c>D81</c> is
/// the record of what that reasoning costs: reading two files tells you what
/// those two files use. A shape this cannot represent is
/// <see cref="PackVersionState.Malformed"/> and says so, rather than being
/// truncated into something that looks right.</para>
/// </remarks>
public readonly record struct PackVersion : IComparable<PackVersion>
{
    /// <summary>The most components a version may carry.</summary>
    public const int MaxComponents = 4;

    private readonly int _a;
    private readonly int _b;
    private readonly int _c;
    private readonly int _d;

    private PackVersion(int length, int a, int b, int c, int d)
    {
        Length = length;
        _a = a;
        _b = b;
        _c = c;
        _d = d;
    }

    /// <summary>How many components were written.</summary>
    public int Length { get; }

    /// <summary>The components, as many as were written.</summary>
    public IReadOnlyList<int> Components => Length switch
    {
        1 => [_a],
        2 => [_a, _b],
        3 => [_a, _b, _c],
        4 => [_a, _b, _c, _d],
        _ => [],
    };

    /// <summary>Build one from components, or null when the shape is unusable.</summary>
    /// <remarks>
    /// Null rather than a throw: the commonest caller is reading somebody else's
    /// file, where an unusable value is an ordinary thing to meet and report.
    /// </remarks>
    public static PackVersion? Of(IReadOnlyList<int> components)
    {
        ArgumentNullException.ThrowIfNull(components);

        if (components.Count is 0 or > MaxComponents || components.Any(c => c < 0))
        {
            return null;
        }

        return new PackVersion(
            components.Count,
            components[0],
            components.Count > 1 ? components[1] : 0,
            components.Count > 2 ? components[2] : 0,
            components.Count > 3 ? components[3] : 0);
    }

    /// <summary>Read a manifest's version field in whichever spelling it uses.</summary>
    /// <param name="element">
    /// The field, or null when the key is absent. <see cref="JsonValueKind.Null"/>
    /// counts as absent: a key written null says no more than no key at all.
    /// </param>
    public static PackVersionRead Read(JsonElement? element)
    {
        if (element is not { } value || value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return PackVersionRead.OfAbsent();
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => FromText(value.GetString() ?? string.Empty),
            JsonValueKind.Array => FromArray(value),
            _ => PackVersionRead.OfMalformed(value.ToString()),
        };
    }

    /// <summary>Read a dotted version out of a plain string.</summary>
    /// <remarks>
    /// The same rules the string spelling of a manifest field gets, exposed
    /// because a server's own reported build — <c>1.26.51.1</c>, four
    /// components — is compared against a pack's engine floor, and comparison
    /// pads the shorter side.
    /// </remarks>
    public static PackVersionRead Parse(string? text) =>
        text is null ? PackVersionRead.OfAbsent() : FromText(text);

    private static PackVersionRead FromText(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return PackVersionRead.OfMalformed(text);
        }

        var parts = trimmed.Split('.');
        var components = new List<int>(parts.Length);

        foreach (var part in parts)
        {
            // Deliberately strict: a prerelease or build suffix makes this
            // malformed rather than being dropped. The components are written
            // back into the world's pack list (`D84`), where dropping one would
            // change which pack the game resolves while looking correct here.
            if (!int.TryParse(part, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var number))
            {
                return PackVersionRead.OfMalformed(text);
            }

            components.Add(number);
        }

        return Of(components) is { } version
            ? PackVersionRead.Of(version)
            : PackVersionRead.OfMalformed(text);
    }

    private static PackVersionRead FromArray(JsonElement array)
    {
        var components = new List<int>(MaxComponents);

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var number))
            {
                return PackVersionRead.OfMalformed(array.ToString());
            }

            components.Add(number);
        }

        return Of(components) is { } version
            ? PackVersionRead.Of(version)
            : PackVersionRead.OfMalformed(array.ToString());
    }

    /// <summary>Component-wise, with a shorter version padded with zeros.</summary>
    public int CompareTo(PackVersion other)
    {
        for (var i = 0; i < MaxComponents; i++)
        {
            var mine = At(i);
            var theirs = other.At(i);

            if (mine != theirs)
            {
                return mine.CompareTo(theirs);
            }
        }

        return 0;
    }

    private int At(int index) => index switch
    {
        0 => Length > 0 ? _a : 0,
        1 => Length > 1 ? _b : 0,
        2 => Length > 2 ? _c : 0,
        3 => Length > 3 ? _d : 0,
        _ => 0,
    };

    /// <summary>Component-wise, with a shorter version padded with zeros.</summary>
    /// <remarks>
    /// <para><strong>Written out so that equality and comparison give one
    /// answer.</strong> The generated equality compares <c>Length</c> along with
    /// the components, so <c>1.1</c> and <c>1.1.0</c> were not equal while
    /// <see cref="CompareTo"/> already called them the same version — two ways
    /// of asking one question with two answers, which a caller reaches for
    /// whichever of interchangeably.</para>
    ///
    /// <para>The spelling is not lost by this: <c>PackVersionRead.Raw</c> keeps
    /// the original text, which is what <c>D84</c>'s write-it-back-as-read rule
    /// reads from.</para>
    /// </remarks>
    public bool Equals(PackVersion other) => CompareTo(other) == 0;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(At(0), At(1), At(2), At(3));

    public static bool operator <(PackVersion left, PackVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(PackVersion left, PackVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(PackVersion left, PackVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(PackVersion left, PackVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => string.Join('.', Components);
}
