using System.Security.Claims;
using BedrockSentry.Core.Security;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace BedrockSentry.Services.Security;

/// <summary>
/// The sign-in between a right password and a right authenticator code (<c>D125</c>),
/// and the sign-in both steps end in.
/// </summary>
/// <remarks>
/// <para>The password step issues a short cookie under its own scheme rather than the
/// panel's. The panel's authorisation policy reads only the default scheme, so the
/// half-signed-in cookie opens nothing but the code page.</para>
///
/// <para>Here rather than in the two sign-in pages, which the suite cannot reference:
/// the claims, the lifetime and the redirect rule are each stated once.</para>
/// </remarks>
public static class SecondStepSignIn
{
    /// <summary>The scheme of the half-signed-in cookie.</summary>
    public const string Scheme = "BedrockSentry.SecondStep";

    /// <summary>The half-signed-in cookie's name.</summary>
    public const string CookieName = "BedrockSentry.SecondStep";

    /// <summary>The code page's path.</summary>
    public const string Path = "/account/second-step";

    /// <summary>How long after the password the code may be typed. Does not slide.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private const string RememberClaim = "bedrocksentry:remember";

    /// <summary>The principal the panel's own cookie carries.</summary>
    public static ClaimsPrincipal SignedIn(string username, string? securityStamp) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, username),
                new Claim(BedrockSentryClaims.SecurityStamp, securityStamp ?? string.Empty),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme));

    /// <summary>
    /// The half-signed-in principal: who, the stamp the password matched, and whether
    /// Keep me signed in was ticked, so the second step can finish what the first began.
    /// </summary>
    public static ClaimsPrincipal Pending(string username, string? securityStamp, bool remember) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, username),
                new Claim(BedrockSentryClaims.SecurityStamp, securityStamp ?? string.Empty),
                new Claim(RememberClaim, remember ? "true" : "false"),
            ],
            Scheme));

    /// <summary>
    /// What a half-signed-in principal carries, or null when it is not one or its stamp
    /// is no longer the stored one (the password changed between the two steps).
    /// </summary>
    public static PendingSignIn? Read(ClaimsPrincipal? principal, string? currentStamp)
    {
        var name = principal?.FindFirstValue(ClaimTypes.Name);
        var stamp = principal?.FindFirstValue(BedrockSentryClaims.SecurityStamp);

        if (string.IsNullOrEmpty(name)
            || string.IsNullOrEmpty(currentStamp)
            || !string.Equals(stamp, currentStamp, StringComparison.Ordinal))
        {
            return null;
        }

        return new PendingSignIn(name, principal!.FindFirstValue(RememberClaim) == "true");
    }

    /// <summary>
    /// Only same-site, path-relative destinations are honoured.
    /// </summary>
    /// <remarks>
    /// The leading-double-slash check is the part that matters: "//evil.com" is a
    /// protocol-relative absolute URL that browsers follow off-site, and it passes
    /// Uri.IsWellFormedUriString and a naive StartsWith('/') test alike. A backslash
    /// is refused too, since browsers read "/\evil.com" as "//evil.com".
    /// </remarks>
    public static string SafeReturnUrl(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)
            || !target.StartsWith('/')
            || target.StartsWith("//", StringComparison.Ordinal)
            || target.StartsWith("/\\", StringComparison.Ordinal))
        {
            return "/";
        }

        return target;
    }
}

/// <summary>A half-finished sign-in, read back from its cookie.</summary>
public sealed record PendingSignIn(string Username, bool Remember);
