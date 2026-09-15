namespace BedrockSentry.Core.Security;

/// <summary>Claim types BedrockSentry issues on its own sign-in cookies.</summary>
public static class BedrockSentryClaims
{
    /// <summary>
    /// Copy of the credential's security stamp at the time of sign-in.
    /// </summary>
    /// <remarks>
    /// Compared against the stored stamp on every request. A password change issues a
    /// new stamp, so cookies minted before the change stop validating — which is what
    /// makes "change the password" actually end other sessions rather than merely
    /// changing what the next sign-in requires.
    /// </remarks>
    public const string SecurityStamp = "bedrocksentry:security_stamp";
}
