namespace BedrockSentry.Core.Backups;

/// <summary>
/// What a backup of this server <em>is</em>: which formats write one, and — when
/// one does — how it is read and how much of it is kept.
/// </summary>
/// <remarks>
/// <para>In <c>Core</c> because three surfaces ask it: the Backups tab's summary
/// card, the <em>Next run</em> card beside it, and whether the schedule controls
/// are live at all. This codebase's recurring defect is exactly that shape — a
/// rule stated in three places is enforced in one (<c>T126</c>, <c>T145</c>,
/// <c>T262</c>). They ask; they do not decide.</para>
///
/// <para><strong>The custom command is not a format, and it is not nothing
/// either.</strong> It is additive and the wrapper does not know what it writes,
/// so an instance backed up only by a command has nothing this panel can restore
/// — but backups do happen, so calling that state <em>disabled</em> would be a
/// sentence the server disproves every night.
/// <see cref="BackupFormatState.CustomCommandOnly"/> is its own arm for that
/// reason.</para>
///
/// <para><strong>A command the install gate refuses writes nothing</strong>
/// (<c>D45</c>), so <see cref="StateOf"/> takes whether it is allowed rather
/// than reading <c>Enabled</c> alone. <c>Core</c> cannot see that config, and a
/// caller that passed <c>true</c> unconditionally would report a gated install
/// as covered.</para>
/// </remarks>
public static class BackupFormatSummary
{
    /// <summary>What, if anything, writes a backup of this server.</summary>
    /// <remarks>
    /// Five states rather than the two a <c>bool</c> could carry: which formats
    /// are on decides the headline, and <em>something writes but nothing here can
    /// restore it</em> is a different answer from <em>nothing writes</em>
    /// (<c>T176</c>).
    /// </remarks>
    public enum BackupFormatState
    {
        /// <summary>Both restorable formats are on.</summary>
        ZipAndFolder,

        /// <summary>The ZIP archive alone.</summary>
        Zip,

        /// <summary>The folder copy alone.</summary>
        Folder,

        /// <summary>
        /// No format, but a custom command runs and is permitted to. Backups
        /// happen; none of them is one this panel can put back.
        /// </summary>
        CustomCommandOnly,

        /// <summary>Nothing at all would write a backup of this server.</summary>
        Disabled,
    }

