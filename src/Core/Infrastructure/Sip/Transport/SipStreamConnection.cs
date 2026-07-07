using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

/// <summary>
/// Represents one stateful SIP TCP/TLS stream connection with
/// framed message receive loop and serialized send path.
/// </summary>
internal sealed class SipStreamConnection : IDisposable, IAsyncDisposable
{
    private static readonly byte[] KeepalivePong = [(byte)'\r', (byte)'\n'];
    private readonly TcpClient _client;
    private readonly Stream _stream;
    private readonly ILogger _logger;
    private readonly Func<IPEndPoint, SipTransportProtocol, ReadOnlyMemory<byte>, Task> _onFrameAsync;
    private readonly Action _onClosed;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SipWireStreamFramer _framer = new();
    private readonly Task _receiveLoop;
    private int _disposed;

    /// <summary>
    /// Creates a stream connection and starts message receive loop.
    /// </summary>
    public SipStreamConnection(
        SipTransportProtocol protocol,
        TcpClient client,
        Stream stream,
        ILogger logger,
        Func<IPEndPoint, SipTransportProtocol, ReadOnlyMemory<byte>, Task> onFrameAsync,
        Action onClosed)
    {
        Protocol = protocol;
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _onFrameAsync = onFrameAsync ?? throw new ArgumentNullException(nameof(onFrameAsync));
        _onClosed = onClosed ?? throw new ArgumentNullException(nameof(onClosed));

        RemoteEndPoint = (IPEndPoint)(_client.Client.RemoteEndPoint
            ?? throw new InvalidOperationException("Stream connection remote endpoint is unavailable."));

        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_stop.Token));
    }

    /// <summary>
    /// Remote endpoint for the stream connection.
    /// </summary>
    public IPEndPoint RemoteEndPoint { get; }

    /// <summary>
    /// SIP transport protocol of this connection.
    /// </summary>
    public SipTransportProtocol Protocol { get; }

    /// <summary>
    /// Sends one framed SIP message on this stream connection.
    /// </summary>
    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length == 0)
            return;

        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(payload, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Continuously reads bytes from stream and emits framed SIP messages.
    /// </summary>
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await _stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read <= 0)
                    break;

                _framer.Append(buffer.AsSpan(0, read));
                var framesDispatched = 0;
                while (_framer.TryReadFrame(out var frame))
                {
                    framesDispatched++;
                    await _onFrameAsync(RemoteEndPoint, Protocol, frame).ConfigureAwait(false);
                }

                // RFC 5626 §4.4.1: respond to a double-CRLF keepalive ping with a single CRLF pong.
                // Only pong when no SIP message was produced — a pure keepalive datagram.
                if (framesDispatched == 0 && _framer.ConsumedKeepalivePing)
                {
                    _framer.ClearKeepalivePingFlag();
                    await SendAsync(KeepalivePong, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected shutdown path
        }
        catch (ObjectDisposedException)
        {
            // expected shutdown race
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "SIP stream connection I/O failure for {Remote}.", RemoteEndPoint);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SIP stream connection receive loop failed for {Remote}.", RemoteEndPoint);
        }
        finally
        {
            _onClosed();
        }
    }

    /// <summary>
    /// Signals shutdown once by cancelling the receive loop.
    /// </summary>
    /// <returns><c>true</c> when this call transitioned the connection into the disposed state.</returns>
    private bool BeginShutdown()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return false;

        _stop.Cancel();
        return true;
    }

    /// <summary>
    /// Disposes socket, stream, and synchronization resources.
    /// </summary>
    private void ReleaseResources()
    {
        try
        {
            _stream.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed disposing SIP stream.");
        }

        try
        {
            _client.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed disposing SIP TCP client.");
        }

        _sendGate.Dispose();
        _stop.Dispose();
    }

    /// <summary>
    /// Synchronously cancels the receive loop and disposes socket and stream resources.
    /// </summary>
    /// <remarks>
    /// The receive loop is only cancelled here, never awaited: blocking on it inside a
    /// synchronous <see cref="IDisposable.Dispose"/> can deadlock when a
    /// <see cref="SynchronizationContext"/> is present. Disposing the underlying stream
    /// aborts any in-flight read so the loop unwinds promptly in the background. Use
    /// <see cref="DisposeAsync"/> to deterministically await loop completion.
    /// </remarks>
    public void Dispose()
    {
        if (!BeginShutdown())
            return;

        ReleaseResources();
    }

    /// <summary>
    /// Asynchronously cancels the receive loop, awaits its completion, and releases resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!BeginShutdown())
            return;

        try
        {
            await _receiveLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: the receive loop observes shutdown cancellation.
        }

        ReleaseResources();
    }
}
