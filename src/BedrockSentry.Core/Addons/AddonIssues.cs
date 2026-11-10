using System.Security.Cryptography;
using System.Text;

namespace BedrockSentry.Core.Addons;

/// <summary>How an issue on the Add-ons tab is labelled.</summary>
/// <remarks>
/// The three finding severities and one more: <see cref="Info"/> is for the
/// server-wide note that script output does not reach the console, which is not
/// a finding about any add-on and may describe a pack that is working.
/// </remarks>
public enum AddonIssueTone
{
    /// <summary>The add-on is doing nothing.</summary>
    Problem,

    /// <summary>It may be doing less than intended.</summary>
    Warning,

    /// <summary>It could not be checked either way.</summary>
    NotChecked,

    /// <summary>Worth knowing, and nothing is wrong with an add-on.</summary>
    Info,
}

/// <summary>
/// Which issues an administrator has hidden, and how an issue is recognised
/// again on the next read (<c>D96</c>).
/// </summary>
/// <remarks>
/// <para><strong>An issue is recognised by what it says, not by where it is in
/// the list.</strong> Its key is the finding's kind, the pack it is about and a
/// hash of its detail, so a hidden issue whose detail changes (the world asks for
/// a different version, another pack starts shadowing it) is a new issue and
/// shows again. Hiding says "I have seen this one", not "never tell me about
/// this pack".</para>
///
/// <para><strong>Kept per instance, and trimmed when written.</strong> Every
/// hide or unhide writes the set back holding only keys of issues the tab can
/// currently see, so a key for something since fixed does not linger in the
/// configuration file.</para>
/// </remarks>
public static class AddonIssues
{
    /// <summary>The key of the note that script output does not reach the console.</summary>
    /// <param name="state">Which way it does not, so a different reason is a different note.</param>
    public static string ScriptOutputKey(string state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);

        return $"ScriptOutput:{state}";
    }

    /// <summary>A finding's key: its kind, its pack, and a hash of its detail.</summary>
    /// <remarks>
    /// The detail is hashed rather than stored, because it names other packs and
    /// versions and the key is kept in the configuration file, where a sentence
    /// would read as a setting somebody could edit.
    /// </remarks>
    public static string KeyOf(AddonFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        var detail = SHA256.HashData(Encoding.UTF8.GetBytes(finding.Detail ?? string.Empty));

        return $"{finding.Kind}:{finding.Subject.ToLowerInvariant()}:{Convert.ToHexStringLower(detail)[..12]}";
    }

    /// <summary>How a finding of this severity is labelled.</summary>
    public static AddonIssueTone ToneOf(AddonFindingSeverity severity) => severity switch
    {
        AddonFindingSeverity.Blocking => AddonIssueTone.Problem,
        AddonFindingSeverity.Warning => AddonIssueTone.Warning,
        AddonFindingSeverity.Unknown => AddonIssueTone.NotChecked,

        _ => throw new ArgumentOutOfRangeException(
            nameof(severity), severity, "No label is chosen for this severity."),
    };

    /// <summary>The word on an issue's label.</summary>
    public static string Label(AddonIssueTone tone) => tone switch
    {
        AddonIssueTone.Problem => "Problem",
        AddonIssueTone.Warning => "Warning",
        AddonIssueTone.NotChecked => "Not checked",
        AddonIssueTone.Info => "Info",

        _ => throw new ArgumentOutOfRangeException(
            nameof(tone), tone, "No word is written for this label."),
    };

    /// <summary>The hidden set to write after hiding one more issue.</summary>
    /// <param name="hidden">What is hidden now.</param>
    /// <param name="current">The keys of every issue the tab can see now.</param>
    /// <param name="key">The issue being hidden.</param>
    public static IReadOnlyList<string> Hide(
        IEnumerable<string> hidden, IEnumerable<string> current, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return [.. Trim(hidden, current).Append(key).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>The hidden set to write after showing an issue again.</summary>
    public static IReadOnlyList<string> Unhide(
        IEnumerable<string> hidden, IEnumerable<string> current, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return [.. Trim(hidden, current).Where(k => !string.Equals(k, key, StringComparison.Ordinal))];
    }

    /// <summary>The hidden keys that still name an issue the tab can see.</summary>
    private static IEnumerable<string> Trim(IEnumerable<string> hidden, IEnumerable<string> current)
    {
        ArgumentNullException.ThrowIfNull(hidden);
        ArgumentNullException.ThrowIfNull(current);

        var seen = current.ToHashSet(StringComparer.Ordinal);

        return hidden.Where(seen.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
    }

    /// <summary>The line above the issues: how many are open and how many hidden.</summary>
    public static string Summary(int open, int hidden)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(open);
        ArgumentOutOfRangeException.ThrowIfNegative(hidden);

        if (open == 0)
        {
            return hidden == 0 ? "No issues on this server." : $"No open issues. {hidden} hidden.";
        }

        var count = open == 1 ? "1 issue" : $"{open} issues";

        return hidden == 0 ? $"{count} on this server." : $"{count} on this server, {hidden} hidden.";
    }

    /// <summary>What the list says when nothing in it is open.</summary>
    public const string NothingOpen = "Nothing needs attention. Hidden issues come back if they change.";

    /// <summary>Said under the list: where these come from, and why they matter.</summary>
    public const string Source =
        "Bedrock reports almost none of this itself, so a pack can be switched on and do nothing. "
        + "These are found by reading this world's files and each pack's manifest.";
}