    /// <summary>What writes a backup of this server, from its settings.</summary>
    /// <param name="settings">This instance's backup settings.</param>
    /// <param name="customCommandAllowed">
    /// Whether this install permits a custom command at all (<c>D45</c>). A
    /// configured command on an install that refuses it writes nothing, and the
    /// panel says so separately — here it simply does not count.
    /// </param>
    public static BackupFormatState StateOf(BackupSettings settings, bool customCommandAllowed)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return (settings.Zip.Enabled, settings.Folder.Enabled) switch
        {
            (true, true) => BackupFormatState.ZipAndFolder,
            (true, false) => BackupFormatState.Zip,
            (false, true) => BackupFormatState.Folder,

            (false, false) => settings.CustomCommand.IsSet && customCommandAllowed
                ? BackupFormatState.CustomCommandOnly
                : BackupFormatState.Disabled,
        };
    }

    /// <summary>The card's value line.</summary>
    public static string Headline(BackupFormatState state) => state switch
    {
        BackupFormatState.ZipAndFolder => "ZIP and folder",
        BackupFormatState.Zip => "ZIP archive",
        BackupFormatState.Folder => "Folder copy",
        BackupFormatState.CustomCommandOnly => "Nothing to restore from",
        BackupFormatState.Disabled => "Backups Disabled",

        _ => throw new NotSupportedException(
            $"No headline for {state}. Add an arm here rather than letting the card " +
            "render a state nobody wrote words for."),
    };

    /// <summary>What the When it runs card says while nothing would write a backup.</summary>
    /// <param name="settings">The settings the card is asked about, which are the form's.</param>
    /// <param name="customCommandAllowed">Whether this install permits a custom command (<c>D45</c>).</param>
    /// <remarks>
    /// <para><strong>Every way out the page offers, and only those</strong>
    /// (<c>T303</c>). With the install gate open a typed custom command makes the
    /// card live as well, so a notice naming only the two formats left a ticked,
    /// empty command box unexplained. With the gate shut the command is no way
    /// out, and naming it would send somebody to a box that cannot help.</para>
    ///
    /// <para>The formats are <em>above</em> the card on the settings sheet;
    /// the command box is named by its label rather than a direction, because it
    /// is not.</para>
    /// </remarks>
    public static string InertNotice(BackupSettings settings, bool customCommandAllowed)
    {
        ArgumentNullException.ThrowIfNull(settings);

        const string Nothing = "No backup type is switched on, so nothing here would write anything.";

        if (!customCommandAllowed)
        {
            return $"{Nothing} Turn on ZIP archive or Folder copy above to set a schedule.";
        }

        return settings.CustomCommand.Enabled
            ? $"{Nothing} Run a custom command is ticked with no command typed in it; type one, or "
                + "turn on ZIP archive or Folder copy above, to set a schedule."
            : $"{Nothing} Turn on ZIP archive or Folder copy above, or Run a custom command, to set "
                + "a schedule.";
    }

    /// <summary>Whether this state is one to say loudly.</summary>
    /// <remarks>
    /// A predicate rather than the card comparing <see cref="Headline"/> to a
    /// string literal, which is what it did: the same words in two files, so
    /// rewording the headline silently took the colour away and nothing failed.
    /// </remarks>
    public static bool IsFault(BackupFormatState state) =>
        state is BackupFormatState.Disabled or BackupFormatState.CustomCommandOnly;

    /// <summary>
    /// The lines under the value, one fact each.
    /// </summary>
    /// <remarks>
    /// <para><strong>A list rather than a sentence.</strong> With both formats on
    /// this had to fold two acquisition-and-retention clauses into one line —
    /// <em>"keeping the newest 1 of the archives and every one of the copies"</em>
    /// — which parses only if you already know which noun goes with which format.
    /// One label per row says the same thing without the reader assembling
    /// it.</para>
    ///
    /// <para>Always a list, including for a single format: a threshold at
    /// <em>two</em> would be a second rule about when the shape changes, and one
    /// row reads no worse than the sentence it replaces.</para>
    ///
    /// <para>The labels are the settings sheet's own — <em>ZIP archive</em>,
    /// <em>Folder copy</em> — because a card naming a control differently from
    /// the control is one thing named twice.</para>
    /// </remarks>
    public static IReadOnlyList<string> Lines(
        BackupSettings settings, BackupFormatState state)
    {
        ArgumentNullException.ThrowIfNull(settings);

        switch (state)
        {
            case BackupFormatState.Disabled:
                return
                [
                    "No Backup Type enabled. Please enable one in backup settings " +
                    "for backups to function.",
                ];

            case BackupFormatState.CustomCommandOnly:
                return ["A custom command runs, and the wrapper cannot restore what it writes."];

            case BackupFormatState.ZipAndFolder:
            case BackupFormatState.Zip:
            case BackupFormatState.Folder:
                break;

            default:
                throw new NotSupportedException(
                    $"No lines for {state}. Add an arm here rather than letting the card " +
                    "describe a state nobody wrote words for.");
        }

        var lines = new List<string>
        {
            settings.Acquisition.UseShadowCopy
                ? "Backup from: Shadow copy"
                : "Backup from: Live world folder",
        };

        if (settings.Zip.Enabled)
        {
            lines.Add($"ZIP archive: {Rule(settings.Zip.Retention)}");
        }

        if (settings.Folder.Enabled)
        {
            lines.Add($"Folder copy: {Rule(settings.Folder.Retention)}");
        }

        return lines;
    }

    /// <summary>One retention rule as the list renders it.</summary>
    /// <remarks>
    /// The <c>_ =&gt; throw</c> is the guard, not an oversight: a mode added later
    /// would otherwise render as whichever arm sorted last and read as a rule
    /// somebody had chosen. <c>BackupFormatSummaryTests</c> walks the enum.
    /// </remarks>
    private static string Rule(BackupRetention retention) => retention.Mode switch
    {
        BackupRetentionMode.KeepAll => "Keep all",
        BackupRetentionMode.Count => $"Keep newest {retention.Value}",
        BackupRetentionMode.Days => $"Keep last {retention.Value} days",

        _ => throw new NotSupportedException(
            $"No retention phrase for {retention.Mode}. Add an arm here rather than " +
            "letting the card describe a rule nobody wrote."),
    };
}
