using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Server;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Turn.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Server;

/// <summary>
/// Standalone TURN server with isolated protocol module boundaries.
/// <para>
/// Supported TURN transactions: Allocate, Refresh, CreatePermission, ChannelBind, Send Indication.
/// Supported server-side relay flow: peer → Data Indication/ChannelData to client.
/// </para>
/// </summary>
internal sealed class TurnServer : IAsyncDisposable
{
    private readonly TurnServerTransport _transport;
    private readonly IStunMessageCodec _codec;
    private readonly ILogger<TurnServer> _logger;
    private readonly TurnServerOptions _options;
    private readonly TurnAuthOptions? _authOptions;
    private readonly TurnServerResponseFactory _responseFactory;
    private readonly TurnServerRequestAuthenticator _requestAuthenticator;
    private readonly TurnMobilityService _mobilityService = new();
    private readonly TurnTcpConnectionBroker _tcpConnectionBroker = new();
    private readonly TurnTcpExtensionHandler _tcpExtensionHandler;
    private readonly TurnTcpPassiveConnectionService _tcpPassiveConnectionService;
    private readonly TurnAllocateRequestHandler _allocateRequestHandler;
    private readonly X509Certificate2? _tlsServerCertificate;
    private readonly UdpClient? _udp;
    private readonly TcpListener? _tcpListener;
    private readonly SemaphoreSlim? _streamConnectionSlots;
    private readonly SemaphoreSlim? _udpPacketSlots;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<int, Task> _connectionTasks = new();
    private int _nextConnectionTaskId;
    private readonly TurnAllocationRegistry _registry;
    private readonly TurnRefreshRequestHandler _refreshHandler;
    private readonly TurnPermissionRequestHandler _permissionHandler;
    private Task? _receiveLoop;
    private Task? _sweepLoop;

    /// <summary>
    /// Local endpoint the TURN server listens on.
    /// </summary>
    public IPEndPoint LocalEndPoint { get; }

