using System.Globalization;

namespace BedrockSentry.Core.Commands;

/// <summary>The badge on one row of the Commands view.</summary>
public enum SourceBadge
{
    /// <summary>Read from this server's own version.</summary>
    Current,

    /// <summary>Usable, but from an older version than the server runs.</summary>
    Behind,

    /// <summary>A throwaway copy is being read now.</summary>
    Reading,

    /// <summary>Queued, or waiting on something that has not happened yet.</summary>
    Waiting,

    /// <summary>The last attempt failed; the row says why.</summary>
    Failed,

    /// <summary>An add-on's commands complete, and the server loads it when it restarts.</summary>
    NextRestart,

    /// <summary>An add-on the running server has loaded.</summary>
    Loaded,

    /// <summary>Nothing of this kind on this server.</summary>
    None,
}

/// <summary>One row of the Commands view: where completion gets one kind of word.</summary>
/// <param name="Sentence">
/// Plain text, with a command the administrator might type written between backticks;
/// <see cref="Runs"/> splits it for the page.
/// </param>
public sealed record SourceRow(SourceBadge Badge, string Title, string Sentence)
{
    /// <summary>The badge's words.</summary>
    public string BadgeText => Badge switch
    {
        SourceBadge.Current => "Current",
        SourceBadge.Behind => "Behind",
        SourceBadge.Reading => "Reading",
        SourceBadge.Waiting => "Waiting",
        SourceBadge.Failed => "Failed",
        SourceBadge.NextRestart => "Next restart",
        SourceBadge.Loaded => "Loaded",
        _ => "None",
    };

    /// <summary>The badge's classes, from the palette every other badge uses.</summary>
    public string BadgeClass => Badge switch
    {
        SourceBadge.Current or SourceBadge.Loaded => "state-badge state-running",
        SourceBadge.Behind or SourceBadge.NextRestart => "state-badge state-warning",
        SourceBadge.Failed => "state-badge state-faulted",
        _ => "state-badge state-info",
    };

    /// <summary>The sentence in pieces, each marked as a command or as prose.</summary>
    public IReadOnlyList<(string Text, bool Code)> Runs =>
        [.. Sentence.Split('`').Select((text, i) => (text, i % 2 == 1)).Where(r => r.text.Length > 0)];
}

/// <summary>Why the commands in use are the ones they are.</summary>
public enum CommandsSourceState
{
    /// <summary>Read from this server's own version.</summary>
    Exact,

    /// <summary>This server's version is being read now.</summary>
    Reading,

    /// <summary>This server's version is queued behind another run.</summary>
    Waiting,

    /// <summary>Reading this server's version failed.</summary>
    Failed,

    /// <summary>This server's version is not in the package store, so there is nothing to run.</summary>
    NoPackage,

    /// <summary>The server has not reported a version yet.</summary>
    VersionUnknown,
}

/// <summary>The commands in use and why.</summary>
/// <param name="Grammar">The commands completion uses, whichever version they came from.</param>
/// <param name="Shipped">
/// <paramref name="Grammar"/> is the list that ships with BedrockSentry, read from
/// <see cref="OptionProbes.MeasuredOn"/>, because no run on this machine has finished.
/// </param>
public sealed record CommandsSource(
    CommandsSourceState State, string? ServerVersion, CommandGrammar Grammar, bool Shipped, string? Reason = null);

/// <summary>How the vanilla data in use compares with the server.</summary>
public enum VanillaSourceState
{
    /// <summary>Mojang's release for this server's version.</summary>
    Exact,

    /// <summary>Mojang has not published this version; an older release is in use.</summary>
    Behind,

    /// <summary>The last fetch failed.</summary>
    Failed,

    /// <summary>Nothing fetched yet.</summary>
    Waiting,

    /// <summary>Nothing fetched, and the BDS update check that fetches it is turned off.</summary>
    CheckingOff,
}

