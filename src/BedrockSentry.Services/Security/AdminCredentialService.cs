using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Security;
using BedrockSentry.Services.Configuration;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BedrockSentry.Services.Security;

/// <inheritdoc cref="IAdminCredentialService"/>
public sealed class AdminCredentialService : IAdminCredentialService
{
    /// <summary>
    /// PBKDF2 iteration count for new hashes.
    /// </summary>
    /// <remarks>
    /// OWASP's current figure for PBKDF2-HMAC-SHA512. Identity's own default is
    /// considerably higher and measured around half a second on modest hardware —
    /// which is a defensible trade for a consumer login page and a poor one here,
    /// where this runs on whatever Windows Server box the admin already had spare.
    /// Raising it later is safe: the iteration count is stored inside each hash, so
    /// verification keeps working and <see cref="CredentialVerification.SuccessRehashNeeded"/>
    /// upgrades existing credentials on the next successful sign-in.
    /// </remarks>
    private const int IterationCount = 210_000;

    private const string UsernameKey = "Sentry:Admin:Username";
    private const string PasswordHashKey = "Sentry:Admin:PasswordHash";
    private const string SecurityStampKey = "Sentry:Admin:SecurityStamp";
    private const string UpdatedUtcKey = "Sentry:Admin:UpdatedUtc";

    private readonly IOptionsMonitor<AdminCredentialOptions> _options;
    private readonly ISentryPaths _paths;
    private readonly IConfigurationRoot _configuration;
    private readonly ILogger<AdminCredentialService> _logger;
    private readonly PasswordHasher<object> _hasher;
    private readonly object _hashSubject = new();

    public AdminCredentialService(
        IOptionsMonitor<AdminCredentialOptions> options,
        ISentryPaths paths,
        IConfiguration configuration,
        ILogger<AdminCredentialService> logger)
    {
        _options = options;
        _paths = paths;
        _logger = logger;

        _configuration = configuration as IConfigurationRoot
            ?? throw new ArgumentException(
                "Credential storage needs the root configuration so it can reload after writing.",
                nameof(configuration));

        _hasher = new PasswordHasher<object>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = IterationCount,
        }));
    }

    public AdminCredentialOptions Current => _options.CurrentValue;

    public bool IsConfigured => Current.IsConfigured;

    public string HashPassword(string password) => _hasher.HashPassword(_hashSubject, password);

    public CredentialVerification Verify(string username, string password)
    {
        var current = Current;

        if (!current.IsConfigured)
        {
            return CredentialVerification.Failed;
        }

        // Compare the username in fixed time too. It leaks far less than the password
        // does, but the cost is a few microseconds and it keeps the whole check
        // uniform rather than leaving one obviously-early exit.
        var usernameMatches = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(username.Trim().ToLowerInvariant()),
            Encoding.UTF8.GetBytes(current.Username!.Trim().ToLowerInvariant()));

        PasswordVerificationResult result;

        try
        {
            result = _hasher.VerifyHashedPassword(_hashSubject, current.PasswordHash!, password);
        }
        catch (FormatException ex)
        {
            // A corrupt or hand-mangled hash must read as a failed sign-in, not as an
            // unhandled exception on the login page.
            _logger.LogError(ex, "The stored administrator password hash is not in a recognised format.");
            return CredentialVerification.Failed;
        }

        if (!usernameMatches)
        {
            return CredentialVerification.Failed;
        }

        return result switch
        {
            PasswordVerificationResult.Success => CredentialVerification.Success,
            PasswordVerificationResult.SuccessRehashNeeded => CredentialVerification.SuccessRehashNeeded,
            _ => CredentialVerification.Failed,
        };
    }

    public async Task SetCredentialAsync(string username, string password, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var file = AuthFilePath(_paths);

        var values = new Dictionary<string, JsonNode?>
        {
            [UsernameKey] = JsonValue.Create(username.Trim()),
            [PasswordHashKey] = JsonValue.Create(HashPassword(password)),

            // A fresh stamp on every credential change is what signs existing sessions
            // out; the cookie carries the old value and stops validating.
            [SecurityStampKey] = JsonValue.Create(Guid.NewGuid().ToString("N")),
            [UpdatedUtcKey] = JsonValue.Create(DateTimeOffset.UtcNow.ToString("O")),
        };

        await JsonConfigWriter.MergeAsync(file, values, ct).ConfigureAwait(false);

        if (OperatingSystem.IsWindows())
        {
            WindowsAcl.RestrictToSystemAndAdministrators(new FileInfo(file));
        }

        // auth.json is deliberately loaded with change-watching off, so nothing picks
        // this up on its own — see AdminCredentialOptions for why. Reload explicitly.
        _configuration.Reload();

        _logger.LogInformation("Administrator credential updated for {Username}.", username.Trim());
    }

    /// <summary>Path to the credential file. Separate from operational settings by design.</summary>
    public static string AuthFilePath(ISentryPaths paths) => Path.Combine(paths.Config, "auth.json");
}
