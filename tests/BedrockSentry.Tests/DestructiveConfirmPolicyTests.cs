using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Security;
using BedrockSentry.Services.Configuration;
using BedrockSentry.Services.Instances;

namespace BedrockSentry.Tests;

/// <summary>
/// `T172`. The switch that drops the password from Remove, Retire and Restore.
/// </summary>
/// <remarks>
/// Small, and tested carefully anyway: it is the only thing in this codebase
/// whose job is to weaken a check, so the defaults and the announcement are the
/// whole of it. The three dialogs read one policy rather than each testing the
/// option themselves — stated in three `.razor` files it would be stated three
/// times and enforced once, and the failure would be a dialog that silently
/// stopped asking.
/// </remarks>
public sealed class DestructiveConfirmPolicyTests
{
    private static readonly FakeAdminCredentials Correct = new("hunter2");

    private static DestructiveConfirmPolicy Policy(bool skip) =>
        new(
            new ChangeableOptionsMonitor<TestingOptions>(
                new TestingOptions { SkipDestructiveConfirmPassword = skip }),
            Correct);

    /// <summary>
    /// The one that matters. A fresh install, an upgrade, and a config file that
    /// has never heard of the section all land here.
    /// </summary>
    [Fact]
    public void The_password_is_required_unless_somebody_turned_it_off()
    {
        Assert.True(new DestructiveConfirmPolicy(
            new ChangeableOptionsMonitor<TestingOptions>(new TestingOptions()),
            Correct).PasswordRequired);

        Assert.True(Policy(skip: false).PasswordRequired);
    }

    [Fact]
    public void Turning_it_off_drops_the_password()
    {
        Assert.False(Policy(skip: true).PasswordRequired);
    }

    /// <remarks>
    /// Read live rather than captured, so turning it back on takes effect without
    /// a service restart. That is the direction that matters: leaving it off is
    /// the dangerous state, and needing a restart to undo it would mean an
    /// install stayed weakened for as long as somebody was afraid to restart it.
    /// </remarks>
    [Fact]
    public void Turning_it_back_on_takes_effect_without_a_restart()
    {
        var options = new ChangeableOptionsMonitor<TestingOptions>(
            new TestingOptions { SkipDestructiveConfirmPassword = true });

        var policy = new DestructiveConfirmPolicy(options, Correct);
        Assert.False(policy.PasswordRequired);

        options.Set(new TestingOptions());

        Assert.True(policy.PasswordRequired);
    }

    /// <remarks>
    /// The waiver is shown in the dialog itself, not only logged at startup — a
    /// weakened check visible only in a log is one nobody notices is still on. It
    /// has to name the setting, or the person reading it cannot turn it off.
    /// </remarks>
    [Fact]
    public void The_waiver_names_the_setting_that_causes_it()
    {
        Assert.Contains(TestingOptions.SectionName, DestructiveConfirmPolicy.Waiver);
        Assert.Contains(
            nameof(TestingOptions.SkipDestructiveConfirmPassword), DestructiveConfirmPolicy.Waiver);
    }

    /// <summary>
    /// It must not travel in a settings export.
    /// </summary>
    /// <remarks>
    /// Free, because `ConfigArchive` carries an *allow*-list of sections rather
    /// than a deny-list, so a new one is excluded until somebody decides
    /// otherwise — which is the whole reason it is shaped that way. Asserted
    /// anyway: "we get it for free" is exactly the sentence that stops being true
    /// when the list is next edited, and a config people email around must not be
    /// able to switch this on at the far end.
    /// </remarks>
    [Fact]
    public void The_testing_section_is_not_something_an_export_can_carry()
    {
        Assert.DoesNotContain(
            TestingOptions.SectionName,
            ConfigArchive.Sections,
            StringComparer.OrdinalIgnoreCase);
    }
    // ---- the two rules the dialogs used to each own a copy of --------------

