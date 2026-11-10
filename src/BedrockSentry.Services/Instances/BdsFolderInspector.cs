using BedrockSentry.Core.Instances;

namespace BedrockSentry.Services.Instances;

/// <summary>What a look inside a would-be adopted folder found.</summary>
/// <param name="HasExecutable">Whether <c>bedrock_server.exe</c> is there.</param>
/// <param name="ServerName">The folder's own <c>server-name</c>, if it says one.</param>
/// <param name="Port">Its <c>server-port</c>, when the file parses to a number.</param>
/// <param name="WorldName">Its <c>level-name</c> — the world it would bring along.</param>
/// <param name="PropertiesRefusal">
/// Windows's own reason when <c>server.properties</c> is there and could not be read,
/// and null when there was nothing to report. <c>T238</c>.
/// </param>
/// <summary>
/// How far a look inside a would-be adopted folder got (<c>T253</c>).
/// </summary>
/// <remarks>
/// Four states because one <see langword="bool"/> was answering for four: a blank
/// field, a path that is not rooted, a folder that is not there, and a folder this
/// service may not see. They shared the sentence <em>"That folder does not exist,
/// or the path is not a full path"</em>, which names none of them and is false of
/// the last.
/// </remarks>
public enum FolderReadState
{
    /// <summary>Nothing has been typed. Not a refusal — it is the form's opening state.</summary>
    NotAsked,

    /// <summary>
    /// Not a full path. Deliberately its own state and deliberately quiet: this is
    /// asked on every keystroke, so a half-typed path is the expected case rather
    /// than a mistake. <c>InstanceRegistry.WhyThisFolderCannotHoldAServer</c>
    /// returns null here for that reason and leaves the sentence to the form.
    /// </summary>
    NotAFullPath,

    /// <summary>
    /// The read was attempted and refused. Which refusal it was is in
    /// <see cref="BdsFolderReport.Refusal"/> rather than here, because the form
    /// renders a sentence and does not branch on the difference.
    /// </summary>
    Refused,

    /// <summary>The folder was read, and the rest of the report means something.</summary>
    Read,
}

public sealed record BdsFolderReport(
    FolderReadState State,
    bool HasExecutable,
    string? ServerName,
    int? Port,
    string? WorldName,
    string? PropertiesRefusal = null,
    ServerPresence? Refusal = null)
{
    /// <summary>
    /// A read that was refused, carrying what the refusal established.
    /// </summary>
    /// <remarks>
    /// A factory rather than a constructor check, so the state and its reason are
    /// paired by construction and cannot be set apart — a
    /// <see cref="FolderReadState.Refused"/> report with nothing to say would render
    /// an empty refusal, which is the too-few-states defect wearing a null
    /// (<c>T176</c>).
    /// </remarks>
    public static BdsFolderReport RefusedBy(ServerPresence refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        return new BdsFolderReport(
            FolderReadState.Refused, false, null, null, null, Refusal: refusal);
    }

    /// <summary>
    /// What to say instead of inspecting it, and null when there is nothing to say
    /// (<c>T253</c>).
    /// </summary>
    /// <remarks>
    /// <para><strong>The refused states render the adoption rule's own sentence</strong>
    /// rather than one written here, so the form and
    /// <see cref="InstanceRegistry.AdoptAsync"/> cannot describe one folder two ways.
    /// That sentence names the directory and points at New instance, and before this
    /// it was unreachable from the form for an absent folder — the pre-flight
    /// answered first with a sentence of its own.</para>
    ///
    /// <para>Here rather than in the form for <c>T145</c>'s reason: the form is a
    /// <c>.razor</c> the suite cannot reference, so a rule written there is verified
    /// on a real page or not at all.</para>
    /// </remarks>
    public string? RefusalSentence(string directory, string executable) => State switch
    {
        FolderReadState.NotAsked or FolderReadState.Read => null,

        // Quiet on purpose. This is what a path looks like half-way through being
        // typed, and it is the one state the registry's own guard declines to answer
        // so that it does not shout on every keystroke.
        FolderReadState.NotAFullPath =>
            "That is not a full path yet — type the whole path, starting with a drive letter.",

        FolderReadState.Refused when Refusal is { } refusal =>
            MissingServerText.AdoptionRefusal(directory, executable, refusal),

        _ => throw new ArgumentOutOfRangeException(
            nameof(State), State, "A refused report has a refusal to render."),
    };

    /// <summary>
    /// The sentence the adoption form shows when the folder's own
    /// <c>server.properties</c> could not be read, and null when it could.
    /// </summary>
    /// <remarks>
    /// <para>Here rather than in the form so a test can reach it, which is the same
    /// move <c>T145</c> asks for — and the form is a <c>.razor</c> the suite cannot
    /// reference at all.</para>
    ///
    /// <para><strong>It names what to do next, because the form is not a dead end.</strong>
    /// Typing a name proceeds and the adoption itself reports the refusal properly
    /// (<c>T234</c> clause 3) — but nothing on screen said a name had to be typed
    /// <em>because the file could not be read</em>, so the door out was invisible from
    /// where somebody was standing. Every field reading as an em-dash is true and reads
    /// as a lie, which is <c>T183</c> clause 5's shape, and what it costs is somebody
    /// retyping a path that was right the first time.</para>
    /// </remarks>
    public string? PreflightRefusal() =>
        PropertiesRefusal is { Length: > 0 } why
            ? $"'{ServerProperties.FileName}' is there and could not be read, so nothing "
              + $"below was filled in from it: {why} Type a name and the rest is read "
              + "again when the server is added."
            : null;
}

