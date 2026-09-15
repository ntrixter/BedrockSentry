using BedrockSentry.Core.Instances;

namespace BedrockSentry.Tests;

/// <summary>
/// Every input here is a verbatim line from the milestone 4 stdin spike run
/// against BDS 1.26.36.1 (docs/design.md § 5.5). Invented sample data would have
/// agreed with whatever the parser happened to do; these lines are the contract.
/// </summary>
public class BdsLogParserTests
{
    [Fact]
    public void Parses_a_normal_info_line()
    {
        var (level, message, timestamp) =
            BdsLogParser.Parse("[2026-08-03 07:00:14:765 INFO] Starting Server");

        Assert.Equal(BdsLogLevel.Info, level);
        Assert.Equal("Starting Server", message);
        Assert.Equal(new DateTime(2026, 8, 3, 7, 0, 14, 765), timestamp);
    }

    [Fact]
    public void Parses_a_warning_line()
    {
        var (level, message, _) = BdsLogParser.Parse(
            "[2026-08-03 07:00:16:314 WARN] Allow list is enabled but contains no entries. ");

        Assert.Equal(BdsLogLevel.Warn, level);
        Assert.Equal("Allow list is enabled but contains no entries. ", message);
    }

    // The save manifest is the reason this parser tolerates unprefixed lines.
    // Requiring a prefix would discard the single most important line BDS emits.
    [Fact]
    public void Keeps_the_unprefixed_save_manifest_intact()
    {
        const string manifest =
            "Bedrock level/db/000005.ldb:509, Bedrock level/db/CURRENT:16, " +
            "Bedrock level/db/MANIFEST-000004:101, Bedrock level/level.dat:3042";

        var (level, message, timestamp) = BdsLogParser.Parse(manifest);

        Assert.Equal(BdsLogLevel.Unknown, level);
        Assert.Equal(manifest, message);
        Assert.Null(timestamp);
    }

    [Theory]
    // Continuation line inside the allow-list warning block.
    [InlineData("Alternatively, the allow list can be turned off by typing allowlist off")]
    // The shutdown confirmation, which carries no prefix at all.
    [InlineData("Quit correctly")]
    // Startup banner.
    [InlineData("#####################################################")]
    [InlineData("#               LOADING VANILLA WORLD               #")]
    // The names line after a `list` header is empty when nobody is online.
    [InlineData("")]
    public void Passes_unprefixed_lines_through_unchanged(string raw)
    {
        var (level, message, timestamp) = BdsLogParser.Parse(raw);

        Assert.Equal(BdsLogLevel.Unknown, level);
        Assert.Equal(raw, message);
        Assert.Null(timestamp);
    }

    [Fact]
    public void Null_is_treated_as_an_empty_line_not_an_error()
    {
        var (level, message, _) = BdsLogParser.Parse(null);

        Assert.Equal(BdsLogLevel.Unknown, level);
        Assert.Equal(string.Empty, message);
    }

    // A chat message or a pack name can open with a bracket. Splitting on it
    // would silently truncate player-authored text, so a bracket that is not a
    // recognised level leaves the line whole.
    [Theory]
    [InlineData("[Server] hello from someone")]
    [InlineData("[not a timestamp INFO2] body")]
    public void Does_not_split_on_a_bracket_that_is_not_a_log_prefix(string raw)
    {
        var (level, message, _) = BdsLogParser.Parse(raw);

        Assert.Equal(BdsLogLevel.Unknown, level);
        Assert.Equal(raw, message);
    }

    // The vanilla dedicated server writes no log file of its own unless told to,
    // and announces that on *every* line rather than once. The prefix sits ahead
    // of the real one, so without this the parser reads a whole session as
    // unstructured text — and readiness, which matches "Server started" against
    // the parsed message, never fires. The server is up and taking players while
    // the wrapper still says Starting.
    [Fact]
    public void Sees_through_the_no_log_file_prefix()
    {
        var (level, message, timestamp) = BdsLogParser.Parse(
            "NO LOG FILE! - [2026-08-04 09:59:08:694 INFO] Server started.");

        Assert.Equal(BdsLogLevel.Info, level);
        Assert.Equal("Server started.", message);
        Assert.Equal(new DateTime(2026, 8, 4, 9, 59, 8, 694), timestamp);
    }

    [Fact]
    public void The_prefix_is_dropped_from_a_line_that_has_nothing_else()
    {
        var (level, message, _) = BdsLogParser.Parse("NO LOG FILE! - setting up server logging...");

        Assert.Equal(BdsLogLevel.Unknown, level);
        Assert.Equal("setting up server logging...", message);
    }

    // Only at the start. A server name or a chat message containing the words is
    // content, not a prefix.
    [Fact]
    public void The_prefix_is_only_stripped_from_the_front()
    {
        var raw = "[2026-08-04 09:59:08:694 INFO] Player said: NO LOG FILE! - what?";

        Assert.Equal("Player said: NO LOG FILE! - what?", BdsLogParser.Parse(raw).Message);
    }