    /// <summary>
    /// THE AGREEMENT NOTHING COULD CHECK BEFORE. The button and the handler must
    /// accept exactly the same inputs.
    /// </summary>
    /// <remarks>
    /// A button freed while the handler still rejects is a dialog that looks ready
    /// and silently does nothing — the symmetric failure `T172` names, and the one a
    /// UI-only observation on the box cannot exclude, because what you see in both
    /// cases is a confirm button you can press. The reverse, a handler that accepts
    /// what the button will not let you submit, is merely a control nobody can use.
    /// Neither was checkable while each dialog carried its own copy in a `.razor`
    /// file.
    /// </remarks>
    [Theory]
    [InlineData(true, null)]
    [InlineData(true, "")]
    [InlineData(true, "hunter2")]
    [InlineData(false, null)]
    [InlineData(false, "")]
    [InlineData(false, "hunter2")]
    public void The_button_and_the_handler_agree_about_what_counts_as_typed(
        bool skip, string? typed)
    {
        var policy = Policy(skip);

        // Correct password, signed in: so the only thing either can disagree about is
        // whether something was typed at all.
        Assert.Equal(policy.MayConfirm(typed), policy.Accepts("admin", typed));
    }

    [Fact]
    public void Nothing_typed_leaves_the_button_disabled_and_the_action_refused()
    {
        var policy = Policy(skip: false);

        Assert.False(policy.MayConfirm(null));
        Assert.False(policy.MayConfirm(string.Empty));
        Assert.False(policy.Accepts("admin", string.Empty));
    }

    [Fact]
    public void A_wrong_password_is_refused_even_though_the_button_was_enabled()
    {
        var policy = Policy(skip: false);

        // The button is a hint; this is the rule. Something was typed, so the button
        // was live, and the action is still refused.
        Assert.True(policy.MayConfirm("wrong"));
        Assert.False(policy.Accepts("admin", "wrong"));
    }

    /// <remarks>
    /// The check proves the person at the keyboard is the account already signed in,
    /// so no name means no proof — a correct password is not enough. This arm is
    /// reachable in a real session: the auth cookie can expire between the dialog
    /// opening and the button being pressed.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_correct_password_with_nobody_signed_in_is_refused(string? signedIn)
    {
        Assert.False(Policy(skip: false).Accepts(signedIn, "hunter2"));
    }

    /// <remarks>
    /// And with the check switched off it goes through regardless — including with
    /// nobody signed in, which is the point of the warning the dialog carries. This
    /// is the `T172` on-state, asserted here rather than inferred from a greyed-out
    /// field on a page.
    /// </remarks>
    [Fact]
    public void With_the_check_off_the_action_proceeds_on_confirmation_alone()
    {
        var policy = Policy(skip: true);

        Assert.True(policy.MayConfirm(null));
        Assert.True(policy.Accepts(null, null));
        Assert.True(policy.Accepts("admin", "wrong"));
    }

    /// <summary>A rehash-needed verdict is a success, not a refusal.</summary>
    /// <remarks>
    /// It means the password was right and the stored hash is older than current
    /// policy. Reading it as a failure would lock somebody out of Remove, Retire and
    /// Restore on exactly the installs that have been running longest.
    /// </remarks>
    [Fact]
    public void A_password_that_is_right_but_needs_rehashing_is_accepted()
    {
        var policy = new DestructiveConfirmPolicy(
            new ChangeableOptionsMonitor<TestingOptions>(new TestingOptions()),
            new FakeAdminCredentials("hunter2", CredentialVerification.SuccessRehashNeeded));

        Assert.True(policy.Accepts("admin", "hunter2"));
    }
}

/// <summary>Accepts one password for any user, and nothing else.</summary>
internal sealed class FakeAdminCredentials(
    string password, CredentialVerification onMatch = CredentialVerification.Success)
    : IAdminCredentialService
{
    public bool IsConfigured => true;

    public AdminCredentialOptions Current { get; } = new();

    public string HashPassword(string plain) => plain;

    public CredentialVerification Verify(string username, string submitted) =>
        submitted == password ? onMatch : CredentialVerification.Failed;

    public Task SetCredentialAsync(string username, string plain, CancellationToken ct = default) =>
        Task.CompletedTask;

}
