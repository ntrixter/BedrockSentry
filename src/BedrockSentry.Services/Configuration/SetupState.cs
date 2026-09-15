using System.Security.Cryptography;
using System.Text;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Services.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BedrockSentry.Services.Configuration;

/// <summary>
/// Tracks whether first-run setup has been completed, and guards the setup wizard
/// with a one-time token.
/// </summary>
/// <remarks>
/// <para>
/// The wizard claims the administrator account, so anything that can reach it before
/// setup completes owns the install. Two controls sit in front of it: the server binds
/// to loopback only until setup is done, and completing the wizard requires a token
/// readable only by SYSTEM and Administrators. Either alone would be thin; together an
/// attacker needs both network position on the box and privileged file access, at which
/// point they have easier routes.
/// </para>
/// <para>
/// The configured flag is a <em>latch</em>. Once set it never clears, even if the
/// credential file later reads as empty. That matters because a malformed credential
/// file makes the configuration provider return nulls, which is indistinguishable from
/// "never set up" — and treating it as such would reopen the wizard on a live,
/// network-reachable server. A typo in a hand-edit must not be a credential-takeover
/// path, so an unreadable credential reports an error and changes nothing.
/// </para>
/// </remarks>
public sealed class SetupState
{
    private const string TokenFileName = "setup-token.txt";

    private readonly ISentryPaths _paths;
    private readonly IOptionsMonitor<AdminCredentialOptions> _credentials;
    private readonly ILogger<SetupState> _logger;
    private readonly Lock _gate = new();

    private bool _configured;
    private string? _token;

    public SetupState(
        ISentryPaths paths,
        IOptionsMonitor<AdminCredentialOptions> credentials,
        ILogger<SetupState> logger)
    {
        _paths = paths;
        _credentials = credentials;
        _logger = logger;

        _configured = credentials.CurrentValue.IsConfigured;

        credentials.OnChange(_ => Latch());
    }

    /// <summary>True once an administrator account exists. Never returns to false.</summary>
    public bool IsConfigured
    {
        get
        {
            Latch();
            lock (_gate)
            {
                return _configured;
            }
        }
    }

    /// <summary>
    /// The current setup token, generating and persisting one if needed.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> once setup is complete — there is nothing left
    /// to authorise, and handing the value out afterwards would only widen exposure.
    /// </remarks>
    public string? GetOrCreateToken()
    {
        if (IsConfigured)
        {
            return null;
        }

        lock (_gate)
        {
            if (_token is not null)
            {
                return _token;
            }

            var file = Path.Combine(_paths.Config, TokenFileName);

            if (File.Exists(file))
            {
                var existing = File.ReadAllText(file).Trim();

                if (existing.Length > 0)
                {
                    _token = existing;
                    return _token;
                }
            }

            _token = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

            Directory.CreateDirectory(_paths.Config);
            File.WriteAllText(file, _token);

            if (OperatingSystem.IsWindows())
            {
                WindowsAcl.RestrictToSystemAndAdministrators(new FileInfo(file));
            }

            _logger.LogWarning(
                "First-run setup token written to {TokenFile}. It is required to create the administrator account.",
                file);

            return _token;
        }
    }

    /// <summary>Compares a submitted token against the stored one in constant time.</summary>
    public bool ValidateToken(string? submitted)
    {
        if (string.IsNullOrWhiteSpace(submitted))
        {
            return false;
        }

        var expected = GetOrCreateToken();

        if (expected is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(submitted.Trim()),
            Encoding.UTF8.GetBytes(expected));
    }

    /// <summary>Removes the setup token once it is no longer needed.</summary>
    public void CompleteSetup()
    {
        lock (_gate)
        {
            _configured = true;
            _token = null;
        }

        var file = Path.Combine(_paths.Config, TokenFileName);

        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch (IOException ex)
        {
            // Harmless: the token is no longer accepted regardless of whether the
            // file survives. Worth a line so it can be cleaned up by hand.
            _logger.LogWarning(ex, "Could not delete the spent setup token at {TokenFile}.", file);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Could not delete the spent setup token at {TokenFile}.", file);
        }
    }

    private void Latch()
    {
        lock (_gate)
        {
            if (_configured)
            {
                return;
            }

            if (_credentials.CurrentValue.IsConfigured)
            {
                _configured = true;
            }
        }
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
