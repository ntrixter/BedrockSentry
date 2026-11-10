using System.Globalization;

namespace BedrockSentry.Core.Commands;

/// <summary>Which release of Mojang's <c>@minecraft/vanilla-data</c> fits a server version.</summary>
/// <param name="Wanted">The release that matches the server exactly.</param>
/// <param name="Chosen">
/// The release to use: <paramref name="Wanted"/> when it is published, otherwise the
/// newest one before it, or null when nothing published is that old.
/// </param>
public sealed record VanillaDataMatch(string Wanted, string? Chosen)
{
    /// <summary>The exact release is published.</summary>
    public bool Exact => Chosen is not null && string.Equals(Chosen, Wanted, StringComparison.Ordinal);
}

/// <summary>Matches a server version to a <c>@minecraft/vanilla-data</c> release.</summary>
/// <remarks>
/// <para>Mojang publishes one release per game version, named without the server's fourth
/// number: BDS <c>1.26.52.3</c> is <c>1.26.52</c>. A Preview build's fourth number is
/// its preview number, so Preview <c>1.26.60.30</c> is <c>1.26.60-preview.30</c>
/// (the registry's list, read 2026-10-07).</para>
///
/// <para><strong>The data can lag the server.</strong> When the exact release is not
/// published yet, the newest one before it is used and the Commands view says so. Order is
/// semver's: a preview sorts below its own release, so a server on <c>1.26.60.5</c> before
/// <c>1.26.60</c> is published gets <c>1.26.60-preview.30</c>, the closest data there is,
/// rather than <c>1.26.52</c>.</para>
/// </remarks>
public static class VanillaDataVersion
{
    /// <summary>The release matching <paramref name="bdsVersion"/>, or null when it is not a version.</summary>
    public static string? Wanted(string bdsVersion, bool preview)
    {
        if (!Version.TryParse(bdsVersion, out var version) || version.Build < 0)
        {
            return null;
        }

        var release = string.Create(
            CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}.{version.Build}");

        return preview && version.Revision >= 0
            ? string.Create(CultureInfo.InvariantCulture, $"{release}-preview.{version.Revision}")
            : release;
    }

    /// <summary>The release to use for <paramref name="bdsVersion"/> among those published.</summary>
    public static VanillaDataMatch? Match(string bdsVersion, bool preview, IEnumerable<string> published)
    {
        ArgumentNullException.ThrowIfNull(published);

        if (Wanted(bdsVersion, preview) is not { } wanted || NpmVersion.Parse(wanted) is not { } target)
        {
            return null;
        }

        string? chosen = null;
        NpmVersion? best = null;
        foreach (var text in published)
        {
            if (NpmVersion.Parse(text) is not { } candidate || candidate.CompareTo(target) > 0)
            {
                continue;
            }

            if (best is null || candidate.CompareTo(best.Value) > 0)
            {
                best = candidate;
                chosen = text;
            }
        }

        return new VanillaDataMatch(wanted, chosen);
    }
}

/// <summary>A semver version as npm writes one: <c>1.26.60-preview.30</c>.</summary>
public readonly record struct NpmVersion(int Major, int Minor, int Patch, string[] Prerelease)
    : IComparable<NpmVersion>
{
    /// <summary>The version, or null when the text is not one.</summary>
    public static NpmVersion? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var plus = text.IndexOf('+', StringComparison.Ordinal);
        var core = plus < 0 ? text : text[..plus];
        var dash = core.IndexOf('-', StringComparison.Ordinal);
        var numbers = (dash < 0 ? core : core[..dash]).Split('.');
        var prerelease = dash < 0 ? [] : core[(dash + 1)..].Split('.');

        if (numbers.Length != 3
            || !int.TryParse(numbers[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(numbers[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(numbers[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch)
            || prerelease.Any(p => p.Length == 0))
        {
            return null;
        }

        return new NpmVersion(major, minor, patch, prerelease);
    }

    public int CompareTo(NpmVersion other)
    {
        var order = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (order != 0)
        {
            return order;
        }

        // A release outranks every prerelease of itself.
        if (Prerelease.Length == 0 || other.Prerelease.Length == 0)
        {
            return other.Prerelease.Length.CompareTo(Prerelease.Length) switch
            {
                < 0 => -1,
                > 0 => 1,
                _ => 0,
            };
        }

        for (var i = 0; i < Math.Min(Prerelease.Length, other.Prerelease.Length); i++)
        {
            var mine = Prerelease[i];
            var theirs = other.Prerelease[i];
            var mineIsNumber = int.TryParse(mine, NumberStyles.None, CultureInfo.InvariantCulture, out var a);
            var theirsIsNumber = int.TryParse(theirs, NumberStyles.None, CultureInfo.InvariantCulture, out var b);

            var step = (mineIsNumber, theirsIsNumber) switch
            {
                (true, true) => a.CompareTo(b),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(mine, theirs),
            };

            if (step != 0)
            {
                return step;
            }
        }

        return Prerelease.Length.CompareTo(other.Prerelease.Length);
    }

    public bool Equals(NpmVersion other) => CompareTo(other) == 0;

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', Prerelease));
}
