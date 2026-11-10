using BedrockSentry.Services.Instances;

namespace BedrockSentry.Services.Addons;

/// <summary>Whether what a server's scripts print reaches its console (<c>D91</c>).</summary>
public enum ScriptOutputState
{
    /// <summary>No script pack is switched on, so there is nothing to print and nothing to say.</summary>
    NotApplicable,

    /// <summary>
    /// The key is <c>true</c> and <c>content-log-level</c> lets script output
    /// through. A script's output reaches the console.
    /// </summary>
    Visible,

    /// <summary>The key is set to something other than <c>true</c>.</summary>
    Hidden,

    /// <summary>
    /// The key is <c>true</c>, and <c>content-log-level</c> is set to a level that
    /// hides what a script prints (<c>D94</c>).
    /// </summary>
    LevelHidden,

    /// <summary>
    /// The key is not in the file. BDS's default is off, so the server behaves
    /// exactly as <see cref="Hidden"/> does; kept apart because the row says
    /// which, and an administrator who set it off is not in the same place as
    /// one who never saw it.
    /// </summary>
    NotSet,

    /// <summary>There is no <c>server.properties</c>, so there is nowhere to set it.</summary>
    FileMissing,

    /// <summary>There is one, and it could not be read.</summary>
    Unreadable,
}

/// <summary>
/// <c>content-log-console-output-enabled</c> and <c>content-log-level</c>: whether
/// script output reaches one server's console, and turning it on when somebody
/// presses the button that asks for it.
/// </summary>
/// <remarks>
/// <para>BDS ships the key <c>false</c>, and false suppresses every line a
/// behaviour pack's scripts print — measured in both directions on one instance
/// with nothing else changed (<c>D91</c>). A script pack can therefore be
/// running correctly while nothing it prints is ever seen. With the key on,
/// <c>content-log-level</c> at <c>warning</c> or <c>error</c> hides the same lines
/// (<c>D94</c>), so the state reads both keys and the write sets both.</para>
///
/// <para><strong>Advisory, not a finding.</strong> Console logging costs disk and
/// noise on a busy server, and an administrator who turned it off is not wrong,
/// so the Add-ons tab reports the state and offers the change rather than scoring
/// it. And <strong>it is never written unasked</strong>: this decides what a
/// running server puts in its own console.</para>
/// </remarks>
public static class ScriptOutput
{
    /// <summary>The key, as BDS spells it.</summary>
    public const string Key = "content-log-console-output-enabled";

    /// <summary>The least serious content-log message BDS lets through, to the console as well.</summary>
    public const string LevelKey = "content-log-level";

    /// <summary>What a level that hides script output is set to: BDS's own default.</summary>
    public const string PassingLevel = "info";

