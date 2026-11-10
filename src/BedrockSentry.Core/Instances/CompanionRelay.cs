using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using BedrockSentry.Core.Addons;
using BedrockSentry.Core.Players;
using BedrockSentry.Core.Structures;
using BedrockSentry.Core.Sync;

namespace BedrockSentry.Core.Instances;

/// <summary>
/// A line the companion pack printed to the console, read by
/// <see cref="CompanionRelayReader"/>.
/// </summary>
public abstract record CompanionRelay
{
    /// <summary>The sentence the pack wrote for a person, on the kinds that carry one.</summary>
    public virtual string? Sentence => null;
}

/// <summary>The pack loaded with the world: <c>hello|protocol|pack version</c>.</summary>
public sealed record HelloRelay(int Protocol, string PackVersion) : CompanionRelay;

/// <summary>The pack's answer to the wrapper's probe: <c>probe|protocol</c>.</summary>
public sealed record ProbeRelay(int Protocol) : CompanionRelay;

/// <summary>
/// A player died: <c>death|name|cause|killer type|killer name|x|y|z|dimension|sentence</c>.
/// </summary>
public sealed record DeathRelay(CompanionDeath Death) : CompanionRelay
{
    public override string? Sentence => Death.Message;
}

/// <summary>A player's first spawn after joining: <c>join|name|x|y|z|dimension|sentence</c>.</summary>
public sealed record JoinRelay(string Gamertag, BlockPosition Position, string Message) : CompanionRelay
{
    public override string? Sentence => Message;
}

/// <summary>Where a player was as they left: <c>leave|name|x|y|z|dimension|sentence</c>.</summary>
public sealed record LeaveRelay(string Gamertag, BlockPosition Position, string Message) : CompanionRelay
{
    public override string? Sentence => Message;
}

/// <summary>
/// One online player's position, answering the stop exchange (<c>D35</c>):
/// <c>where|name|x|y|z|dimension</c>.
/// </summary>
public sealed record WhereRelay(string Gamertag, BlockPosition Position) : CompanionRelay;

/// <summary>The end of the stop exchange's answer and how many players it named: <c>where-end|count</c>.</summary>
public sealed record WhereEndRelay(int Count) : CompanionRelay;

/// <summary>
/// The pack took a group of outlines (<c>D106</c>): <c>outline|group|count</c>, after the last
/// part of <c>scriptevent bedrocksentry:outline</c>.
/// </summary>
public sealed record OutlineRelay(string Group, int Count) : CompanionRelay;

/// <summary>
/// A <c>syncarea</c> an operator typed (<c>D106</c>):
/// <c>area|player|action|corner|show|x|y|z|dimension|name</c>, the name last so it may hold
/// anything a quoted name can. The player and the dimension are empty for the console, and
/// the fields an action does not use are empty.
/// </summary>
public sealed record AreaRelay(SyncAreaCommand Command) : CompanionRelay;

/// <summary>
/// A <c>showtickingarea</c> an operator typed (<c>D107</c>): <c>tickshow|player|show|dimension|name</c>,
/// the name last; the player and the dimension are empty for the console.
/// </summary>
public sealed record TickShowRelay(TickShowCommand Command) : CompanionRelay;

/// <summary>
/// A <c>restorearea</c> an operator typed (<c>D108</c>):
/// <c>restore|player|backup|dimension|x1|y1|z1|x2|y2|z2|name</c>, the name last. The player
/// and the dimension are empty for the console, the backup is empty for the newest, and the
/// six coordinates are all empty when a Sync Area is named and all whole numbers when they
/// say where.
/// </summary>
public sealed record RestoreRelay(AreaRestoreCommand Command) : CompanionRelay;

/// <summary>
/// One of the companion pack's commands a player ran in game: <c>cmd|player|command line</c>,
/// the command line last and as the player typed it, without its slash. The console's own
/// commands are not relayed, since the console shows them as typed.
/// </summary>
public sealed record CommandRelay(string Player, string Command) : CompanionRelay
{
    public override string? Sentence => $"{Player} ran /{Command}";
}

