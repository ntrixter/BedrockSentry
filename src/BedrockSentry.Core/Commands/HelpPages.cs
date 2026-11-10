using System.Globalization;
using System.Text.RegularExpressions;
using BedrockSentry.Core.Instances;

namespace BedrockSentry.Core.Commands;

/// <summary>Reads the pages the server prints for <c>help &lt;page&gt;</c>.</summary>
/// <remarks>
/// <para>A page is a header, <c>--- Showing help page 3 of 44 (/help &lt;page&gt;) ---</c>,
/// then one form per line, then a tip. Only the header carries the
/// <c>[timestamp LEVEL]</c> prefix; the forms arrive bare (BDS 1.26.52.3).</para>
///
/// <para><strong>The listing is complete only with cheats on.</strong> With
/// <c>allow-cheats=false</c> the same server listed 17 names on 4 pages, not 91 on 44,
/// and left out every add-on command. That is why the listing is read from a throwaway
/// copy of the server, never from an administrator's own (<c>D105</c>).</para>
/// </remarks>
public static partial class HelpPages
{
    [GeneratedRegex(@"Showing help page (?<page>\d+) of (?<of>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderPattern { get; }

    /// <summary>One raw output line without its <c>[timestamp LEVEL]</c> prefix.</summary>
    public static string Message(string raw) => BdsLogParser.Parse(raw).Message;

    /// <summary>The page and the page count a header names, or null when the line is not one.</summary>
    public static (int Page, int Of)? Header(string message)
    {
        var match = HeaderPattern.Match(message ?? string.Empty);
        if (!match.Success
            || !int.TryParse(match.Groups["page"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var page)
            || !int.TryParse(match.Groups["of"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var of))
        {
            return null;
        }

        return (page, of);
    }

    /// <summary>The form lines among one page's messages, in the order printed.</summary>
    public static IReadOnlyList<string> Forms(IEnumerable<string> messages) =>
        [.. messages.Where(m => HelpLine.Parse(m) is not null)];
}
