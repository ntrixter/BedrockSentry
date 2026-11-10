using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using BedrockSentry.Core;
using BedrockSentry.Core.Broker;

namespace BedrockSentry.Services.Broker;

/// <summary>How the main service reaches the broker.</summary>
public interface IBrokerChannel
{
    /// <summary>
    /// Sends one request. Never throws for a broker that is down or refuses the
    /// connection: that is an answer with <see cref="BrokerResponse.Succeeded"/> false.
    /// </summary>
    Task<BrokerResponse> SendAsync(BrokerRequest request, CancellationToken ct = default);
}

/// <summary>The broker in this process: the suite's channel, and a test host's.</summary>
public sealed class InProcessBrokerChannel(BrokerDispatcher dispatcher) : IBrokerChannel
{
    public Task<BrokerResponse> SendAsync(BrokerRequest request, CancellationToken ct = default) =>
        dispatcher.DispatchAsync(request, ct);
}

/// <summary>
/// Length-prefixed JSON over a stream, one message at a time, the same on both ends.
/// </summary>
public static class BrokerWire
{
    /// <summary>
    /// The largest message either side reads. A Defender exclusion list is the
    /// biggest answer and is a few kilobytes; the bound is so a peer cannot make the
    /// other allocate whatever it names.
    /// </summary>
    public const int MaxMessageBytes = 1 << 20;

    /// <param name="json">The contract's options; the broker's when omitted.</param>
    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken ct, JsonSerializerOptions? json = null)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, json ?? BrokerContract.Json);

        if (body.Length > MaxMessageBytes)
        {
            throw new InvalidDataException($"A broker message of {body.Length} bytes is over the limit.");
        }

        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, body.Length);

        await stream.WriteAsync(length, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    /// <returns>The message, or null when the peer closed the stream first.</returns>
    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken ct, JsonSerializerOptions? json = null)
        where T : class
    {
        var length = new byte[4];

        if (!await ReadExactlyOrEndAsync(stream, length, ct))
        {
            return null;
        }

        var size = BinaryPrimitives.ReadInt32LittleEndian(length);

        if (size is <= 0 or > MaxMessageBytes)
        {
            throw new InvalidDataException($"A broker message claims {size} bytes.");
        }

        var body = new byte[size];

        if (!await ReadExactlyOrEndAsync(stream, body, ct))
        {
            return null;
        }

        return JsonSerializer.Deserialize<T>(body, json ?? BrokerContract.Json);
    }

    private static async Task<bool> ReadExactlyOrEndAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, ct);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }
}

/// <summary>Decides whether the process answering on the broker's pipe is the broker.</summary>
/// <remarks>
/// The mirror of <see cref="IBrokerCallerCheck"/>. A process carrying the main service's
/// SID, Bedrock included, could create the pipe's name before the broker starts; the
/// broker then refuses to start, and without this the main service would take that
/// process's answers, a shadow copy's device path among them, as the broker's.
/// </remarks>
public interface IBrokerServerCheck
{
    /// <summary>Null to trust the server end, or why not.</summary>
    string? Refuse(NamedPipeClientStream pipe);
}

/// <summary>The broker over its named pipe.</summary>
/// <remarks>
/// <para>One connection per request. Requests are rare (a button, a backup, an update)
/// and a connection that lives across them is a connection to keep authorised.</para>
/// <para>The client connects at <see cref="TokenImpersonationLevel.Identification"/>,
/// so whatever answers on the pipe's name can learn who called but never act as them.</para>
/// </remarks>
public sealed class PipeBrokerChannel : IBrokerChannel
{
    private readonly string _pipeName;
    private readonly IBrokerServerCheck _servers;
    private readonly TimeSpan _connectTimeout;

    public PipeBrokerChannel(string pipeName, IBrokerServerCheck servers, TimeSpan? connectTimeout = null)
    {
        _pipeName = pipeName;
        _servers = servers;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
    }

    public async Task<BrokerResponse> SendAsync(BrokerRequest request, CancellationToken ct = default)
    {
        await using var pipe = new NamedPipeClientStream(
            ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);

        try
        {
            await pipe.ConnectAsync(_connectTimeout, ct);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return BrokerResponse.Unreachable($"The broker service is not answering ({ex.Message}).");
        }

        // Before a byte is sent: nothing reaches, or is read from, a server that is not the broker.
        if (_servers.Refuse(pipe) is { } impostor)
        {
            return BrokerResponse.Unreachable($"The broker's pipe is held by another process: {impostor}.");
        }

        try
        {
            await BrokerWire.WriteAsync(pipe, new BrokerHello(BrokerContract.Version, BuildInfo.Identity), ct);

            var hello = await BrokerWire.ReadAsync<BrokerHello>(pipe, ct);

            if (BrokerContract.Refuse(hello) is { } mismatch)
            {
                return BrokerResponse.Unreachable(mismatch);
            }

            await BrokerWire.WriteAsync(pipe, request, ct);

            return await BrokerWire.ReadAsync<BrokerResponse>(pipe, ct)
                ?? BrokerResponse.Unreachable("The broker closed the connection without answering.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            return BrokerResponse.Unreachable($"The broker connection failed: {ex.Message}");
        }
    }
}
