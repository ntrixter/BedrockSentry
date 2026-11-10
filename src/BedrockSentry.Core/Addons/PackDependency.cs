using System.Text.Json;

namespace BedrockSentry.Core.Addons;

/// <summary>What a manifest's dependency entry names.</summary>
/// <remarks>
/// <strong>Two kinds that fail for two different reasons, and a reader that
/// conflates them gives an administrator the wrong repair.</strong> A pack
/// dependency is fixed by enabling the other pack; a script-module dependency
/// cannot be fixed by enabling anything, because it is a floor against the
/// server's own runtime. Telling somebody to find a missing pack when their
/// Bedrock build is simply too old is a wrong answer that reads as a real one.
/// </remarks>
public enum PackDependencyKind
{
    /// <summary>Another pack, by <c>uuid</c>. Satisfied from the library.</summary>
    OnPack,

    /// <summary>
    /// A script API module by <c>module_name</c> — <c>@minecraft/server</c> and
    /// its siblings. Satisfied by the server's Bedrock build and by nothing else.
    /// </summary>
    OnScriptModule,

    /// <summary>The entry names neither, or names both, or carries no version.</summary>
    Unreadable,
}

/// <summary>Whether a script module dependency asks for a stable module or a beta one.</summary>
/// <remarks>
/// <strong>This, and not the presence of scripts, is what needs the world's Beta
/// APIs experiment.</strong> A stable module runs without it: <c>@minecraft/server</c>
/// 2.8.0 was measured running on 1.26.45.1 and 1.26.51.1 in worlds without the
/// experiment (<c>T257</c>). A beta module is spelled with a prerelease tail —
/// <c>2.1.0-beta</c>, <c>1.0.0-beta.1.26.50-preview.25</c> — and is usable only
/// with it on.
/// </remarks>
public enum ScriptModuleRelease
{
    /// <summary>A plain version. No world experiment needed.</summary>
    Stable,

    /// <summary>A version with a prerelease tail. The world needs Beta APIs on.</summary>
    Prerelease,

    /// <summary>
    /// No version, or one this reader cannot place, or no script module named at
    /// all. Whether an experiment is needed cannot be told from the manifest.
    /// </summary>
    Unknown,
}

/// <summary>One entry of a manifest's <c>dependencies</c> array.</summary>
/// <param name="Kind">Which of the two it is. Callers switch; nothing reads
/// <paramref name="Target"/> without asking this first.</param>
/// <param name="Target">
/// The uuid for <see cref="PackDependencyKind.OnPack"/>, the module name for
/// <see cref="PackDependencyKind.OnScriptModule"/>, and the raw entry for
/// <see cref="PackDependencyKind.Unreadable"/>. <strong>One field on purpose:</strong>
/// two nullable fields where exactly one is ever set is a shape that invites a
/// reader to check the wrong one and find null, which looks like an absent
/// dependency rather than a misread one.
/// </param>
/// <param name="Version">The version floor, in either spelling, with its own three states.</param>
public sealed record PackDependency(
    PackDependencyKind Kind, string Target, PackVersionRead Version)
{
    /// <summary>
    /// A version string whose numeric part is followed by a prerelease tail.
    /// Read off the raw text because the version reader refuses the tail by
    /// design: it writes versions back into a world's pack list (<c>D84</c>).
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex PrereleaseText =
        new(@"^\s*\d+(\.\d+){0,3}-\S+", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Stable, prerelease or unknown, for a script module; <see cref="ScriptModuleRelease.Unknown"/>
    /// for any other kind of entry, which has no release channel.
    /// </summary>
    public ScriptModuleRelease Release => Kind != PackDependencyKind.OnScriptModule
        ? ScriptModuleRelease.Unknown
        : Version.State switch
        {
            PackVersionState.Read => ScriptModuleRelease.Stable,
            PackVersionState.Malformed when Version.Raw is { } raw && PrereleaseText.IsMatch(raw)
                => ScriptModuleRelease.Prerelease,
            _ => ScriptModuleRelease.Unknown,
        };

    /// <summary>Read one entry. Never throws on somebody else's file.</summary>
    public static PackDependency Read(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return new PackDependency(PackDependencyKind.Unreadable, entry.ToString(), PackVersionRead.OfAbsent());
        }

        var uuid = PackJson.Text(entry, "uuid");
        var module = PackJson.Text(entry, "module_name");
        var version = PackVersion.Read(PackJson.Field(entry, "version"));

        // Both, or neither, is a shape nothing here can act on. Preferring one
        // would be choosing which repair to offer for an entry whose author
        // meant something this cannot tell.
        if ((uuid is null) == (module is null))
        {
            return new PackDependency(PackDependencyKind.Unreadable, entry.ToString(), version);
        }

        return uuid is not null
            ? new PackDependency(PackDependencyKind.OnPack, uuid, version)
            : new PackDependency(PackDependencyKind.OnScriptModule, module!, version);
    }
}
