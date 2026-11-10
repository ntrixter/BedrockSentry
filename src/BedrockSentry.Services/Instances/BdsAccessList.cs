using System.Text.Json;
using System.Text.Json.Nodes;

namespace BedrockSentry.Services.Instances;

/// <summary>
/// Reads one of BDS's own access-control lists — <c>allowlist.json</c>,
/// <c>permissions.json</c> — as a JSON array.
/// </summary>
/// <remarks>
/// <para>Here rather than inside each file's own <c>Load</c> because both
/// carried the identical reader and therefore the identical defect (<c>T203</c>).
/// One rule written in two places is enforced in neither, and this repository's
/// own recurring class is exactly that.</para>
///
/// <para><strong>There are three spellings of an empty list and only two were
/// understood.</strong> A file that is not there is empty — a server that has
/// never had an access edit may not have written one yet. <c>[]</c> is empty,
/// and is what this wrapper writes. And <c>null</c> is empty, which is what
/// <em>BDS itself</em> writes when the last entry is removed while the server is
/// running: six bytes, the four characters and a CRLF, observed on BDS
/// 1.26.45.1. The wrapper's own writer never produces it, which is what
/// identifies it as the server's spelling rather than damage.</para>
///
/// <para>Reading that third spelling as damage did not merely misreport one
/// removal. Every allowlist path reads the file before it writes, so the throw
/// left the list unreadable <em>and unwritable</em> from the panel, permanently —
/// and the operator was told the removal had failed at the moment it succeeded.
/// Recovery needed a text editor at an elevated prompt, for one button pressed
/// once, correctly.</para>
///
/// <para><strong>Everything else still throws, and that rule is unchanged.</strong>
/// Guessing at a broken access-control file is worse than saying so: a wrapper
/// that reads an unreadable allowlist as an empty one reports "nobody is
/// allowlisted" over a list that exists. What was wrong was never that rule; it
/// was that the server's own output was being called broken. A JSON <c>null</c>
/// is a value BDS writes. A truncated object is damage.</para>
///
/// <para><strong>It is a permanent accommodation rather than a shim with a known
/// end</strong>, which was the open question and is now measured (<c>T203</c>
/// clause 5). The original reproduction was BDS 1.26.45.1, a RakNet-era build;
/// 1.26.51.1 on NetherNet writes the <em>identical</em> six bytes. So
/// <c>null</c> is the vendor's convention rather than one version's spelling, and
/// nothing here is waiting to be removed.</para>
/// </remarks>
internal static class BdsAccessList
{
    /// <summary>
    /// The entries in <paramref name="path"/>, or an empty array for any of the
    /// three spellings of empty. Throws for anything else.
    /// </summary>
    public static JsonArray ReadOrEmpty(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var parsed = JsonNode.Parse(File.ReadAllText(path));

        return parsed switch
        {
            JsonArray entries => entries,

            // JsonNode.Parse answers null for the JSON literal `null` — the
            // server's own empty list. Distinct from the missing-file arm above
            // only in how it got here, and identical in what it means.
            null => [],

            _ => throw new JsonException(
                $"{Path.GetFileName(path)} does not contain a JSON array."),
        };
    }
}