    /// <summary>
    /// Creates a TURN server for the chosen transport.
    /// </summary>
    public TurnServer(
        IPEndPoint bindEndPoint,
        TurnServerTransport transport,
        IStunMessageCodec codec,
        ILogger<TurnServer> logger,
        TurnAuthOptions? authOptions = null,
        X509Certificate2? tlsServerCertificate = null,
        TurnServerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(bindEndPoint);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(logger);

        if (transport == TurnServerTransport.Tls && tlsServerCertificate is null)
            throw new ArgumentNullException(nameof(tlsServerCertificate), "TLS transport requires a server certificate.");

        _options = options ?? TurnServerOptions.Default;
        if (_options.TcpListenBacklog <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "TCP listen backlog must be positive.");
        if (_options.MaxConcurrentStreamConnections < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxConcurrentStreamConnections must be >= 0.");
        if (_options.MaxConcurrentUdpPacketHandlers < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxConcurrentUdpPacketHandlers must be >= 0.");
        if (_options.DefaultAllocationLifetimeSeconds > _options.MaxAllocationLifetimeSeconds)
            throw new ArgumentOutOfRangeException(nameof(options), "Default allocation lifetime cannot exceed max allocation lifetime.");
        if (_options.MaxPermissionsPerAllocation < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxPermissionsPerAllocation must be >= 0.");
        if (_options.MaxChannelBindingsPerAllocation < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxChannelBindingsPerAllocation must be >= 0.");
        if (_options.MaxTotalAllocations < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxTotalAllocations must be >= 0.");
        if (_options.RequireAuthentication && authOptions is null)
            throw new ArgumentNullException(nameof(authOptions), "TURN authentication is required but auth options are missing.");

        _transport = transport;
        _codec = codec;
        _logger = logger;
        _authOptions = authOptions;
        _responseFactory = new TurnServerResponseFactory(_authOptions);
        _requestAuthenticator = new TurnServerRequestAuthenticator(_options, _authOptions, _codec, _responseFactory);
        _tcpPassiveConnectionService = new TurnTcpPassiveConnectionService(_codec, _tcpConnectionBroker, _logger);
        _registry = new TurnAllocationRegistry(
            _options,
            _logger,
            _mobilityService,
            _tcpConnectionBroker,
            StartRelayTask);
        _refreshHandler = new TurnRefreshRequestHandler(_options, _responseFactory, _mobilityService, _registry);
        _permissionHandler = new TurnPermissionRequestHandler(_options, _responseFactory, _registry);
        _allocateRequestHandler = new TurnAllocateRequestHandler(
            _options,
            _responseFactory,
            _mobilityService,
            _registry.ReplaceAsync,
            _registry.HasCapacity,
            _logger);
        _tcpExtensionHandler = new TurnTcpExtensionHandler(
            _registry.Table,
            _responseFactory,
            _tcpConnectionBroker,
            key => _registry.TryGetLive(key, out var allocation) ? allocation : null,
            _logger);
        _tlsServerCertificate = tlsServerCertificate;
        _streamConnectionSlots = _options.MaxConcurrentStreamConnections > 0
            ? new SemaphoreSlim(_options.MaxConcurrentStreamConnections, _options.MaxConcurrentStreamConnections)
            : null;
        _udpPacketSlots = _options.MaxConcurrentUdpPacketHandlers > 0
            ? new SemaphoreSlim(_options.MaxConcurrentUdpPacketHandlers, _options.MaxConcurrentUdpPacketHandlers)
            : null;

        switch (transport)
        {
            case TurnServerTransport.Udp:
                _udp = new UdpClient(bindEndPoint);
                LocalEndPoint = (IPEndPoint)_udp.Client.LocalEndPoint!;
                break;

            case TurnServerTransport.Tcp:
            case TurnServerTransport.Tls:
                _tcpListener = new TcpListener(bindEndPoint);
                _tcpListener.Start(_options.TcpListenBacklog);
                LocalEndPoint = (IPEndPoint)_tcpListener.LocalEndpoint;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(transport), transport, "Unsupported TURN server transport.");
        }
    }

    /// <summary>
    /// Starts background receive loops.
    /// </summary>
    public void Start()
    {
        if (_receiveLoop is not null)
            throw new InvalidOperationException("TURN server is already started.");

        _receiveLoop = _transport switch
        {
            TurnServerTransport.Udp => ReceiveUdpLoopAsync(_stop.Token),
            TurnServerTransport.Tcp or TurnServerTransport.Tls => AcceptStreamLoopAsync(_stop.Token),
            _ => throw new ArgumentOutOfRangeException()
        };

        _sweepLoop = _registry.RunSweepAsync(_stop.Token);

        _logger.LogInformation(
            "TURN server listening on {EndPoint} via {Transport} (stream-cap={Cap}, udp-cap={UdpCap}, policy={Policy}, backlog={Backlog})",
            LocalEndPoint,
            _transport,
            _options.MaxConcurrentStreamConnections,
            _options.MaxConcurrentUdpPacketHandlers,
            _options.ConnectionCapPolicy,
            _options.TcpListenBacklog);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
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
                _logger.LogError(ex, "TURN receive loop exited with error");
            }
        }

        if (_sweepLoop is not null)
        {
            try
            {
                await _sweepLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TURN allocation sweep loop exited with error");
            }
        }

        await _registry.DisposeAllAsync().ConfigureAwait(false);

        var connectionTasks = _connectionTasks.Values.ToArray();
        if (connectionTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(connectionTasks).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "TURN connection tasks completed with errors during shutdown");
            }
        }

        _udp?.Dispose();
        _tcpListener?.Stop();
        await _tcpConnectionBroker.DisposeAsync().ConfigureAwait(false);
        _streamConnectionSlots?.Dispose();
        _udpPacketSlots?.Dispose();
        _registry.Dispose();
        _stop.Dispose();
    }

    // ── UDP listener ────────────────────────────────────────────────────────

    private async Task ReceiveUdpLoopAsync(CancellationToken ct)
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
                _logger.LogError(ex, "TURN UDP receive error");
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

