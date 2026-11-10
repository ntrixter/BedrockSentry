using BedrockSentry.Core.Instances;

namespace BedrockSentry.Core.Structures;

/// <summary>What one console command of a piece's sequence is for.</summary>
public enum StructureStep
{
    /// <summary><c>tickingarea add</c>: keeps the piece's chunks loaded with nobody near.</summary>
    AddTickingArea,

    /// <summary><c>testforblocks</c> over the piece's top layer: whether every chunk is loaded yet.</summary>
    Probe,

    /// <summary><c>structure save … true disk</c>.</summary>
    Save,

    /// <summary><c>kill</c> of every entity but players in the piece's box.</summary>
    Sweep,

    /// <summary><c>structure load</c> at the piece's lowest corner.</summary>
    Load,

    /// <summary><c>tickingarea remove</c>.</summary>
    RemoveTickingArea,
}

/// <summary>How BDS answered one command.</summary>
public enum StructureAnswerKind
{
    /// <summary>It did what it was sent to do.</summary>
    Done,

    /// <summary>
    /// The area is not loaded yet: <em>"Cannot access blocks outside of the
    /// world"</em>, the probe's answer until its ticking area has loaded.
    /// </summary>
    NotLoaded,

    /// <summary>Every ticking area the dimension holds is in use.</summary>
    TickingAreasFull,

    /// <summary>A ticking area of this name is already there, left by an earlier sequence.</summary>
    TickingAreaNameTaken,

    /// <summary>The ticking area would cover more than 100 chunks.</summary>
    TickingAreaTooLarge,

    /// <summary>There is no ticking area of this name to remove.</summary>
    NoTickingArea,

    /// <summary>The sweep found nothing to remove.</summary>
    NothingToRemove,

    /// <summary>The structure is in neither the world nor its packs.</summary>
    StructureNotFound,

    /// <summary>
    /// BDS queued the load until the area is loaded, and says nothing when it
    /// runs (<c>D86</c> finding 7), so whether it ran is unknown.
    /// </summary>
    LoadQueued,

    /// <summary>An error this table does not name; its text is BDS's own.</summary>
    OtherError,
}

/// <summary>One answer, in BDS's words.</summary>
public sealed record StructureAnswer(StructureAnswerKind Kind, string Text);

/// <summary>A command of a piece's sequence and how to read its answer.</summary>
public sealed class StructureCommand
{
    private readonly Func<string, BdsLogLevel, StructureAnswerKind?> _read;

    internal StructureCommand(StructureStep step, string text, Func<string, BdsLogLevel, StructureAnswerKind?> read)
    {
        Step = step;
        Text = text;
        _read = read;
    }

    /// <summary>What it is for.</summary>
    public StructureStep Step { get; }

    /// <summary>The line sent to the server.</summary>
    public string Text { get; }

    /// <summary>
    /// This command's answer, or null for a line that is not one: anything the
    /// server printed that is neither a shape this command is answered with nor
    /// an error.
    /// </summary>
    /// <remarks>
    /// <para>An error this table does not name is taken as the answer, as
    /// <see cref="StructureAnswerKind.OtherError"/>. A sequence sends one command
    /// and waits, so an unnamed error arriving in that wait is most likely about
    /// that command, and reading it as one stops the sequence, which is the safe
    /// direction; ignoring it would wait out the whole answer budget and then
    /// report no answer at all.</para>
    ///
    /// <para>Content-log lines are not taken, error or not: BDS prints them as
    /// <c>[Scripting] …</c>, <c>[Json] …</c> and so on after its own prefix, and a
    /// pack's script failing is not this command failing. An error message
    /// opening with <c>[</c> is one of those.</para>
    /// </remarks>
    public StructureAnswer? Read(ConsoleLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (line.Origin != ConsoleOrigin.Stdout)
        {
            return null;
        }

        var message = line.Message.Trim();

        // Under execute in, a failed command adds this line after its own error (BDS
        // 1.26.52.3, D106). It names nothing the error has not, and taken as an answer it
        // would be read by the next command's wait.
        if (message.StartsWith("Failed to execute '", StringComparison.Ordinal))
        {
            return null;
        }

        if (_read(message, line.Level) is { } kind)
        {
            return new StructureAnswer(kind, message);
        }

        return line.Level == BdsLogLevel.Error && message.Length > 0 && message[0] != '['
            ? new StructureAnswer(StructureAnswerKind.OtherError, message)
            : null;
    }

