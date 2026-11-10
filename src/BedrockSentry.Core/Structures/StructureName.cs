using System.Text;

namespace BedrockSentry.Core.Structures;

/// <summary>
/// A structure's name as Bedrock's <c>/structure</c> command takes it: a
/// namespace and a name.
/// </summary>
/// <remarks>
/// <para>Measured on BDS 1.26.52.3 (<c>D86</c>): <c>structure save mytest …</c>
/// answered <em>"Saved a structure with name mystructure:mytest"</em> and stored
/// it under the database key <c>structuretemplate_mystructure:mytest</c>, so a
/// name given without a namespace is in <see cref="DefaultNamespace"/>, and the
/// key is <see cref="KeyPrefix"/> followed by both parts as typed.</para>
///
/// <para>Bedrock splits at the first colon. What it allows in either part beyond
/// what was measured is not known here, so parsing accepts any non-empty parts;
/// a name this wrapper turns into a file goes through the stricter
/// <see cref="IsPlain"/>.</para>
/// </remarks>
public readonly record struct StructureName
{
    /// <summary>The namespace of a name given without one.</summary>
    public const string DefaultNamespace = "mystructure";

    /// <summary>What every structure's key in a world's database starts with.</summary>
    public const string KeyPrefix = "structuretemplate_";

    private StructureName(string space, string name)
    {
        Namespace = space;
        Name = name;
    }

    /// <summary>The part before the colon.</summary>
    public string Namespace { get; }

    /// <summary>The part after it.</summary>
    public string Name { get; }

    /// <summary>The key this structure is stored under in a world's database.</summary>
    public string DatabaseKey => KeyPrefix + this;

    /// <summary><see cref="DatabaseKey"/> as the bytes the database compares.</summary>
    public byte[] DatabaseKeyBytes => Encoding.UTF8.GetBytes(DatabaseKey);

    /// <summary>The prefix every structure key in a database starts with, as bytes.</summary>
    public static byte[] KeyPrefixBytes => Encoding.UTF8.GetBytes(KeyPrefix);

    /// <summary>A name in a namespace.</summary>
    /// <exception cref="ArgumentException">Either part is empty, or the namespace holds a colon.</exception>
    public static StructureName Of(string space, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(space);
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (space.Contains(':'))
        {
            throw new ArgumentException("A namespace cannot hold a colon.", nameof(space));
        }

        return new StructureName(space, name);
    }

    /// <summary>
    /// Reads a name the way <c>/structure</c> does: <c>namespace:name</c>, or a
    /// bare name in <see cref="DefaultNamespace"/>.
    /// </summary>
    public static bool TryParse(string? text, out StructureName name)
    {
        name = default;

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var colon = text.IndexOf(':');

        if (colon < 0)
        {
            name = new StructureName(DefaultNamespace, text);
            return true;
        }

        if (colon == 0 || colon == text.Length - 1)
        {
            return false;
        }

        name = new StructureName(text[..colon], text[(colon + 1)..]);
        return true;
    }

    /// <summary>The structure a database key holds, when it is a structure's key.</summary>
    public static bool TryFromDatabaseKey(ReadOnlySpan<byte> key, out StructureName name)
    {
        name = default;
        var prefix = KeyPrefixBytes;

        if (!key.StartsWith(prefix))
        {
            return false;
        }

        string rest;

        try
        {
            rest = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(key[prefix.Length..]);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        // A stored key always carries its namespace; one without is not Bedrock's.
        return rest.Contains(':') && TryParse(rest, out name);
    }

    /// <summary>
    /// Whether both parts are lower-case ASCII letters, digits and underscores,
    /// at most 64 characters each: the names this wrapper gives its own
    /// structures, safe as a file and a folder name on every platform.
    /// </summary>
    public bool IsPlain => IsPlainPart(Namespace) && IsPlainPart(Name);

    /// <inheritdoc cref="IsPlain"/>
    public static bool IsPlainPart(string? part) =>
        part is { Length: > 0 and <= 64 }
        && part.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');

    public override string ToString() => $"{Namespace}:{Name}";
}
