using BedrockSentry.Core.Instances;

namespace BedrockSentry.Tests;

/// <summary>
/// Every line here was measured off a real server (<c>docs/design.md</c> § 5.5, and
/// the <c>D25</c> connect/disconnect shapes) rather than taken from
/// documentation — the same rule the save-manifest and player-event parsers
/// follow, and for the same reason.
/// </summary>
public sealed class ConsoleLineClassifierTests
{
    [Theory]
    [InlineData("Starting Server")]
    [InlineData("Version: 1.26.36.1")]
    [InlineData("Server started.")]
    [InlineData("Server stop requested.")]
    [InlineData("Stopping server...")]
    [InlineData("Quit correctly")]
    public void Lifecycle_lines_are_the_spine_of_the_log(string message) =>
        Assert.Equal(ConsoleLineKind.Lifecycle, ConsoleLineClassifier.Classify(message));

    [Fact]
    public void A_connect_is_an_arrival() =>
        Assert.Equal(
            ConsoleLineKind.PlayerJoined,
            ConsoleLineClassifier.Classify("Player connected: SteveExample, xuid: 2535000000000001"));

    [Fact]
    public void A_disconnect_is_a_departure() =>
        Assert.Equal(
            ConsoleLineKind.PlayerLeft,
            ConsoleLineClassifier.Classify(
                "Player disconnected: SteveExample, xuid: 2535000000000001, pfid: 0000000000000000"));

    // These follow a connect within about two seconds and restate an arrival
    // already on screen. Classified before the Player checks, because both
    // begin with "Player " and neither is one.
    [Theory]
    [InlineData("Player Spawned: SteveExample xuid: 2535000000000001, pfid: 0000000000000000")]
    [InlineData("Player PartyIdUpdate:  pfid: 0000000000000000, partyid: , isLeader: false")]
    [InlineData("Running AutoCompaction...")]
    [InlineData("setting up server logging...")]
    [InlineData("opening worlds/Bedrock level/db")]
    public void Housekeeping_is_recognised_and_dimmed(string message) =>
        Assert.Equal(ConsoleLineKind.Housekeeping, ConsoleLineClassifier.Classify(message));

    [Theory]
    [InlineData("Saving...")]
    [InlineData("Data saved. Files are now ready to be copied.")]
    [InlineData("Changes to the world are resumed.")]
    public void The_save_handshake_reads_as_one_block(string message) =>
        Assert.Equal(ConsoleLineKind.Save, ConsoleLineClassifier.Classify(message));