    public override string ToString() => Text;
}

/// <summary>
/// The console commands that save, sweep and load one piece of an area, and the
/// answers BDS gives them (<c>D86</c>, <c>D103</c>).
/// </summary>
/// <remarks>
/// <para>Every answer shape here was measured on BDS 1.26.52.3 on Linux, from the
/// server console, with <c>allow-cheats</c> both on and off; a Windows server's
/// wording has not been read. Each command's documentation quotes what it
/// answered.</para>
///
/// <para><strong>One ticking area, one name.</strong> Every piece borrows the
/// ticking area <see cref="TickingAreaName"/> and gives it back before the next:
/// a world holds ten across its dimensions (<c>D107</c>), the operator's own count
/// against them, and a piece never needs more than one. A name is unique in a
/// dimension, so a ticking area left by a sequence that was cut off is found by its
/// name in that dimension.</para>
///
/// <para><strong>Every command in the piece's dimension.</strong> The console runs in
/// the overworld, so a piece elsewhere sends each command through <c>execute in</c>
/// (<see cref="BedrockDimension.Run"/>), which answers in the same words.</para>
///
/// <para><strong>The piece and one chunk around it.</strong> The ticking area and
/// the probe reach <see cref="Ring"/> blocks past the piece on every side, because
/// a sweep sees only entities in loaded chunks and an entity whose body crosses
/// the piece's edge can stand in the next chunk. Measured end to end: an armor
/// stand at <c>x</c> 159.9, saved with both pieces either side of <c>x</c> 160,
/// came back twice when each ticking area covered its own piece only, since the
/// second piece's sweep could not see the copy the first had put down; and one
/// at 95.9 beside a piece from 96 was not swept. A 64 × 64 piece and its ring
/// touch at most 7 × 7 chunks, 49 of the 100 a ticking area may cover.</para>
/// </remarks>
public static class StructureCommands
{
    /// <summary>The name of the ticking area a piece borrows.</summary>
    public const string TickingAreaName = "bedrocksentry";

    /// <summary>How far past the piece, on <c>x</c> and <c>z</c>, its ticking area and probe reach: one chunk.</summary>
    public const int Ring = 16;

    /// <summary>
    /// Loads the piece's chunks and those around it with nobody near:
    /// <c>tickingarea add</c> over its box widened by <see cref="Ring"/>.
    /// </summary>
    /// <remarks>
    /// Measured: <em>"Added ticking area from 0, 0, 0 to 63, 0, 63."</em> and then
    /// <em>"1/10 ticking areas in use."</em> on a line of its own. BDS widens the
    /// box to whole chunks and prints <c>y</c> as 0 whatever was given, so the
    /// answer is recognised by its opening words rather than by coordinates.
    /// Refusals: <em>"Max number of ticking areas (10) has already been reached.
    /// Cannot add more ticking areas."</em>, <em>"A ticking area with the name
    /// twin already exists."</em>, and <em>"Ticking area contains more than 100
    /// chunks, ticking area is too large and cannot be created."</em>
    /// </remarks>
    public static StructureCommand AddTickingArea(BlockBox box, BedrockDimension? dimension = null) => new(
        StructureStep.AddTickingArea,
        In(dimension).Run($"tickingarea add {Corners(WithRing(box))} {TickingAreaName}"),
        (message, level) => message switch
        {
            _ when level != BdsLogLevel.Error && message.StartsWith("Added ticking area from ", StringComparison.Ordinal)
                => StructureAnswerKind.Done,
            _ when level == BdsLogLevel.Error && message.StartsWith("Max number of ticking areas", StringComparison.Ordinal)
                => StructureAnswerKind.TickingAreasFull,
            _ when level == BdsLogLevel.Error
                && message == $"A ticking area with the name {TickingAreaName} already exists."
                => StructureAnswerKind.TickingAreaNameTaken,
            _ when level == BdsLogLevel.Error && message.StartsWith("Ticking area contains more than", StringComparison.Ordinal)
                => StructureAnswerKind.TickingAreaTooLarge,
            _ => null,
        });

