namespace BedrockSentry.Core.Packages;

/// <summary>Which Bedrock build an instance is on, for comparing against an offer.</summary>
/// <remarks>
/// <para><strong>The server's own answer when it parses, and otherwise the package
/// this wrapper laid down</strong> (<c>D53</c>). A Preview build installed from
/// <c>bedrock-server-1.26.60.21.zip</c> announces itself as
/// <em>1.26.60-beta21</em>, which no version parser here reads, so the panel called a
/// running server unreported and an instance set to Automatic never updated
/// (<c>T146</c>). The package is something the wrapper wrote rather than something
/// Mojang printed, so comparing against it does not depend on how the next build
/// spells itself.</para>
///
/// <para><strong>The package stands only while the server still says what it said on
/// those files.</strong> Somebody unzipping another build by hand changes the
/// announcement, and then the package describes files that are no longer there. A
/// changed announcement that does not parse is an unknown build, which is the
/// honest answer; guessing it from the stale package is how a panel offers the
/// wrong update.</para>
/// </remarks>
public static class InstalledBuild
{
    /// <summary>The build to compare an offer against, or null when it cannot be told.</summary>
    /// <param name="reported">
    /// What the server announced: this run's, or failing that the last recorded.
    /// </param>
    /// <param name="package">The package version this wrapper last installed here, if any.</param>
    /// <param name="packageReported">
    /// What the server announced on the first start after that install, or null when it
    /// has not started on those files yet.
    /// </param>
    public static Version? Identify(string? reported, string? package, string? packageReported)
    {
        if (Parse(reported) is { } announced)
        {
            return announced;
        }

        if (package is not { Length: > 0 })
        {
            return null;
        }

        var stillThose = packageReported is null
            || reported is null
            || string.Equals(packageReported.Trim(), reported.Trim(), StringComparison.OrdinalIgnoreCase);

        return stillThose ? Parse(package) : null;
    }

    /// <summary>What the page says a server is on.</summary>
    /// <param name="identity">From <see cref="Identify"/>.</param>
    /// <param name="announced">What the server calls itself, or null when it never has.</param>
    /// <remarks>
    /// The announcement stays a displayed fact when the identity came from the package:
    /// it is the running server's own answer, and a Preview build's spelling is what
    /// somebody would search for (<c>D53</c>).
    /// </remarks>
    public static InstalledBuildText Describe(Version? identity, string? announced)
    {
        var said = announced is { Length: > 0 } ? announced.Trim() : null;

        if (identity is not null)
        {
            return new InstalledBuildText(
                identity.ToString(),
                said is not null && Parse(said) is null ? $"reports itself as {said}" : null);
        }

        return said is not null
            ? new InstalledBuildText(said, "cannot be compared with Mojang's builds")
            : new InstalledBuildText(null, "not reported — start the server once");
    }

    /// <summary>A version as a release build announces it, <c>1.26.36.1</c>.</summary>
    /// <remarks>
    /// Two to four numeric parts, which is what <see cref="Version.TryParse(string?, out Version?)"/>
    /// accepts and what Bedrock uses. Anything with a tail does not parse, deliberately:
    /// reading <c>-beta21</c> as a fourth component is the one-line answer <c>D53</c>
    /// refused, because it is right only until the next spelling.
    /// </remarks>
    public static Version? Parse(string? text) =>
        !string.IsNullOrWhiteSpace(text) && Version.TryParse(text.Trim(), out var version)
            ? version
            : null;
}

/// <summary>What the page says a server is on: a value, and a note beside it.</summary>
public readonly record struct InstalledBuildText(string? Value, string? Detail);
