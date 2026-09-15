using System;
using System.IO;
using System.Linq;

namespace BedrockSentry.Tests;

/// <summary>
/// Pins the cascade relationship between <c>.page-notice</c> and the loss variant that
/// <see cref="BedrockSentry.Core.Instances.InstanceRemovalOutcome.AnythingLost"/> switches on.
/// </summary>
/// <remarks>
/// WHY A TEST READS A STYLESHEET AS TEXT. `T186` moved the *decision* into Core, where the
/// suite can reach it, and left the *presentation* in a .razor and a .css that it could not —
/// and that fix's own commit message flagged the gap as "not verifiable here". `T189` then
/// landed in precisely that hole: the loss rule shipped in build.88 at equal specificity and
/// 31 lines ABOVE the rule it had to beat, so the danger frame never rendered once. The parts
/// that did work — role="alert" and the un-muted list — are what made it look applied.
///
/// WHAT THIS CAN AND CANNOT PROVE, stated rather than implied. There is no CSS engine here, so
/// this cannot compute a used value; it pins the two things that *made* the rule lose, which
/// are the only two ways a single-class override can lose to another single-class rule:
/// specificity and document order. Both are asserted, so neither alone is load-bearing. What
/// still needs a real browser is that the resulting colour is the intended one, and that is a
/// clause on `T186`.
/// </remarks>
public class RemovalNoticeStyleTests
{
    private const string BaseSelector = ".page-notice {";
    private const string LossSelector = ".page-notice.page-notice-loss {";

    private static string Stylesheet()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "app.css");
        Assert.True(
            File.Exists(path),
            $"app.css was not copied to the test output at '{path}'. It is a linked None item in " +
            "BedrockSentry.Tests.csproj; if that item was removed, these tests stop testing anything.");
        return File.ReadAllText(path);
    }

    [Fact]
    public void The_loss_frame_carries_both_classes_so_it_outranks_the_base_notice()
    {
        var css = Stylesheet();

        // `.page-notice-loss` alone is (0,1,0) — the SAME weight as `.page-notice`, which is how
        // it lost. Naming both classes makes it (0,2,0) and no ordering can take that away.
        Assert.Contains(LossSelector, css);

        // And the bare form must not come back. Matching on the opening brace keeps this from
        // firing on the descendant rule `.page-notice-loss .milestone-list`, which is correctly
        // (0,2,0) already and is the one arm of the original that did work.
        Assert.DoesNotContain("\n.page-notice-loss {", css);
    }

    [Fact]
    public void The_loss_frame_is_declared_after_the_notice_it_overrides()
    {
        var css = Stylesheet();

        var baseAt = css.IndexOf(BaseSelector, StringComparison.Ordinal);
        var lossAt = css.IndexOf(LossSelector, StringComparison.Ordinal);

        Assert.True(baseAt >= 0, $"'{BaseSelector}' is missing from app.css.");
        Assert.True(lossAt >= 0, $"'{LossSelector}' is missing from app.css.");

        Assert.True(
            lossAt > baseAt,
            "The loss frame must be declared after .page-notice. It shipped 31 lines above it in " +
            "build.88 and rendered nothing (`T189`). Specificity already protects it, so this is " +
            "the second of two independent guards — and it keeps the neighbouring comment, which " +
            "says the loss rule is 'below', honest.");
    }

    [Fact]
    public void The_base_notice_still_uses_the_border_shorthand_that_made_this_subtle()
    {
        var css = Stylesheet();

        var baseAt = css.IndexOf(BaseSelector, StringComparison.Ordinal);
        Assert.True(baseAt >= 0);
        var block = css[baseAt..css.IndexOf('}', baseAt)];

        // This is the mechanism, pinned so the next reader understands why colour alone was not
        // enough: `border-left: 3px solid var(...)` is a SHORTHAND, so it resets border-left-color
        // rather than leaving an earlier value alone. Had .page-notice set border-left-color
        // longhand, the original rule would merely have been fragile instead of dead.
        Assert.Contains("border-left:", block);

        // The loss rule overrides the colour longhand, which only works because of the two guards
        // above. Asserted here so all three facts fail together if anyone rewrites this block.
        var lossAt = css.IndexOf(LossSelector, StringComparison.Ordinal);
        var lossBlock = css[lossAt..css.IndexOf('}', lossAt)];
        Assert.Contains("border-left-color:", lossBlock);
        Assert.Contains("background:", lossBlock);
    }

    [Fact]
    public void Nothing_in_the_notice_rules_reaches_for_important()
    {
        var css = Stylesheet();

        var baseAt = css.IndexOf(BaseSelector, StringComparison.Ordinal);
        var region = css[baseAt..Math.Min(css.Length, baseAt + 1400)];

        // The dead `.checkbox-row { display: flex !important }` was deleted in build.88 precisely
        // because it encoded the worse of the two ways to win a cascade fight, and the next author
        // finds and copies whatever is there. Specificity is the answer; !important is the tell
        // that somebody stopped asking why they were losing.
        Assert.DoesNotContain("!important", region);
    }
}