    // An unparseable timestamp still yields a usable level and message: losing
    // the whole line because one field is odd is a worse outcome than a null date.
    [Fact]
    public void Keeps_level_and_message_when_the_timestamp_is_unreadable()
    {
        var (level, message, timestamp) = BdsLogParser.Parse("[not-a-date INFO] Server started.");

        Assert.Equal(BdsLogLevel.Info, level);
        Assert.Equal("Server started.", message);
        Assert.Null(timestamp);
    }

    // Indentation inside the banner and the warning blocks is load-bearing for
    // readability, so only the single separator space after "]" is removed.
    [Fact]
    public void Removes_exactly_one_space_after_the_prefix()
    {
        var (_, message, _) = BdsLogParser.Parse("[2026-08-03 07:00:14:770 INFO]    indented");

        Assert.Equal("   indented", message);
    }

    [Fact]
    public void Handles_a_prefix_with_an_empty_message()
    {
        var (level, message, _) = BdsLogParser.Parse("[2026-08-03 07:00:16:314 INFO] ");

        Assert.Equal(BdsLogLevel.Info, level);
        Assert.Equal(string.Empty, message);
    }

    // T160-StrayBytePrefixDefeatsReplyMatching. One NUL byte in 4,811 lines of a
    // real transcript put the whole "[timestamp INFO] " prefix into Message, so
    // no matcher could match it. Three matchers share that fallthrough and the
    // third is the dangerous one: a stray byte on "Server started" leaves the
    // instance in Starting for ever, which rolls back a good build.

    [Theory]
    [InlineData("\0")]
    [InlineData("")]
    [InlineData("?")]
    [InlineData("\0\0")]
    public void A_stray_byte_before_the_prefix_does_not_defeat_the_parse(string stray)
    {
        var (level, message, timestamp) = BdsLogParser.Parse(
            stray + "[2026-09-04 14:24:10:397 INFO] Changes to the world are resumed.");

        Assert.Equal(BdsLogLevel.Info, level);
        Assert.Equal("Changes to the world are resumed.", message);
        Assert.Equal(new DateTime(2026, 9, 4, 14, 24, 10, 397), timestamp);
    }

    [Fact]
    public void A_stray_byte_before_the_readiness_line_still_yields_server_started()
    {
        // The clause that matters most: InstanceSupervisor gates Running on
        // Message.StartsWith("Server started").
        var (_, message, _) = BdsLogParser.Parse(
            "\0[2026-09-04 14:24:10:397 INFO] Server started.");

        Assert.StartsWith("Server started", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stray_byte_before_the_version_line_still_yields_version()
    {
        var (_, message, _) = BdsLogParser.Parse(
            "\0[2026-09-04 14:24:10:397 INFO] Version: 1.26.45.1");

        Assert.StartsWith("Version:", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stray_byte_salvage_still_strips_the_no_log_file_prefix_first()
    {
        var (level, message, _) = BdsLogParser.Parse(
            BdsLogParser.NoLogFilePrefix
            + "\0[2026-09-04 14:24:10:397 INFO] Server started.");

        Assert.Equal(BdsLogLevel.Info, level);
        Assert.Equal("Server started.", message);
    }

    // The other half of the fix: content must not be reinterpreted. The salvage
    // accepts only a strict prefix, and only within a few characters of the
    // start, so a line that merely contains a bracket is left whole.

    [Fact]
    public void A_bare_line_that_merely_contains_a_bracket_is_left_whole()
    {
        const string raw = "Steve: look at this [2026-09-04 14:24:10:397 INFO] gotcha";

        var (level, message, timestamp) = BdsLogParser.Parse(raw);

        Assert.Equal(BdsLogLevel.Unknown, level);
        Assert.Equal(raw, message);
        Assert.Null(timestamp);
    }

    [Fact]
    public void A_prefix_further_in_than_the_bound_is_content_not_a_prefix()
    {
        // Five characters of lead-in. Nothing observed puts that much in front of
        // a prefix, and treating it as one would start rewriting chat.
        const string raw = "abcde[2026-09-04 14:24:10:397 INFO] not a log line";

        var (level, message, _) = BdsLogParser.Parse(raw);

        Assert.Equal(BdsLogLevel.Unknown, level);
        Assert.Equal(raw, message);
    }

    [Fact]
    public void A_stray_byte_before_something_that_is_not_a_prefix_keeps_the_line_whole()
    {
        const string raw = "\0[not a timestamp] hello";

        var (level, message, _) = BdsLogParser.Parse(raw);

        Assert.Equal(BdsLogLevel.Unknown, level);
        Assert.Equal(raw, message);
    }

    [Fact]
    public void A_stray_byte_before_an_unrecognised_level_keeps_the_line_whole()
    {
        const string raw = "\0[2026-09-04 14:24:10:397 TRACE] hello";

        var (level, message, _) = BdsLogParser.Parse(raw);

        Assert.Equal(BdsLogLevel.Unknown, level);
        Assert.Equal(raw, message);
    }
}