            var task = ProcessUdpDatagramWithSlotReleaseAsync(received.Buffer, received.RemoteEndPoint, ct, slotAcquired);
            TrackConnectionTask(task);
        }
    }

    private async Task ProcessUdpDatagramWithSlotReleaseAsync(
        byte[] datagram,
        IPEndPoint remoteEndPoint,
        CancellationToken ct,
        bool ownsPacketSlot)
    {
        try
        {
            await ProcessUdpDatagramAsync(datagram, remoteEndPoint, ct).ConfigureAwait(false);
        }
        finally
        {
            if (ownsPacketSlot)
                _udpPacketSlots!.Release();
        }
    }

    private async Task ProcessUdpDatagramAsync(byte[] datagram, IPEndPoint remoteEndPoint, CancellationToken ct)
    {
        var context = new TurnClientContext(
            TurnServerTransport.Udp,
            remoteEndPoint,
            StreamConnection: null);

        if (_codec.IsStunPacket(datagram))
        {
            await ProcessStunPacketAsync(datagram, context, ct).ConfigureAwait(false);
            return;
        }

        if (TurnChannelDataCodec.TryParse(datagram, out var channelNumber, out var data))
        {
            await HandleChannelDataFromClientAsync(context, channelNumber, data, ct).ConfigureAwait(false);
            return;
        }
    }

    // ── TCP/TLS listener ────────────────────────────────────────────────────

    private async Task AcceptStreamLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool slotAcquired = false;
            if (_streamConnectionSlots is not null
                && _options.ConnectionCapPolicy == TurnConnectionCapPolicy.Backpressure)
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
                _logger.LogError(ex, "TURN stream accept error");
                continue;
            }

            if (_streamConnectionSlots is not null
                && _options.ConnectionCapPolicy == TurnConnectionCapPolicy.RejectNew
                && !slotAcquired)
            {
                slotAcquired = _streamConnectionSlots.Wait(0);
                if (!slotAcquired)
                {
                    var remote = client.Client.RemoteEndPoint;
                    _logger.LogWarning(
                        "TURN stream connection rejected due to cap ({Cap}) from {Remote}",
                        _options.MaxConcurrentStreamConnections,
                        remote);
                    client.Dispose();
                    continue;
                }
            }

            var task = ProcessStreamClientAsync(client, slotAcquired, ct);
            TrackConnectionTask(task);
        }
    }

    private async Task ProcessStreamClientAsync(TcpClient client, bool ownsConnectionSlot, CancellationToken ct)
    {
        TurnStreamConnection? connection = null;

        try
        {
            using (client)
            {
                var remote = client.Client.RemoteEndPoint as IPEndPoint;
                if (remote is null)
                    return;

                await using var networkStream = client.GetStream();
                Stream stream = networkStream;

                if (_transport == TurnServerTransport.Tls)
                {
                    var tlsStream = new SslStream(networkStream, leaveInnerStreamOpen: true);
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
                        _logger.LogError(ex, "TURN TLS handshake failed for {Sender}", remote);
                        return;
                    }

                    stream = tlsStream;
                }

                connection = new TurnStreamConnection
                {
                    RemoteEndPoint = remote,
                    Transport = _transport,
                    Stream = stream
                };

                var context = new TurnClientContext(_transport, remote, connection);
                await ProcessStreamLoopAsync(context, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (connection is not null)
            {
                await _registry.RemoveAsync(connection.ClientKey()).ConfigureAwait(false);
                _tcpConnectionBroker.RemoveByClientStream(connection.Id);
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            if (ownsConnectionSlot)
                _streamConnectionSlots!.Release();
        }
    }

    private async Task ProcessStreamLoopAsync(TurnClientContext context, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (context.StreamConnection is not null
                && await _tcpConnectionBroker.TryRunBoundRelayAsync(context.StreamConnection, _logger, ct).ConfigureAwait(false))
            {
                break;
            }

            TurnStreamFrame? frame;
            try
            {
                frame = await TurnStreamFramer.ReadFrameAsync(context.StreamConnection!.Stream, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "TURN stream I/O error from {Sender}", context.RemoteEndPoint);
                break;
            }
            catch (InvalidDataException ex)
            {
                _logger.LogWarning(ex, "TURN stream framing error from {Sender}", context.RemoteEndPoint);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TURN stream receive error from {Sender}", context.RemoteEndPoint);
                break;
            }

            if (frame is null)
                break;

            if (frame.IsChannelData)
            {
                await HandleChannelDataFromClientAsync(context, frame.ChannelNumber, frame.Payload, ct).ConfigureAwait(false);
                continue;
            }

            await ProcessStunPacketAsync(frame.Payload, context, ct).ConfigureAwait(false);
        }
    }

    // ── STUN/TURN packet processing ─────────────────────────────────────────

    private async Task ProcessStunPacketAsync(byte[] rawPacket, TurnClientContext context, CancellationToken ct)
    {
        var message = _codec.Decode(rawPacket);
        if (message is null)
        {
            _logger.LogWarning("TURN received malformed STUN packet from {Sender}", context.RemoteEndPoint);
            return;
        }

        switch (message.MessageClass)
        {
            case StunMessageClass.Request:
                await HandleTurnRequestAsync(message, rawPacket, context, ct).ConfigureAwait(false);
                break;

            case StunMessageClass.Indication:
                await HandleTurnIndicationAsync(message, rawPacket, context, ct).ConfigureAwait(false);
                break;

            default:
                break;
        }
    }

    private async Task HandleTurnRequestAsync(
        StunMessage request,
        byte[] rawRequest,
        TurnClientContext context,
        CancellationToken ct)
    {
        if (!_requestAuthenticator.TryAuthenticateRequest(request, rawRequest, out var authenticatedCredentials, out var challenge))
        {
            if (challenge is not null)
                await SendResponseAsync(context, challenge, integrityKey: null, ct).ConfigureAwait(false);
            return;
        }

        var method = (TurnMessageMethod)(ushort)request.MessageMethod;
        StunMessage response = method switch
        {
            TurnMessageMethod.Allocate => await _allocateRequestHandler.HandleAsync(request, context, authenticatedCredentials, ct)
                .ConfigureAwait(false),
            TurnMessageMethod.Refresh => await _refreshHandler.HandleAsync(request, context).ConfigureAwait(false),
            TurnMessageMethod.CreatePermission => _permissionHandler.HandleCreatePermission(request, context),
            TurnMessageMethod.ChannelBind => _permissionHandler.HandleChannelBind(request, context),
            TurnMessageMethod.Connect => await _tcpExtensionHandler.HandleConnectRequestAsync(request, context, ct).ConfigureAwait(false),
            TurnMessageMethod.ConnectionBind => _tcpExtensionHandler.HandleConnectionBindRequest(request, context),
            _ => _responseFactory.BuildErrorResponse(request, 400, "Bad Request", includeAuthAttributes: false)
        };

        var responseIntegrityKey = authenticatedCredentials?.DeriveHmacKey();
        await SendResponseAsync(context, response, responseIntegrityKey, ct).ConfigureAwait(false);
    }

    private async Task HandleTurnIndicationAsync(
        StunMessage indication,
        byte[] rawIndication,
        TurnClientContext context,
        CancellationToken ct)
    {
        if (!_requestAuthenticator.IsAuthenticatedIndication(indication, rawIndication))
            return;

        var method = (TurnMessageMethod)(ushort)indication.MessageMethod;
        if (method != TurnMessageMethod.Send)
            return;

        if (!_registry.TryGetLive(context.ClientKey, out var allocation))
            return;

        var peer = TurnAttributeMapper.DecodeXorPeerAddress(indication)?.EndPoint;
        var data = TurnAttributeMapper.DecodeData(indication)?.Value;
        if (peer is null || data is null)
            return;

        if (!IsPeerFamilyMatchingAllocation(allocation!, peer))
            return;

        var now = DateTimeOffset.UtcNow;
        if (!allocation!.HasValidPermission(peer, now))
            return;

        if (allocation.RelayedTransport != TurnRequestedTransportProtocol.Udp || allocation.RelaySocket is null)
            return;

        try
        {
            await allocation.RelaySocket.SendAsync(data.Value, peer, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "TURN failed to forward Send Indication payload to {Peer}", peer);
        }
    }

    private async Task HandleChannelDataFromClientAsync(
        TurnClientContext context,
        ushort channelNumber,
        byte[] payload,
        CancellationToken ct)
    {
        if (!_registry.TryGetLive(context.ClientKey, out var allocation))
            return;

        var now = DateTimeOffset.UtcNow;
        if (!allocation!.TryResolvePeerByChannel(channelNumber, now, out var peerEndPoint) || peerEndPoint is null)
            return;

        if (!allocation.HasValidPermission(peerEndPoint, now))
            return;

        if (allocation.RelayedTransport != TurnRequestedTransportProtocol.Udp || allocation.RelaySocket is null)
            return;

        try
        {
            await allocation.RelaySocket.SendAsync(payload, peerEndPoint, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "TURN failed to forward ChannelData to {Peer}", peerEndPoint);
        }
    }

    // ── Relay receive path ──────────────────────────────────────────────────

    private async Task RelayReceiveLoopAsync(TurnServerAllocation allocation, CancellationToken serverStop)
    {
        if (allocation.RelaySocket is null || allocation.RelayStop is null)
            return;

        using var linkedStop = CancellationTokenSource.CreateLinkedTokenSource(serverStop, allocation.RelayStop.Token);
        var ct = linkedStop.Token;

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await allocation.RelaySocket.ReceiveAsync(ct).ConfigureAwait(false);
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
                _logger.LogError(ex, "TURN relay receive error for client {Client}", allocation.ClientKey);
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            if (now > allocation.ExpiresAtUtc)
            {
                await _registry.RemoveAsync(allocation.ClientKey).ConfigureAwait(false);
                break;
            }

            if (!allocation.HasValidPermission(received.RemoteEndPoint, now))
                continue;

            if (allocation.TryResolveChannelByPeer(received.RemoteEndPoint, now, out var channelNumber))
            {
                var channelData = TurnChannelDataCodec.Encode(channelNumber, received.Buffer);
                await SendToClientAsync(allocation, channelData, ct).ConfigureAwait(false);
                continue;
            }

            await SendDataIndicationToClientAsync(allocation, received.RemoteEndPoint, received.Buffer, ct)
                .ConfigureAwait(false);
        }
    }

    private async Task SendDataIndicationToClientAsync(
        TurnServerAllocation allocation,
        IPEndPoint peerEndPoint,
        byte[] data,
        CancellationToken ct)
    {
        var txId = new byte[StunWireConstants.TransactionIdLength];
        RandomNumberGenerator.Fill(txId);

        var indication = new StunMessage
        {
            MessageClass = StunMessageClass.Indication,
            MessageMethod = (StunMessageMethod)(ushort)TurnMessageMethod.Data,
            TransactionId = txId,
            Attributes =
            [
                TurnAttributeMapper.Encode(new TurnXorPeerAddressAttribute { EndPoint = peerEndPoint }, txId),
                TurnAttributeMapper.Encode(new TurnDataAttribute { Value = data })
            ]
        };

        var bytes = _codec.Encode(indication);
        await SendToClientAsync(allocation, bytes, ct).ConfigureAwait(false);
    }

    private async Task SendToClientAsync(TurnServerAllocation allocation, byte[] bytes, CancellationToken ct)
    {
        try
        {
            if (allocation.ClientTransport == TurnServerTransport.Udp)
            {
                var remote = allocation.ClientUdpEndPoint;
                if (remote is null)
                    return;

                await _udp!.SendAsync(bytes, remote, ct).ConfigureAwait(false);
                return;
            }

            if (allocation.ClientStreamConnection is not null)
                await allocation.ClientStreamConnection.SendAsync(bytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "TURN failed to send data back to client {Client}", allocation.ClientKey);
        }
    }

    // ── Shared helpers ──────────────────────────────────────────────────────

    private Task StartRelayTask(TurnServerAllocation allocation, CancellationToken ct)
    {
        return allocation.RelayedTransport switch
        {
            TurnRequestedTransportProtocol.Udp => RelayReceiveLoopAsync(allocation, ct),
            TurnRequestedTransportProtocol.Tcp => _tcpPassiveConnectionService.RunAsync(allocation, SendToClientAsync, ct),
            _ => Task.CompletedTask
        };
    }

    private static bool IsPeerFamilyMatchingAllocation(TurnServerAllocation allocation, IPEndPoint peerEndPoint)
        => TurnMobilityService.IsPeerFamilyMatchingAllocation(allocation, peerEndPoint);

    private async Task SendResponseAsync(
        TurnClientContext context,
        StunMessage response,
        byte[]? integrityKey,
        CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            bytes = integrityKey is not null
                ? _codec.EncodeWithIntegrity(response, integrityKey, addFingerprint: true)
                : _codec.Encode(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TURN failed to encode response for {Sender}", context.RemoteEndPoint);
            return;
        }

        try
        {
            if (context.Transport == TurnServerTransport.Udp)
            {
                await _udp!.SendAsync(bytes, context.RemoteEndPoint, ct).ConfigureAwait(false);
                return;
            }

            if (context.StreamConnection is not null)
                await context.StreamConnection.SendAsync(bytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "TURN failed to send response to {Sender}", context.RemoteEndPoint);
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
