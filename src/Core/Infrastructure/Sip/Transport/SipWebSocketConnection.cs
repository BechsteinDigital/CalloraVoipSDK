using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

/// <summary>
/// Represents one SIP-over-WebSocket connection with message receive loop and serialized sends.
/// </summary>
internal sealed class SipWebSocketConnection : IDisposable
{
    private readonly WebSocket _socket;
    private readonly ILogger _logger;
    private readonly Func<IPEndPoint, SipTransportProtocol, ReadOnlyMemory<byte>, Task> _onFrameAsync;
    private readonly Action _onClosed;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Task _receiveLoop;
    private readonly TimeSpan _readTimeout;
    private int _disposed;

    private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromMinutes(5);

    // Cap the aggregated WS message at the size the TCP framer allows (RFC 3261 header + body), so a
    // fragmented message can never grow the receive buffer without limit (memory DoS).
    private const int MaxMessageBytes = SipWireStreamFramer.DefaultMaxHeaderBytes + SipWireStreamFramer.DefaultMaxBodyBytes;

    /// <summary>
    /// Creates one WebSocket connection wrapper and starts receive loop.
    /// </summary>
    /// <param name="readTimeout">
    /// Idle timeout for a single receive: a peer that sends nothing (or stalls mid-message) within this
    /// window has the connection torn down. Defaults to 5 minutes, well above any RFC 5626 keepalive
    /// interval so a healthy connection is never reaped.
    /// </param>
    public SipWebSocketConnection(
        SipTransportProtocol protocol,
        WebSocket socket,
        IPEndPoint remoteEndPoint,
        ILogger logger,
        Func<IPEndPoint, SipTransportProtocol, ReadOnlyMemory<byte>, Task> onFrameAsync,
        Action onClosed,
        TimeSpan? readTimeout = null)
    {
        Protocol = protocol;
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        RemoteEndPoint = remoteEndPoint ?? throw new ArgumentNullException(nameof(remoteEndPoint));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _onFrameAsync = onFrameAsync ?? throw new ArgumentNullException(nameof(onFrameAsync));
        _onClosed = onClosed ?? throw new ArgumentNullException(nameof(onClosed));
        _readTimeout = readTimeout ?? DefaultReadTimeout;

        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_stop.Token));
    }

    /// <summary>
    /// Remote endpoint associated with this WebSocket transport.
    /// </summary>
    public IPEndPoint RemoteEndPoint { get; }

    /// <summary>
    /// Effective SIP transport protocol (WS or WSS).
    /// </summary>
    public SipTransportProtocol Protocol { get; }

    /// <summary>
    /// Sends one SIP message as WebSocket text frame.
    /// </summary>
    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length == 0)
            return;

        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
                throw new IOException("WebSocket is not open.");

            await _socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Receive loop reading complete WS messages and forwarding them as SIP payloads.
    /// </summary>
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var aggregate = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && _socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                WebSocketReceiveResult result;
                using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    readCts.CancelAfter(_readTimeout);
                    try
                    {
                        result = await _socket.ReceiveAsync(buffer, readCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Idle/stalled peer: no data within the read window — tear the connection down.
                        _logger.LogDebug("SIP WebSocket idle for {Timeout} from {Remote}; closing.", _readTimeout, RemoteEndPoint);
                        break;
                    }
                }

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.Count > 0)
                {
                    if (aggregate.Length + result.Count > MaxMessageBytes)
                    {
                        _logger.LogWarning(
                            "SIP WebSocket message from {Remote} exceeds {Max} bytes; closing connection.",
                            RemoteEndPoint, MaxMessageBytes);
                        break;
                    }

                    await aggregate.WriteAsync(buffer.AsMemory(0, result.Count), ct).ConfigureAwait(false);
                }

                if (!result.EndOfMessage)
                    continue;

                var payload = aggregate.ToArray();
                aggregate.SetLength(0);
                if (payload.Length == 0)
                    continue;

                await _onFrameAsync(RemoteEndPoint, Protocol, payload).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on dispose
        }
        catch (ObjectDisposedException)
        {
            // expected on dispose
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SIP WebSocket receive loop failed for {Remote}.", RemoteEndPoint);
        }
        finally
        {
            aggregate.Dispose();
            _onClosed();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _stop.Cancel();
        try
        {
            _receiveLoop.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SIP WebSocket loop ended with exception during disposal.");
        }

        try
        {
            _socket.Abort();
            _socket.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SIP WebSocket dispose failed for {Remote}.", RemoteEndPoint);
        }

        _sendGate.Dispose();
        _stop.Dispose();
    }
}

