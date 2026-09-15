using System.Collections.Immutable;
using BedrockSentry.Core.Configuration;

namespace BedrockSentry.Core.Diagnostics;

/// <summary>
/// The request paths that may be served only on the wrapper's loopback listener,
/// and the match the port gate uses to recognise them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This exists because the gate and endpoint routing have to answer the
/// same question about a path, and they did not</strong> (<c>T175</c>). The gate
/// compared with <c>PathString.Equals</c> while the endpoints are mapped through
/// <c>MapHealthChecks</c>, and routing tolerates a trailing slash — so
/// <c>/readyz</c> was refused on the UI listener and <c>/readyz/</c> was served in
/// full, including the per-check detail. The TLS listener binds every interface,
/// so that was an unauthenticated read of instance names and fault reasons from
/// off the machine. Case was handled; the slash was not.
/// </para>
/// <para>
/// <strong>The match is deliberately wider than routing, not narrower.</strong> A
/// path matches when it equals one of these roots or continues past it at a
/// segment boundary, so <c>/readyz</c>, <c>/readyz/</c> and <c>/readyz/anything</c>
/// are all gated and <c>/readyzebra</c> is not. Over-gating beneath these roots
/// costs nothing — no route lives there and none should — while under-gating is
/// what <c>T175</c> was. When the two are not equally cheap, gate.
/// </para>
/// <para>
/// <strong>The list is data, and that is half the point.</strong> It used to be
/// three <c>||</c> clauses inside <c>Program.cs</c> under a comment warning that
/// "anything added here and not to this list is served on the UI listener" — a
/// rule stated in a file the test project cannot reach, which is this codebase's
/// recurring defect. Adding a loopback-only endpoint means adding it here, where
/// <c>LoopbackOnlyPathTests</c> can see it.
/// </para>
/// <para>
/// Here in <c>Core</c> on plain <c>net10.0</c> (<c>D9</c>), so it takes a
/// <see cref="string"/> rather than ASP.NET's <c>PathString</c> and <c>Core</c>
/// keeps depending on nothing.
/// </para>
/// </remarks>
public static class LoopbackOnlyPaths
{
    /// <summary>
    /// Every path root served only on the loopback listener.
    /// </summary>
    /// <remarks>
    /// Two of these are the updater's frozen cross-version contract
    /// (<see cref="UpdateOptions.HealthPath"/>, <see cref="UpdateOptions.DrainPath"/>)
    /// and one is not (<see cref="HealthContract.ReadyPath"/>); they are gated
    /// identically regardless, because who may reach a path is a different
    /// question from whether its spelling may change.
    /// </remarks>
    public static readonly ImmutableArray<string> All =
    [
        UpdateOptions.HealthPath,
        UpdateOptions.DrainPath,
        HealthContract.ReadyPath,
    ];

    /// <summary>
    /// Whether <paramref name="path"/> is one of <see cref="All"/> or sits beneath
    /// one of them, compared case-insensitively.
    /// </summary>
    /// <remarks>
    /// Null and empty are not matches: there is nothing to gate, and the caller's
    /// own routing will answer them.
    /// </remarks>
    public static bool Matches(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (var root in All)
        {
            if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // A segment boundary, so /readyzebra is a different path rather than a
            // longer spelling of this one.
            if (path.Length > root.Length
                && path[root.Length] == '/'
                && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
