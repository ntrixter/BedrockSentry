using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Instances;

namespace BedrockSentry.Tests;

/// <summary>
/// The orphan rules that can be pinned without a process table (`D56`, `T149`).
/// </summary>
/// <remarks>
/// The sentences and the path guard. Enumerating and killing are Windows-only
/// and are `T154`'s job; what is here is everything an operator reads and the
/// one comparison that decides whether something gets terminated.
/// </remarks>
public class OrphanTests
{
    private static readonly OrphanedServer Held =
        new(8172, @"C:\instances\survival\bedrock_server.exe", null);

    [Fact]
    public void A_line_names_the_process_the_pid_and_where_it_runs_from()
    {
        var line = OrphanText.Line(Held);

        Assert.Contains("bedrock_server", line);
        Assert.Contains("8172", line);
        Assert.Contains(@"C:\instances\survival\bedrock_server.exe", line);
    }

    // The operator's first question is whether the world is being written by two
    // things. It is not — that is what the refusal bought — and the sentence has
    // to say so before it says anything else.
    [Fact]
    public void What_the_console_says_leads_with_the_world_being_safe()
    {
        var text = OrphanText.Found([Held], "Survival");

        Assert.Contains("Survival", text);
        Assert.Contains("not being written by two processes", text);
        Assert.Contains("8172", text);

        // T161: this sentence shipped with no verb — "A Bedrock server is for
        // Survival" — and it is the first thing an operator reads when D56
        // fires. Count() returns only the copula so each caller supplies its
        // own verb; this one forgot. Pinned here, and for the plural below,
        // because nothing asserted the opening clause at all.
        Assert.StartsWith("A Bedrock server is running for Survival", text, StringComparison.Ordinal);
    }

    // And it has to say why leaving it is not free: the wrapper cannot reach it,
    // so no graceful stop and no backup until it is gone.
    [Fact]
    public void What_the_console_says_explains_why_it_cannot_be_asked_to_stop()
    {
        var text = OrphanText.Found([Held], "Survival");

        Assert.Contains("console died", text);
        Assert.Contains("instance page", text);
    }

    [Fact]
    public void Several_orphans_are_counted_rather_than_described_as_one()
    {
        var text = OrphanText.Found(
            [Held, new OrphanedServer(9001, @"C:\instances\survival\bedrock_server.exe", null)],
            "Survival");

        Assert.StartsWith("2 Bedrock servers are running for Survival", text, StringComparison.Ordinal);
        Assert.Contains("9001", text);
    }

    // The refusal is what lands on the badge and the console when a launch is
    // stopped. It must say nothing was launched — the whole point of refusing is
    // that the world was never opened a second time.
    [Fact]
    public void The_refusal_says_the_world_was_not_opened_twice()
    {
        var reason = OrphanText.RefusalReason([Held]);

        Assert.StartsWith("not started: ", reason);
        Assert.Contains("8172", reason);
        Assert.Contains("was not opened twice", reason);
        Assert.DoesNotContain("gave up", reason);
    }

    // D56 requirement 3. The history line has to carry the cost, because a
    // terminated server loses whatever it had not written and nothing else will
    // tell the operator that.
    //
    // T162: this test used to assert "above this line", which was the defect —
    // the table is newest-first, so "above" named the archives taken AFTER the
    // kill and sent the operator to the wrong half of the one list D56
    // requirement 3 exists to divide. It now asserts the archive is NAMED, and
    // that no direction word is used at all: a direction can be silently
    // re-broken by a sort change in a .razor file no test can see, and a name
    // cannot.
    [Fact]
    public void The_history_line_says_what_the_kill_cost_and_names_the_last_good_backup()
    {
        var note = OrphanText.IncidentNote(Held, "backup-20260904-1200.zip");

        Assert.Contains("8172", note);
        Assert.Contains("could not be asked to save", note);
        Assert.Contains("lost", note);
        Assert.Contains("backup-20260904-1200.zip", note);

        Assert.DoesNotContain("above this line", note);
        Assert.DoesNotContain("below this line", note);
    }

    [Fact]
    public void The_history_line_says_plainly_when_there_was_no_backup_to_name()
    {
        // Worse than a wrong direction would be implying a safe archive exists.
        var note = OrphanText.IncidentNote(Held);

        Assert.Contains("No backup had been taken before this", note);
        Assert.DoesNotContain("above this line", note);
    }

    // The comparison that decides whether a process is killed. A prefix match
    // without the separator says survival-old is inside survival, and this is
    // not a place to be approximately right.
    //
    // Built from Path.Combine rather than written as Windows literals so it runs
    // on both platforms and actually asserts on each: an early return under an
    // OS check would leave this green on Linux having tested nothing.
    [Theory]
    [InlineData("survival/bedrock_server.exe", "survival", true)]
    [InlineData("survival/versions/1/bedrock_server.exe", "survival", true)]
    [InlineData("survival-old/bedrock_server.exe", "survival", false)]
    [InlineData("other/bedrock_server.exe", "survival", false)]
    public void Only_an_executable_under_this_instances_own_folder_counts(
        string relativePath, string relativeDirectory, bool expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "bs-orphan-guard");
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.Combine(root, relativeDirectory);

        Assert.Equal(expected, ProcessOrphanInspector.IsUnder(path, directory));
        Assert.Equal(
            expected,
            ProcessOrphanInspector.IsUnder(path, directory + Path.DirectorySeparatorChar));
    }

    // Case-insensitive on purpose: the paths compared are a Windows process's
    // MainModule and a directory out of the config file, and Windows does not
    // agree with itself about case between the two.
    [Fact]
    public void The_comparison_ignores_case()
    {
        var root = Path.Combine(Path.GetTempPath(), "bs-orphan-guard");

        Assert.True(ProcessOrphanInspector.IsUnder(
            Path.Combine(root, "SURVIVAL", "bedrock_server.exe"),
            Path.Combine(root, "survival")));
    }

    // A path that cannot be resolved is not a match. For an operation that ends
    // a process, unknown is not permission.
    [Fact]
    public void An_unresolvable_path_is_not_a_match() =>
        Assert.False(ProcessOrphanInspector.IsUnder("\0not-a-path", Path.GetTempPath()));
}
