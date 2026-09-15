using System.Collections.Immutable;
using BedrockSentry.Core.Instances;

namespace BedrockSentry.Tests;

/// <summary>
/// <c>D43</c> and <c>D52</c>: the directories a server may not live in.
/// </summary>
/// <remarks>
/// Pure, and runs on every platform. The rules below stand for a resolved C:-drive
/// box rather than being read off this machine — <c>Environment.GetFolderPath</c>
/// answers with empty strings off Windows, and the resolution itself is asserted
/// separately in <see cref="ReservedSystemDirectoryResolutionTests"/>.
/// </remarks>
public class ReservedSystemDirectoryTests
{
    private static readonly ImmutableArray<ReservedSystemDirectory> Rules =
    [
        new(@"C:\Windows", ReservedScope.Subtree, "the Windows directory"),
        new(@"C:\Program Files", ReservedScope.Subtree, "where Windows installs applications"),
        new(@"C:\Program Files (x86)", ReservedScope.Subtree, "where Windows installs applications"),
        new(@"C:\Windows.old", ReservedScope.Subtree, "the previous Windows installation"),
        new(@"C:\PerfLogs", ReservedScope.Subtree, "one of Windows' own folders"),
        new(@"C:\Recovery", ReservedScope.Subtree, "one of Windows' own folders"),
        new(@"C:\Boot", ReservedScope.Subtree, "one of Windows' own folders"),
        new(@"C:\EFI", ReservedScope.Subtree, "one of Windows' own folders"),
        new(@"C:\Users", ReservedScope.FolderAndItsImmediateChildren,
            "the folder that holds every account's own folder"),
        new(@"C:\ProgramData", ReservedScope.FolderItself,
            "the root every application's data sits under"),
    ];

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Windows\System32\bedrock")]
    [InlineData(@"c:\windows\temp\srv")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files\BedrockSentry")]
    [InlineData(@"C:\Program Files (x86)\x")]
    [InlineData(@"C:\Windows.old")]
    [InlineData(@"C:\Windows.old\Users\Alice\srv")]
    [InlineData(@"C:\PerfLogs")]
    [InlineData(@"C:\Recovery\x")]
    [InlineData(@"C:\Boot")]
    [InlineData(@"C:\EFI")]
    public void A_path_inside_an_operating_system_directory_is_refused(string path)
    {
        Assert.NotNull(ReservedSystemDirectories.Refuse(path, Rules));
    }

    /// <summary>
    /// Boundary, not prefix. The two Program-Files-ish rows are the point.
    /// </summary>
    [Theory]
    [InlineData(@"C:\ExampleServer")]
    [InlineData(@"D:\minecraft\survival")]
    [InlineData(@"E:\ExampleServer")]
    [InlineData(@"C:\Windows Servers")]
    [InlineData(@"C:\Program Files Extra\x")]
    [InlineData(@"C:\ProgramData\MyServers")]
    [InlineData(@"C:\Users\Alice\Documents\Servers")]
    public void An_ordinary_folder_is_not_refused(string path)
    {
        Assert.Null(ReservedSystemDirectories.Refuse(path, Rules));
    }

    /// <summary>
    /// The container and each account's own root, and nothing deeper.
    /// </summary>
    /// <remarks>
    /// Refusing the whole subtree would forbid <c>C:\Users\Alice\Documents\Servers</c>,
    /// which is where a lot of people would reasonably put one — blocking a
    /// reasonable setup to prevent an unlikely one.
    /// </remarks>
    [Theory]
    [InlineData(@"C:\Users", true)]
    [InlineData(@"C:\Users\", true)]
    [InlineData(@"C:\Users\Alice", true)]
    [InlineData(@"C:\Users\Alice\", true)]
    [InlineData(@"C:\Users\Public", true)]
    [InlineData(@"C:\Users\Alice\Documents", false)]
    [InlineData(@"C:\Users\Alice\Documents\Servers", false)]
    [InlineData(@"C:\Users\Public\Servers", false)]
    public void The_users_container_and_each_account_root_are_refused_but_nothing_deeper(
        string path, bool refused)
    {
        Assert.Equal(refused, ReservedSystemDirectories.Refuse(path, Rules) is not null);
    }

    /// <summary>
    /// The ProgramData root only — and this is where the carve-out is enforced.
    /// </summary>
    /// <remarks>
    /// The wrapper's own instances live at <c>&lt;dataRoot&gt;\instances\&lt;id&gt;</c>,
    /// which is allowed <em>because</em> this rule is folder-itself. There is no
    /// carve-out branch anywhere in the code, deliberately: one would be dead today and
    /// would let <c>C:\Windows\instances\x</c> through tomorrow. This test is the
    /// enforcement.
    /// </remarks>
    [Theory]
    [InlineData(@"C:\ProgramData", true)]
    [InlineData(@"C:\ProgramData\", true)]
    [InlineData(@"C:\ProgramData\MyServers", false)]
    [InlineData(@"C:\ProgramData\BedrockSentry\instances\survival", false)]
    public void The_program_data_root_is_refused_but_not_what_is_under_it(string path, bool refused)
    {
        Assert.Equal(refused, ReservedSystemDirectories.Refuse(path, Rules) is not null);
    }

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData(@"D:\", true)]
    [InlineData(@"E:\", true)]
    [InlineData("D:", true)]
    [InlineData(@"D:\x", false)]
    [InlineData(@"C:\ExampleServer", false)]
    public void A_bare_volume_root_is_refused_and_a_folder_on_it_is_not(string path, bool refused)
    {
        Assert.Equal(refused, ReservedSystemDirectories.RefuseAVolumeRoot(path) is not null);
    }

    [Theory]
    [InlineData(@"C:\$Recycle.Bin", true)]
    [InlineData(@"E:\$Recycle.Bin\S-1-5-21-x", true)]
    [InlineData(@"D:\System Volume Information", true)]
    [InlineData(@"D:\System Volume Information\x", true)]
    [InlineData(@"D:\Recycle\x", false)]
    [InlineData(@"D:\srv\System Volume Information", false)]
    public void A_per_volume_system_folder_is_refused_on_any_volume(string path, bool refused)
    {
        Assert.Equal(refused, ReservedSystemDirectories.RefuseAVolumeFolder(path) is not null);
    }

    /// <summary>
    /// THE ARM THAT CAN LIE, and the reason it is guarded three times.
    /// </summary>
    /// <remarks>
    /// Off Windows <c>GetFolderPath</c> answers <c>""</c>, <c>Path.GetPathRoot("")</c>
    /// is <c>""</c>, and <c>Path.Combine("", "PerfLogs")</c> is <c>"PerfLogs"</c>. A
    /// subtree rule whose path is empty, matched the obvious way with
    /// <c>StartsWith</c> and no separator, matches <strong>everything</strong> — the
    /// guard would refuse every path on the machine and name a folder that does not
    /// exist. Nothing else in this file would catch that.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_rule_with_no_path_matches_nothing(string empty)
    {
        ImmutableArray<ReservedSystemDirectory> broken =
            [new(empty, ReservedScope.Subtree, "nothing at all")];

        Assert.Null(ReservedSystemDirectories.Refuse(@"C:\ExampleServer", broken));
        Assert.Null(ReservedSystemDirectories.Refuse(@"D:\minecraft\survival", broken));
        Assert.Null(ReservedSystemDirectories.Refuse(@"\\fileserver\share", broken));
    }

    [Fact]
    public void An_empty_path_is_refused_by_nothing()
    {
        Assert.Null(ReservedSystemDirectories.Refuse("", Rules));
        Assert.Null(ReservedSystemDirectories.RefuseAVolumeRoot(""));
        Assert.Null(ReservedSystemDirectories.RefuseAVolumeFolder(""));
        Assert.Null(ReservedSystemDirectories.RefuseAnUnsupportedPathForm(""));
    }

    [Theory]
    [InlineData(@"\\fileserver\bedrock")]
    [InlineData(@"\\fileserver\bedrock\worlds")]
    [InlineData(@"\\fileserver\bedrock\")]
    [InlineData(@"\\?\UNC\fileserver\share\x")]
    public void A_network_share_is_refused(string path)
    {
        var refusal = ReservedSystemDirectories.RefuseAnUnsupportedPathForm(path);

        Assert.NotNull(refusal);
        Assert.Contains("network share", refusal);
    }

    /// <summary>
    /// Checked before the share arm, and not cosmetically.
    /// </summary>
    /// <remarks>
    /// <c>\\?\</c> begins with two backslashes so it would otherwise read as a share —
    /// and it bypasses every rule in <c>Refuse</c>, because <c>Path.GetFullPath</c>
    /// leaves extended paths alone and <c>\\?\C:\Windows</c> does not equal
    /// <c>C:\Windows</c>. Without this the whole guard has a trivial hole.
    /// </remarks>
    [Theory]
    [InlineData(@"\\?\C:\Windows\bedrock")]
    [InlineData(@"\\.\PhysicalDrive0")]
    public void An_extended_length_or_device_path_is_refused(string path)
    {
        var refusal = ReservedSystemDirectories.RefuseAnUnsupportedPathForm(path);

        Assert.NotNull(refusal);
        Assert.Contains("extended-length or device path", refusal);
    }

    [Fact]
    public void The_extended_path_that_names_a_share_is_still_called_a_share()
    {
        var refusal = ReservedSystemDirectories.RefuseAnUnsupportedPathForm(@"\\?\UNC\fs\share\x");

        Assert.Contains("network share", refusal);
        Assert.DoesNotContain("extended-length", refusal);
    }

    [Theory]
    [InlineData(@"C:\srv")]
    [InlineData(@"D:\srv")]
    [InlineData("/srv")]
    public void A_local_path_is_not_a_network_share(string path)
    {
        Assert.Null(ReservedSystemDirectories.RefuseAnUnsupportedPathForm(path));
    }

    /// <summary>
    /// The decision's requirement, asserted rather than assumed: a refusal names the
    /// path, the rule it broke, and something that would work instead.
    /// </summary>
    /// <remarks>
    /// This is what makes `T183` provable on a box. "It refused" is not the claim —
    /// "it refused in a way an administrator can act on" is.
    /// </remarks>
    [Theory]
    [InlineData(@"C:\Windows\System32\bedrock")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\Users\Alice")]
    [InlineData(@"C:\ProgramData")]
    public void Every_refusal_names_a_path_a_rule_and_a_suggestion(string path)
    {
        var refusal = ReservedSystemDirectories.Refuse(path, Rules);

        Assert.NotNull(refusal);
        Assert.Contains(path, refusal);
        Assert.Contains("such as", refusal);
    }

    [Fact]
    public void The_volume_root_and_volume_folder_refusals_also_suggest_something()
    {
        Assert.Contains("such as", ReservedSystemDirectories.RefuseAVolumeRoot(@"D:\"));
        Assert.Contains("such as", ReservedSystemDirectories.RefuseAVolumeFolder(@"D:\$Recycle.Bin"));
    }

    // The create side and the delete side must say the same thing about the same rule.
    [Fact]
    public void The_volume_root_refusal_uses_the_same_phrase_the_delete_side_does()
    {
        Assert.Contains("is the root of a drive", ReservedSystemDirectories.RefuseAVolumeRoot(@"D:\"));
    }

    /// <summary>
    /// Forward slashes are separators too, because Windows accepts them.
    /// </summary>
    /// <remarks>
    /// <c>C:/Windows/System32</c> and <c>//fileserver/share</c> are both paths Windows
    /// resolves. A guard that understood only backslashes would refuse the first
    /// spelling of each and wave the second through — which is a hole, not a
    /// cosmetic gap. Over-matching is the cheap direction: <c>C:/Windows</c> is not a
    /// folder anybody has a use for.
    /// </remarks>
    [Theory]
    [InlineData("C:/Windows/System32/bedrock")]
    [InlineData("C:/Users/Alice")]
    [InlineData("C:/ProgramData")]
    public void A_forward_slash_path_is_refused_the_same_as_a_backslash_one(string path)
    {
        Assert.NotNull(ReservedSystemDirectories.Refuse(path.Replace('/', '\\'), Rules));
        Assert.NotNull(ReservedSystemDirectories.Refuse(path, Rules));
    }

    [Theory]
    [InlineData("//fileserver/bedrock/survival")]
    [InlineData(@"\\fileserver/bedrock")]
    public void A_share_spelt_with_forward_slashes_is_still_a_share(string path)
    {
        var refusal = ReservedSystemDirectories.RefuseAnUnsupportedPathForm(path);

        Assert.NotNull(refusal);
        Assert.Contains("network share", refusal);

        // The message quotes what they typed, not a normalised rewrite of it.
        Assert.Contains(path, refusal);
    }

    // A user's own folder is refused, and one level deeper is not — with '/' as well,
    // since the depth test is where a second separator kind is easiest to forget.
    [Theory]
    [InlineData("C:/Users/Alice", true)]
    [InlineData("C:/Users/Alice/Documents/Servers", false)]
    [InlineData(@"C:\Users\Alice/Documents", false)]
    public void The_immediate_children_rule_counts_depth_across_both_separators(
        string path, bool refused)
    {
        Assert.Equal(refused, ReservedSystemDirectories.Refuse(path, Rules) is not null);
    }

    /// <summary>
    /// <c>IsAtOrUnder</c> is a boundary test, and a blank folder matches nothing.
    /// </summary>
    /// <remarks>
    /// The blank case is the arm that can lie, for the reason the empty-rule test
    /// above gives: this is read with <c>AccountsFolder</c>, which is null off Windows
    /// and can be null on it, and a prefix match against "" is true for every path.
    /// The profile warning used to match as a prefix, so <c>C:\UsersData</c> warned.
    /// </remarks>
    [Theory]
    [InlineData(@"C:\Users\Alice", @"C:\Users", true)]
    [InlineData(@"C:\Users", @"C:\Users\", true)]
    [InlineData("C:/Users/Alice/Documents", @"C:\Users", true)]
    [InlineData(@"C:\UsersData\x", @"C:\Users", false)]
    [InlineData(@"C:\Users\Alice", "", false)]
    [InlineData(@"C:\Users\Alice", null, false)]
    [InlineData("", @"C:\Users", false)]
    public void IsAtOrUnder_is_a_boundary_test_and_a_blank_folder_matches_nothing(
        string? full, string? folder, bool inside)
    {
        Assert.Equal(inside, ReservedSystemDirectories.IsAtOrUnder(full, folder));
    }

    // ---- T183 clause 2: the two drive-type sentences --------------------------

    /// <summary>
    /// Both sentences a drive this service cannot reach can produce.
    /// </summary>
    /// <remarks>
    /// Added because <strong>no test anywhere pinned either</strong> — so a single
    /// manual observation on one machine was the whole coverage of that switch. The
    /// Windows-only half is answered too: with <c>Z:</c> genuinely mapped to a real
    /// share, the service still
    /// refused it with the not-present sentence, so <c>NoRootDirectory</c> is what
    /// fires for a user-mapped drive and <c>Network</c> is the backstop.
    /// </remarks>
    [Fact]
    public void A_drive_this_service_cannot_reach_says_which_of_the_two_it_is()
    {
        var absent = ReservedSystemDirectories.UnreachableDrive(
            @"Z:\servers", @"Z:\", networkDrive: false);

        Assert.Contains("which this machine does not have", absent);
        Assert.Contains(@"Z:", absent);
        Assert.Contains("LocalSystem", absent);
        Assert.Contains("attached to this machine", absent);

        // Not the share sentence: the two arms must stay textually distinct, because
        // telling them apart is the only way to know which DriveType fired.
        Assert.DoesNotContain("is on a network share", absent);
    }

    [Fact]
    public void A_drive_mapped_to_a_share_under_this_account_gets_the_share_sentence()
    {
        var network = ReservedSystemDirectories.UnreachableDrive(
            @"Z:\servers", @"Z:\", networkDrive: true);

        Assert.Equal(ReservedSystemDirectories.NetworkShare(@"Z:\servers"), network);
        Assert.Contains("network share", network);
        Assert.DoesNotContain("which this machine does not have", network);
    }

    /// <summary>
    /// The drive letter reads as <c>Z:</c> rather than <c>Z:\</c>.
    /// </summary>
    /// <remarks>
    /// A bare volume root is the one thing
    /// <c>Path.TrimEndingDirectorySeparator</c> never trims, which is the trap this
    /// class writes its own trimming to avoid — so the naive call would have left
    /// "on drive Z:\, which this machine does not have".
    /// </remarks>
    [Theory]
    [InlineData(@"Z:\")]
    [InlineData("Z:/")]
    [InlineData("Z:")]
    public void The_drive_letter_is_named_without_its_separator(string root)
    {
        var refusal = ReservedSystemDirectories.UnreachableDrive(
            @"Z:\servers", root, networkDrive: false);

        Assert.Contains("on drive Z:,", refusal);
    }
}