    /// <summary>Whether a <c>content-log-level</c> value hides what a script prints.</summary>
    /// <remarks>
    /// Measured on Linux BDS 1.26.51.1 (<c>D94</c>): <c>warning</c> and <c>error</c>
    /// suppress every <c>console.log</c> line with the key on, and <c>info</c> and
    /// <c>verbose</c> pass them. Any other value is unmeasured and read as passing,
    /// so nothing is rewritten on a guess.
    /// </remarks>
    public static bool LevelHides(string? level) =>
        level?.Trim() is { } value
        && (value.Equals("warning", StringComparison.OrdinalIgnoreCase)
            || value.Equals("error", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What the companion pack's row says before it is switched on (<c>D93</c>):
    /// its Switch on also sets the key, because the pack reports through it.
    /// </summary>
    public const string CompanionSwitchOnNote =
        $"Switching it on also sets {Key}=true in {ServerProperties.FileName}, and {LevelKey}={PassingLevel} "
        + "where it is warning or error, because this pack reports to BedrockSentry through the console.";

    /// <summary>
    /// Is any script pack switched on here? Only one that is both installed and
    /// registered runs: a registration with no folder loads nothing.
    /// </summary>
    public static bool AnyScriptPackSwitchedOn(InstanceAddonState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state.Installed.Any(p =>
            p.Manifest.HasScriptModule && state.Registrations.Find(p.Manifest.Uuid) is not null);
    }

    /// <summary>The state, from whether it matters and what the file says.</summary>
    public static ScriptOutputState StateOf(bool scriptPackSwitchedOn, PropertiesFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (!scriptPackSwitchedOn)
        {
            return ScriptOutputState.NotApplicable;
        }

        return file.State switch
        {
            PropertiesReadState.Missing => ScriptOutputState.FileMissing,
            PropertiesReadState.Unreadable => ScriptOutputState.Unreadable,
            _ when !file.Values.TryGetValue(Key, out var value) => ScriptOutputState.NotSet,
            _ when !string.Equals(file.Values[Key], "true", StringComparison.OrdinalIgnoreCase)
                => ScriptOutputState.Hidden,
            _ when LevelHides(file.Values.GetValueOrDefault(LevelKey)) => ScriptOutputState.LevelHidden,
            _ => ScriptOutputState.Visible,
        };
    }

    /// <summary>The state of one server, read from its own files.</summary>
    public static ScriptOutputState Read(InstanceAddonState state, string instanceDirectory) =>
        StateOf(
            AnyScriptPackSwitchedOn(state),
            ServerProperties.ReadFile(ServerProperties.PathFor(instanceDirectory)));

    /// <summary>
    /// The label in the Add-ons tab's header, or null when no script pack is
    /// switched on and there is nothing to say.
    /// </summary>
    /// <remarks>
    /// Three words for seven states, because the header says which way it is and
    /// the Issues view says why: every state but <see cref="ScriptOutputState.Visible"/>
    /// also raises a card there.
    /// </remarks>
    public static string? HeaderLabel(ScriptOutputState state) => state switch
    {
        ScriptOutputState.NotApplicable => null,
        ScriptOutputState.Visible => "Script output shown",
        ScriptOutputState.Hidden or ScriptOutputState.NotSet or ScriptOutputState.LevelHidden =>
            "Script output hidden",
        ScriptOutputState.FileMissing or ScriptOutputState.Unreadable => "Script output unknown",

        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "No label is written for this state."),
    };

    /// <summary>The header label's badge: green when shown, amber when hidden, neutral when unknown.</summary>
    public static string HeaderBadgeClass(ScriptOutputState state) => state switch
    {
        ScriptOutputState.Visible => "state-badge state-running",
        ScriptOutputState.Hidden or ScriptOutputState.NotSet or ScriptOutputState.LevelHidden =>
            "state-badge state-warning",
        _ => "state-badge",
    };

    /// <summary>Whether the button is offered: only where pressing it can do what it says.</summary>
    public static bool CanTurnOn(ScriptOutputState state) =>
        state is ScriptOutputState.Hidden or ScriptOutputState.NotSet or ScriptOutputState.LevelHidden;

    /// <summary>What the row says.</summary>
    public static string Describe(ScriptOutputState state) => state switch
    {
        ScriptOutputState.NotApplicable => "No script pack is switched on here.",
        ScriptOutputState.Visible =>
            "What this server's scripts print reaches its console.",
        ScriptOutputState.Hidden =>
            $"What this server's scripts print does not reach its console: {Key} is set to off. "
            + "A script pack can be working and show nothing at all.",
        ScriptOutputState.NotSet =>
            $"What this server's scripts print does not reach its console: {Key} is not set, "
            + "and Bedrock's default is off. A script pack can be working and show nothing at all.",
        ScriptOutputState.LevelHidden =>
            $"What this server's scripts print does not reach its console: {Key} is on, but "
            + $"{LevelKey} is set to warning or error, which hides it. A script pack can be working "
            + "and show nothing at all.",
        ScriptOutputState.FileMissing =>
            $"This server has no {ServerProperties.FileName}, so whether script output reaches "
            + "its console cannot be set here.",
        ScriptOutputState.Unreadable =>
            $"This server's {ServerProperties.FileName} could not be read, so whether script "
            + "output reaches its console is not known.",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    /// <summary>
    /// Set the key to <c>true</c>, and a level that hides script output to
    /// <see cref="PassingLevel"/>. Null when it was written; otherwise why not.
    /// </summary>
    /// <remarks>
    /// <para>A missing file is refused rather than created: a one-line
    /// <c>server.properties</c> would replace every setting BDS's own copy
    /// carries with its defaults, and the package's file and comments are the
    /// better starting point. It takes effect at the server's next start.</para>
    ///
    /// <para>The level is changed only from <c>warning</c> or <c>error</c>
    /// (<c>D94</c>): <c>verbose</c> already passes script output and shows more,
    /// so lowering it to <c>info</c> would take away lines somebody asked for.
    /// Every set occurrence is checked, since readers disagree about which of two
    /// wins, and <see cref="ServerPropertiesDocument.Set"/> leaves one.</para>
    /// </remarks>
    public static Task<string?> TurnOnAsync(
        string instanceDirectory, CancellationToken cancellationToken = default) =>
        TurnOnAsync(instanceDirectory, ServerPropertiesDocument.LoadAsync, cancellationToken);

    /// <summary><see cref="TurnOnAsync(string, CancellationToken)"/> with the load injectable.</summary>
    /// <remarks>
    /// A seam for the unreadable arm alone, which a build running as root cannot
    /// produce by denying itself a file (<c>T176</c>).
    /// </remarks>
    internal static async Task<string?> TurnOnAsync(
        string instanceDirectory,
        Func<string, CancellationToken, Task<ServerPropertiesDocument>> load,
        CancellationToken cancellationToken = default)
    {
        var path = ServerProperties.PathFor(instanceDirectory);
        var document = await load(path, cancellationToken).ConfigureAwait(false);

        switch (document.State)
        {
            case PropertiesReadState.Missing:
                return $"This server has no {ServerProperties.FileName}, so there is nowhere to "
                       + "set it. Nothing has been written.";

            case PropertiesReadState.Unreadable:
                return $"{ServerProperties.FileName} could not be read"
                       + (document.Why is { Length: > 0 } why ? $": {why}" : ".")
                       + " Nothing has been written.";
        }

        document.Set(Key, "true");

        if (document.ValuesOf(LevelKey).Any(LevelHides))
        {
            document.Set(LevelKey, PassingLevel);
        }

        await document.SaveAsync(path, cancellationToken).ConfigureAwait(false);
        return null;
    }
}
