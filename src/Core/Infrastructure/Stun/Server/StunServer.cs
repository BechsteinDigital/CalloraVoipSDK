using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Server;

/// <summary>
/// Standalone STUN server supporting UDP, TCP, and TLS transports.
/// Incoming requests are dispatched to an <see cref="IStunRequestHandler"/>.
/// <para>
/// When <paramref name="responseIntegrityKey"/> is provided, every outbound response
/// is protected with MESSAGE-INTEGRITY and FINGERPRINT (RFC 5389 §10).
/// </para>
/// <para>
/// Non-STUN packets (wrong magic cookie) are silently discarded.
/// Malformed packets and handler failures are logged and dropped.
/// </para>
/// </summary>
internal sealed class StunServer : IAsyncDisposable
{
    private readonly StunServerTransport _transport;
    private readonly IStunMessageCodec _codec;
    private readonly ILogger<StunServer> _logger;
    private readonly StunServerOptions _options;
    private readonly byte[]? _responseIntegrityKey;
    private readonly X509Certificate2? _tlsServerCertificate;
    private readonly UdpClient? _udp;
    private readonly TcpListener? _tcpListener;
    private readonly SemaphoreSlim? _streamConnectionSlots;
    private readonly SemaphoreSlim? _udpPacketSlots;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<int, Task> _connectionTasks = new();
    private int _nextConnectionTaskId;
    private Task? _receiveLoop;

    /// <summary>The local endpoint this server is bound to.</summary>
    public IPEndPoint LocalEndPoint { get; }

    /// <summary>
    /// Binds a UDP STUN server to <paramref name="bindEndPoint"/>.
    /// Call <see cref="Start"/> to begin processing requests.
    /// </summary>
    /// <param name="bindEndPoint">
    /// Local endpoint to listen on. Use port 0 to let the OS assign a free port.
    /// </param>
    /// <param name="codec">STUN message codec.</param>
    /// <param name="responseIntegrityKey">
    /// Optional HMAC key to protect responses. Derive with <see cref="StunKeyDerivation"/>.
    /// When null, responses are sent without MESSAGE-INTEGRITY or FINGERPRINT.
    /// </param>
    /// <param name="logger">Logger instance.</param>
    public StunServer(
        IPEndPoint bindEndPoint,
        IStunMessageCodec codec,
        byte[]? responseIntegrityKey,
        ILogger<StunServer> logger,
        StunServerOptions? options = null)
        : this(
            bindEndPoint,
            StunServerTransport.Udp,
            codec,
            responseIntegrityKey,
            tlsServerCertificate: null,
            logger,
            options)
    {
    }