/// <summary>
/// Reads what an existing Bedrock folder says about itself, for the adoption
/// form to pre-fill (<c>docs/design.md</c> § 4.4).
/// </summary>
/// <remarks>
/// <para>Read-only by construction — this runs while the folder still belongs to
/// whatever managed it yesterday, possibly a live server, so it opens nothing
/// for writing and treats an unreadable file as an empty answer rather than an
/// error. The pre-fill is the point: the adoption form seeds its name field from
/// the folder's own <c>server-name</c>, which is what makes the wrapper's later
/// write of that key back into the file the identity rather than a rename.</para>
///
/// <para><strong>That justification covers CARRYING ON and does not cover SAYING
/// NOTHING</strong>, which is the whole of <c>T238</c>. Not failing is right: the form
/// must still work with less pre-filled. But an empty dictionary and a file nobody could
/// read are two different answers, and rendering both as em-dashes tells somebody their
/// folder is not a Bedrock server when it is. The reason is carried out alongside the
/// (still empty) properties rather than in place of them — <c>T176</c>, a type with too
/// few states, and the second reader of the file <c>T234</c> made three-state.</para>
///
/// <para><strong>It asks <see cref="ServerProperties.ReadFile(string)"/> rather than
/// deriving a third state of its own</strong>, which is the other half of that lesson:
/// <c>T234</c>'s fix moved one file's read into one three-state type, and a second
/// reader re-catching the same exceptions beside it is how the two come to disagree.
/// <c>T202</c>'s audit is a grep for the reader, not for the symptom.</para>
/// </remarks>
public static class BdsFolderInspector
{
    public static BdsFolderReport Inspect(string directory) =>
        Inspect(directory, File.ReadAllLines, ProbeDirectory);

    /// <summary>
    /// Reads the folder itself, for the only reason that separates absent from
    /// refused (<c>T253</c>).
    /// </summary>
    /// <remarks>
    /// <see cref="Directory.Exists"/> answers false for both, which is the swallow
    /// <c>T246</c> was filed for and <c>T252</c> removed one layer down. What tells
    /// them apart is the exception a real read throws, so a real read is what this
    /// does; the result is discarded and only the throw matters.
    /// </remarks>
    private static void ProbeDirectory(string directory) =>
        _ = Directory.EnumerateFileSystemEntries(directory).FirstOrDefault();

    /// <summary>
    /// <see cref="Inspect(string)"/> with the read itself injectable.
    /// </summary>
    /// <remarks>
    /// The seam <see cref="ServerProperties.ReadFile(string, Func{string, string[]})"/>
    /// has, for its reason: this build runs as root, so a rig that makes a file
    /// unreadable and then reads it <em>succeeds</em> (<c>T176</c>).
    /// </remarks>
    internal static BdsFolderReport Inspect(
        string directory, Func<string, string[]> read, Action<string> probe)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return Nothing(FolderReadState.NotAsked);
        }

        if (!Path.IsPathRooted(directory))
        {
            return Nothing(FolderReadState.NotAFullPath);
        }

        // `T253`. This used to be one more clause of the condition above, asking
        // Directory.Exists -- which answers false for a folder it may not traverse as
        // readily as for one that is not there, so four states shared a sentence that
        // is false of the last. The read's own exception is what separates them, and
        // ServerPresence.From is the same mapping the launch guard and the launch
        // itself use rather than a third copy of it.
        try
        {
            probe(directory);
        }
        catch (Exception ex) when (ServerPresence.From(ex) is { } refusal)
        {
            return BdsFolderReport.RefusedBy(refusal);
        }

        var hasExecutable = File.Exists(
            Path.Combine(directory, new Core.Instances.InstanceDefinition().Executable));

        // A live BDS mid-rewrite, or permissions, still leaves the form working with
        // less pre-filled -- the adoption itself re-checks what matters. What changed
        // for `T238` is that the reason travels with the empty answer instead of being
        // dropped.
        //
        // `Why` is null on the MISSING state, which is the distinction that has to
        // survive: a folder with no `server.properties` is an ordinary state BDS itself
        // creates on first start, and reporting that as unreadable would be the
        // over-report `T231` exists to remove, one form along.
        var file = ServerProperties.ReadFile(ServerProperties.PathFor(directory), read);
        var properties = file.Values;

        return new BdsFolderReport(
            FolderReadState.Read,
            hasExecutable,
            ServerName: Value(properties, "server-name"),
            Port: int.TryParse(Value(properties, "server-port"), out var port) ? port : null,
            WorldName: Value(properties, "level-name"),
            PropertiesRefusal: file.Why);
    }

    /// <summary>A report that found nothing, in whichever state explains why.</summary>
    private static BdsFolderReport Nothing(FolderReadState state) =>
        new(state, false, null, null, null);

    private static string? Value(IReadOnlyDictionary<string, string> properties, string key) =>
        properties.TryGetValue(key, out var value) && value.Trim() is { Length: > 0 } trimmed
            ? trimmed
            : null;
}
