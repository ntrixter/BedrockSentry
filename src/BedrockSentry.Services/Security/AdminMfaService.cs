using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QRCoder;

namespace BedrockSentry.Services.Security;

/// <inheritdoc cref="IAdminMfaService"/>
/// <remarks>
/// <para><strong>The secret is encrypted, not hashed.</strong> The host has to compute the
/// codes the phone computes, so it needs the secret back. It is protected with the
/// data-protection key ring that already guards the auth cookie, which on Windows is
/// itself DPAPI-protected to the machine, and it sits in <c>auth.json</c>, which only
/// SYSTEM and Administrators can read.</para>
///
/// <para><strong>The recovery codes are hashed.</strong> They are random enough that a
/// single SHA-256 is the right cost, and they do not depend on the key ring: if the
/// ring is lost the secret is unreadable (<see cref="MfaCheck.SecretUnreadable"/>) and a
/// recovery code still signs in.</para>
///
/// <para><strong>A used code is refused.</strong> The last accepted step is held in
/// memory, so a code cannot be used twice inside its minute and a half. A restart
/// forgets it, which reopens only the code that was current at the restart.</para>
/// </remarks>
public sealed class AdminMfaService : IAdminMfaService
{
    /// <summary>The name in the authenticator app's list, beside the account.</summary>
    public const string Issuer = "BedrockSentry";

    /// <summary>How many recovery codes a set holds.</summary>
    public const int RecoveryCodeCount = 10;

    /// <summary>Lower-case letters and digits with the look-alikes (0 o 1 l i) left out.</summary>
    private const string RecoveryAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    private const int RecoveryCodeLength = 10;

    private const string MfaKey = "Sentry:Admin:Mfa";
    private const string ProtectorPurpose = "BedrockSentry.AdminMfa.Secret.v1";

    private readonly IOptionsMonitor<AdminCredentialOptions> _options;
    private readonly AdminCredentialService _writer;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _time;
    private readonly ILogger<AdminMfaService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long? _lastAcceptedStep;

    public AdminMfaService(
        IOptionsMonitor<AdminCredentialOptions> options,
        AdminCredentialService writer,
        IDataProtectionProvider dataProtection,
        TimeProvider time,
        ILogger<AdminMfaService> logger)
    {
        _options = options;
        _writer = writer;
        _protector = dataProtection.CreateProtector(ProtectorPurpose);
        _time = time;
        _logger = logger;
    }

    public bool IsEnabled => _options.CurrentValue.MfaEnabled;

    public int RecoveryCodesLeft => IsEnabled ? StoredRecoveryHashes().Count : 0;

    public MfaEnrolment BeginEnrolment(string account)
    {
        var secret = Totp.NewSecret();
        var uri = Totp.ProvisioningUri(secret, account, Issuer);

        return new MfaEnrolment(Totp.ToBase32(secret), uri, QrCodeDataUri(uri));
    }

