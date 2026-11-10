namespace BedrockSentry.Core.Backups;

/// <summary>
/// What one backup row's action cell is showing.
/// </summary>
/// <remarks>
/// <para>An action that needs a second thought asks for it <em>in the row</em>: the
/// buttons are replaced by the choice, so the question is attached to the archive it
/// is about. A panel below the table has to name the archive again to be
/// unambiguous, and naming it again is how the panel ends up explaining what delete
/// means.</para>
///
/// <para><strong>Only one row is ever off <see cref="Actions"/>.</strong> Two rows
/// mid-question is two different archives asking the same thing in the same column,
/// which is the state a reader answers for the wrong one.</para>
/// </remarks>
public enum BackupRowStage
{
    /// <summary>At rest: Note, Keep, Restore, the download icon and the bin.</summary>
    Actions,

    /// <summary>The bin was pressed: Delete and Cancel, and nothing else.</summary>
    ConfirmDelete,

    /// <summary>Restore was pressed: Restore, Restore as new, An area… (<c>D108</c>), and Cancel.</summary>
    ChooseRestore,

    /// <summary>
    /// Remove was pressed on a run with nothing on disk: Remove from history and
    /// Cancel (<c>D98</c>).
    /// </summary>
    ConfirmRemoveFromHistory,

    /// <summary>
    /// The download icon was pressed: the two formats a backup can be saved as, and Cancel
    /// (<c>D110</c>).
    /// </summary>
    ChooseDownload,
}

/// <summary>
/// Which set of controls a row renders, as three predicates over
/// <see cref="BackupRowStage"/>.
/// </summary>
/// <remarks>
/// <para>In <c>Core</c> rather than as three <c>@if</c> blocks in the tab, for the
/// reason this codebase keeps paying for: the test project does not reference
/// <c>BedrockSentry.Host</c>, so a rule left in a <c>.razor</c> is verified on a real
/// page or not at all (<c>T126</c>, <c>T136</c>, <c>T145</c>). The same move
/// <c>OffersInstall</c> and <c>CouldNotBeRead</c> made for the Status tab.</para>
///
/// <para><strong>What is worth pinning is the partition, not any one arm.</strong>
/// A stage matching no predicate renders an action cell with nothing in it, and a
/// stage matching two renders Delete beside Restore as new — both of which compile
/// clean and neither of which any other check can see (<c>T252</c>).
/// <c>BackupRowStageTests</c> walks the enum and asserts exactly one is true.</para>
/// </remarks>
public static class BackupRowControls
{
    /// <summary>Note, Keep, Restore, and the bin.</summary>
    public static bool ShowsActions(BackupRowStage stage) =>
        stage is BackupRowStage.Actions;

    /// <summary>Delete and Cancel.</summary>
    public static bool ShowsDeleteConfirm(BackupRowStage stage) =>
        stage is BackupRowStage.ConfirmDelete;

    /// <summary>Restore, Restore as new, An area…, and Cancel.</summary>
    public static bool ShowsRestoreChoice(BackupRowStage stage) =>
        stage is BackupRowStage.ChooseRestore;

    /// <summary>Remove from history and Cancel (<c>D98</c>).</summary>
    public static bool ShowsRemoveFromHistoryConfirm(BackupRowStage stage) =>
        stage is BackupRowStage.ConfirmRemoveFromHistory;

    /// <summary>Whether the row shows the download formats (<c>D110</c>).</summary>
    public static bool ShowsDownloadChoice(BackupRowStage stage) =>
        stage is BackupRowStage.ChooseDownload;

    /// <summary>
    /// Whether the row is asking a question rather than offering its buttons, which
    /// moves its controls to the other end of the cell (<c>T282</c>).
    /// </summary>
    /// <remarks>
    /// <para>At rest the buttons sit against the right edge with the bin last. A
    /// question puts its answers against the left edge instead, for two reasons. The
    /// move is what tells somebody the row has changed, which a new set of buttons in
    /// the same place does not. And the second press of a double-click lands where
    /// the bin or Restore was, which is no longer where Delete or a restore over the
    /// world is.</para>
    ///
    /// <para>Defined as <em>not at rest</em> rather than as a list of the questions,
    /// so a stage added later moves with the others instead of leaving its answers
    /// under the pointer.</para>
    /// </remarks>
    public static bool IsAsking(BackupRowStage stage) => !ShowsActions(stage);
}