    /// <summary>
    /// Binds a STUN server transport to <paramref name="bindEndPoint"/>.
    /// Call <see cref="Start"/> to begin processing requests.
    /// </summary>
    /// <param name="bindEndPoint">
    /// Local endpoint to listen on. Use port 0 to let the OS assign a free port.
    /// </param>
    /// <param name="transport">Listener transport (UDP, TCP, or TLS).</param>
    /// <param name="codec">STUN message codec.</param>
    /// <param name="responseIntegrityKey">
    /// Optional HMAC key to protect responses. Derive with <see cref="StunKeyDerivation"/>.
    /// When null, responses are sent without MESSAGE-INTEGRITY or FINGERPRINT.
    /// </param>
    /// <param name="tlsServerCertificate">
    /// TLS server certificate used only when <paramref name="transport"/> is <see cref="StunServerTransport.Tls"/>.
    /// </param>
    /// <param name="logger">Logger instance.</param>
    public StunServer(
        IPEndPoint bindEndPoint,
        StunServerTransport transport,
        IStunMessageCodec codec,
        byte[]? responseIntegrityKey,
        X509Certificate2? tlsServerCertificate,
        ILogger<StunServer> logger,
        StunServerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(bindEndPoint);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(logger);

        if (transport == StunServerTransport.Tls && tlsServerCertificate is null)
            throw new ArgumentNullException(nameof(tlsServerCertificate), "TLS transport requires a server certificate.");

        _options = options ?? StunServerOptions.Default;
        if (_options.TcpListenBacklog <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "TCP listen backlog must be positive.");
        if (_options.MaxConcurrentStreamConnections < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxConcurrentStreamConnections must be >= 0.");
        if (_options.MaxConcurrentUdpPacketHandlers < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxConcurrentUdpPacketHandlers must be >= 0.");

        _transport = transport;
        _codec = codec;
        _responseIntegrityKey = responseIntegrityKey;
        _tlsServerCertificate = tlsServerCertificate;
        _logger = logger;
        _streamConnectionSlots = _options.MaxConcurrentStreamConnections > 0
            ? new SemaphoreSlim(_options.MaxConcurrentStreamConnections, _options.MaxConcurrentStreamConnections)
            : null;
        _udpPacketSlots = _options.MaxConcurrentUdpPacketHandlers > 0
            ? new SemaphoreSlim(_options.MaxConcurrentUdpPacketHandlers, _options.MaxConcurrentUdpPacketHandlers)
            : null;

        switch (transport)
        {
            case StunServerTransport.Udp:
                _udp = new UdpClient(bindEndPoint);
                LocalEndPoint = (IPEndPoint)_udp.Client.LocalEndPoint!;
                break;

            case StunServerTransport.Tcp:
            case StunServerTransport.Tls:
                _tcpListener = new TcpListener(bindEndPoint);
                _tcpListener.Start(_options.TcpListenBacklog);
                LocalEndPoint = (IPEndPoint)_tcpListener.LocalEndpoint;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(transport), transport, "Unsupported STUN server transport.");
        }
    }

    /// <summary>
    /// Starts the background receive loop, dispatching incoming packets/messages to <paramref name="handler"/>.
    /// </summary>
    public void Start(IStunRequestHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        if (_receiveLoop is not null)
            throw new InvalidOperationException("STUN server is already started.");

        _receiveLoop = _transport switch
        {
            StunServerTransport.Udp => ReceiveUdpLoopAsync(handler, _stop.Token),
            StunServerTransport.Tcp or StunServerTransport.Tls => AcceptTcpLoopAsync(handler, _stop.Token),
            _ => throw new ArgumentOutOfRangeException()
        };

        _logger.LogInformation(
            "STUN server listening on {EndPoint} via {Transport} (stream-cap={Cap}, udp-cap={UdpCap}, policy={Policy}, backlog={Backlog})",
            LocalEndPoint,
            _transport,
            _options.MaxConcurrentStreamConnections,
            _options.MaxConcurrentUdpPacketHandlers,
            _options.ConnectionCapPolicy,
            _options.TcpListenBacklog);
    }

    /// <summary>Stops the server and releases transport resources.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);

        // Stop listener early to unblock Accept() immediately.
        _tcpListener?.Stop();

        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "STUN server receive loop exited with error");
            }
        }

        var connectionTasks = _connectionTasks.Values.ToArray();
        if (connectionTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(connectionTasks).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "STUN server connection tasks ended with errors during shutdown");
            }
        }

        _udp?.Dispose();
        _tcpListener?.Stop();
        _streamConnectionSlots?.Dispose();
        _udpPacketSlots?.Dispose();
        _stop.Dispose();
    }

    // ── UDP receive loop ────────────────────────────────────────────────────

    private async Task ReceiveUdpLoopAsync(IStunRequestHandler handler, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _udp!.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "STUN server UDP receive error");
                continue;
            }

            bool slotAcquired = false;
            if (_udpPacketSlots is not null)
            {
                try
                {
                    await _udpPacketSlots.WaitAsync(ct).ConfigureAwait(false);
                    slotAcquired = true;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            var task = ProcessUdpPacketWithSlotReleaseAsync(handler, received, slotAcquired);
            TrackConnectionTask(task);
        }
    }

    private async Task ProcessUdpPacketWithSlotReleaseAsync(
        IStunRequestHandler handler,
        UdpReceiveResult received,
        bool ownsPacketSlot)
    {
        try
        {
            await ProcessUdpPacketAsync(handler, received).ConfigureAwait(false);
        }
        finally
        {
            if (ownsPacketSlot)
                _udpPacketSlots!.Release();
        }
    }

    private async Task ProcessUdpPacketAsync(IStunRequestHandler handler, UdpReceiveResult received)
    {
        if (!TryBuildResponse(handler, received.Buffer, received.RemoteEndPoint, out var responseBytes))
            return;

        try
        {
            await _udp!.SendAsync(responseBytes, received.RemoteEndPoint).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "STUN server failed to send UDP response to {Sender}", received.RemoteEndPoint);
        }
    }

    // ── TCP/TLS receive loop ────────────────────────────────────────────────

    private async Task AcceptTcpLoopAsync(IStunRequestHandler handler, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool slotAcquired = false;

            if (_streamConnectionSlots is not null
                && _options.ConnectionCapPolicy == StunConnectionCapPolicy.Backpressure)
            {
                try
                {
                    await _streamConnectionSlots.WaitAsync(ct).ConfigureAwait(false);
                    slotAcquired = true;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            TcpClient client;
            try
            {
                client = await _tcpListener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (slotAcquired)
                    _streamConnectionSlots!.Release();
                break;
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
                if (slotAcquired)
                    _streamConnectionSlots!.Release();
                break;
            }
            catch (Exception ex)
            {
                if (slotAcquired)
                    _streamConnectionSlots!.Release();
                _logger.LogError(ex, "STUN server TCP accept error");
                continue;
            }

            if (_streamConnectionSlots is not null
                && _options.ConnectionCapPolicy == StunConnectionCapPolicy.RejectNew
                && !slotAcquired)
            {
                // CA2016: Wait(0) is a non-blocking try-acquire — forwarding the accept-loop token would
                // switch it to Wait(0, ct), which throws on an already-cancelled token instead of returning
                // false, bypassing the connection-cap reject path below. The non-throwing poll is intended.
#pragma warning disable CA2016
                slotAcquired = _streamConnectionSlots.Wait(0);
#pragma warning restore CA2016
                if (!slotAcquired)
                {
                    var remote = client.Client.RemoteEndPoint;
                    _logger.LogWarning(
                        "STUN stream connection rejected due to max-connections cap ({Cap}) from {Remote}",
                        _options.MaxConcurrentStreamConnections,
                        remote);
                    client.Dispose();
                    continue;
                }
            }

            var task = ProcessTcpClientAsync(handler, client, ct, slotAcquired);
            TrackConnectionTask(task);
        }
    }

    private async Task ProcessTcpClientAsync(
        IStunRequestHandler handler,
        TcpClient client,
        CancellationToken ct,
        bool ownsConnectionSlot)
    {
        try
        {
            using (client)
            {
                var remote = client.Client.RemoteEndPoint as IPEndPoint;
                if (remote is null)
                    return;

                await using var networkStream = client.GetStream();

                if (_transport == StunServerTransport.Tls)
                {
                    await using var tlsStream = new SslStream(networkStream, leaveInnerStreamOpen: true);
                    try
                    {
                        await tlsStream.AuthenticateAsServerAsync(
                                new SslServerAuthenticationOptions
                                {
                                    ServerCertificate = _tlsServerCertificate!,
                                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                                },
                                ct)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "STUN TLS handshake failed for {Sender}", remote);
                        return;
                    }

                    await ProcessStreamLoopAsync(handler, tlsStream, remote, ct).ConfigureAwait(false);
                    return;
                }

                await ProcessStreamLoopAsync(handler, networkStream, remote, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (ownsConnectionSlot)
                _streamConnectionSlots!.Release();
        }
    }

    private async Task ProcessStreamLoopAsync(
        IStunRequestHandler handler,
        Stream stream,
        IPEndPoint remoteEndPoint,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            byte[]? raw;
            try
            {
                raw = await StunTcpFramer.ReadMessageAsync(stream, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (InvalidDataException ex)
            {
                _logger.LogWarning(ex, "STUN stream framing error from {Sender}", remoteEndPoint);
                break;
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "STUN stream I/O error from {Sender}", remoteEndPoint);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "STUN stream receive error from {Sender}", remoteEndPoint);
                break;
            }

            if (raw is null)
                break; // Clean EOF.

            if (!TryBuildResponse(handler, raw, remoteEndPoint, out var responseBytes))
                continue;

            try
            {
                await stream.WriteAsync(responseBytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "STUN server failed to send stream response to {Sender}", remoteEndPoint);
                break;
            }
        }
    }

    // ── Shared packet processing ────────────────────────────────────────────

    private bool TryBuildResponse(
        IStunRequestHandler handler,
        byte[] rawRequest,
        IPEndPoint remoteEndPoint,
        out byte[] responseBytes)
    {
        responseBytes = Array.Empty<byte>();

        if (!_codec.IsStunPacket(rawRequest))
            return false; // Silently discard non-STUN (e.g. RTP multiplexed on same port).

        var request = _codec.Decode(rawRequest);
        if (request is null)
        {
            _logger.LogWarning("Received malformed STUN packet from {Sender}", remoteEndPoint);
            return false;
        }

        StunRequestHandlingResult? handlingResult;
        try
        {
            handlingResult = handler.Handle(request, rawRequest.AsSpan(), remoteEndPoint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "STUN request handler threw for {Sender}", remoteEndPoint);
            return false;
        }

        if (handlingResult is null)
            return false;

        try
        {
            var integrityKey = handlingResult.ResponseIntegrityKey ?? _responseIntegrityKey;
            responseBytes = integrityKey is not null
                ? _codec.EncodeWithIntegrity(handlingResult.Response, integrityKey)
                : _codec.Encode(handlingResult.Response);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "STUN server failed to encode response for {Sender}", remoteEndPoint);
            return false;
        }
    }

    private void TrackConnectionTask(Task task)
    {
        var taskId = Interlocked.Increment(ref _nextConnectionTaskId);
        _connectionTasks[taskId] = task;

        _ = task.ContinueWith(
            _ =>
            {
                _connectionTasks.TryRemove(taskId, out Task? _);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
