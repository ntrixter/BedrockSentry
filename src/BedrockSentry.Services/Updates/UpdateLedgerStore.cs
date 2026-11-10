using System.Text.Json;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Updates;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Updates;

/// <summary>Reads and writes the update ledger under the data root.</summary>
/// <remarks>
/// Lives outside the install tree so it survives the update it is recording. A ledger
/// stored alongside the binaries would be erased by the very swap that produced the
/// failure it needs to remember, and the wrapper would retry the same bad build
/// forever.
/// </remarks>
public sealed class UpdateLedgerStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly ISentryPaths _paths;
    private readonly ILogger<UpdateLedgerStore> _logger;
    private readonly Lock _gate = new();

    public UpdateLedgerStore(ISentryPaths paths, ILogger<UpdateLedgerStore> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    private string LedgerPath => Path.Combine(_paths.Update, "ledger.json");

    public UpdateLedger Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(LedgerPath))
                {
                    return new UpdateLedger();
                }

                return JsonSerializer.Deserialize<UpdateLedger>(File.ReadAllText(LedgerPath))
                    ?? new UpdateLedger();
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // A fresh ledger loses the memory of past failures, so say so loudly
                // rather than quietly re-enabling updates that were blocked.
                _logger.LogError(ex, "The update ledger could not be read; starting from an empty one.");
                return new UpdateLedger();
            }
        }
    }

    public void Save(UpdateLedger ledger)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_paths.Update);

                var temp = LedgerPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(ledger, Options));
                File.Move(temp, LedgerPath, overwrite: true);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Could not write the update ledger.");
            }
        }
    }

    /// <summary>Records a successful update and clears the failure counters and the pause.</summary>
    /// <remarks>
    /// The pause went with the counter only from <c>D101</c> on: before, a successful
    /// <b>Install now</b> left automatic updates off with nothing on the page to say so.
    /// </remarks>
    public void RecordSuccess(string identity)
    {
        var ledger = Load();

        ledger.AutoUpdateDisabled = false;
        ledger.ConsecutiveFailures = 0;
        ledger.CooldownUntilUtc = null;
        ledger.LastFailureReason = null;
        ledger.LastSuccessIdentity = identity;
        ledger.LastSuccessUtc = DateTimeOffset.UtcNow;

        Save(ledger);
    }

    /// <summary>
    /// Resumes automatic updates that repeated failures paused (<c>D101</c>).
    /// </summary>
    /// <remarks>
    /// Clears the failure count as well as the flag, because <see cref="UpdateLedger.CanAttempt"/>
    /// refuses at the count too and the button would otherwise change nothing. A build that
    /// failed stays blocked, and a cooldown still running keeps running.
    /// </remarks>
    public void ResumeAutomaticUpdates()
    {
        var ledger = Load();

        ledger.AutoUpdateDisabled = false;
        ledger.ConsecutiveFailures = 0;

        Save(ledger);
        _logger.LogInformation("Automatic updates resumed from the Settings page.");
    }

    /// <summary>Records a failed update and applies all three anti-loop guards.</summary>
    public void RecordFailure(string identity, string reason, UpdateOptions options)
    {
        var ledger = Load();

        if (!ledger.BlockedIdentities.Contains(identity, StringComparer.OrdinalIgnoreCase))
        {
            ledger.BlockedIdentities.Add(identity);
        }

        ledger.ConsecutiveFailures++;
        ledger.CooldownUntilUtc = DateTimeOffset.UtcNow.Add(options.CooldownAfterRollback);
        ledger.LastFailureReason = reason;
        ledger.LastAttemptedIdentity = identity;
        ledger.LastAttemptUtc = DateTimeOffset.UtcNow;

        if (ledger.ConsecutiveFailures >= options.MaxConsecutiveFailures)
        {
            ledger.AutoUpdateDisabled = true;

            _logger.LogError(
                "Automatic updates disabled after {Count} consecutive failures. Latest: {Reason}",
                ledger.ConsecutiveFailures,
                reason);
        }

        Save(ledger);
    }

    /// <summary>
    /// Picks up the outcome the helper left behind and folds it into the ledger.
    /// </summary>
    /// <remarks>
    /// Called on startup. When an update succeeds this is running in the new build;
    /// when it was rolled back, in the old one. Either way the result file is how the
    /// service learns what happened while it was not running.
    /// </remarks>
    /// <param name="resultPath">
    /// Where the helper wrote it: <see cref="IWrapperUpdateInstaller.ResultPath"/>. Null
    /// is the data root's <c>update\result.json</c>, where the in-process helper writes.
    /// </param>
    public UpdateResult? ConsumeResult(UpdateOptions options, string? resultPath = null)
    {
        resultPath ??= Path.Combine(_paths.Update, "result.json");

        if (!File.Exists(resultPath))
        {
            return null;
        }

        try
        {
            var result = JsonSerializer.Deserialize<UpdateResult>(File.ReadAllText(resultPath));

            if (result is not null && Load().LastConsumedResultUtc is { } consumed && result.CompletedUtc <= consumed)
            {
                // Already counted: a result the service may not delete is read again
                // at every start, and a success counted twice is harmless but a
                // failure counted twice blocks updates for a fault that happened once.
                TryDelete(resultPath);
                return null;
            }

            if (result is not null)
            {
                if (result.Succeeded)
                {
                    _logger.LogInformation("Update to {Identity} completed.", result.Identity);
                    RecordSuccess(result.Identity);
                }
                else
                {
                    _logger.LogError(
                        "Update to {Identity} failed and was {Outcome}: {Message}",
                        result.Identity,
                        result.RolledBack ? "rolled back" : "abandoned",
                        result.Message);

                    RecordFailure(result.Identity, result.Message, options);
                }

                var ledger = Load();
                ledger.LastConsumedResultUtc = result.CompletedUtc;
                Save(ledger);
            }

            TryDelete(resultPath);
            return result;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not read the update result file.");
            return null;
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The broker's helper writes where this account may only read; the
            // ledger's mark above is what keeps it from being counted again.
            _logger.LogDebug(ex, "The update result file {Path} stays where it is.", path);
        }
    }
}
