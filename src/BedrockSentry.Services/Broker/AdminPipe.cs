using System.IO.Pipes;
using System.Text.Json;
using BedrockSentry.Core;
using BedrockSentry.Core.Broker;
using BedrockSentry.Core.Configuration;
using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Core.Instances;
using BedrockSentry.Services.Instances;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BedrockSentry.Services.Broker;

/// <summary>
/// The installer's drain and call-off (<c>D62</c>, <c>D100</c>), as the admin pipe serves them.
/// </summary>
public sealed class AdminDrain
{
    private readonly IManagedServerShutdown _shutdown;
    private readonly DrainCountdowns _countdowns;
    private readonly IOptionsMonitor<UpdateOptions> _options;

    public AdminDrain(IManagedServerShutdown shutdown, DrainCountdowns countdowns, IOptionsMonitor<UpdateOptions> options)
    {
        _shutdown = shutdown;
        _countdowns = countdowns;
        _options = options;
    }

    /// <summary>Warns, drains every server and takes any backup-on-shutdown, as a self-update does.</summary>
    public async Task<AdminDrainAnswer> DrainAsync()
    {
        var warning = _options.CurrentValue.PlayerWarning;

        // CancellationToken.None: the caller is an installer that is about to stop
        // this service anyway, and a drain abandoned half-way is worse than either
        // finishing it or never starting it. Calling it off before the stops is
        // CallOff (`D100`).
        var countdown = _countdowns.Begin();
        ManagedShutdownResult drained;

        try
        {
            drained = await _shutdown.DrainAsync(StopReason.WrapperUpdate, warning, CancellationToken.None, countdown);
        }
        finally
        {
            _countdowns.End(countdown);
        }

        return new AdminDrainAnswer(
            drained.Stopped, drained.Failed, drained.CalledOff, (int)warning.TotalSeconds, BuildInfo.Identity);
    }

    /// <summary>Calls off every drain still counting down; too late once its stops have begun.</summary>
    public AdminCallOffAnswer CallOff()
    {
        var (calledOff, tooLate) = _countdowns.CallOffAll();
        return new AdminCallOffAnswer(calledOff, tooLate);
    }
}

/// <summary>Whether the admin pipe is being served, for the Readiness panel.</summary>
public sealed class AdminPipeStatus
{
    /// <summary>Why the pipe could not be created, or null while it is served or not yet tried.</summary>
    public string? Problem { get; internal set; }

    /// <summary>
    /// The Readiness row: an installer that cannot reach the pipe stops the service
    /// without the countdown, so a pipe something else holds is worth a sentence.
    /// </summary>
    /// <param name="pipeName">The pipe's name, for the sentence.</param>
    public SystemCheck Check(string pipeName) => Problem is { } problem
        ? new SystemCheck(
            Title, SystemCheckState.Missing,
            $"This service could not create its admin pipe, {pipeName} ({problem}). Usually another "
            + "process created the name first while the service was stopped. The next install.ps1 run "
            + "then stops the service without warning players or taking a backup first; every world is "
            + "still saved. Restart this service to take the name back.",
            CanFix: false)
        : new SystemCheck(
            Title, SystemCheckState.Good,
            $"install.ps1 can warn players and drain every server over {pipeName} before it stops this service.");

    /// <summary>The row's name.</summary>
    public const string Title = "Installer drain";
}

/// <summary>Creates the admin pipe's instances.</summary>
public interface IAdminPipeFactory
{
    /// <summary>
    /// One server instance. <paramref name="firstInstance"/> is true for the first only:
    /// it fails if anything already holds the name.
    /// </summary>
    NamedPipeServerStream Create(bool firstInstance);
}