/// <summary>The vanilla data in use.</summary>
/// <param name="Wanted">The release matching the server, or null when its version is unknown.</param>
/// <param name="Chosen">The release in use, or null when none has been fetched.</param>
/// <param name="CheckedUtc">When the registry was last asked.</param>
public sealed record VanillaSource(
    VanillaSourceState State,
    string? Wanted,
    string? Chosen,
    DateTimeOffset? FetchedUtc,
    DateTimeOffset? CheckedUtc,
    int Blocks,
    int Items,
    int Entities,
    string? Reason = null);

/// <summary>Where the add-on row stands.</summary>
public enum AddonSourceState
{
    /// <summary>No switched-on add-on adds commands or content.</summary>
    None,

    /// <summary>A throwaway copy with the add-ons in it is being read now.</summary>
    Reading,

    /// <summary>That run is queued behind another.</summary>
    Waiting,

    /// <summary>That run failed.</summary>
    Failed,

    /// <summary>Read; the running server has not loaded the add-ons yet.</summary>
    NextRestart,

    /// <summary>Read; the server is not running and loads them when it starts.</summary>
    NextStart,

    /// <summary>Read, and the running server started after the add-ons were switched on.</summary>
    Loaded,
}

/// <summary>What the switched-on add-ons add.</summary>
/// <param name="Packs">Their names, as the Add-ons tab shows them.</param>
/// <param name="Commands">Commands read from a throwaway copy; zero when none of them runs scripts.</param>
/// <param name="ReadUtc">When those commands were read, or null when nothing needed reading.</param>
/// <param name="Ids">What their files declare.</param>
public sealed record AddonSource(
    AddonSourceState State,
    IReadOnlyList<string> Packs,
    int Commands,
    DateTimeOffset? ReadUtc,
    AddonIds Ids,
    string? Reason = null);

/// <summary>The Commands view's rows, worded once.</summary>
/// <remarks>
/// <para>Each row says where one kind of word comes from and how fresh it is, because a
/// list that completes confidently from the wrong version is worse than one that says it
/// is behind (<c>D105</c>).</para>
///
/// <para><paramref name="at"/> on each method turns a UTC moment into the words that
/// follow it in a sentence, <c>at 4:12 PM on 7 Oct</c>; the page passes the
/// conversion to local time, which this cannot do in a test.</para>
/// </remarks>
public static class CompletionSources
{
    /// <summary>What completion does not offer, and why.</summary>
    public const string NotCompleted =
        "Not completed, because nothing lists them: sounds, particles, music, loot tables, recipes, fog ids, "
        + "block-state values and the values of an add-on's own option lists. The server checks them when the "
        + "command runs.";

    /// <summary>The commands, their forms and descriptions.</summary>
    public static SourceRow Commands(CommandsSource source, Func<DateTimeOffset, string> at)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(at);

        const string Title = "Commands, forms and descriptions";
        var g = source.Grammar;
        var counts =
            $"{Count(g.Commands.Count, "command")}, {Count(g.AliasCount, "alias", "aliases")} and {Count(g.FormCount, "form")}";

        if (source.State is CommandsSourceState.Exact && !source.Shipped)
        {
            return new SourceRow(SourceBadge.Current, Title,
                $"Read with `help` from a throwaway copy of Bedrock {g.BdsVersion} with cheats on, {at(g.CapturedUtc)}. "
                + $"{counts}. Your world was not touched.");
        }

        var used = source.Shipped
            ? $"the list that ships with BedrockSentry, read from Bedrock {g.BdsVersion}: {counts}"
            : $"Bedrock {g.BdsVersion}'s, read {at(g.CapturedUtc)}: {counts}";
        var version = source.ServerVersion ?? "this server's version";