/// <summary>
/// Something a player said: <c>chat|name|text</c>. <see cref="ChatParser"/> reads
/// this shape as chat; the pack does not print it.
/// </summary>
public sealed record ChatRelay(string Gamertag, string Text) : CompanionRelay;

/// <summary>A kind this wrapper does not read, from a newer pack.</summary>
public sealed record UnknownRelay(string Kind) : CompanionRelay;

/// <summary>
/// Reads a console message as a line the companion pack printed, by the layout of
/// <see cref="CompanionPack.Protocol"/>.
/// </summary>
/// <remarks>
/// <para><strong>The grammar.</strong> The pack prints
/// <c>[BedrockSentry] kind|field|…</c> (<c>companion-pack/scripts/main.js</c>). A
/// field before the last never holds a pipe, and on the kinds that carry a sentence
/// for a person the sentence is last and takes every pipe after its field.</para>
///
/// <para><strong>Tags before the marker.</strong> Linux BDS 1.26.51.1 prints the
/// line as <c>[Scripting] [BedrockSentry] …</c> after its own timestamp and level
/// (<c>D93</c>'s spike). Up to <see cref="MaxTags"/> bracketed tags are accepted
/// until Windows BDS is measured (<c>T294</c>). Anything else in front of the marker,
/// such as a name or a sentence, means the line is not the pack's.</para>
///
/// <para><strong>Forward compatibility.</strong> A newer pack's kind reads as an
/// <see cref="UnknownRelay"/>. A later protocol puts a new field before the
/// sentence, which shifts it, so a line is only what this reads it as when its pack
/// announced the same protocol in a <see cref="HelloRelay"/> or a
/// <see cref="ProbeRelay"/>; checking that is the caller's decision. A kind with no
/// sentence takes fields appended after the ones read here, so a newer pack's
/// <c>hello</c> still says which protocol it speaks.</para>
///
/// <para><strong>A line that breaks the layout is not a relay.</strong> A known kind
/// with a field missing, a coordinate that is not a whole number or a dimension that
/// is not a namespaced id reads as ordinary output rather than as a position somebody
/// would believe.</para>
/// </remarks>
public static class CompanionRelayReader
{
    /// <summary>How many bracketed tags may stand before the marker.</summary>
    private const int MaxTags = 3;

    /// <summary>The longest tag accepted, between its brackets.</summary>
    private const int MaxTagLength = 32;

    /// <summary>Reads <paramref name="message"/>, the text after BDS's own prefix.</summary>
    public static bool TryRead(string? message, [NotNullWhen(true)] out CompanionRelay? relay)
    {
        relay = null;

        if (Body(message) is not { } body)
        {
            return false;
        }

        var bar = body.IndexOf('|');
        var kind = bar < 0 ? body : body[..bar];
        var rest = bar < 0 ? null : body[(bar + 1)..];

        if (kind.Length == 0 || kind.Any(char.IsWhiteSpace))
        {
            return false;
        }

        relay = kind switch
        {
            "hello" => Hello(rest),
            "probe" => Probe(rest),
            "death" => Death(rest),
            "join" => Join(rest),
            "leave" => Leave(rest),
            "where" => Where(rest),
            "where-end" => WhereEnd(rest),
            "outline" => Outline(rest),
            "area" => Area(rest),
            "tickshow" => TickShow(rest),
            "restore" => Restore(rest),
            "cmd" => Command(rest),
            "chat" => Chat(rest),
            _ => new UnknownRelay(kind),
        };

        return relay is not null;
    }

