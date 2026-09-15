using System.Reflection;
using BedrockSentry.Core.Backups;

namespace BedrockSentry.Tests;

/// <summary>
/// What a backup record reads as (<c>T158</c>), and whether two settings objects
/// compare as the same (<c>T150</c>).
/// </summary>
/// <remarks>
/// Both rules used to live in <c>.razor</c> files this project cannot reference —
/// the outcome ladder in two of them, which is why they were able to disagree, and
/// the settings comparison in one, where two of its seventeen clauses were simply
/// missing.
/// </remarks>
public class BackupOutcomeTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private static BackupRecord Record(
        bool succeeded = true,
        bool skipped = false,
        string? skipReason = null,
        SaveResumeOutcome? resumed = SaveResumeOutcome.Confirmed,
        params BackupStrategyResult[] strategies) =>
        new(At, At.AddMinutes(1), BackupReason.Manual, null, skipped, skipReason, succeeded,
            strategies, resumed);

    // ---- T158: the outcome a page renders ---------------------------------

    [Fact]
    public void A_confirmed_backup_reads_as_plain_OK()
    {
        var outcome = Record(resumed: SaveResumeOutcome.Confirmed).Outcome();

        Assert.Equal("OK", outcome.Label);
        Assert.Equal(BackupSeverity.Ok, outcome.Severity);
    }

    // THE defect. The archive is fine, so Succeeded stays true - but the server
    // may still be holding saves, which means it is writing nothing to disk, and
    // the only trace used to be a LogCritical nobody read.
    [Fact]
    public void An_unconfirmed_resume_is_a_warning_that_says_what_to_do()
    {
        var outcome = Record(resumed: SaveResumeOutcome.Unconfirmed).Outcome();

        Assert.Equal(BackupSeverity.Warning, outcome.Severity);
        Assert.Contains("saves unconfirmed", outcome.Label);
        Assert.Contains("writing nothing to disk", outcome.Detail);

        // It must say what is unknown rather than what to type. This assertion used
        // to be Contains("save resume") - it pinned the defect (`T173`), because BDS
        // answers a hand-typed resume with "A previous save has not been completed."
        // whenever no hold is outstanding, which is whenever it already worked.
        Assert.DoesNotContain("by hand", outcome.Detail!);
        Assert.Contains("Changes to the world are resumed", outcome.Detail);
    }

    /// <summary>
    /// The two resume warnings have to give <em>different</em> advice, and that is
    /// the point of testing them together (`T173`).
    /// </summary>
    /// <remarks>
    /// Unconfirmed means the wrapper does not know whether saves are held; NotSent
    /// means it does, because the command never reached the server. So "send it by
    /// hand" is wrong on the first and necessary on the second. Sharing one sentence
    /// between them is the tidying this forbids — the same shape as `T151` clause 6,
    /// where two callers were made to agree on an answer only one of them wanted.
    /// </remarks>
    [Fact]
    public void Only_the_branch_that_knows_saves_are_held_says_to_send_it_by_hand()
    {
        var unconfirmed = Record(resumed: SaveResumeOutcome.Unconfirmed).Outcome();
        var notSent = Record(resumed: SaveResumeOutcome.NotSent).Outcome();

        Assert.DoesNotContain("by hand", unconfirmed.Detail!);
        Assert.Contains("by hand", notSent.Detail!);
        Assert.NotEqual(unconfirmed.Detail, notSent.Detail);
    }

    [Fact]
    public void A_resume_that_could_not_be_sent_is_also_a_warning()
    {
        var outcome = Record(resumed: SaveResumeOutcome.NotSent).Outcome();

        Assert.Equal(BackupSeverity.Warning, outcome.Severity);
        Assert.Contains("could not be sent", outcome.Detail);
    }

    // A process that has exited is holding nothing. Flagging it would make every
    // backup-on-shutdown look like a problem.
    [Fact]
    public void A_server_that_had_already_exited_is_not_a_warning()
    {
        var outcome = Record(resumed: SaveResumeOutcome.ServerGone).Outcome();

        Assert.Equal(BackupSeverity.Ok, outcome.Severity);
        Assert.Equal("OK", outcome.Label);
    }

    // The case that makes the field nullable. Records written before it existed
    // deserialise with no value, and a bool would have read false and turned every
    // historic backup into one that left a server holding saves.
    [Fact]
    public void A_record_from_before_the_field_existed_reads_as_plain_OK()
    {
        var outcome = Record(resumed: null).Outcome();

        Assert.Equal(BackupSeverity.Ok, outcome.Severity);
        Assert.Equal("OK", outcome.Label);
    }

    [Fact]
    public void The_resume_outcome_defaults_to_null_so_the_other_eleven_call_sites_need_no_change()
    {
        var record = new BackupRecord(
            At, At, BackupReason.Safety, null, false, null, true, []);

        Assert.Null(record.SavesResumed);
    }

    [Fact]
    public void A_skip_reads_as_skipped_and_carries_its_reason()
    {
        var outcome = Record(
            succeeded: false, skipped: true, skipReason: "Nobody has played since the last backup.",
            resumed: null).Outcome();

        Assert.Equal(BackupSeverity.Skipped, outcome.Severity);
        Assert.Equal("Skipped", outcome.Label);
        Assert.Contains("Nobody has played", outcome.Detail);
    }

    [Fact]
    public void A_failure_names_only_the_strategies_that_failed()
    {
        var outcome = Record(
            succeeded: false,
            resumed: SaveResumeOutcome.Confirmed,
            strategies:
            [
                new BackupStrategyResult("zip", true, "wrote 12 files", @"C:\b\a.zip"),
                new BackupStrategyResult("vss", false, "the shadow copy could not be made", null),
            ]).Outcome();

        Assert.Equal(BackupSeverity.Failed, outcome.Severity);
        Assert.Contains("vss", outcome.Detail);
        Assert.DoesNotContain("zip", outcome.Detail);
    }

    // A failure outranks an unconfirmed resume: there is no archive to reassure
    // anybody about, so the louder of the two words is the right one.
    [Fact]
    public void A_failed_backup_whose_resume_was_also_unconfirmed_still_reads_as_failed()
    {
        var outcome = Record(succeeded: false, resumed: SaveResumeOutcome.Unconfirmed).Outcome();

        Assert.Equal(BackupSeverity.Failed, outcome.Severity);
    }

    // ---- D45: a strategy the install refused to run ------------------------

    private static readonly BlockedStrategy Refused =
        new("custom-command", "Switched off for this install.", "Re-run install.ps1 with the switch.");

    /// <summary>
    /// THE BACK-COMPAT ASSERTION. Null and empty both mean "nothing was blocked",
    /// and every record already in <c>history.json</c> binds the field to null.
    /// </summary>
    /// <remarks>
    /// Across all three severities, because the field is composed into every arm of
    /// the ladder and a regression could land on any one of them. Same trap as
    /// <c>SavesResumed</c>: a non-nullable field would bind to its default on every
    /// historic record and change what they read as.
    /// </remarks>
    [Fact]
    public void A_record_from_before_the_field_existed_reads_exactly_as_it_used_to()
    {
        foreach (var (succeeded, resumed) in new[]
        {
            (true, SaveResumeOutcome.Confirmed),
            (true, SaveResumeOutcome.Unconfirmed),
            (false, SaveResumeOutcome.Confirmed),
        })
        {
            var absent = Record(succeeded: succeeded, resumed: resumed).Outcome();
            var empty = (Record(succeeded: succeeded, resumed: resumed) with
            {
                BlockedStrategies = [],
            }).Outcome();

            Assert.Equal(absent.Label, empty.Label);
            Assert.Equal(absent.Severity, empty.Severity);
            Assert.Equal(absent.Detail, empty.Detail);
        }
    }

    [Fact]
    public void The_blocked_list_defaults_to_null_so_the_other_call_sites_need_no_change()
    {
        var record = new BackupRecord(
            At, At, BackupReason.Safety, null, false, null, true, []);

        Assert.Null(record.BlockedStrategies);
        Assert.Empty(record.Blocked);
    }

    // A good archive stays a good archive. Reading it as failed would hide it from
    // LastSuccessfulUtcAsync, blank "last backup" and make the skip rule back up on
    // every tick -- the same reasoning that keeps an unconfirmed resume green.
    [Fact]
    public void A_blocked_strategy_is_a_warning_rather_than_a_failure_when_something_worked()
    {
        var outcome = (Record(resumed: SaveResumeOutcome.Confirmed) with
        {
            BlockedStrategies = [Refused],
        }).Outcome();

        Assert.Equal(BackupSeverity.Warning, outcome.Severity);
        Assert.Contains("custom-command", outcome.Label);
        Assert.Contains("Switched off for this install.", outcome.Detail);
    }

    /// <summary>
    /// THE COMPOSITION. A resume warning outranks the block for the badge and must
    /// not swallow its sentence.
    /// </summary>
    /// <remarks>
    /// Both are <c>Warning</c>, so the only question is which wins the label. A
    /// possibly-held world is actionable now; a blocked command is also stated on the
    /// panel, in the startup log and on every later row. Unify these into one
    /// sentence and the branch that cannot afford it loses its own — the shape
    /// `T173` was reintroduced by.
    /// </remarks>
    [Fact]
    public void An_unconfirmed_resume_keeps_the_badge_and_the_block_keeps_its_sentence()
    {
        var outcome = (Record(resumed: SaveResumeOutcome.Unconfirmed) with
        {
            BlockedStrategies = [Refused],
        }).Outcome();

        Assert.Equal("OK, saves unconfirmed", outcome.Label);
        Assert.Contains("writing nothing to disk", outcome.Detail);
        Assert.Contains("Switched off for this install.", outcome.Detail);
    }

    /// <summary>
    /// And a failure with nothing else to report still says something.
    /// </summary>
    /// <remarks>
    /// <c>Describe</c> over an empty sequence returns null, and <c>Strategies</c> is
    /// empty when the only strategy was blocked — so without composing the block into
    /// this arm the row renders "Failed" with no explanation at all, for the case
    /// that most needs one.
    /// </remarks>
    [Fact]
    public void A_failure_whose_only_strategy_was_blocked_is_never_left_without_a_reason()
    {
        var outcome = (Record(succeeded: false, resumed: null) with
        {
            BlockedStrategies = [Refused],
        }).Outcome();

        Assert.Equal(BackupSeverity.Failed, outcome.Severity);
        Assert.NotNull(outcome.Detail);
        Assert.Contains("Switched off for this install.", outcome.Detail);
    }

    // ---- T150: the settings comparison ------------------------------------

    [Fact]
    public void Two_default_settings_objects_are_the_same()
    {
        Assert.True(new BackupSettings().SameAs(new BackupSettings()));
    }

    [Fact]
    public void The_two_fields_that_were_missing_now_register_as_a_change()
    {
        // The whole of T150: these two were saved correctly and compared never, so
        // ticking either on its own left Save disabled and the tick was discarded.
        var a = new BackupSettings();
        a.OnShutdown.OnServerUpdate = true;
        Assert.False(new BackupSettings().SameAs(a));

        var b = new BackupSettings();
        b.OnShutdown.OnWrapperUpdate = true;
        Assert.False(new BackupSettings().SameAs(b));
    }

    /// <summary>
    /// THE GUARD, and the reason the comparison was worth moving at all.
    /// </summary>
    /// <remarks>
    /// Walks every settable leaf of <see cref="BackupSettings"/> and its children,
    /// changes one at a time, and requires <c>SameAs</c> to notice. A field added
    /// later is covered whether anybody remembers <c>SameAs</c> exists or not —
    /// which is the only way a hand-written comparison stops being the place the
    /// next omission hides. A hand-written test listing seventeen fields would have
    /// exactly the defect it is testing for.
    /// </remarks>
    [Fact]
    public void Every_settable_leaf_of_the_settings_tree_makes_them_differ()
    {
        var leaves = Leaves(typeof(BackupSettings), []).ToList();

        // If this number falls, a child object stopped being walked and the guard
        // has quietly stopped guarding.
        Assert.True(leaves.Count >= 17, $"only found {leaves.Count} settable leaves");

        foreach (var path in leaves)
        {
            var mutated = new BackupSettings();
            Mutate(mutated, path);

            Assert.False(
                new BackupSettings().SameAs(mutated),
                $"BackupSettings.SameAs ignores {string.Join('.', path.Select(p => p.Name))}");
        }
    }

    /// <summary>Every settable scalar property, as a path from the root.</summary>
    private static IEnumerable<PropertyInfo[]> Leaves(Type type, PropertyInfo[] prefix)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // Computed members (Interval, Enabled, MinimumGap) have no setter, are
            // derived from leaves that do, and cannot be assigned.
            if (property.GetSetMethod() is null)
            {
                continue;
            }

            var path = prefix.Append(property).ToArray();

            if (property.PropertyType.Namespace?.StartsWith("BedrockSentry", StringComparison.Ordinal) == true
                && !property.PropertyType.IsEnum)
            {
                foreach (var nested in Leaves(property.PropertyType, path))
                {
                    yield return nested;
                }
            }
            else
            {
                yield return path;
            }
        }
    }

    /// <summary>Sets the leaf at <paramref name="path"/> to something else.</summary>
    private static void Mutate(object root, PropertyInfo[] path)
    {
        var target = root;

        for (var i = 0; i < path.Length - 1; i++)
        {
            target = path[i].GetValue(target)!;
        }

        var leaf = path[^1];
        leaf.SetValue(target, Different(leaf.GetValue(target), leaf.PropertyType));
    }

    private static object Different(object? current, Type type)
    {
        if (type == typeof(bool))
        {
            return !(bool)current!;
        }

        if (type == typeof(int))
        {
            return (int)current! + 7;
        }

        if (type == typeof(string))
        {
            return (current as string ?? string.Empty) + "-changed";
        }

        if (type == typeof(TimeOnly))
        {
            return ((TimeOnly)current!).AddMinutes(13);
        }

        if (type.IsEnum)
        {
            var values = Enum.GetValues(type).Cast<object>().ToList();
            return values.First(v => !Equals(v, current));
        }

        throw new NotSupportedException(
            $"A settings leaf of type {type.Name} was added and this test does not know how to "
            + "change it. Teach Different() about it rather than narrowing the walk.");
    }
}
