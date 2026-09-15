using BedrockSentry.Services.Instances;

namespace BedrockSentry.Tests;

/// <summary>
/// Every fixture here is copied verbatim out of the <c>server.properties</c>
/// that BDS 1.26.36.1 writes on first run. That is the point: the editor's whole
/// design is that Bedrock documents its own settings, so the parser is only
/// worth anything if it survives the real file's layout — descriptions that run
/// to a dozen lines, allowed-value lists spread across several of them, and
/// settings that are commented out with their default sitting on the commented
/// line.
/// </summary>
public class ServerPropertiesDocumentTests
{
    private static ServerPropertiesDocument Parse(string text) =>
        ServerPropertiesDocument.Parse(text.Split('\n').Select(l => l.TrimEnd('\r')));

    [Fact]
    public void Reads_a_setting_with_its_description_and_allowed_values()
    {
        var document = Parse(
            """
            gamemode=survival
            # Sets the game mode for new players.
            # Allowed values: "survival", "creative", or "adventure"
            """);

        var gamemode = Assert.Single(document.Properties);

        Assert.Equal("gamemode", gamemode.Key);
        Assert.Equal("survival", gamemode.Value);
        Assert.True(gamemode.IsSet);
        Assert.Equal("Sets the game mode for new players.", gamemode.Description);
        Assert.Equal(ServerPropertyEditor.Choice, gamemode.Editor);
        Assert.Equal(["survival", "creative", "adventure"], gamemode.Choices);
    }

    // Mojang ships several settings commented out. Skipping them would hide a
    // setting from anyone who did not already know it existed — which is exactly
    // the person this page is for.
    [Fact]
    public void Reads_a_commented_out_setting_as_unset()
    {
        var document = Parse(
            """
            # allow-player-joining=true
            # When set to 'false' players will not be able to join the server unless explicitly allowed by scripting (via AsyncPlayerJoinBeforeEvent)
            # Allowed values: true, false
            # Default: true
            """);

        var property = Assert.Single(document.Properties);

        Assert.Equal("allow-player-joining", property.Key);
        Assert.False(property.IsSet);
        Assert.Equal("true", property.Value);
        Assert.Equal("true", property.DocumentedDefault);
        Assert.Equal(ServerPropertyEditor.Boolean, property.Editor);

        // Reading it as configuration would be acting on a default we only
        // guessed at, so the two accessors deliberately disagree here.
        Assert.Equal("true", document.ValueOf("allow-player-joining"));
        Assert.Null(document.SetValueOf("allow-player-joining"));
    }

    // force-gamemode's own documentation opens with "# force-gamemode=false (or
    // force-gamemode is not defined ...)". Read naively that is a second,
    // commented-out setting, and the real one two lines up gets overwritten by
    // a description of itself.
    [Fact]
    public void A_setting_quoted_inside_its_own_description_is_not_a_second_setting()
    {
        var document = Parse(
            """
            force-gamemode=false
            # force-gamemode=false (or force-gamemode is not defined in the server.properties)
            # prevents the server from sending to the client gamemode values other
            # than the gamemode value saved by the server during world creation.

            difficulty=easy
            # Sets the difficulty of the world.
            """);

        Assert.Equal(["force-gamemode", "difficulty"], document.Properties.Select(p => p.Key));
        Assert.True(document.Properties[0].IsSet);
        Assert.StartsWith("force-gamemode=false (or", document.Properties[0].Description);
    }

    [Fact]
    public void An_allowed_values_list_that_runs_over_several_lines_is_one_block()
    {
        var document = Parse(
            """
            # server-udp-ports=
            # Configures UDP client transport ports.
            # Allowed values:
            #   * `internal` or `start-end` — the internal port constrains allocation.
            #   * `[ip:]external:internal` — additionally publishes a mapping.
            """);

        var property = Assert.Single(document.Properties);

        Assert.Equal("server-udp-ports", property.Key);
        Assert.Equal(string.Empty, property.Value);
        Assert.Contains("internal port constrains allocation", property.AllowedValues);

        // Prose about port mappings is not a set of values to choose from.
        Assert.Equal(ServerPropertyEditor.Text, property.Editor);
    }