    /// <summary>What follows the marker, or null when the marker is not where the pack puts it.</summary>
    private static string? Body(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        var at = 0;

        for (var tags = 0; ; tags++)
        {
            if (message.AsSpan(at).StartsWith(CompanionPack.Marker, StringComparison.Ordinal))
            {
                return message[(at + CompanionPack.Marker.Length)..];
            }

            if (tags == MaxTags || at >= message.Length || message[at] != '[')
            {
                return null;
            }

            var close = message.IndexOf(']', at + 1);
            var length = close - at - 1;

            if (close < 0 || length < 1 || length > MaxTagLength
                || message.AsSpan(at + 1, length).Contains('['))
            {
                return null;
            }

            at = close + 1;

            if (at < message.Length && message[at] == ' ')
            {
                at++;
            }
        }
    }

    private static HelloRelay? Hello(string? rest) =>
        Fields(rest, 2, sentence: false) is [var protocol, var version, ..]
        && Protocol(protocol) is { } number && version.Length > 0
            ? new HelloRelay(number, version)
            : null;

    private static ProbeRelay? Probe(string? rest) =>
        Fields(rest, 1, sentence: false) is [var protocol, ..] && Protocol(protocol) is { } number
            ? new ProbeRelay(number)
            : null;

    private static DeathRelay? Death(string? rest) =>
        Fields(rest, 8, sentence: true) is [var name, var cause, var killerType, var killerName, var x, var y, var z, var dimension, var sentence]
        && IsName(name) && cause.Length > 0 && Position(x, y, z, dimension) is { } position
            ? new DeathRelay(new CompanionDeath(name, cause, Optional(killerType), Optional(killerName), position, sentence))
            : null;

    private static JoinRelay? Join(string? rest) =>
        Fields(rest, 5, sentence: true) is [var name, var x, var y, var z, var dimension, var sentence]
        && IsName(name) && Position(x, y, z, dimension) is { } position
            ? new JoinRelay(name, position, sentence)
            : null;

    private static LeaveRelay? Leave(string? rest) =>
        Fields(rest, 5, sentence: true) is [var name, var x, var y, var z, var dimension, var sentence]
        && IsName(name) && Position(x, y, z, dimension) is { } position
            ? new LeaveRelay(name, position, sentence)
            : null;

    private static WhereRelay? Where(string? rest) =>
        Fields(rest, 5, sentence: false) is [var name, var x, var y, var z, var dimension, ..]
        && IsName(name) && Position(x, y, z, dimension) is { } position
            ? new WhereRelay(name, position)
            : null;