    // Recognised by the same parser the backup uses, so the console cannot call
    // something a manifest that the backup does not.
    [Fact]
    public void The_manifest_line_belongs_to_the_save_it_describes() =>
        Assert.Equal(
            ConsoleLineKind.Save,
            ConsoleLineClassifier.Classify(
                "Bedrock level/db/000005.ldb:509, Bedrock level/db/CURRENT:16, "
                + "Bedrock level/level.dat:2543, Bedrock level/levelname.txt:13"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Level Name: Bedrock level")]
    [InlineData("Game mode: 0 Survival")]
    [InlineData("Difficulty: 1 EASY")]
    [InlineData("IPv4 supported, port: 19132")]
    [InlineData("There are 0/10 players online:")]
    [InlineData("score: 5, time: 3")]
    public void Anything_unrecognised_keeps_full_text_colour(string? message) =>
        Assert.Equal(ConsoleLineKind.Ordinary, ConsoleLineClassifier.Classify(message));

    private static ConsoleLine Line(
        ConsoleOrigin origin, string message, BdsLogLevel level = BdsLogLevel.Info) =>
        new(1, DateTimeOffset.UnixEpoch, origin, message, level, message, null);

    // Severity outranks subject. A warning that happens to mention saving is a
    // warning first, and two colours arguing over one line helps nobody.
    [Theory]
    [InlineData(BdsLogLevel.Warn)]
    [InlineData(BdsLogLevel.Error)]
    public void A_flagged_line_keeps_its_severity(BdsLogLevel level) =>
        Assert.Equal(
            ConsoleLineKind.Ordinary,
            ConsoleLineClassifier.Classify(Line(ConsoleOrigin.Stdout, "Saving...", level)));

    // Origin outranks subject too: the wrapper's own notices, the commands it
    // sent, and chat each already have a treatment.
    [Theory]
    [InlineData(ConsoleOrigin.Command)]
    [InlineData(ConsoleOrigin.Notice)]
    [InlineData(ConsoleOrigin.Chat)]
    [InlineData(ConsoleOrigin.Launch)]
    public void Only_plain_server_output_is_classified_by_content(ConsoleOrigin origin) =>
        Assert.Equal(
            ConsoleLineKind.Ordinary,
            ConsoleLineClassifier.Classify(Line(origin, "Server started.")));

    [Fact]
    public void Plain_stdout_is_classified()
    {
        Assert.Equal(
            ConsoleLineKind.Lifecycle,
            ConsoleLineClassifier.Classify(Line(ConsoleOrigin.Stdout, "Server started.")));

        // Unknown level is the common case for bare lines and must classify too
        // — the manifest arrives with no prefix at all.
        Assert.Equal(
            ConsoleLineKind.Save,
            ConsoleLineClassifier.Classify(
                Line(ConsoleOrigin.Stdout, "Bedrock level/level.dat:2543", BdsLogLevel.Unknown)));
    }

    // The startup banner arrives indented, and BdsLogParser deliberately keeps
    // that indentation rather than trimming it.
    [Fact]
    public void Indented_output_still_classifies() =>
        Assert.Equal(
            ConsoleLineKind.Lifecycle,
            ConsoleLineClassifier.Classify("   Version: 1.26.36.1"));

    // Both blocks below are transcribed from a screenshot of a real 1.26.40
    // console, which is why the wording is odd in places — it is BDS's, not
    // ours.
    private static ConsoleBuffer Buffer() => new();

    private static IReadOnlyList<ConsoleLine> Feed(params string[] raw)
    {
        var buffer = Buffer();
        var at = new DateTimeOffset(2026, 8, 15, 16, 53, 21, TimeSpan.Zero);

        return raw.Select(r => buffer.Append(ConsoleOrigin.Stdout, r, at)).ToList();
    }

    /// <summary>
    /// A warning wraps onto rows carrying no prefix of their own, and those rows
    /// were rendering as ordinary output — half the block amber, half not.
    /// </summary>
    [Fact]
    public void A_wrapped_warning_stays_a_warning_to_the_end_of_the_block()
    {
        var lines = Feed(
            "[2026-08-15 16:53:21:000 WARN] =========== ALLOW LIST WARNING ===========",
            "[2026-08-15 16:53:21:000 WARN] Allow list is enabled but contains no entries.",
            "        Alternatively, the allow list can be turned off by typing allowlist off.",
            "[2026-08-15 16:53:21:000 WARN] ==========================================");

        Assert.All(lines, l => Assert.Equal(BdsLogLevel.Warn, l.Level));
    }

    // The next prefixed line ends the block: a warning does not colour the rest
    // of the session.
    [Fact]
    public void A_prefixed_line_ends_the_carry()
    {
        var lines = Feed(
            "[2026-08-15 16:53:21:000 WARN] Allow list is enabled but contains no entries.",
            "        a wrapped continuation",
            "[2026-08-15 16:53:22:000 INFO] Server started.",
            "        something bare after the warning ended");

        Assert.Equal(BdsLogLevel.Warn, lines[1].Level);
        Assert.Equal(BdsLogLevel.Info, lines[2].Level);
        Assert.Equal(BdsLogLevel.Unknown, lines[3].Level);
    }

    // The save manifest arrives bare too. Inheriting an alarm from an unrelated
    // warning above it would colour a routine backup as a fault.
    [Fact]
    public void A_bare_line_the_classifier_recognises_keeps_its_own_meaning()
    {
        var lines = Feed(
            "[2026-08-15 16:53:21:000 WARN] Allow list is enabled but contains no entries.",
            "Bedrock level/db/CURRENT:16, Bedrock level/level.dat:2543");

        Assert.Equal(BdsLogLevel.Unknown, lines[1].Level);
        Assert.Equal(ConsoleLineKind.Save, lines[1].Kind);
    }

    /// <summary>
    /// Bedrock's telemetry notice: several lines between two rules, advice
    /// rather than an event, and worth dimming whole.
    /// </summary>
    [Fact]
    public void The_telemetry_notice_is_dimmed_from_its_banner_to_its_closing_rule()
    {
        var lines = Feed(
            "=============== TELEMETRY MESSAGE ===============",
            "Server Telemetry is currently not enabled.",
            "Enabling this telemetry helps us improve the game.",
            "",
            "To enable this feature, add the line 'emit-server-telemetry=true'",
            "to the server.properties file in the handheld/src-server directory",
            "=================================================",
            "Server started.");

        Assert.All(lines.Take(7), l => Assert.Equal(ConsoleLineKind.Housekeeping, l.Kind));

        // And the box closes: the line after it is read normally again.
        Assert.Equal(ConsoleLineKind.Lifecycle, lines[7].Kind);
    }

    // The rules are identical between one notice and the next, so opening a
    // block on a bare row of "=" would swallow whatever followed the next one.
    [Fact]
    public void A_rule_that_opens_no_known_banner_dims_nothing()
    {
        var lines = Feed(
            "==================================================",
            "Server started.");

        Assert.Equal(ConsoleLineKind.Ordinary, lines[0].Kind);
        Assert.Equal(ConsoleLineKind.Lifecycle, lines[1].Kind);
    }
}
