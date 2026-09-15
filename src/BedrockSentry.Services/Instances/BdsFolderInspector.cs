namespace BedrockSentry.Services.Instances;

/// <summary>What a look inside a would-be adopted folder found.</summary>
/// <param name="HasExecutable">Whether <c>bedrock_server.exe</c> is there.</param>
/// <param name="ServerName">The folder's own <c>server-name</c>, if it says one.</param>
/// <param name="Port">Its <c>server-port</c>, when the file parses to a number.</param>
/// <param name="WorldName">Its <c>level-name</c> — the world it would bring along.</param>
public sealed record BdsFolderReport(
    bool Exists,
    bool HasExecutable,
    string? ServerName,
    int? Port,
    string? WorldName);

/// <summary>
/// Reads what an existing Bedrock folder says about itself, for the adoption
/// form to pre-fill (<c>docs/design.md</c> § 4.4).
/// </summary>
/// <remarks>
/// Read-only by construction — this runs while the folder still belongs to
/// whatever managed it yesterday, possibly a live server, so it opens nothing
/// for writing and treats an unreadable file as an empty answer rather than an
/// error. The pre-fill is the point: the adoption form seeds its name field from
/// the folder's own <c>server-name</c>, which is what makes the wrapper's later
/// write of that key back into the file the identity rather than a rename.
/// </remarks>
public static class BdsFolderInspector
{
    public static BdsFolderReport Inspect(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)
            || !Path.IsPathRooted(directory)
            || !Directory.Exists(directory))
        {
            return new BdsFolderReport(false, false, null, null, null);
        }

        var hasExecutable = File.Exists(
            Path.Combine(directory, new Core.Instances.InstanceDefinition().Executable));

        IReadOnlyDictionary<string, string> properties;

        try
        {
            properties = ServerProperties.Read(ServerProperties.PathFor(directory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A live BDS mid-rewrite, or permissions. The form still works with
            // less pre-filled; the adoption itself re-checks what matters.
            properties = new Dictionary<string, string>();
        }

        return new BdsFolderReport(
            Exists: true,
            hasExecutable,
            ServerName: Value(properties, "server-name"),
            Port: int.TryParse(Value(properties, "server-port"), out var port) ? port : null,
            WorldName: Value(properties, "level-name"));
    }

    private static string? Value(IReadOnlyDictionary<string, string> properties, string key) =>
        properties.TryGetValue(key, out var value) && value.Trim() is { Length: > 0 } trimmed
            ? trimmed
            : null;
}
