using BedrockSentry.Core.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BedrockSentry.Services.Diagnostics;

/// <summary>
/// Reports whether the wrapper can still write where it keeps everything
/// (<c>T165</c>).
/// </summary>
/// <remarks>
/// <para>The one condition worth calling <c>Unhealthy</c> rather than
/// <c>Degraded</c>: a data root that cannot be written to means no backup can be
/// taken, no config change can be saved and no session can be recorded. Unlike a
/// faulted server, nothing about it is the wrapper working correctly.</para>
///
/// <para>It is tested by writing, not by asking. <c>Directory.Exists</c> is true
/// for a full volume, a revoked ACL and a disconnected share alike, and the whole
/// value of this check is catching the cases where the path looks fine and the
/// write fails. The probe file is created and deleted in the same call and is
/// named so that one left behind is recognisable rather than mysterious.</para>
///
/// <para>The write is <see cref="DataRootProbe"/>'s, at most once per
/// <see cref="DataRootProbe.Interval"/>, never once per request: <c>/readyz</c> is
/// anonymous, and a request must not be a way to make the service write.</para>
/// </remarks>
public sealed class DataRootHealthCheck : IHealthCheck
{
    /// <summary>The name this check reports under, in the JSON and in registration.</summary>
    public const string CheckName = "data-root";

    private readonly DataRootProbe _probe;

    public DataRootHealthCheck(DataRootProbe probe) => _probe = probe;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(_probe.Current());

    /// <summary>
    /// The rule, taking a path so a test can point it at a directory it controls.
    /// </summary>
    public static HealthCheckResult Evaluate(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            return HealthCheckResult.Unhealthy("The data root is not configured.");
        }

        var probe = Path.Combine(dataRoot, $".healthcheck-{Guid.NewGuid():N}.tmp");

        try
        {
            // Zero bytes is enough: the question is whether a create succeeds, and
            // a larger write would only add a way for a nearly-full volume to pass.
            using (File.Create(probe))
            {
            }

            File.Delete(probe);

            return HealthCheckResult.Healthy($"The data root is writable ({dataRoot}).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or DirectoryNotFoundException or NotSupportedException)
        {
            return HealthCheckResult.Unhealthy(
                $"The data root cannot be written to ({dataRoot}): {ex.Message}");
        }
        finally
        {
            // A probe left behind would be collected by the next run of this
            // check, but only if that run gets far enough to delete it — so try
            // here too, and never let the cleanup itself fail the check.
            try
            {
                if (File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>
/// The data root's last write test, repeated only once it is older than
/// <see cref="Interval"/>.
/// </summary>
/// <remarks>
/// A singleton, since the health check itself is created per request. However often
/// <c>/readyz</c> is asked, the data root sees one probe file per interval, and none
/// when nobody asks.
/// </remarks>
public sealed class DataRootProbe
{
    /// <summary>How long a result stands before the next request tests again.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly ISentryPaths _paths;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private (DateTimeOffset At, HealthCheckResult Result)? _last;

    public DataRootProbe(ISentryPaths paths, TimeProvider time)
    {
        _paths = paths;
        _time = time;
    }

    /// <summary>The last result, or a new one when it is older than <see cref="Interval"/>.</summary>
    public HealthCheckResult Current()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();

            if (_last is { } last && now - last.At < Interval)
            {
                return last.Result;
            }

            var result = DataRootHealthCheck.Evaluate(_paths.DataRoot);
            _last = (now, result);
            return result;
        }
    }
}
