using BedrockSentry.Core.Configuration;

namespace BedrockSentry.Tests;

/// <summary>
/// The one phrase per update mode, shared by the Status page and the Settings page.
/// </summary>
/// <remarks>
/// It lived in <c>Settings.razor</c> while <c>Home.razor</c> rendered the bare enum, so
/// the same setting read "when told" on one page and "Notify" on the other. Both pages
/// are out of this suite's reach, which is exactly why the rule had to come out of them.
/// </remarks>
public class UpdateModeTextTests
{
    [Theory]
    [InlineData(UpdateMode.Automatic, "automatically")]
    [InlineData(UpdateMode.Notify, "when told")]
    [InlineData(UpdateMode.Disabled, "never — not checking")]
    public void Every_mode_has_its_own_phrase(UpdateMode mode, string expected)
    {
        Assert.Equal(expected, UpdateModeText.Applying(mode));
    }

    [Fact]
    public void No_two_modes_read_the_same()
    {
        var phrases = Enum.GetValues<UpdateMode>().Select(UpdateModeText.Applying).ToList();

        Assert.Equal(phrases.Count, phrases.Distinct().Count());
    }

    /// <summary>
    /// THE ARM THAT USED TO LIE. A value no member names must not read as "disabled".
    /// </summary>
    /// <remarks>
    /// Reachable without a code change: the config binder will take <c>"Mode": 7</c>.
    /// The old catch-all on the Settings page would have called that "never — not
    /// checking" while the wrapper did whatever an unrecognised mode does, which is the
    /// one answer somebody reading that page must not be given.
    /// </remarks>
    [Fact]
    public void A_value_no_member_names_says_so_and_names_the_setting()
    {
        var text = UpdateModeText.Applying((UpdateMode)7);

        Assert.Contains("unrecognised", text);
        Assert.Contains("7", text);
        Assert.Contains("Sentry:Update:Mode", text);
        Assert.DoesNotContain("never", text);
    }
}