    // chat-restriction lists its values first and then explains them, unlike
    // every other setting in the file. Reading the explanation as part of the
    // list turns a three-item dropdown into a text box.
    [Fact]
    public void Prose_after_a_finished_allowed_values_line_is_not_part_of_the_list()
    {
        var document = Parse(
            """
            chat-restriction=None
            # Allowed values: "None", "Dropped", "Disabled"
            # This represents the level of restriction applied to the chat for each player.
            # "None" is the default and represents regular free chat.
            """);

        var property = Assert.Single(document.Properties);

        Assert.Equal(ServerPropertyEditor.Choice, property.Editor);
        Assert.Equal(["None", "Dropped", "Disabled"], property.Choices);
        Assert.StartsWith("This represents", property.Description);
    }

    [Fact]
    public void Turning_a_commented_setting_on_uncomments_it_in_place()
    {
        var document = Parse(
            """
            server-name=Dedicated Server

            # server-ip=
            # Leave empty to bind to all interfaces.
            """);

        document.Set("server-ip", "203.0.113.77");

        Assert.Equal(
            [
                "server-name=Dedicated Server",
                "",
                "server-ip=203.0.113.77",
                "# Leave empty to bind to all interfaces.",
            ],
            document.Lines);

        Assert.True(document.Find("server-ip")!.IsSet);
    }

    // Appending instead would leave the file saying the setting twice — once
    // commented above its documentation, once live at the bottom.
    [Fact]
    public void Turning_a_commented_setting_on_does_not_also_append_it()
    {
        var document = Parse("# allow-cheats=false");

        document.Set("allow-cheats", "true");

        Assert.Equal(["allow-cheats=true"], document.Lines);
        Assert.Single(document.Properties);
    }

    [Fact]
    public void A_setting_the_file_never_mentioned_is_appended_after_a_blank_line()
    {
        var document = Parse("server-name=Dedicated Server");

        document.Set("level-name", "Survival");

        Assert.Equal(
            ["server-name=Dedicated Server", "", "level-name=Survival"],
            document.Lines);
    }

    // A newline in a value would turn one setting into two, and the second half
    // would be something the server refuses to parse.
    [Fact]
    public void A_value_containing_a_line_break_is_flattened_on_write()
    {
        var document = Parse("server-name=Dedicated Server");

        document.Set("server-name", "Sur\nvival");

        Assert.Equal(["server-name=Survival"], document.Lines);
    }

    // Applying a set of edits means walking the settings and writing the ones
    // that changed, which is a loop over Properties that calls Set inside it.
    // Handing out the backing list makes that throw "Collection was modified"
    // partway through — and it did, on the first real save anyone tried.
    [Fact]
    public void Settings_can_be_written_while_walking_them()
    {
        var document = Parse(
            """
            server-name=Dedicated Server

            enable-lan-visibility=true

            # server-ip=
            """);

        foreach (var property in document.Properties)
        {
            document.Set(property.Key, property.Key == "enable-lan-visibility" ? "false" : property.Value);
        }

        Assert.Equal("false", document.ValueOf("enable-lan-visibility"));
        Assert.Equal("Dedicated Server", document.ValueOf("server-name"));
    }

