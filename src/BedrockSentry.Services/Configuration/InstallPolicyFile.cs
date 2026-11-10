using System.Text.Json;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Updates;

namespace BedrockSentry.Services.Configuration;

/// <summary><see cref="IInstallPolicy"/> read from <c>policy.json</c> in the install folder.</summary>
/// <remarks>
/// Read on every question rather than cached, so an administrator who turns a switch
/// off is obeyed at once, with no restart and no file watcher to trust. The questions
/// are rare (a backup starting, a page drawing) and the file is a few bytes.
/// </remarks>
public sealed class InstallPolicyFile : IInstallPolicy
{
    /// <summary>The file's name, beside <c>versions</c> and <c>current</c> so an update leaves it alone.</summary>
    public const string FileName = "policy.json";

    /// <summary>The key <c>install.ps1 -AllowCustomBackupCommand</c> writes.</summary>
    public const string AllowCustomBackupCommandKey = "allowCustomBackupCommand";

    private readonly string? _path;

    /// <remarks>
    /// Only a versioned install has an install root the installer secured. A copy run
    /// from anywhere else (a build folder, a portable copy in a user's folder) would
    /// read <c>policy.json</c> from a parent any user may write, so it reads nothing
    /// and every switch is off.
    /// </remarks>
    public InstallPolicyFile(IInstallLayout layout) : this(layout.IsVersionedInstall ? PathIn(layout.InstallRoot) : null)
    {
    }

    /// <param name="path">The file to read, or null for none, so every switch is off.</param>
    public InstallPolicyFile(string? path) => _path = path;

    /// <summary>Where the file lives for an install rooted at <paramref name="installRoot"/>.</summary>
    public static string PathIn(string installRoot) => Path.Combine(installRoot, FileName);

    /// <summary>The key an administrator writes to point the broker at another update feed.</summary>
    public const string UpdateFeedUrlTemplateKey = "updateFeedUrlTemplate";

    public bool AllowCustomBackupCommand => ReadTrue(AllowCustomBackupCommandKey);

    /// <summary>The key <c>install.ps1 -AllowRemoteSetup</c> writes.</summary>
    public const string AllowRemoteSetupKey = "allowRemoteSetup";

    /// <inheritdoc/>
    public bool AllowRemoteSetup => ReadTrue(AllowRemoteSetupKey);

    /// <summary>The key <c>install.ps1 -AllowPanelFolderGrants</c> writes and <c>-DenyPanelFolderGrants</c> clears.</summary>
    public const string AllowPanelFolderGrantsKey = "allowPanelFolderGrants";

    /// <inheritdoc/>
    public bool AllowPanelFolderGrants => ReadTrue(AllowPanelFolderGrantsKey);

    /// <inheritdoc/>
    public string? UpdateFeedUrlTemplate =>
        Read(UpdateFeedUrlTemplateKey) is { ValueKind: JsonValueKind.String } value
        && value.GetString() is { } template
        && template.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        && Uri.IsWellFormedUriString(template.Replace("{channel}", "stable", StringComparison.OrdinalIgnoreCase), UriKind.Absolute)
            ? template
            : null;

    /// <summary>True only for a literal JSON <c>true</c>; anything else, including no file, is false.</summary>
    private bool ReadTrue(string key) => Read(key) is { ValueKind: JsonValueKind.True };

    /// <summary>The key's value, or null when there is no file, no such key or no object to hold one.</summary>
    private JsonElement? Read(string key)
    {
        if (_path is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(_path));

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(key, out var value)
                ? value.Clone()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
