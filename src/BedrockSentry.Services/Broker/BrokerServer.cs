using System.IO.Pipes;
using System.Text.Json;
using BedrockSentry.Core;
using BedrockSentry.Core.Broker;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BedrockSentry.Services.Broker;

/// <summary>Decides whether the process on the other end of a connection may ask anything.</summary>
/// <remarks>
/// A seam because the answer is Windows-only (the client's process id against the
/// main service's, from the service control manager) and the rest of the server is not.
/// There is deliberately no implementation that admits everyone.
/// </remarks>
public interface IBrokerCallerCheck
{
    /// <summary>Null to admit the caller, or the reason it is turned away, for the log.</summary>
    string? Refuse(NamedPipeServerStream pipe);
}

/// <summary>Creates the broker's pipe instances.</summary>
public interface IBrokerPipeFactory
{
    /// <summary>
    /// One server instance of the broker's pipe. <paramref name="firstInstance"/> is
    /// true for the first only: it fails if anything already holds the name, so a
    /// process that created the pipe first is never mistaken for the broker's own.
    /// </summary>
    NamedPipeServerStream Create(bool firstInstance);
}

/// <summary>One connection: the handshake, one request, one answer.</summary>
public static class BrokerConnection
{
    /// <summary>How long a caller has to send its hello and its request.</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Serves one request on <paramref name="stream"/>. Never throws for anything the
    /// caller sent; a connection that breaks is logged and ends.
    /// </summary>
    public static async Task ServeAsync(
        Stream stream, BrokerDispatcher dispatcher, ILogger log, CancellationToken ct, TimeSpan? readTimeout = null)
    {
        try
        {
            BrokerRequest? request;

            // The deadline covers what the caller sends, not the work: a shadow copy
            // can take longer than any caller should take to say what it wants.
            using (var reading = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                reading.CancelAfter(readTimeout ?? ReadTimeout);

                var hello = await BrokerWire.ReadAsync<BrokerHello>(stream, reading.Token);

                // Answered before the verdict, so a caller of another version learns
                // why from the broker's hello rather than from a closed pipe.
                await BrokerWire.WriteAsync(stream, new BrokerHello(BrokerContract.Version, BuildInfo.Identity), reading.Token);

                if (BrokerContract.Refuse(hello) is { } mismatch)
                {
                    log.LogWarning("Broker caller refused at the handshake: {Why}", mismatch);
                    return;
                }

                try
                {
                    request = await BrokerWire.ReadAsync<BrokerRequest>(stream, reading.Token);
                }
                catch (JsonException ex)
                {
                    // A field the contract does not have, a duplicate, or not JSON:
                    // refused whole, never read in part.
                    log.LogWarning("Broker request could not be read: {Why}", ex.Message);
                    await BrokerWire.WriteAsync(
                        stream, BrokerResponse.Refusal("The request is not one this broker can read."), reading.Token);
                    return;
                }
            }

            if (request is null)
            {
                return;
            }

            var response = await dispatcher.DispatchAsync(request, ct);
            await BrokerWire.WriteAsync(stream, response, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Broker caller sent nothing within {Seconds} seconds", (readTimeout ?? ReadTimeout).TotalSeconds);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            log.LogWarning("Broker connection ended: {Why}", ex.Message);
        }
    }
}

/// <summary>The broker's listener: a fixed set of pipe instances, each serving one caller at a time.</summary>
/// <remarks>
/// <para>Every instance is created once and reused with
/// <see cref="NamedPipeServerStream.Disconnect"/>, so the name is held from the first
/// instance until the service stops. An instance closed and created again would leave
/// a moment in which another process could create the name and answer as the broker.</para>
/// <para>The caller check runs before anything is read, so a refused caller costs a
/// connection and nothing else.</para>
/// </remarks>
public sealed class BrokerPipeServer : BackgroundService
{
    /// <summary>Callers served at once.</summary>
    /// <remarks>
    /// A firewall or Defender read holds an instance for a whole PowerShell run, and a
    /// page asks several at once. Four was held by two open pages, and the next caller's
    /// connect timed out (<c>T412</c>); eight covers a backup, a page per server and the
    /// orphan checks, with the reads shared (<see cref="SharedRead{TKey, TValue}"/>).
    /// </remarks>
    public const int Instances = 8;

    private readonly IBrokerPipeFactory _pipes;
    private readonly IBrokerCallerCheck _callers;
    private readonly BrokerDispatcher _dispatcher;
    private readonly ILogger<BrokerPipeServer> _log;

    public BrokerPipeServer(
        IBrokerPipeFactory pipes, IBrokerCallerCheck callers, BrokerDispatcher dispatcher, ILogger<BrokerPipeServer> log)
    {
        _pipes = pipes;
        _callers = callers;
        _dispatcher = dispatcher;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first instance fails if the name is taken, and the service stops with
        // it: a broker that cannot own its pipe must not serve one somebody else made.
        var pipes = new List<NamedPipeServerStream> { _pipes.Create(firstInstance: true) };

        try
        {
            for (var i = 1; i < Instances; i++)
            {
                pipes.Add(_pipes.Create(firstInstance: false));
            }

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
                // A client that connected and left before the wait returned. The
                // pause keeps an instance that stays broken from spinning.
                _log.LogDebug(ex, "Broker pipe connection dropped before it was accepted");
                Disconnect(pipe);
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ContinueWith(_ => { }, TaskScheduler.Default);
                continue;
            }

            try
            {
                if (_callers.Refuse(pipe) is { } refused)
                {
                    _log.LogWarning("Broker caller refused: {Why}", refused);
                    continue;
                }

                await BrokerConnection.ServeAsync(pipe, _dispatcher, _log, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Broker connection failed");
            }
            finally
            {
                Disconnect(pipe);
            }
        }
    }

    /// <remarks>
    /// Whether or not it reads as connected. A caller that leaves while the broker works
    /// breaks the pipe under the answer, and a broken instance reads as not connected but
    /// still holds the old connection until it is disconnected; skipped, the instance never
    /// accepted anyone again, and once each instance had lost a caller the broker answered
    /// nobody (<c>T412</c>). One never connected throws here, which is ignored.
    /// </remarks>
    private void Disconnect(NamedPipeServerStream pipe)
    {
        try
        {
            pipe.Disconnect();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _log.LogDebug(ex, "Broker pipe disconnect failed");
        }
    }
}