    private static WhereEndRelay? WhereEnd(string? rest) =>
        Fields(rest, 1, sentence: false) is [var count, ..]
        && int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var players)
            ? new WhereEndRelay(players)
            : null;

    private static OutlineRelay? Outline(string? rest) =>
        Fields(rest, 2, sentence: false) is [var group, var count, ..]
        && group.Length > 0
        && int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var areas)
            ? new OutlineRelay(group, areas)
            : null;

    private static AreaRelay? Area(string? rest)
    {
        if (Fields(rest, 8, sentence: true) is not [var player, var action, var corner, var show, var x, var y, var z, var dimension, var name])
        {
            return null;
        }

        SyncAreaAction? verb = action switch
        {
            "new" => SyncAreaAction.New,
            "update" => SyncAreaAction.Update,
            "show" => SyncAreaAction.Show,
            "list" => SyncAreaAction.List,
            _ => null,
        };

        int? cornerNumber = corner switch { "" => null, "1" => 1, "2" => 2, _ => -1 };
        bool? shown = show switch { "" => null, "true" => true, "false" => false, _ => null };
        var where = dimension.Length == 0 ? null : BedrockDimension.Find(dimension);
        var coordinates = (Coordinate(x), Coordinate(y), Coordinate(z));
        var allEmpty = x.Length == 0 && y.Length == 0 && z.Length == 0;

        if (verb is not { } known || cornerNumber == -1
            || (show.Length > 0 && shown is null)
            || (dimension.Length > 0 && where is null)
            || (!allEmpty && coordinates is not ({ }, { }, { })))
        {
            return null;
        }

        BlockVector? position = coordinates is ({ } bx, { } by, { } bz) ? new BlockVector(bx, by, bz) : null;
        return new AreaRelay(new SyncAreaCommand(
            Optional(player), known, name, cornerNumber, shown, position, where));
    }

    private static TickShowRelay? TickShow(string? rest)
    {
        if (Fields(rest, 3, sentence: true) is not [var player, var show, var dimension, var name] || name.Trim().Length == 0)
        {
            return null;
        }

        bool? shown = show switch { "true" => true, "false" => false, _ => null };
        var where = dimension.Length == 0 ? null : BedrockDimension.Find(dimension);

        return shown is { } value && (dimension.Length == 0 || where is not null)
            ? new TickShowRelay(new TickShowCommand(Optional(player), value, where, name))
            : null;
    }

    private static RestoreRelay? Restore(string? rest)
    {
        if (Fields(rest, 9, sentence: true) is not [var player, var backup, var dimension, var x1, var y1, var z1, var x2, var y2, var z2, var name])
        {
            return null;
        }

        var where = dimension.Length == 0 ? null : BedrockDimension.Find(dimension);
        string[] texts = [x1, y1, z1, x2, y2, z2];
        var numbers = texts.Select(Coordinate).ToArray();

        if (dimension.Length > 0 && where is null)
        {
            return null;
        }

        AreaRestoreTarget target;

        if (texts.All(t => t.Length == 0))
        {
            if (name.Trim().Length == 0)
            {
                return null;
            }

            target = AreaRestoreTarget.Named(name);
        }
        else if (numbers.All(n => n is not null))
        {
            target = AreaRestoreTarget.Corners(
                new BlockVector(numbers[0]!.Value, numbers[1]!.Value, numbers[2]!.Value),
                new BlockVector(numbers[3]!.Value, numbers[4]!.Value, numbers[5]!.Value),
                where ?? BedrockDimension.Overworld);
        }
        else
        {
            return null;
        }

        return new RestoreRelay(new AreaRestoreCommand(Optional(player), Optional(backup), where, target));
    }

    private static CommandRelay? Command(string? rest) =>
        Fields(rest, 1, sentence: true) is [var player, var command] && IsName(player) && command.Trim().Length > 0
            ? new CommandRelay(player, command.Trim())
            : null;

    private static ChatRelay? Chat(string? rest) =>
        Fields(rest, 1, sentence: true) is [var name, var text] && IsName(name)
            ? new ChatRelay(name, text)
            : null;

    /// <summary>
    /// The fields after the kind: exactly <paramref name="count"/> and then the
    /// sentence, which keeps every further pipe, on a kind that carries one; at least
    /// <paramref name="count"/> on a kind that does not.
    /// </summary>
    private static string[]? Fields(string? rest, int count, bool sentence)
    {
        if (rest is null)
        {
            return null;
        }

        var fields = sentence ? rest.Split('|', count + 1) : rest.Split('|');
        return fields.Length >= (sentence ? count + 1 : count) ? fields : null;
    }

    private static int? Protocol(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : null;

    private static bool IsName(string text) => !string.IsNullOrWhiteSpace(text);

    private static string? Optional(string text) => text.Length == 0 ? null : text;

    /// <summary>
    /// A whole-block position. The dimension is checked for the shape of an id,
    /// <c>namespace:name</c>, rather than against a list, so a dimension added later
    /// is read and named as it is (<see cref="DimensionNames"/>).
    /// </summary>
    private static BlockPosition? Position(string x, string y, string z, string dimension)
    {
        var colon = dimension.IndexOf(':');

        return Coordinate(x) is { } bx && Coordinate(y) is { } by && Coordinate(z) is { } bz
            && colon > 0 && colon < dimension.Length - 1 && !dimension.Any(char.IsWhiteSpace)
                ? new BlockPosition(bx, by, bz, dimension)
                : null;
    }

    private static int? Coordinate(string text) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