        return source.State switch
        {
            CommandsSourceState.Reading => new SourceRow(SourceBadge.Reading, Title,
                $"Reading Bedrock {version}'s commands from a throwaway copy with cheats on. Until then, completion uses {used}."),
            CommandsSourceState.Waiting => new SourceRow(SourceBadge.Waiting, Title,
                $"Bedrock {version}'s commands are read from a throwaway copy after the run ahead of it. Until then, completion uses {used}."),
            CommandsSourceState.Failed => new SourceRow(SourceBadge.Failed, Title,
                $"Bedrock {version}'s commands could not be read: {Sentence(source.Reason)} Completion uses {used}. "
                + "The next BDS update check tries again."),
            CommandsSourceState.NoPackage => new SourceRow(SourceBadge.Behind, Title,
                $"Bedrock {version}'s package is not in the package store, so its commands cannot be read. Completion uses {used}."),
            CommandsSourceState.VersionUnknown => new SourceRow(SourceBadge.Waiting, Title,
                $"This server has not reported its Bedrock version yet; it does when it starts. Until then, completion uses {used}."),
            _ => new SourceRow(SourceBadge.Behind, Title, $"Completion uses {used}."),
        };
    }

    /// <summary>The option lists.</summary>
    public static SourceRow Options(CommandsSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        const string Title = "Option lists";
        var g = source.Grammar;
        var lists = g.Options.Count(o => o.Values.Count > 0);
        var notChecked = g.Options.Count(o => !o.Checked);
        var badge = source.State is CommandsSourceState.Exact && !source.Shipped ? SourceBadge.Current : SourceBadge.Behind;

        var how = source.Shipped
            ? $"They ship with BedrockSentry, measured by sending every value to Bedrock {g.BdsVersion} and keeping the ones it took."
            : $"They ship with BedrockSentry, and the run on Bedrock {g.BdsVersion} sent every value to the server and kept the ones it took.";
        var unchecked_ = notChecked == 0
            ? string.Empty
            : $" {Count(notChecked, "list")} could not be checked on that version and use the values measured on Bedrock {OptionProbes.MeasuredOn}.";

        return new SourceRow(badge, Title,
            $"{Count(lists, "list")}, such as game modes, damage causes and ease types. {how}{unchecked_}");
    }

    /// <summary>The gamerules.</summary>
    public static SourceRow GameRules(CommandsSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var g = source.Grammar;
        var badge = source.State is CommandsSourceState.Exact && !source.Shipped ? SourceBadge.Current : SourceBadge.Behind;
        return new SourceRow(badge, "Gamerules",
            $"{Count(g.GameRules.Count, "rule")}, listed by `gamerule` on Bedrock {g.BdsVersion}.");
    }

    /// <summary>The blocks, items, entities and the rest of Mojang's data.</summary>
    public static SourceRow Vanilla(VanillaSource source, Func<DateTimeOffset, string> at)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(at);

        const string Title = "Blocks, items and entities";
        var contents =
            $"{Count(source.Blocks, "block")}, {Count(source.Items, "item")}, {Count(source.Entities, "entity", "entities")}, "
            + "and the biomes, effects, enchantments and camera presets";
        var fetched = source.FetchedUtc is { } f ? $", fetched {at(f)}" : string.Empty;
        var lastChecked = source.CheckedUtc is { } c ? $", last {at(c)}" : string.Empty;

        return source.State switch
        {
            VanillaSourceState.Exact => new SourceRow(SourceBadge.Current, Title,
                $"Mojang's vanilla data {source.Chosen}{fetched}: {contents}."),
            VanillaSourceState.Behind => new SourceRow(SourceBadge.Behind, Title,
                $"Mojang has not published its data for {source.Wanted} yet, so completion uses {source.Chosen} for now: "
                + $"{contents}. Every BDS update check looks again{lastChecked}."),
            VanillaSourceState.Failed when source.Chosen is not null => new SourceRow(SourceBadge.Failed, Title,
                $"Mojang's vanilla data could not be fetched{(source.CheckedUtc is { } fc ? " " + at(fc) : string.Empty)}: "
                + $"{Sentence(source.Reason)} Completion uses {source.Chosen}{fetched}: {contents}."),
            VanillaSourceState.Failed => new SourceRow(SourceBadge.Failed, Title,
                $"Mojang's vanilla data could not be fetched{(source.CheckedUtc is { } fc2 ? " " + at(fc2) : string.Empty)}: "
                + $"{Sentence(source.Reason)} Blocks, items and entities complete once a BDS update check fetches it."),
            VanillaSourceState.CheckingOff => new SourceRow(SourceBadge.None, Title,
                "Mojang's vanilla data is fetched with the BDS update check, which is turned off in Settings, "
                + "so blocks, items and entities do not complete."),
            _ => new SourceRow(SourceBadge.Waiting, Title,
                source.Wanted is { } wanted
                    ? $"Mojang's vanilla data for {wanted} is fetched at the next BDS update check."
                    : "Mojang's vanilla data is fetched at the next BDS update check, for the version this server reports when it starts."),
        };
    }

    /// <summary>Player names.</summary>
    /// <param name="seenNotListed">Players seen anywhere who are not on this server's allowlist.</param>
    /// <param name="listed">Names on the allowlist, or null when it could not be read.</param>
    public static SourceRow Players(int seenNotListed, int? listed)
    {
        var remove = listed is { } n
            ? $"`allowlist remove` offers the {Count(n, "name")} on it."
            : "`allowlist remove` offers nothing, because this server's allowlist could not be read.";

        return new SourceRow(SourceBadge.Current, "Player names",
            "Online players wherever the server takes a player or a selector. `allowlist add` offers the players "
            + $"BedrockSentry has seen that are not on this server's allowlist ({seenNotListed} now); {remove}");
    }

    /// <summary>The switched-on add-ons.</summary>
    public static SourceRow Addons(AddonSource source, Func<DateTimeOffset, string> at)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(at);

        const string Title = "Add-ons";
        if (source.State is AddonSourceState.None)
        {
            return new SourceRow(SourceBadge.None, Title, "No add-on on this server adds commands or content.");
        }

        var names = Names(source.Packs);
        var files = Files(source.Ids);
        var read = source.ReadUtc is { } r
            ? $"{Count(source.Commands, "command")} read {at(r)}" + (files is null ? string.Empty : $", and {files}")
            : files ?? "nothing completion can use";

        return source.State switch
        {
            AddonSourceState.Reading => new SourceRow(SourceBadge.Reading, Title,
                $"Reading the commands of {names} from a throwaway copy with them in it."
                + (files is null ? string.Empty : $" Their {files} complete already.")),
            AddonSourceState.Waiting => new SourceRow(SourceBadge.Waiting, Title,
                $"The commands of {names} are read from a throwaway copy after the run ahead of it."
                + (files is null ? string.Empty : $" Their {files} complete already.")),
            AddonSourceState.Failed => new SourceRow(SourceBadge.Failed, Title,
                $"The commands of {names} could not be read: {Sentence(source.Reason)}"
                + (files is null ? string.Empty : $" Their {files} still complete.")),
            AddonSourceState.NextRestart => new SourceRow(SourceBadge.NextRestart, Title,
                $"{names}: {read}. Completion offers them now; the server loads them at its next restart."),
            AddonSourceState.NextStart => new SourceRow(SourceBadge.NextRestart, Title,
                $"{names}: {read}. Completion offers them now; the server loads them when it starts."),
            _ => new SourceRow(SourceBadge.Loaded, Title, $"{names}: {read}."),
        };
    }

    private static string? Files(AddonIds ids)
    {
        if (ids.Count == 0)
        {
            return null;
        }

        var parts = new List<string>();
        if (ids.Blocks.Count > 0)
        {
            parts.Add(Count(ids.Blocks.Count, "block"));
        }

        if (ids.Items.Count > 0)
        {
            parts.Add(Count(ids.Items.Count, "item"));
        }

        if (ids.Entities.Count > 0)
        {
            parts.Add(Count(ids.Entities.Count, "entity", "entities"));
        }

        if (ids.Functions.Count > 0)
        {
            parts.Add(Count(ids.Functions.Count, "function"));
        }

        return $"{Names(parts)} from their files";
    }

    /// <summary>A list in words: <c>A</c>, <c>A and B</c>, <c>A, B and C</c>.</summary>
    internal static string Names(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "the add-ons",
        1 => names[0],
        _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}",
    };

    private static string Count(int n, string one, string? many = null) =>
        n == 1 ? $"1 {one}" : $"{n.ToString("N0", CultureInfo.InvariantCulture)} {many ?? one + "s"}";

    // A reason is stored without a full stop so it can sit inside a sentence; one is added here.
    private static string Sentence(string? reason) =>
        reason is { Length: > 0 } r ? (r.EndsWith('.') ? r : r + ".") : "no reason was recorded.";
}