/// <summary>One connection on the admin pipe: one request, one answer.</summary>
public static class AdminConnection
{
    /// <summary>How long a caller has to send its request.</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Serves one request. Never throws for anything the caller sent.</summary>
    public static async Task ServeAsync(
        Stream stream, AdminDrain drain, ILogger log, CancellationToken ct, TimeSpan? readTimeout = null)
    {
        var json = AdminPipeContract.Json;

        try
        {
            AdminRequest? request;

            // The deadline covers what the caller sends, not the drain, which lasts the
            // warning plus every server's stop and backup.
            using (var reading = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                reading.CancelAfter(readTimeout ?? ReadTimeout);

                try
                {
                    request = await BrokerWire.ReadAsync<AdminRequest>(stream, reading.Token, json);
                }
                catch (JsonException ex)
                {
                    log.LogWarning("Admin pipe request could not be read: {Why}", ex.Message);
                    await BrokerWire.WriteAsync(stream, new AdminRefusal("The request is not one this wrapper can read."), reading.Token, json);
                    return;
                }
            }

            switch (request?.Operation)
            {
                case null:
                    return;

                case AdminOperation.Drain:
                    log.LogInformation("Draining every server for the installer, as asked on the admin pipe.");
                    await BrokerWire.WriteAsync(stream, await drain.DrainAsync(), ct, json);
                    return;

                case AdminOperation.CallOff:
                    log.LogInformation("Calling off the installer's drain, as asked on the admin pipe.");
                    await BrokerWire.WriteAsync(stream, drain.CallOff(), ct, json);
                    return;

                default:
                    await BrokerWire.WriteAsync(stream, new AdminRefusal($"'{request.Operation}' is not an operation."), ct, json);
                    return;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Admin pipe caller sent nothing within {Seconds} seconds", (readTimeout ?? ReadTimeout).TotalSeconds);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            log.LogWarning("Admin pipe connection ended: {Why}", ex.Message);
        }
    }
}

/// <summary>
/// The main service's admin pipe: the drain and the call-off <c>install.ps1</c> asks
/// for before it stops the service.
/// </summary>
/// <remarks>
/// <para>Two instances, so a call-off can arrive while a drain is being answered.
/// Both are created at start and reused, so the name is held, at its instance limit,
/// until the service stops.</para>
/// <para>Who may ask is the pipe's access list (<see cref="WindowsAdminPipeFactory"/>):
/// an elevated administrator or LocalSystem. A pipe this service cannot create is
/// logged and left out, never fatal: the panel and the servers do not depend on it,
/// and the installer then stops the service the ordinary way.</para>
/// </remarks>
public sealed class AdminPipeServer : BackgroundService
{
    /// <summary>A drain and its call-off.</summary>
    public const int Instances = 2;

    private readonly IAdminPipeFactory _pipes;
    private readonly AdminDrain _drain;
    private readonly ILogger<AdminPipeServer> _log;
    private readonly AdminPipeStatus _status;

    public AdminPipeServer(
        IAdminPipeFactory pipes, AdminDrain drain, ILogger<AdminPipeServer> log, AdminPipeStatus? status = null)
    {
        _pipes = pipes;
        _drain = drain;
        _log = log;
        _status = status ?? new AdminPipeStatus();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pipes = new List<NamedPipeServerStream>();

        try
        {
            pipes.Add(_pipes.Create(firstInstance: true));

            for (var i = 1; i < Instances; i++)
            {
                pipes.Add(_pipes.Create(firstInstance: false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(
                "The admin pipe could not be created ({Why}). An installer cannot drain the servers before it stops this service.",
                ex.Message);

            _status.Problem = ex.Message;

            foreach (var pipe in pipes)
            {
                await pipe.DisposeAsync();
            }

            return;
        }

        try
        {
            await Task.WhenAll(pipes.Select(pipe => ListenAsync(pipe, stoppingToken)));
        }
        finally
        {
            foreach (var pipe in pipes)
            {
                await pipe.DisposeAsync();
            }
        }
    }

    private async Task ListenAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                _log.LogDebug(ex, "Admin pipe connection dropped before it was accepted");
                Disconnect(pipe);
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ContinueWith(_ => { }, TaskScheduler.Default);
                continue;
            }

            try
            {
                await AdminConnection.ServeAsync(pipe, _drain, _log, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Admin pipe connection failed");
            }
            finally
            {
                Disconnect(pipe);
            }
        }
    }

    private void Disconnect(NamedPipeServerStream pipe)
    {
        try
        {
            if (pipe.IsConnected)
            {
                pipe.Disconnect();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _log.LogDebug(ex, "Admin pipe disconnect failed");
        }
    }
}
