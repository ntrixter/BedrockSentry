namespace BedrockSentry.Core.Addons;

/// <summary>Why a settings write is refused.</summary>
public enum SettingsWriteRefusal
{
    None,

    /// <summary>
    /// A value this cannot write is already in the file. Structural, so it is
    /// checked for every pack in the document including ones not installed here.
    /// </summary>
    ValueCannotBeWritten,

    /// <summary>The pack declares no such setting.</summary>
    UnknownSetting,

    /// <summary>The value is not the shape the control takes.</summary>
    WrongType,

    /// <summary>
    /// A multiselect override cannot be expressed at all on this Bedrock
    /// build — see <see cref="PackSettingsWrite"/>.
    /// </summary>
    MultiselectNotWritable,

    /// <summary>A label is text on a panel, not something with a value.</summary>
    NotAControl,

    /// <summary>The value is not one of the dropdown's options.</summary>
    NotAnOption,

    /// <summary>The number is outside the range the pack declares.</summary>
    OutOfRange,
}

/// <summary>One thing wrong with a settings document.</summary>
public sealed record SettingsWriteProblem(
    string PackId, string? Setting, SettingsWriteRefusal Refusal, string Detail);

/// <summary>Whether a whole settings document may be written.</summary>
/// <param name="Accepted">True when every entry for every pack is writable.</param>
public readonly record struct PackSettingsWriteCheck(
    bool Accepted, IReadOnlyList<SettingsWriteProblem> Problems);

/// <summary>
/// What may be written into a world's pack-settings file.
/// </summary>
/// <remarks>
/// <para><strong>The whole document is checked, not the change.</strong>
/// Measured on BDS 1.26.51.1 with a second pack's byte-identical entry as a live
/// control across three runs, both directions: one rejected value discards
/// <em>every</em> pack's overrides in that world. So a change that is itself
/// perfect still un-configures every add-on if anything else in the file is
/// wrong, and checking only what is being changed would miss exactly that.</para>
///
/// <para><strong>A multiselect override cannot be expressed at all</strong> on
/// that build, and both spellings fail differently. The array form produces the
/// rejection above and takes the file with it; the comma-joined string is
/// accepted by the parser, applies nothing and says nothing, which is the worse
/// of the two because it looks like it worked. Until a third spelling is found,
/// the control renders read-only at its defaults and a write is refused —
/// offering one would either break the world's other settings or do nothing.</para>
///
/// <para>Structural and semantic checks are separated on purpose. Whether a
/// value can be written at all is asked of every entry in the file, including
/// packs this instance does not have — a stale entry is measured to be ignored
/// by the server, so it must not block a write, but a stale entry holding an
/// array destroys the file and must.</para>
/// </remarks>
public static class PackSettingsWrite
{
    /// <summary>Check a whole document before any of it is written.</summary>
    /// <param name="document">The document as it would be written.</param>
    /// <param name="schemaFor">
    /// A pack's declared controls, or null when this instance does not have that
    /// pack — which is ordinary, and not by itself a reason to refuse.
    /// </param>
    public static PackSettingsWriteCheck Check(
        PackSettingsDocument document, Func<string, PackSettingsSchema?> schemaFor)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(schemaFor);

        var problems = new List<SettingsWriteProblem>();

        foreach (var entry in document.Entries)
        {
            var schema = schemaFor(entry.PackId);

            foreach (var (name, value) in entry.Values)
            {
                // Structural first, and for every pack. This is the one that
                // takes the whole file down.
                if (value is PackSettingValue.Unsupported unsupported)
                {
                    problems.Add(new SettingsWriteProblem(
                        entry.PackId, name, SettingsWriteRefusal.ValueCannotBeWritten,
                        $"'{name}' holds {unsupported.Raw}, which this server rejects — and a "
                        + "rejected value discards every add-on's settings in this world."));
                    continue;
                }

                // Semantic only where the pack is here to be asked. A stale
                // entry for a pack somebody removed is ignored by the server and
                // is not a reason to stop somebody changing a different pack.
                if (schema is not { State: PackSettingsState.Read } declared)
                {
                    continue;
                }

                if (declared.Find(name) is not { } setting)
                {
                    problems.Add(new SettingsWriteProblem(
                        entry.PackId, name, SettingsWriteRefusal.UnknownSetting,
                        $"This add-on declares no setting called '{name}'."));
                    continue;
                }

                if (Problem(entry.PackId, name, setting, value) is { } problem)
                {
                    problems.Add(problem);
                }
            }
        }

        return new PackSettingsWriteCheck(problems.Count == 0, problems);
    }

    private static SettingsWriteProblem? Problem(
        string packId, string name, PackSetting setting, PackSettingValue value) => setting.Kind switch
    {
        PackSettingKind.Label => new SettingsWriteProblem(
            packId, name, SettingsWriteRefusal.NotAControl,
            $"'{name}' is a label rather than a setting, so it holds no value."),

        PackSettingKind.Multiselect => new SettingsWriteProblem(
            packId, name, SettingsWriteRefusal.MultiselectNotWritable,
            $"'{name}' is a multiple-choice setting, and this Bedrock build accepts no way of "
            + "writing one: a list breaks every add-on's settings in this world, and a joined "
            + "string is ignored without saying so. It shows what the add-on chose by default."),

        PackSettingKind.Toggle => value is PackSettingValue.Toggle
            ? null
            : WrongType(packId, name, "on or off"),

        PackSettingKind.Dropdown => value is PackSettingValue.Text text
            ? Option(packId, name, setting, text.Value)
            : WrongType(packId, name, "one of its choices"),

        PackSettingKind.Slider => value is PackSettingValue.Number number
            ? Range(packId, name, setting, number.Value)
            : WrongType(packId, name, "a number"),

        _ => throw new ArgumentOutOfRangeException(
            nameof(setting), setting.Kind, "No write rule is written for this kind of setting."),
    };

    private static SettingsWriteProblem WrongType(string packId, string name, string wanted) =>
        new(packId, name, SettingsWriteRefusal.WrongType,
            $"'{name}' takes {wanted}. This server does not convert a value to the right kind — "
            + "it keeps what it is given, and the add-on then ignores it.");

    private static SettingsWriteProblem? Option(
        string packId, string name, PackSetting setting, string value) =>
        setting.Options.Count == 0
        || setting.Options.Any(o => string.Equals(o.Name, value, StringComparison.Ordinal))
            ? null
            : new SettingsWriteProblem(
                packId, name, SettingsWriteRefusal.NotAnOption,
                $"'{value}' is not one of the choices this add-on offers for '{name}'.");

    private static SettingsWriteProblem? Range(
        string packId, string name, PackSetting setting, double value)
    {
        // Step is deliberately NOT enforced: measured, this server does not snap
        // a value to the declared step and keeps whatever it is given, so
        // refusing an off-step value would refuse something that works.
        if (setting.Min is { } min && value < min)
        {
            return OutOfRange(packId, name, setting, value);
        }

        return setting.Max is { } max && value > max
            ? OutOfRange(packId, name, setting, value)
            : null;
    }

    private static SettingsWriteProblem OutOfRange(
        string packId, string name, PackSetting setting, double value) =>
        new(packId, name, SettingsWriteRefusal.OutOfRange,
            $"{value} is outside the {setting.Min} to {setting.Max} this add-on allows for "
            + $"'{name}'. What this server does with a value outside that range is not known, "
            + "so it is not written.");
}