    /// <summary>Gives the ticking area back: <c>tickingarea remove</c> by its name.</summary>
    /// <remarks>
    /// Measured: <em>"Removed ticking area(s)"</em>, then a line naming each one
    /// removed, a blank line and the count in use. With none of that name:
    /// <em>"No ticking areas named twin exist in the current dimension."</em>,
    /// which leaves nothing to give back.
    /// </remarks>
    public static StructureCommand RemoveTickingArea(BedrockDimension? dimension = null) => new(
        StructureStep.RemoveTickingArea,
        In(dimension).Run($"tickingarea remove {TickingAreaName}"),
        (message, level) => message switch
        {
            "Removed ticking area(s)" when level != BdsLogLevel.Error => StructureAnswerKind.Done,
            _ when level == BdsLogLevel.Error
                && message == $"No ticking areas named {TickingAreaName} exist in the current dimension."
                => StructureAnswerKind.NoTickingArea,
            _ => null,
        });

    /// <summary>
    /// Asks whether every chunk the ticking area holds is loaded: <c>testforblocks</c>
    /// comparing the top layer of the piece and its ring with itself.
    /// </summary>
    /// <remarks>
    /// <para>One command for every chunk the ticking area holds. Measured on a 5 × 5
    /// chunk area of terrain never generated before: <em>"Cannot access blocks
    /// outside of the world"</em> for 1.9 s after the ticking area was added, then
    /// <em>"4096 blocks compared"</em>; a <c>testforblock</c> at either corner
    /// turned in the same 0.1 s. A layer compared with itself always matches,
    /// so the only other answer is an error.</para>
    ///
    /// <para>The top layer because a chunk loads whole. A piece and its ring make
    /// a layer of at most 96 × 96 blocks; <em>"9216 blocks compared"</em> was
    /// measured for one, and <em>"512000 blocks compared"</em> for a far larger
    /// box.</para>
    ///
    /// <para>In the Nether, a chunk not loaded answers the same words at any height, 0 and
    /// 127 included (BDS 1.26.52.3, <c>D106</c>), so the dimension's own top layer serves.</para>
    /// </remarks>
    public static StructureCommand Probe(BlockBox box, BedrockDimension? dimension = null)
    {
        var top = In(dimension).Height.Top;
        box = WithRing(box);

        return new(
            StructureStep.Probe,
            In(dimension).Run(
                $"testforblocks {box.Min.X} {top} {box.Min.Z} {box.Max.X} {top} {box.Max.Z} {box.Min.X} {top} {box.Min.Z}"),
            (message, level) => message switch
            {
                _ when level != BdsLogLevel.Error && message.EndsWith(" blocks compared", StringComparison.Ordinal)
                    => StructureAnswerKind.Done,
                "Cannot access blocks outside of the world" when level == BdsLogLevel.Error
                    => StructureAnswerKind.NotLoaded,
                _ => null,
            });
    }

    /// <summary>
    /// Saves the piece into the world's database, entities included:
    /// <c>structure save <paramref name="name"/> … true disk</c>.
    /// </summary>
    /// <remarks>
    /// Measured: <em>"Saved a structure with name bedrocksentry:edge"</em>; over an
    /// area not loaded, <em>"Cannot access blocks outside of the world"</em>. An
    /// entity whose body crosses the piece's edge is saved with it: armor
    /// stands at <c>x</c> −0.1 and 64.1 went with a box from 0 to 63.
    /// </remarks>
    public static StructureCommand Save(StructureName name, BlockBox box, BedrockDimension? dimension = null) => new(
        StructureStep.Save,
        In(dimension).Run($"structure save {name} {Corners(box)} true disk"),
        (message, level) => message switch
        {
            _ when level != BdsLogLevel.Error && message == $"Saved a structure with name {name}"
                => StructureAnswerKind.Done,
            "Cannot access blocks outside of the world" when level == BdsLogLevel.Error
                => StructureAnswerKind.NotLoaded,
            _ => null,
        });

