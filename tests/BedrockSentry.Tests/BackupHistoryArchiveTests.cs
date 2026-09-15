using BedrockSentry.Core.Backups;
using BedrockSentry.Services.Backups;

namespace BedrockSentry.Tests;

/// <summary>
/// `BackupHistory.LastArchiveNameAsync` — which archive an operator could
/// actually restore.
/// </summary>
/// <remarks>
/// Written with `T162`, whose incident note names that archive rather than
/// pointing "above this line" in a newest-first table. The selection started life
/// as a private method inside `OrphanCleanup`, which has no tests at all, and was
/// moved here for that reason: the rule matters more than its first caller, and
/// `T168` will want the same answer.
/// </remarks>
public sealed class BackupHistoryArchiveTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"bs-history-{Guid.NewGuid():N}");

    private readonly BackupHistory _history;

    public BackupHistoryArchiveTests()
    {
        Directory.CreateDirectory(_directory);
        _history = new BackupHistory(new TestPaths(Path.Combine(_directory, "data")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private static readonly DateTimeOffset At = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private Task Append(BackupRecord record) => _history.AppendAsync("survival", record);

    private static BackupRecord Archive(string name, int minute) =>
        new(
            At.AddMinutes(minute),
            At.AddMinutes(minute),
            BackupReason.Manual,
            Note: null,
            Skipped: false,
            SkipReason: null,
            Succeeded: true,
            Strategies: [new BackupStrategyResult("zip", true, null, @$"C:\backups\survival\{name}")]);

    [Fact]
    public async Task Nothing_archived_yet_is_null_rather_than_a_guess()
    {
        Assert.Null(await _history.LastArchiveNameAsync("survival"));
    }

    [Fact]
    public async Task The_newest_archive_wins_and_comes_back_as_a_file_name()
    {
        await Append(Archive("backup-1200.zip", 0));
        await Append(Archive("backup-1300.zip", 60));

        // A name, not the path: the path is an implementation detail of the layout
        // on disk, and the operator recognises the archive by what the picker calls it.
        Assert.Equal("backup-1300.zip", await _history.LastArchiveNameAsync("survival"));
    }

    // The case that made this worth moving out of a private method. An
    // orphan-kill note is Skipped + not Succeeded (D56 requirement 3) and is a
    // timeline marker, not a backup — naming it as the archive that predates the
    // incident would point the operator at the incident itself.
    [Fact]
    public async Task An_orphan_incident_note_is_never_named_as_an_archive()
    {
        await Append(Archive("backup-1200.zip", 0));

        await Append(new BackupRecord(
            At.AddMinutes(90),
            At.AddMinutes(90),
            BackupReason.Manual,
            Note: "Ended an orphaned bedrock_server (PID 8172), running from C:\\x",
            Skipped: true,
            SkipReason: "Not a backup — an orphaned server was ended.",
            Succeeded: false,
            Strategies: []));

        Assert.Equal("backup-1200.zip", await _history.LastArchiveNameAsync("survival"));
    }

    [Fact]
    public async Task A_skipped_backup_is_not_an_archive()
    {
        await Append(Archive("backup-1200.zip", 0));

        await Append(new BackupRecord(
            At.AddMinutes(60),
            At.AddMinutes(60),
            BackupReason.Scheduled,
            Note: null,
            Skipped: true,
            SkipReason: "No backup strategy is enabled for this instance.",
            Succeeded: false,
            Strategies: []));

        Assert.Equal("backup-1200.zip", await _history.LastArchiveNameAsync("survival"));
    }

    [Fact]
    public async Task A_failed_backup_is_not_an_archive()
    {
        await Append(Archive("backup-1200.zip", 0));

        await Append(new BackupRecord(
            At.AddMinutes(60),
            At.AddMinutes(60),
            BackupReason.Scheduled,
            Note: null,
            Skipped: false,
            SkipReason: null,
            Succeeded: false,
            Strategies: [new BackupStrategyResult("zip", false, "the disk was full", null)]));

        Assert.Equal("backup-1200.zip", await _history.LastArchiveNameAsync("survival"));
    }

    // A record can succeed overall while one strategy wrote nothing — a custom
    // command that reports success but produces no file the wrapper knows about.
    // The archive has to come from a strategy that actually produced a path.
    [Fact]
    public async Task A_strategy_that_produced_no_path_is_not_the_archive()
    {
        await Append(new BackupRecord(
            At,
            At,
            BackupReason.Manual,
            Note: null,
            Skipped: false,
            SkipReason: null,
            Succeeded: true,
            Strategies:
            [
                new BackupStrategyResult("custom", true, "exit code 0", null),
                new BackupStrategyResult("zip", true, null, @"C:\backups\survival\backup-1200.zip"),
            ]));

        Assert.Equal("backup-1200.zip", await _history.LastArchiveNameAsync("survival"));
    }
}
