using BedrockSentry.Core.Broker;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Instances;
using BedrockSentry.Core.Updates;
using BedrockSentry.Services.Instances;

namespace BedrockSentry.Services.Broker;

/// <summary>
/// A folder the panel names outside the data root (<c>D120</c>): why the service may not
/// use it, the command that hands it over, and, where the install allows it, the broker's grant.
/// </summary>
/// <remarks>
/// <para>The service's account holds the data root and nothing else, so a folder outside
/// it is the service's only once an administrator grants it. By default only
/// <c>install.ps1 -GrantFolder</c> does that, and the panel prints the exact command.
/// <c>install.ps1 -AllowPanelFolderGrants</c> lets the panel ask the broker instead
/// (<see cref="IInstallPolicy.AllowPanelFolderGrants"/>), which grants a folder it makes
/// or an empty one (<c>D115</c> (b)).</para>
/// <para>The broker checks the switch itself: this only spares the panel a request it
/// would refuse.</para>
/// </remarks>
public sealed class OutsideFolders(
    ISentryPaths paths,
    IInstallPolicy policy,
    IInstallLayout? layout = null,
    IBrokerChannel? broker = null,
    InstalledService? service = null)
{
    /// <summary>Whether <paramref name="folder"/> is outside the data root.</summary>
    public bool IsOutside(string folder) => IsOutside(folder, paths.DataRoot);

    /// <summary>Whether <paramref name="folder"/> is outside <paramref name="dataRoot"/>, whatever the case or trailing separator.</summary>
    public static bool IsOutside(string folder, string dataRoot)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        return !(string.Equals(full, root, comparison)
                 || full.StartsWith(root + Path.DirectorySeparatorChar, comparison));
    }

    /// <summary>
    /// Whether <paramref name="folder"/> exists and carries the low label a grant puts on
    /// it, which is what a start checks (<see cref="IServerFolderAccess.IsLowLabelled"/>).
    /// </summary>
    /// <remarks>
    /// The label rather than a write test: the service can often write to a folder
    /// nobody granted, and a server still cannot start there. The folder itself, not a
    /// parent, so a note is never withheld for a folder the start would refuse. A label
    /// that cannot be read counts as none.
    /// </remarks>
    internal Func<string, bool> CarriesGrant { get; init; } = static folder =>
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(folder))
        {
            return false;
        }

        try
        {
            return new WindowsServerFolderAccess().IsLowLabelled(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    };

    /// <summary>Whether the panel may ask the broker to grant a folder.</summary>
    public bool PanelMayGrant => broker is not null && policy.AllowPanelFolderGrants;

    /// <summary>The command an administrator runs to hand <paramref name="folder"/> to the service.</summary>
    public string Command(string folder) =>
        CommandFor(folder, layout is { IsVersionedInstall: true } l ? l.InstallRoot : null, service?.Name);

    /// <summary>
    /// <c>install.ps1 -GrantFolder</c> on the folder, run from the install's <c>current</c>
    /// folder when its root is known, with <c>-ServiceName</c> for any service but the
    /// default, which the script would otherwise act on (<c>T419</c>).
    /// </summary>
    public static string CommandFor(string folder, string? installRoot, string? serviceName = null)
    {
        var script = installRoot is null ? @".\install.ps1" : $"& \"{Path.Combine(installRoot, "current", "install.ps1")}\"";
        var service = serviceName is null || serviceName == BrokerNames.DefaultMainService ? "" : $" -ServiceName {serviceName}";

        return $"{script} -GrantFolder \"{Path.GetFullPath(folder)}\"{service}";
    }

    /// <summary>Why the service could not use <paramref name="folder"/>, and what to run.</summary>
    /// <param name="refusal">The broker's refusal, when it was asked; null when it was not.</param>
    /// <summary>
    /// What the New instance form says under a typed folder outside the data root
    /// (<c>T448</c>); null for a blank, relative or inside one. Said before Create, since
    /// the server will not start there until the folder is granted.
    /// </summary>
    /// <remarks>
    /// Null too for a folder that already carries the grant (<c>T457</c>): the note told
    /// an administrator to run a command they had run.
    /// </remarks>
    public string? FormNote(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathRooted(folder.Trim()) || !IsOutside(folder.Trim()))
        {
            return null;
        }

        if (CarriesGrant(folder.Trim()))
        {
            return null;
        }

        return PanelMayGrant
            ? "Outside the data root: the panel asks the broker to grant this folder when you create the server."
            : $"Outside the data root: an administrator must grant it first. In PowerShell run as administrator: {Command(folder.Trim())}";
    }

    public string Refusal(string folder, string? refusal = null)
    {
        var why = refusal is null
            ? $"'{Path.GetFullPath(folder)}' is outside the data root, and the service may use a folder there only once an administrator hands it over."
            : $"'{Path.GetFullPath(folder)}' is outside the data root, and the broker did not hand it to the service: {refusal}";

        return $"{why} In PowerShell run as administrator, run: {Command(folder)} Then try again.";
    }

    /// <summary>
    /// What the adoption form says for a folder it could not read: this refusal when the
    /// folder is outside the data root, as adopting it would say (<c>T391</c> clause 9),
    /// and the report's own sentence otherwise.
    /// </summary>
    public string? AdoptionRefusal(BdsFolderReport report, string folder, string executable) =>
        report is { State: FolderReadState.Refused, Refusal.State: ServerPresenceState.Unreadable or ServerPresenceState.Unknown }
        && Path.IsPathRooted(folder) && IsOutside(folder)
            ? Refusal(folder)
            : report.RefusalSentence(folder, executable);

    /// <summary>Asks the broker to grant a folder; null when granted, otherwise why not.</summary>
    public async Task<string?> GrantAsync(BrokerGrantTarget target, string? instanceId, CancellationToken ct)
    {
        if (!PanelMayGrant)
        {
            return "Folders outside the data root are granted by install.ps1 -GrantFolder on this install.";
        }

        var answer = await broker!.SendAsync(
            new BrokerRequest { Operation = BrokerOperation.GrantFolder, GrantTarget = target, InstanceId = instanceId }, ct)
            .ConfigureAwait(false);

        return answer.Succeeded ? null : answer.Message;
    }
}