    /// <summary>
    /// Removes every entity but players from the piece's box, before a load puts
    /// the piece's own back: <c>kill @e[type=!player,…]</c>.
    /// </summary>
    /// <remarks>
    /// <para>Measured: <em>"Killed kB, kC, kD, kE"</em>, naming each entity, and
    /// with nothing there <em>"No targets matched selector"</em> as an error. The
    /// box reaches whatever a saved piece holds: an entity whose body crosses
    /// its edge (armor stands at <c>x</c> −0.1 and 64.1 for a box from 0 to 63,
    /// not those at −0.6 and 64.6), the same rule a save takes them by.</para>
    ///
    /// <para>A mob killed this way drops what it would on any death: a sweep that
    /// answered <em>"Killed Armor Stand, Pig, Zombie, Cow, Sheep, Chicken"</em> left
    /// drops a second sweep answered <em>"Killed Raw Porkchop, Rotten Flesh, Raw
    /// Beef, White Wool, Raw Mutton, Raw Chicken"</em> to, and a third found
    /// nothing. So a box is swept until nothing answers.</para>
    ///
    /// <para>Pieces of one area are swept and loaded one after another, so an
    /// entity across the edge between two pieces, saved with both, ends up once:
    /// the later piece's sweep, its chunk loaded by the ring, removes the copy
    /// the earlier load put down, and its own load puts it back.</para>
    /// </remarks>
    public static StructureCommand Sweep(BlockBox box, BedrockDimension? dimension = null)
    {
        var size = box.Size;

        return new(
            StructureStep.Sweep,
            In(dimension).Run(
                $"kill @e[type=!player,x={box.Min.X},y={box.Min.Y},z={box.Min.Z},"
                + $"dx={size.X - 1},dy={size.Y - 1},dz={size.Z - 1}]"),
            (message, level) => message switch
            {
                _ when level != BdsLogLevel.Error && message.StartsWith("Killed ", StringComparison.Ordinal)
                    => StructureAnswerKind.Done,
                "No targets matched selector" when level == BdsLogLevel.Error
                    => StructureAnswerKind.NothingToRemove,
                _ => null,
            });
    }

    /// <summary>Places the piece at its lowest corner: <c>structure load</c>.</summary>
    /// <remarks>
    /// Measured: <em>"Loaded a structure of name bedrocksentry:edge"</em>; a name
    /// in neither the world nor its packs, <em>"The structure bedrocksentry:nosuch
    /// can't be found. Make sure the name was spelled correctly and try
    /// again."</em>; and over an area not loaded, <em>"A placement request has been
    /// queued and it will be executed when the specified area is fully
    /// loaded."</em>, not an error.
    /// </remarks>
    public static StructureCommand Load(StructureName name, BlockVector at, BedrockDimension? dimension = null) => new(
        StructureStep.Load,
        In(dimension).Run($"structure load {name} {at.X} {at.Y} {at.Z}"),
        (message, level) => message switch
        {
            _ when level != BdsLogLevel.Error && message == $"Loaded a structure of name {name}"
                => StructureAnswerKind.Done,
            _ when level != BdsLogLevel.Error
                && message.StartsWith("A placement request has been queued", StringComparison.Ordinal)
                => StructureAnswerKind.LoadQueued,
            _ when level == BdsLogLevel.Error && message.StartsWith($"The structure {name} can't be found", StringComparison.Ordinal)
                => StructureAnswerKind.StructureNotFound,
            _ => null,
        });

    /// <summary><paramref name="box"/> widened by <see cref="Ring"/> on <c>x</c> and <c>z</c>.</summary>
    public static BlockBox WithRing(BlockBox box) => BlockBox.Between(
        new BlockVector(box.Min.X - Ring, box.Min.Y, box.Min.Z - Ring),
        new BlockVector(box.Max.X + Ring, box.Max.Y, box.Max.Z + Ring));

    private static BedrockDimension In(BedrockDimension? dimension) => dimension ?? BedrockDimension.Overworld;

    private static string Corners(BlockBox box) =>
        $"{box.Min.X} {box.Min.Y} {box.Min.Z} {box.Max.X} {box.Max.Y} {box.Max.Z}";
}