    public async Task<IReadOnlyList<string>?> CompleteEnrolmentAsync(
        string secret, string code, CancellationToken ct = default)
    {
        var bytes = Totp.FromBase32(secret);

        if (bytes is not { Length: Totp.SecretBytes })
        {
            return null;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var step = Totp.Match(bytes, code, _time.GetUtcNow(), lastAcceptedStep: null);

            if (step is null)
            {
                return null;
            }

            var codes = NewRecoveryCodes();

            await _writer.WriteWithNewStampAsync(
                new Dictionary<string, JsonNode?>
                {
                    [MfaKey] = new JsonObject
                    {
                        ["Secret"] = _protector.Protect(Convert.ToBase64String(bytes)),
                        ["RecoveryCodes"] = JoinHashes(codes),
                        ["EnabledUtc"] = _time.GetUtcNow().ToString("O"),
                    },
                },
                ct).ConfigureAwait(false);

            _lastAcceptedStep = step;
            _logger.LogInformation("The authenticator-app sign-in step was switched on.");

            return codes;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MfaCheck> VerifyAsync(string code, bool allowRecoveryCode = true, CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            return MfaCheck.Rejected;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var secret = ReadSecret();

            if (secret is not null)
            {
                var step = Totp.Match(secret, code, _time.GetUtcNow(), _lastAcceptedStep);

                if (step is not null)
                {
                    _lastAcceptedStep = step;
                    return MfaCheck.Accepted;
                }
            }

            if (allowRecoveryCode && await TryUseRecoveryCodeAsync(code, ct).ConfigureAwait(false))
            {
                return MfaCheck.AcceptedRecoveryCode;
            }

            return secret is null ? MfaCheck.SecretUnreadable : MfaCheck.Rejected;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> RegenerateRecoveryCodesAsync(CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            throw new InvalidOperationException("The authenticator-app sign-in step is off.");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var codes = NewRecoveryCodes();

            await _writer.WriteWithNewStampAsync(
                new Dictionary<string, JsonNode?> { [MfaKey + ":RecoveryCodes"] = JoinHashes(codes) },
                ct).ConfigureAwait(false);

            _logger.LogInformation("The authenticator-app recovery codes were replaced.");

            return codes;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisableAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // Null rather than removed, so the switch and the new stamp are one write.
            await _writer.WriteWithNewStampAsync(
                new Dictionary<string, JsonNode?> { [MfaKey] = null },
                ct).ConfigureAwait(false);

            _lastAcceptedStep = null;
            _logger.LogInformation("The authenticator-app sign-in step was switched off.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The hash a recovery code is stored as.</summary>
    internal static string HashRecoveryCode(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormaliseRecoveryCode(code))));

    /// <summary>Lower case, with the dash and any spaces taken out.</summary>
    internal static string NormaliseRecoveryCode(string code) =>
        new([.. code.Trim().ToLowerInvariant().Where(c => c is not ('-' or ' '))]);

    private byte[]? ReadSecret()
    {
        var stored = _options.CurrentValue.Mfa?.Secret;

        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(_protector.Unprotect(stored));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            _logger.LogError(
                ex,
                "The authenticator-app secret could not be decrypted; the data-protection key ring has "
                + "changed. Sign in with a recovery code and set the authenticator up again.");
            return null;
        }
    }

    private async Task<bool> TryUseRecoveryCodeAsync(string code, CancellationToken ct)
    {
        var normalised = NormaliseRecoveryCode(code);

        if (normalised.Length != RecoveryCodeLength)
        {
            return false;
        }

        var typed = Encoding.ASCII.GetBytes(HashRecoveryCode(normalised));
        var stored = StoredRecoveryHashes();
        string? matched = null;

        foreach (var hash in stored)
        {
            if (CryptographicOperations.FixedTimeEquals(typed, Encoding.ASCII.GetBytes(hash)))
            {
                matched ??= hash;
            }
        }

        if (matched is null)
        {
            return false;
        }

        var left = stored.Where(h => !string.Equals(h, matched, StringComparison.Ordinal)).ToList();

        await _writer.WriteWithNewStampAsync(
            new Dictionary<string, JsonNode?> { [MfaKey + ":RecoveryCodes"] = string.Join(';', left) },
            ct).ConfigureAwait(false);

        _logger.LogWarning("A recovery code was used to sign in; {Left} remain.", left.Count);

        return true;
    }

    private List<string> StoredRecoveryHashes() =>
        (_options.CurrentValue.Mfa?.RecoveryCodes ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(h => h.ToUpperInvariant())
            .ToList();

    private static List<string> NewRecoveryCodes()
    {
        var codes = new List<string>(RecoveryCodeCount);

        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            var raw = RandomNumberGenerator.GetString(RecoveryAlphabet, RecoveryCodeLength);
            codes.Add(raw[..5] + "-" + raw[5..]);
        }

        return codes;
    }

    private static string JoinHashes(IEnumerable<string> codes) =>
        string.Join(';', codes.Select(HashRecoveryCode));

    private static string QrCodeDataUri(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(6);

        return "data:image/png;base64," + Convert.ToBase64String(png);
    }
}