    // The text view saves by parsing whatever was typed and writing the document
    // back. That is only safe if parsing is lossless — a line the parser does not
    // recognise has to survive, or editing one setting by hand would quietly
    // delete the comment above it.
    [Fact]
    public async Task Parsing_and_saving_returns_the_file_unchanged()
    {
        var original = new[]
        {
            "# Mojang's own header",
            "server-name=Dedicated Server",
            "# Used as the server name",
            "",
            "  indented-setting = spaced out  ",
            "this line is not a setting at all",
            "# emit-server-telemetry=false",
            "",
        };

        var directory = Path.Combine(Path.GetTempPath(), "bedrocksentry-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "server.properties");

        try
        {
            await ServerPropertiesDocument.Parse(original).SaveAsync(path);

            Assert.Equal(original, await File.ReadAllLinesAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void An_empty_document_has_no_settings() =>
        Assert.Empty(Parse(string.Empty).Properties);

    [Fact]
    public async Task A_missing_file_loads_as_an_empty_document()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"), "server.properties");

        Assert.Empty((await ServerPropertiesDocument.LoadAsync(path)).Properties);
    }
}

/// <summary>
/// The mapping from Mojang's "Allowed values" wording to a control. Each case
/// below is a line that actually appears in the shipped file.
/// </summary>
public class AllowedValuesTests
{
    private static (ServerPropertyEditor Editor, IReadOnlyList<string> Choices,
        decimal? Minimum, decimal? Maximum, bool IsInteger) Read(string allowed, string value = "") =>
        AllowedValues.Interpret(allowed, value);

    [Theory]
    [InlineData("\"true\" or \"false\"")]
    [InlineData("true, false")]
    public void A_pair_of_booleans_becomes_a_toggle(string allowed)
    {
        var (editor, choices, _, _, _) = Read(allowed);

        Assert.Equal(ServerPropertyEditor.Boolean, editor);
        Assert.Equal(["true", "false"], choices);
    }

    [Theory]
    [InlineData("\"peaceful\", \"easy\", \"normal\", or \"hard\"", 4)]
    [InlineData("\"visitor\", \"member\", \"operator\"", 3)]
    [InlineData("\"zlib\", \"snappy\"", 2)]
    public void A_quoted_list_becomes_a_choice(string allowed, int count)
    {
        var (editor, choices, _, _, _) = Read(allowed);

        Assert.Equal(ServerPropertyEditor.Choice, editor);
        Assert.Equal(count, choices.Count);
    }

    // server-build-radius-ratio quotes one value and then describes a range.
    // Reading it as a one-item list would hide every legal number behind a
    // dropdown containing the word "Disabled".
    [Fact]
    public void A_quoted_value_mixed_with_prose_is_not_a_choice() =>
        Assert.Equal(
            ServerPropertyEditor.Text,
            Read("\"Disabled\" or any value in range [0.0, 1.0]").Editor);

    [Fact]
    public void A_sentence_containing_a_comma_is_not_a_choice() =>
        Assert.Equal(
            ServerPropertyEditor.Text,
            Read("An IPv4 or IPv6 literal, or empty.").Editor);

    [Fact]
    public void A_bracketed_range_of_integers_bounds_the_field()
    {
        var (editor, _, minimum, maximum, isInteger) = Read("Integers in the range [1, 65535]");

        Assert.Equal(ServerPropertyEditor.Number, editor);
        Assert.Equal(1, minimum);
        Assert.Equal(65535, maximum);
        Assert.True(isInteger);
    }

    // player-movement-action-direction-threshold's range looks identical to an
    // integer one. The word "integer" is the only thing that separates them, and
    // its shipped default is 0.85.
    [Fact]
    public void A_range_that_does_not_say_integer_allows_fractions()
    {
        var (editor, _, minimum, maximum, isInteger) = Read("Any value in the range of [0, 1]");

        Assert.Equal(ServerPropertyEditor.Number, editor);
        Assert.Equal(0, minimum);
        Assert.Equal(1, maximum);
        Assert.False(isInteger);
    }

    [Theory]
    [InlineData("Any positive integer.", 1)]
    [InlineData("Any non-negative integer.", 0)]
    [InlineData("Positive integer equal to 5 or greater.", 5)]
    public void A_described_floor_bounds_the_field(string allowed, int expected)
    {
        var (editor, _, minimum, _, _) = Read(allowed);

        Assert.Equal(ServerPropertyEditor.Number, editor);
        Assert.Equal(expected, minimum);
    }

    [Fact]
    public void A_bare_dash_range_is_a_range()
    {
        var (editor, _, minimum, maximum, _) = Read("0-65535");

        Assert.Equal(ServerPropertyEditor.Number, editor);
        Assert.Equal(0, minimum);
        Assert.Equal(65535, maximum);
    }

    [Fact]
    public void Any_string_stays_a_text_box() =>
        Assert.Equal(ServerPropertyEditor.Text, Read("Any string without semicolon symbol.").Editor);

    // Several movement settings ship with no allowed-values line at all. A value
    // of exactly true or false is unambiguous; anything else is guesswork, and a
    // text box is the honest answer to guesswork.
    [Theory]
    [InlineData("false", ServerPropertyEditor.Boolean)]
    [InlineData("0.5", ServerPropertyEditor.Text)]
    [InlineData("", ServerPropertyEditor.Text)]
    public void An_undocumented_setting_falls_back_to_its_value(string value, ServerPropertyEditor expected) =>
        Assert.Equal(expected, AllowedValues.Interpret(null, value).Editor);
}

/// <summary>
/// Windows stores the port in its UDP table in network byte order, inside a
/// DWORD. Read straight through, 19132 comes out as 48202 — a number that looks
/// enough like a port to survive being glanced at, and that would make the
/// wrapper report conflicts on ports nothing is using while missing the real one.
/// </summary>
public class UdpListenerPortTests
{
    [Theory]
    [InlineData(0xBC4A, 19132)]   // 0x4ABC, byte-swapped
    [InlineData(0xBD4A, 19133)]
    [InlineData(0x0100, 1)]
    [InlineData(0xFFFF, 65535)]
    public void The_port_is_read_out_of_network_byte_order(int raw, int expected) =>
        Assert.Equal(expected, UdpListeners.PortOf(raw));

    // Swapping twice is the identity, which is the cheap way to say the two
    // halves are not being read from the same end.
    [Theory]
    [InlineData(19132)]
    [InlineData(19133)]
    [InlineData(25565)]
    public void Swapping_a_port_twice_returns_it(int port) =>
        Assert.Equal(port, UdpListeners.PortOf(UdpListeners.PortOf(port)));
}

/// <summary>
/// What the editor refuses outright, and what it merely flags.
/// </summary>
public class ServerPropertyValidationTests
{
    /// <param name="description">
    /// The prose above the allowed-values line, which the range parser now reads
    /// because Mojang's two halves can disagree about it.
    /// </param>
    private static ServerProperty Property(
        string allowed, string? description = null, string value = "") =>
        ServerPropertiesDocument
            .Parse(description is null
                ? [$"key={value}", $"# Allowed values: {allowed}"]
                : [$"key={value}", $"# {description}", $"# Allowed values: {allowed}"])
            .Properties[0];

    [Fact]
    public void A_value_that_is_not_on_the_list_is_refused() =>
        Assert.NotNull(Property("\"survival\", \"creative\"").Reject("spectator"));

    [Fact]
    public void A_value_on_the_list_is_accepted() =>
        Assert.Null(Property("\"survival\", \"creative\"").Reject("creative"));

    [Fact]
    public void Text_in_a_number_is_refused() =>
        Assert.NotNull(Property("Integers in the range [1, 65535]").Reject("nineteen"));

    [Fact]
    public void A_fraction_in_an_integer_field_is_refused() =>
        Assert.NotNull(Property("Integers in the range [1, 65535]").Reject("19132.5"));

    [Fact]
    public void A_fraction_where_the_file_allows_one_is_accepted() =>
        Assert.Null(Property("Any value in the range of [0, 1]").Reject("0.85"));

    // A value genuinely outside the range is flagged, never refused: Mojang's
    // documentation and implementation disagree often enough that a wrapper
    // which blocked a save would be wrong more often than the file is.
    [Fact]
    public void A_number_outside_the_documented_range_is_flagged_but_allowed()
    {
        var property = Property("Any positive integer.");

        Assert.Null(property.Reject("-1"));
        Assert.NotNull(property.Warn("-1"));
    }

    /// <summary>
    /// The two halves of Mojang's own documentation disagree, and the editor was
    /// believing the wrong one. <c>max-threads</c> says "Any positive integer" on
    /// its allowed-values line and "If set to 0 or removed then it will use as
    /// many as possible" in the prose directly above it — so the editor warned
    /// that 0 was below a minimum of 1, in the same row as the text explaining
    /// what 0 does. Reported from a real install.
    /// </summary>
    [Fact]
    public void A_zero_the_description_documents_is_inside_the_range()
    {
        var property = Property(
            "Any positive integer.",
            description:
                "Maximum number of threads the server will try to use. If set to 0 or removed "
                + "then it will use as many as possible.");

        Assert.Equal(0, property.Minimum);
        Assert.Null(property.Reject("0"));
        Assert.Null(property.Warn("0"));

        // Still an integer field, and still a floor — a negative thread count is
        // as wrong as it ever was.
        Assert.NotNull(property.Warn("-1"));
    }

    // Without that sentence, "any positive integer" means what it says.
    [Fact]
    public void A_zero_the_description_says_nothing_about_is_still_flagged()
    {
        var property = Property(
            "Any positive integer.", description: "How many somethings to allow.");

        Assert.Equal(1, property.Minimum);
        Assert.NotNull(property.Warn("0"));
    }

    /// <summary>
    /// The narrow-regex cases. A bare digit match would find the "0" in "0.85" —
    /// the shipped default of
    /// <c>player-movement-action-direction-threshold</c> — and in every bracket
    /// range that starts at zero, quietly lowering floors nobody documented.
    /// </summary>
    [Theory]
    [InlineData("Values above 0.85 are rejected by the server.")]
    [InlineData("Any value in the range of [0, 1] is accepted.")]
    [InlineData("A multiplier of 10 applies here.")]
    public void An_incidental_zero_in_the_prose_does_not_lower_the_floor(string description)
    {
        Assert.Equal(1, Property("Any positive integer.", description).Minimum);
    }

    [Theory]
    [InlineData("Set to 0 to disable.")]
    [InlineData("0 means unlimited.")]
    [InlineData("Use 0 for no limit.")]
    [InlineData("A value of 0 turns this off.")]
    [InlineData("0 disables the check entirely.")]
    public void The_phrasings_mojang_actually_uses_are_recognised(string description)
    {
        Assert.Equal(0, Property("Any positive integer.", description).Minimum);
    }

    [Fact]
    public void A_number_inside_the_range_says_nothing() =>
        Assert.Null(Property("Integers in the range [4, 12]").Warn("8"));

    [Fact]
    public void A_line_break_in_a_text_value_is_refused() =>
        Assert.NotNull(Property("Any string").Reject("one\ntwo"));
}

/// <summary>
/// The allowlist badge's one rule, in the one place that states it (`T136`).
/// </summary>
/// <remarks>
/// These live in a unit test rather than on the page for the reason `T126`
/// records: the test project deliberately does not reference the host, so a rule
/// left in a `.razor` file is verified on a real Windows box or not at all. This
/// one was, and it was wrong for months — the fixture that caught it carried only
/// the legacy `white-list=true`, and the panel contradicted itself, with the
/// Properties tab showing no `allow-list` row while the Players tab claimed one
/// was enforced.
/// </remarks>
public class AllowlistEnforcementTests
{
    private static bool Enforced(params (string Key, string Value)[] properties) =>
        ServerProperties.AllowlistEnforced(
            properties.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// The defect exactly: no key at all used to read as *enabled*. BDS's own
    /// default is off, and a file that never mentions the setting is the most
    /// common file there is.
    /// </summary>
    [Fact]
    public void An_absent_key_is_not_enforcement() => Assert.False(Enforced());

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("True")]
    public void Allow_list_true_is_enforcement(string value) =>
        Assert.True(Enforced(("allow-list", value)));

    [Theory]
    [InlineData("false")]
    [InlineData("FALSE")]
    public void Allow_list_false_is_not(string value) =>
        Assert.False(Enforced(("allow-list", value)));

    /// <summary>
    /// The old test was "not the literal string false", so anything unexpected
    /// meant enforced. Only <c>true</c> means true.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("ture")]
    public void Anything_that_is_not_true_is_not_enforcement(string value) =>
        Assert.False(Enforced(("allow-list", value)));

    /// <summary>
    /// `white-list` is the pre-1.16 name Bedrock still honours, and
    /// `ServerConfigFiles` already knew the matching `whitelist.json` filename —
    /// so knowing only half the old naming was the real gap.
    /// </summary>
    [Fact]
    public void The_legacy_key_is_read_when_the_modern_one_is_missing()
    {
        Assert.True(Enforced(("white-list", "true")));
        Assert.False(Enforced(("white-list", "false")));
    }

    /// <summary>
    /// A file carrying both is a file mid-migration, and BDS reads the modern
    /// one. The fallback must not out-vote a setting that is actually present.
    /// </summary>
    [Fact]
    public void The_modern_key_wins_when_both_are_present()
    {
        Assert.False(Enforced(("allow-list", "false"), ("white-list", "true")));
        Assert.True(Enforced(("allow-list", "true"), ("white-list", "false")));
    }
}

/// <summary>
/// The cheats badge, which reads the same way as the allowlist beside it.
/// </summary>
/// <remarks>
/// Its own tests rather than a line added to the allowlist ones, because the
/// point is that the two flags shown side by side on the Status tab answer a
/// blank or misspelt value **identically**. Two helpers that agree today and are
/// only tested once between them are the shape `T136` was.
/// </remarks>
public class CheatsAllowedTests
{
    private static bool Allowed(params (string Key, string Value)[] properties) =>
        ServerProperties.CheatsAllowed(
            properties.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void An_absent_key_is_not_permission() => Assert.False(Allowed());

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("  true  ")]
    public void Allow_cheats_true_permits(string value) =>
        Assert.True(Allowed(("allow-cheats", value)));

    /// <remarks>
    /// Everything that is not a literal true is off — including the empty value
    /// and the typo, which an "anything but false" test would have read as on.
    /// </remarks>
    [Theory]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("yes")]
    [InlineData("1")]
    public void Anything_else_is_off(string value) =>
        Assert.False(Allowed(("allow-cheats", value)));
}
