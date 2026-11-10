using System.Text.Json;

namespace BedrockSentry.Core.Addons;

/// <summary>Which of a server's two pack folders a pack belongs in.</summary>
public enum PackPlacement
{
    /// <summary>Nothing in <c>modules</c> says. Never guessed at.</summary>
    Unknown,

    /// <summary><c>behavior_packs</c> — it carries a data or script module.</summary>
    Behavior,

    /// <summary><c>resource_packs</c>.</summary>
    Resource,
}

/// <summary>How reading a <c>manifest.json</c> went.</summary>
public enum PackManifestState
{
    /// <summary>Read, and it has an identity.</summary>
    Read,

    /// <summary>The bytes are not JSON.</summary>
    NotJson,

    /// <summary>
    /// JSON, and no <c>header.uuid</c> — so there is nothing to manage it by.
    /// Deliberately not the same as <see cref="NotJson"/>: one is a broken file
    /// and the other is a folder that is not a pack, and the folder is the case
    /// an administrator meets by dropping the wrong thing in the library.
    /// </summary>
    NotAPack,

    /// <summary>The file could not be opened at all.</summary>
    Unreadable,
}

/// <summary>A pack's <c>manifest.json</c>, as far as this can read it.</summary>
/// <remarks>
/// <para><strong>Four of this type's fields were written from recollection once
/// and every one of them was wrong</strong> (<c>D81</c>): <c>version</c> is a
/// semver string at <c>format_version: 3</c> rather than always a triple, so is
/// <c>min_engine_version</c>; <c>dependencies</c> carries two different shapes;
/// and the author is in <c>metadata.authors</c> rather than in the header. Each
/// would have produced a manager that silently mishandles ordinary files, which
/// is why each has a test naming the shape rather than only the happy path.</para>
///
/// <para><strong>Nothing here is inferred from a missing key.</strong> An absent
/// version, an absent engine floor and an unreadable one are separate states
/// carried on <see cref="PackVersionRead"/>, and <see cref="Placement"/> has an
/// <see cref="PackPlacement.Unknown"/> arm rather than defaulting to behaviour —
/// putting a pack in the wrong folder is a write to somebody's server that looks
/// like it worked.</para>
/// </remarks>
public sealed record PackManifest(
    string Uuid,
    string Name,
    string? Description,
    PackVersionRead Version,
    PackVersionRead MinEngineVersion,
    int? FormatVersion,
    IReadOnlyList<string> ModuleTypes,
    IReadOnlyList<PackDependency> Dependencies,
    IReadOnlyList<string> Authors)
{
    // The module types this reader acts on, named once. "script" decides two
    // different questions — which folder the pack belongs in, and whether the
    // world needs an experiment — and a literal spelled at each site is how two
    // callers of one rule come to disagree about it.
    private const string DataModule = "data";
    private const string ScriptModule = "script";
    private const string ResourcesModule = "resources";

    /// <summary>Case-insensitive, because manifests are written by hand.</summary>
    private bool Declares(string moduleType) =>
        ModuleTypes.Any(t => string.Equals(t, moduleType, StringComparison.OrdinalIgnoreCase));

    /// <summary>Does it carry a script module?</summary>
    /// <remarks>
    /// <c>D80</c>'s third and fourth silent failures both start here: every script
    /// pack needs the server's own <c>permissions.json</c> to list
    /// <c>@minecraft/server</c>, and one that depends on a beta module also needs
    /// Beta APIs on in the world (<see cref="ScriptsRelease"/>). Neither is visible
    /// from anywhere an administrator would think to look.
    /// </remarks>
    public bool HasScriptModule => Declares(ScriptModule);

    /// <summary>
    /// Whether its scripts need the world's Beta APIs experiment, read from the
    /// script modules it depends on.
    /// </summary>
    /// <remarks>
    /// One beta module is enough to need it; all of them stable is the only way
    /// to say it is not needed. A pack naming no script module, or naming one
    /// this cannot place, is <see cref="ScriptModuleRelease.Unknown"/>: saying
    /// either answer for it would be a guess.
    /// </remarks>
    public ScriptModuleRelease ScriptsRelease
    {
        get
        {
            var releases = ScriptModuleDependencies.Select(d => d.Release).ToList();

            if (releases.Contains(ScriptModuleRelease.Prerelease))
            {
                return ScriptModuleRelease.Prerelease;
            }

            return releases.Count > 0 && releases.All(r => r == ScriptModuleRelease.Stable)
                ? ScriptModuleRelease.Stable
                : ScriptModuleRelease.Unknown;
        }
    }

    /// <summary>Which folder it belongs in, or that nothing said.</summary>
    public PackPlacement Placement
    {
        get
        {
            if (Declares(DataModule) || Declares(ScriptModule))
            {
                return PackPlacement.Behavior;
            }

            return Declares(ResourcesModule) ? PackPlacement.Resource : PackPlacement.Unknown;
        }
    }

    /// <summary>The pack dependencies alone, which the library can satisfy.</summary>
    public IEnumerable<PackDependency> PackDependencies =>
        Dependencies.Where(d => d.Kind == PackDependencyKind.OnPack);

    /// <summary>The script-module floors, which only the Bedrock build can satisfy.</summary>
    public IEnumerable<PackDependency> ScriptModuleDependencies =>
        Dependencies.Where(d => d.Kind == PackDependencyKind.OnScriptModule);

    /// <summary>
    /// Apply a pack's own <c>texts/en_US.lang</c> over the header's strings.
    /// </summary>
    /// <remarks>
    /// A pack that localises itself puts its display name in <c>pack.name</c>
    /// and its description in <c>pack.description</c>, and its header then holds
    /// a key rather than a sentence. A manager reading the header alone shows
    /// the wrong text — often a bare token — for any pack that does this, and it
    /// is ordinary rather than rare. Applied as a separate step so the two
    /// sources stay legible and a pack without the file is untouched.
    /// </remarks>
    public PackManifest WithDisplayText(IReadOnlyDictionary<string, string>? language)
    {
        if (language is null)
        {
            return this;
        }

        var name = language.TryGetValue("pack.name", out var n) && n.Length > 0 ? n : Name;
        var description = language.TryGetValue("pack.description", out var d) && d.Length > 0
            ? d
            : Description;

        return this with { Name = name, Description = description };
    }
}

/// <summary>A manifest read, in one of four states.</summary>
public readonly record struct PackManifestRead(
    PackManifestState State, PackManifest? Manifest, string? Reason)
{
    public static PackManifestRead Of(PackManifest manifest) =>
        new(PackManifestState.Read, manifest, null);

    public static PackManifestRead Failed(PackManifestState state, string reason) =>
        new(state, null, reason);
}
