namespace BedrockSentry.Core.Addons;

/// <summary>Why a settings save was refused before anything was written.</summary>
public enum SettingsSaveRefusal
{
    None,

    /// <summary>
    /// The world's settings file could not be read. <strong>Never overwritten</strong>:
    /// a save replaces the whole file, so writing over one nobody could parse
    /// discards overrides nobody has seen — every other pack's included.
    /// </summary>
    FileNotRead,

    /// <summary>A value in the document would be rejected by the server.</summary>
    ValuesRefused,
}

/// <summary>What a settings save should do, decided before any of it is done.</summary>
/// <param name="Json">The whole file's new text, or null when refused.</param>
/// <param name="Problems">
/// Empty unless <see cref="Refusal"/> is <see cref="SettingsSaveRefusal.ValuesRefused"/>.
/// A problem may name a pack other than the one being changed, because the
/// whole document is checked.
/// </param>
public readonly record struct PackSettingsSavePlan(
    SettingsSaveRefusal Refusal, string? Json, IReadOnlyList<SettingsWriteProblem> Problems)
{
    /// <summary>Is there something to write?</summary>
    public bool CanWrite => Refusal == SettingsSaveRefusal.None && Json is not null;
}

/// <summary>
/// Decides a whole settings save: whether it may happen, what the file becomes,
/// and which <c>format_version</c> it carries.
/// </summary>
/// <remarks>
/// <para>Separate from the writing so it can be asked without a filesystem. The
/// three decisions here are each one somebody would otherwise make at a call
/// site, and two of them lose data when they are wrong.</para>
/// </remarks>
public static class PackSettingsSave
{
    /// <summary>Plan a save of one pack's values into a world's settings file.</summary>
    /// <param name="document">The file as it was read.</param>
    /// <param name="state">How that read went. Only Read and Absent may be written.</param>
    /// <param name="packId">The pack's <strong>header</strong> uuid.</param>
    /// <param name="values">Its overrides, replacing whatever it had.</param>
    /// <param name="schemaFor">
    /// A pack's declared controls, or null when this instance does not have it.
    /// </param>
    public static PackSettingsSavePlan Plan(
        PackSettingsDocument document,
        PackSettingsDocumentState state,
        string packId,
        IReadOnlyDictionary<string, PackSettingValue> values,
        Func<string, PackSettingsSchema?> schemaFor)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(schemaFor);

        if (state is not (PackSettingsDocumentState.Read or PackSettingsDocumentState.Absent))
        {
            return new PackSettingsSavePlan(SettingsSaveRefusal.FileNotRead, null, []);
        }

        var written = document.With(packId, values);
        var check = PackSettingsWrite.Check(written, schemaFor);

        if (!check.Accepted)
        {
            return new PackSettingsSavePlan(SettingsSaveRefusal.ValuesRefused, null, check.Problems);
        }

        // Its own when the file had one, and the measured value only when
        // creating it from nothing. Never a guess either way: a format_version
        // the server rejects discards every pack's overrides in the world.
        var format = document.FormatVersion ?? PackSettingsDocument.KnownFormatVersion;

        return new PackSettingsSavePlan(SettingsSaveRefusal.None, written.Write(format), []);
    }
}
