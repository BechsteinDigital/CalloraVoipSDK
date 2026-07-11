using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Common.Disposal;
using CalloraVoipSdk.Core.Infrastructure.Common.Network;
using CalloraVoipSdk.Core.Infrastructure.Sip.Routing;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using CalloraVoipSdk.Core.Security;
namespace CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

/// <summary>
/// Shared SIP transport runtime supporting UDP, TCP, TLS, WS, and WSS transports.
/// Maintains connection handling for stateful transports and dispatches parsed SIP messages.
/// </summary>
internal sealed class SipTransportRuntime : ISipTransportRuntime
{
    private readonly UdpClient _udp;
    private readonly TcpListener _tcpListener;
    private readonly TcpListener? _tlsListener;
    private readonly HttpListener? _wsListener;
    private readonly HttpListener? _wssListener;
    private readonly IPEndPoint _wsLocalEndPoint;
    private readonly IPEndPoint _wssLocalEndPoint;
    private readonly TlsConfiguration? _tlsConfiguration;
    private readonly X509Certificate2? _tlsCertificate;
    private readonly ILogger<SipTransportRuntime> _logger;
    private readonly ISipWireCodec _wireCodec;
    private readonly ISipRouteResolver _routeResolver;
    private readonly SipTransportProtocol _defaultTransport;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<int, Action<IPEndPoint, SipRequest>> _requestHandlers = new();
    private readonly ConcurrentDictionary<int, Action<IPEndPoint, SipResponse>> _responseHandlers = new();
    private readonly ConcurrentDictionary<string, SipTransportProtocol> _endpointTransportHints = new();
    // Maps a resolved endpoint (transport+addr:port key) to the SIP domain it was resolved from, so
    // outbound TLS uses the domain for SNI and certificate name validation, not the literal IP.
    private readonly ConcurrentDictionary<string, string> _endpointTlsHosts = new();
    private readonly ConcurrentDictionary<string, SipStreamConnection> _outboundStreamConnections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, SipStreamConnection> _inboundStreamConnections = new();
    private readonly ConcurrentDictionary<string, SipWebSocketConnection> _outboundWebSocketConnections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, SipWebSocketConnection> _inboundWebSocketConnections = new();
    private readonly SemaphoreSlim _outboundConnectionGate = new(1, 1);
    private readonly SemaphoreSlim _outboundWebSocketConnectionGate = new(1, 1);
    private readonly Task _udpReceiveLoop;
    private readonly Task _tcpAcceptLoop;
    private readonly Task _tlsAcceptLoop;
    private readonly Task _wsAcceptLoop;
    private readonly Task _wssAcceptLoop;

    private int _handlerIdSequence;
    private int _inboundConnectionId;
    private int _inboundWebSocketConnectionId;
    private int _disposed;

    /// <summary>
    /// Creates a runtime with default codec and UDP-first outbound transport.
    /// </summary>
    public SipTransportRuntime(ILoggerFactory loggerFactory)
        : this(loggerFactory, new SipWireProtocol(), null, SipTransportProtocol.Udp, null)
    {
    }

    /// <summary>
    /// Creates a runtime with injected wire codec and UDP-first outbound transport.
    /// </summary>
    public SipTransportRuntime(
        ILoggerFactory loggerFactory,
        ISipWireCodec wireCodec)
        : this(loggerFactory, wireCodec, null, SipTransportProtocol.Udp, null)
    {
    }

    /// <summary>
    /// Creates a runtime with configurable TLS and outbound default transport.
    /// </summary>
    public SipTransportRuntime(
        ILoggerFactory loggerFactory,
        ISipWireCodec wireCodec,
        TlsConfiguration? tlsConfiguration,
        SipTransportProtocol defaultTransport,
        ISipRouteResolver? routeResolver)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _logger = loggerFactory.CreateLogger<SipTransportRuntime>();
        _wireCodec = wireCodec ?? throw new ArgumentNullException(nameof(wireCodec));
        _routeResolver = routeResolver ?? new SipDnsRouteResolver(loggerFactory);
        _tlsConfiguration = tlsConfiguration;
        _tlsCertificate = tlsConfiguration?.GetCertificate();
        _defaultTransport = defaultTransport;

        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));

        _tcpListener = new TcpListener(IPAddress.Any, 0);
        _tcpListener.Start();

        if (_tlsCertificate is not null)
        {
            _tlsListener = new TcpListener(IPAddress.Any, 0);
            _tlsListener.Start();
            _logger.LogInformation("SIP TLS listener started on {EndPoint}.", _tlsListener.LocalEndpoint);
        }

        _wsListener = StartWebSocketListener(secure: false, out _wsLocalEndPoint);
        _wssListener = StartWebSocketListener(secure: true, out _wssLocalEndPoint);

        _udpReceiveLoop = Task.Run(() => UdpReceiveLoopAsync(_stop.Token));
        _tcpAcceptLoop = Task.Run(() => AcceptLoopAsync(_tcpListener, SipTransportProtocol.Tcp, _stop.Token));
        _tlsAcceptLoop = _tlsListener is null
            ? Task.CompletedTask
            : Task.Run(() => AcceptLoopAsync(_tlsListener, SipTransportProtocol.Tls, _stop.Token));
        _wsAcceptLoop = _wsListener is null
            ? Task.CompletedTask
            : Task.Run(() => AcceptWebSocketLoopAsync(_wsListener, SipTransportProtocol.Ws, _stop.Token));
        _wssAcceptLoop = _wssListener is null
            ? Task.CompletedTask
            : Task.Run(() => AcceptWebSocketLoopAsync(_wssListener, SipTransportProtocol.Wss, _stop.Token));
    }

    /// <summary>
    /// Local endpoint for the default outbound transport protocol.
    /// </summary>
    public IPEndPoint LocalEndPoint => GetLocalEndPoint(_defaultTransport);

    /// <summary>
    /// Returns local endpoint bound for one transport protocol.
    /// </summary>
    public IPEndPoint GetLocalEndPoint(SipTransportProtocol transport) => transport switch
    {
        SipTransportProtocol.Tcp => SipTransportRuntimeUtilities.NormalizeWildcardEndPoint((IPEndPoint)_tcpListener.LocalEndpoint),
        SipTransportProtocol.Tls when _tlsListener is not null => SipTransportRuntimeUtilities.NormalizeWildcardEndPoint((IPEndPoint)_tlsListener.LocalEndpoint),
        SipTransportProtocol.Ws => SipTransportRuntimeUtilities.NormalizeWildcardEndPoint(_wsLocalEndPoint),
        SipTransportProtocol.Wss => _wssListener is not null
            ? SipTransportRuntimeUtilities.NormalizeWildcardEndPoint(_wssLocalEndPoint)
            : SipTransportRuntimeUtilities.NormalizeWildcardEndPoint(_wsLocalEndPoint),
        _ => SipTransportRuntimeUtilities.NormalizeWildcardEndPoint((IPEndPoint)(_udp.Client.LocalEndPoint ?? new IPEndPoint(IPAddress.Any, 0)))
    };

    /// <summary>
    /// Registers a SIP request handler and returns a disposal token for unsubscription.
    /// </summary>
    public IDisposable SubscribeRequests(Action<IPEndPoint, SipRequest> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var id = Interlocked.Increment(ref _handlerIdSequence);
        _requestHandlers[id] = handler;
        return new DisposeAction(() => _requestHandlers.TryRemove(id, out _));
    }

    /// <summary>
    /// Registers a SIP response handler and returns a disposal token for unsubscription.
    /// </summary>
    public IDisposable SubscribeResponses(Action<IPEndPoint, SipResponse> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var id = Interlocked.Increment(ref _handlerIdSequence);
        _responseHandlers[id] = handler;
        return new DisposeAction(() => _responseHandlers.TryRemove(id, out _));
    }

    /// <summary>
    /// Sends a SIP request, inferring transport from URI and endpoint hints.
    /// </summary>
    public Task SendRequestAsync(
        string method,
        string requestUri,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        IPEndPoint remoteEndPoint,
        CancellationToken ct = default)
    {
        var transport = InferTransport(requestUri, remoteEndPoint);
        return SendRequestAsync(method, requestUri, headers, body, remoteEndPoint, transport, ct);
    }

    /// <summary>
    /// Sends a SIP request over an explicit transport.
    /// RFC 3261 §18.1.1: if the serialized message exceeds <see cref="UdpMtuThreshold"/> bytes
    /// and UDP was selected, the message MUST be sent over TCP and the Via transport token
    /// updated accordingly.
    /// </summary>
    public async Task SendRequestAsync(
        string method,
        string requestUri,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        IPEndPoint remoteEndPoint,
        SipTransportProtocol transport,
        CancellationToken ct = default)
    {
        var bytes = _wireCodec.SerializeRequest(method, requestUri, headers, body);

        // RFC 3261 §18.1.1: congestion-controlled transport (TCP) is required for messages
        // larger than 1300 bytes when the path MTU is unknown.
        if (transport == SipTransportProtocol.Udp && bytes.Length > UdpMtuThreshold)
        {
            transport = SipTransportProtocol.Tcp;
            headers   = SipTransportRuntimeUtilities.EscalateViaTransportToTcp(headers);
            bytes     = _wireCodec.SerializeRequest(method, requestUri, headers, body);
            _logger.LogDebug(
                "SIP {Method} message ({Size} bytes) exceeds UDP MTU threshold; escalated to TCP.",
                method, bytes.Length);
        }

        SipWireTraceLogger.RequestSent(_logger, method, headers, body, remoteEndPoint, transport);
        await SendPayloadAsync(remoteEndPoint, bytes, transport, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Maximum datagram size for UDP before escalating to TCP per RFC 3261 §18.1.1.
    /// </summary>
    internal const int UdpMtuThreshold = 1300;

    /// <summary>
    /// Sends a SIP response, inferring transport from endpoint hints.
    /// </summary>
    public Task SendResponseAsync(
        int statusCode,
        string reasonPhrase,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        IPEndPoint remoteEndPoint,
        CancellationToken ct = default)
    {
        var transport = InferTransport(requestUri: null, remoteEndPoint);
        return SendResponseAsync(statusCode, reasonPhrase, headers, body, remoteEndPoint, transport, ct);
    }

    /// <summary>
    /// Sends a SIP response over an explicit transport.
    /// </summary>
    public async Task SendResponseAsync(
        int statusCode,
        string reasonPhrase,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        IPEndPoint remoteEndPoint,
        SipTransportProtocol transport,
        CancellationToken ct = default)
    {
        SipWireTraceLogger.ResponseSent(_logger, statusCode, reasonPhrase, headers, body, remoteEndPoint, transport);
        var bytes = _wireCodec.SerializeResponse(statusCode, reasonPhrase, headers, body);
        await SendPayloadAsync(remoteEndPoint, bytes, transport, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a remote endpoint using default transport behavior.
    /// </summary>
    public Task<IPEndPoint> ResolveRemoteEndPointAsync(
        string host,
        int port,
        CancellationToken ct = default) =>
        ResolveRemoteEndPointAsync(host, port, _defaultTransport, ct);

    /// <summary>
    /// Resolves ordered remote route candidates for one host/port and preferred transport.
    /// </summary>
    public async Task<IReadOnlyList<SipRouteCandidate>> ResolveRemoteRouteCandidatesAsync(
        string host,
        int port,
        SipTransportProtocol transport,
        CancellationToken ct = default)
    {
        try
        {
            var resolution = await _routeResolver.ResolveAsync(
                    new SipRouteResolutionRequest
                    {
                        Host = host,
                        Port = port > 0 ? port : null,
                        PreferredTransport = transport
                    },
                    ct)
                .ConfigureAwait(false);

            foreach (var candidate in resolution.Candidates)
            {
                var endpointKey = SipTransportRuntimeUtilities.BuildEndpointKey(null, candidate.EndPoint);
                var transportEndpointKey = SipTransportRuntimeUtilities.BuildEndpointKey(candidate.Transport, candidate.EndPoint);
                _endpointTransportHints[endpointKey] = candidate.Transport;
                _endpointTransportHints[transportEndpointKey] = candidate.Transport;
                _endpointTlsHosts[transportEndpointKey] = host;
            }

            return resolution.Candidates;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "SIP route resolution failed for {Host}:{Port} ({Transport}); falling back to direct host lookup.",
                host, port, transport);
            var effectivePort = port > 0
                ? port
                : transport switch
                {
                    SipTransportProtocol.Ws => 80,
                    SipTransportProtocol.Wss => 443,
                    SipTransportProtocol.Tls => 5061,
                    _ => 5060
                };
            var endpoint = await RemoteEndPointResolver.ResolveAsync(host, effectivePort, ct).ConfigureAwait(false);
            _endpointTlsHosts[SipTransportRuntimeUtilities.BuildEndpointKey(transport, endpoint)] = host;
            return
            [
                new SipRouteCandidate
                {
                    EndPoint = endpoint,
                    Transport = transport,
                    Source = "direct-host-fallback"
                }
            ];
        }
    }

    /// <summary>
    /// Resolves a remote endpoint for an explicit transport.
    /// </summary>
    public async Task<IPEndPoint> ResolveRemoteEndPointAsync(
        string host,
        int port,
        SipTransportProtocol transport,
        CancellationToken ct = default)
    {
        var candidates = await ResolveRemoteRouteCandidatesAsync(host, port, transport, ct).ConfigureAwait(false);
        return candidates.Count > 0
            ? candidates[0].EndPoint
            : throw new InvalidOperationException($"SIP route resolution returned no candidates for '{host}:{port}'.");
    }

    /// <summary>
    /// Starts one WebSocket listener on an ephemeral port.
    /// WSS listener requires platform HTTPS certificate bindings.
    /// </summary>
    private HttpListener? StartWebSocketListener(
        bool secure,
        out IPEndPoint localEndPoint)
    {
        localEndPoint = new IPEndPoint(IPAddress.Any, 0);
        if (secure && _tlsCertificate is null)
            return null;

        var scheme = secure ? "https" : "http";
        var port = SipTransportRuntimeUtilities.AllocateEphemeralPort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"{scheme}://+:{port}/");
        try
        {
            listener.Start();
            localEndPoint = new IPEndPoint(IPAddress.Any, port);
            _logger.LogInformation(
                "SIP {Transport} listener started on {EndPoint}.",
                secure ? SipTransportProtocol.Wss : SipTransportProtocol.Ws,
                localEndPoint);
            return listener;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "SIP {Transport} listener failed to start on port {Port}.",
                secure ? SipTransportProtocol.Wss : SipTransportProtocol.Ws,
                port);
            try
            {
                listener.Close();
            }
            catch (Exception closeEx)
            {
                _logger.LogDebug(closeEx, "Failed closing SIP {Transport} listener.", secure ? "WSS" : "WS");
            }

            return null;
        }
    }

    /// <summary>
    /// Receives and dispatches SIP datagrams on UDP.
    /// </summary>
    private async Task UdpReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogDebug(ex, "SIP UDP receive loop canceled.");
                break;
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogDebug(ex, "SIP UDP socket disposed; stopping receive loop.");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SIP UDP receive failed.");
                continue;
            }

            await HandleInboundPayloadAsync(packet.RemoteEndPoint, SipTransportProtocol.Udp, packet.Buffer)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Accepts stream connections for TCP/TLS listener and registers receive pipelines.
    /// </summary>
    private async Task AcceptLoopAsync(TcpListener listener, SipTransportProtocol protocol, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogDebug(ex, "SIP {Transport} accept loop canceled.", protocol);
                break;
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogDebug(ex, "SIP {Transport} listener disposed.", protocol);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SIP {Transport} accept failed.", protocol);
                continue;
            }

            _ = RegisterInboundStreamConnectionAsync(client, protocol, ct);
        }
    }

    /// <summary>
    /// Accepts inbound WebSocket upgrade requests and tracks active WS/WSS connections.
    /// </summary>
    private async Task AcceptWebSocketLoopAsync(
        HttpListener listener,
        SipTransportProtocol protocol,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogDebug(ex, "SIP {Transport} WebSocket accept loop canceled.", protocol);
                break;
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogDebug(ex, "SIP {Transport} WebSocket listener disposed.", protocol);
                break;
            }
            catch (HttpListenerException ex)
            {
                _logger.LogDebug(ex, "SIP {Transport} WebSocket listener stopped.", protocol);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SIP {Transport} WebSocket accept failed.", protocol);
                continue;
            }

            _ = RegisterInboundWebSocketConnectionAsync(context, protocol, ct);
        }
    }

    /// <summary>
    /// Registers one accepted inbound WS/WSS connection.
    /// </summary>
    private async Task RegisterInboundWebSocketConnectionAsync(
        HttpListenerContext context,
        SipTransportProtocol protocol,
        CancellationToken ct)
    {
        try
        {
            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                context.Response.Close();
                return;
            }
            var wsContext = await context.AcceptWebSocketAsync(SipTransportRuntimeUtilities.SelectOfferedSipSubProtocol(context.Request)).WaitAsync(ct).ConfigureAwait(false);
            var remoteEndPoint = context.Request.RemoteEndPoint ?? new IPEndPoint(IPAddress.Loopback, 0);
            var id = Interlocked.Increment(ref _inboundWebSocketConnectionId);
            var connection = new SipWebSocketConnection(
                protocol,
                wsContext.WebSocket,
                remoteEndPoint,
                _logger,
                HandleInboundPayloadAsync,
                onClosed: () => _inboundWebSocketConnections.TryRemove(id, out _));
            _inboundWebSocketConnections[id] = connection;
            _logger.LogDebug("Accepted SIP {Transport} WebSocket from {Remote}.", protocol, remoteEndPoint);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to accept SIP {Transport} WebSocket connection.", protocol);
            try
            {
                context.Response.Abort();
            }
            catch (Exception abortEx)
            {
                _logger.LogDebug(abortEx, "Failed aborting failed SIP {Transport} WebSocket context.", protocol);
            }
        }
    }

    /// <summary>
    /// Registers one accepted inbound TCP/TLS connection.
    /// </summary>
    private async Task RegisterInboundStreamConnectionAsync(
        TcpClient client,
        SipTransportProtocol protocol,
        CancellationToken ct)
    {
        Stream? stream = null;
        try
        {
            stream = client.GetStream();
            if (protocol == SipTransportProtocol.Tls)
            {
                if (_tlsCertificate is null)
                    throw new InvalidOperationException("TLS listener has no certificate.");

                var sslStream = new SslStream(stream, leaveInnerStreamOpen: false);
                await sslStream.AuthenticateAsServerAsync(
                        _tlsCertificate,
                        clientCertificateRequired: false,
                        enabledSslProtocols: SslProtocols.Tls12 | SslProtocols.Tls13,
                        checkCertificateRevocation: false)
                    .ConfigureAwait(false);
                stream = sslStream;
            }

            var id = Interlocked.Increment(ref _inboundConnectionId);
            var connection = new SipStreamConnection(
                protocol,
                client,
                stream,
                _logger,
                HandleInboundPayloadAsync,
                onClosed: () => _inboundStreamConnections.TryRemove(id, out _));
            _inboundStreamConnections[id] = connection;
            _logger.LogDebug("Accepted SIP {Transport} stream from {Remote}.", protocol, connection.RemoteEndPoint);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to register inbound SIP {Transport} stream connection.", protocol);
            try
            {
                stream?.Dispose();
            }
            catch
            {
                // best effort cleanup
            }

            try
            {
                client.Dispose();
            }
            catch
            {
                // best effort cleanup
            }
        }
    }

    /// <summary>
    /// Sends serialized payload over transport-specific channel.
    /// RFC 3261 §18.4: if a stream send fails, the stale connection is removed and one retry
    /// is attempted over a new connection.
    /// </summary>
    private async Task SendPayloadAsync(
        IPEndPoint remoteEndPoint,
        ReadOnlyMemory<byte> payload,
        SipTransportProtocol transport,
        CancellationToken ct)
    {
        var targetEndPoint = SipTransportRuntimeUtilities.NormalizeWildcardEndPoint(remoteEndPoint);

        switch (transport)
        {
            case SipTransportProtocol.Udp:
                await _udp.SendAsync(payload, targetEndPoint, ct).ConfigureAwait(false);
                break;

            case SipTransportProtocol.Tcp:
            case SipTransportProtocol.Tls:
            {
                var connection = await GetOrCreateOutboundStreamConnectionAsync(targetEndPoint, transport, ct)
                    .ConfigureAwait(false);
                try
                {
                    await connection.SendAsync(payload, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // RFC 3261 §18.4: remove the stale connection and retry once on a fresh one.
                    var staleKey = SipTransportRuntimeUtilities.BuildEndpointKey(transport, targetEndPoint);
                    if (_outboundStreamConnections.TryRemove(staleKey, out var stale))
                    {
                        _logger.LogDebug(
                            ex,
                            "SIP {Transport} send to {Remote} failed; retrying on new connection (RFC §18.4).",
                            transport, targetEndPoint);
                        stale.Dispose();
                    }

                    var fresh = await GetOrCreateOutboundStreamConnectionAsync(targetEndPoint, transport, ct)
                        .ConfigureAwait(false);
                    await fresh.SendAsync(payload, ct).ConfigureAwait(false);
                }

                break;
            }

            case SipTransportProtocol.Ws:
            case SipTransportProtocol.Wss:
            {
                var connection = await GetOrCreateOutboundWebSocketConnectionAsync(targetEndPoint, transport, ct)
                    .ConfigureAwait(false);
                await connection.SendAsync(payload, ct).ConfigureAwait(false);
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(transport), transport, "Unknown SIP transport.");
        }
    }

    /// <summary>
    /// Gets or creates one reusable outbound stream connection.
    /// </summary>
    private async Task<SipStreamConnection> GetOrCreateOutboundStreamConnectionAsync(
        IPEndPoint remoteEndPoint,
        SipTransportProtocol transport,
        CancellationToken ct)
    {
        remoteEndPoint = SipTransportRuntimeUtilities.NormalizeWildcardEndPoint(remoteEndPoint);
        var key = SipTransportRuntimeUtilities.BuildEndpointKey(transport, remoteEndPoint);
        if (_outboundStreamConnections.TryGetValue(key, out var existing))
            return existing;

        await _outboundConnectionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_outboundStreamConnections.TryGetValue(key, out existing))
                return existing;

            var client = new TcpClient(remoteEndPoint.AddressFamily);
            await client.ConnectAsync(remoteEndPoint, ct).ConfigureAwait(false);
            Stream stream = client.GetStream();
            if (transport == SipTransportProtocol.Tls)
            {
                var targetHost = SipTransportRuntimeUtilities.SelectTlsTargetHost(_endpointTlsHosts, key, remoteEndPoint.Address);
                stream = await SipTransportRuntimeUtilities.AuthenticateOutboundTlsAsync(
                    stream, targetHost, ValidateTlsServerCertificate, ct).ConfigureAwait(false);
            }

            var created = new SipStreamConnection(
                transport,
                client,
                stream,
                _logger,
                HandleInboundPayloadAsync,
                onClosed: () => _outboundStreamConnections.TryRemove(key, out _));
            _outboundStreamConnections[key] = created;
            return created;
        }
        finally
        {
            _outboundConnectionGate.Release();
        }
    }

    /// <summary>
    /// Gets or creates one reusable outbound WS/WSS connection.
    /// </summary>
    private async Task<SipWebSocketConnection> GetOrCreateOutboundWebSocketConnectionAsync(
        IPEndPoint remoteEndPoint,
        SipTransportProtocol transport,
        CancellationToken ct)
    {
        remoteEndPoint = SipTransportRuntimeUtilities.NormalizeWildcardEndPoint(remoteEndPoint);
        var key = SipTransportRuntimeUtilities.BuildEndpointKey(transport, remoteEndPoint);
        if (_outboundWebSocketConnections.TryGetValue(key, out var existing))
            return existing;

        await _outboundWebSocketConnectionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_outboundWebSocketConnections.TryGetValue(key, out existing))
                return existing;

            var socket = new ClientWebSocket();
            socket.Options.AddSubProtocol("sip"); // RFC 7118: SIP-over-WebSocket subprotocol
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            if (transport == SipTransportProtocol.Wss)
            {
                socket.Options.RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
                    ValidateTlsServerCertificate(sender, certificate, chain, errors);
            }

            var targetUri = SipTransportRuntimeUtilities.BuildWebSocketTargetUri(remoteEndPoint, transport);
            await socket.ConnectAsync(targetUri, ct).ConfigureAwait(false);
            var created = new SipWebSocketConnection(
                transport,
                socket,
                remoteEndPoint,
                _logger,
                HandleInboundPayloadAsync,
                onClosed: () => _outboundWebSocketConnections.TryRemove(key, out _));
            _outboundWebSocketConnections[key] = created;
            return created;
        }
        finally
        {
            _outboundWebSocketConnectionGate.Release();
        }
    }

    /// <summary>
    /// Handles one inbound payload from any transport and dispatches parsed messages.
    /// </summary>
    private Task HandleInboundPayloadAsync(
        IPEndPoint remoteEndPoint,
        SipTransportProtocol transport,
        ReadOnlyMemory<byte> payload)
    {
        _endpointTransportHints[SipTransportRuntimeUtilities.BuildEndpointKey(transport, remoteEndPoint)] = transport;
        _endpointTransportHints[SipTransportRuntimeUtilities.BuildEndpointKey(null, remoteEndPoint)] = transport;

        try
        {
            if (_wireCodec.TryParseRequest(payload.Span, out var request) && request is not null)
            {
                SipWireTraceLogger.RequestReceived(_logger, request, remoteEndPoint, transport);
                DispatchRequest(remoteEndPoint, request);
                return Task.CompletedTask;
            }

            if (_wireCodec.TryParseResponse(payload.Span, out var response) && response is not null)
            {
                SipWireTraceLogger.ResponseReceived(_logger, response, remoteEndPoint, transport);
                DispatchResponse(remoteEndPoint, response);
                return Task.CompletedTask;
            }

            _logger.LogDebug("Ignored unparsable SIP payload from {Remote} on {Transport}.", remoteEndPoint, transport);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SIP message dispatch failed for {Remote} on {Transport}.", remoteEndPoint, transport);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Dispatches parsed SIP requests to subscribed handlers.
    /// Uses a snapshot of the handler collection via <c>.ToArray()</c> to guard against
    /// concurrent handler removal during iteration (e.g., a handler unsubscribing itself).
    /// </summary>
    private void DispatchRequest(IPEndPoint remoteEndPoint, SipRequest request)
    {
        foreach (var handler in _requestHandlers.Values.ToArray()) // snapshot before iterating
        {
            try
            {
                handler(remoteEndPoint, request);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SIP request handler failed.");
            }
        }
    }

    /// <summary>
    /// Dispatches parsed SIP responses to subscribed handlers.
    /// Uses a snapshot of the handler collection via <c>.ToArray()</c> to guard against
    /// concurrent handler removal during iteration (e.g., a handler unsubscribing itself).
    /// </summary>
    private void DispatchResponse(IPEndPoint remoteEndPoint, SipResponse response)
    {
        foreach (var handler in _responseHandlers.Values.ToArray()) // snapshot before iterating
        {
            try
            {
                handler(remoteEndPoint, response);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SIP response handler failed.");
            }
        }
    }

    /// <summary>
    /// Selects best transport for outbound message when protocol is not explicit.
    /// </summary>
    private SipTransportProtocol InferTransport(string? requestUri, IPEndPoint remoteEndPoint)
    {
        if (!string.IsNullOrWhiteSpace(requestUri)
            && requestUri.StartsWith("sips:", StringComparison.OrdinalIgnoreCase))
        {
            if (requestUri.Contains(";transport=wss", StringComparison.OrdinalIgnoreCase))
                return SipTransportProtocol.Wss;
            return SipTransportProtocol.Tls;
        }

        if (!string.IsNullOrWhiteSpace(requestUri))
        {
            if (requestUri.Contains(";transport=wss", StringComparison.OrdinalIgnoreCase))
                return SipTransportProtocol.Wss;
            if (requestUri.Contains(";transport=ws", StringComparison.OrdinalIgnoreCase))
                return SipTransportProtocol.Ws;
            if (requestUri.Contains(";transport=tls", StringComparison.OrdinalIgnoreCase))
                return SipTransportProtocol.Tls;
            if (requestUri.Contains(";transport=tcp", StringComparison.OrdinalIgnoreCase))
                return SipTransportProtocol.Tcp;
            if (requestUri.Contains(";transport=udp", StringComparison.OrdinalIgnoreCase))
                return SipTransportProtocol.Udp;
        }

        if (_endpointTransportHints.TryGetValue(SipTransportRuntimeUtilities.BuildEndpointKey(null, remoteEndPoint), out var hinted))
            return hinted;

        return _defaultTransport;
    }

    /// <summary>
    /// Validates a remote TLS server certificate against the configured trust
    /// policy and, when <see cref="TlsConfiguration.ExpectedSipDomain"/> is set,
    /// performs RFC 5922 §7.1 SIP domain identity validation against the
    /// certificate's Subject Alternative Name (SAN) extension.
    /// </summary>
    private bool ValidateTlsServerCertificate(
        object? _,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        if (_tlsConfiguration?.AcceptUntrustedCertificates == true)
            return true;

        if (sslPolicyErrors != SslPolicyErrors.None)
        {
            _logger.LogWarning(
                "SIP TLS certificate validation failed: {Errors}.",
                sslPolicyErrors);
            return false;
        }

        // RFC 5922 §7.1: additional SIP domain SAN check when configured.
        if (_tlsConfiguration?.ExpectedSipDomain is not null
            && certificate is X509Certificate2 cert2)
        {
            if (!_tlsConfiguration.ValidatePeerCertificateSipDomain(cert2))
            {
                _logger.LogWarning(
                    "RFC 5922 SIP domain SAN validation failed for domain '{SipDomain}'.",
                    _tlsConfiguration.ExpectedSipDomain);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Disposes all transport resources and background loops.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _stop.Cancel();

        try
        {
            _tcpListener.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed stopping SIP TCP listener.");
        }

        if (_tlsListener is not null)
        {
            try
            {
                _tlsListener.Stop();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed stopping SIP TLS listener.");
            }
        }

        if (_wsListener is not null)
        {
            try
            {
                _wsListener.Stop();
                _wsListener.Close();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed stopping SIP WS listener.");
            }
        }

        if (_wssListener is not null)
        {
            try
            {
                _wssListener.Stop();
                _wssListener.Close();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed stopping SIP WSS listener.");
            }
        }

        try
        {
            _udp.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed disposing SIP UDP socket.");
        }

        try
        {
            Task.WaitAll(
            [
                _udpReceiveLoop,
                _tcpAcceptLoop,
                _tlsAcceptLoop,
                _wsAcceptLoop,
                _wssAcceptLoop
            ], TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SIP transport loops finished with exceptions during disposal.");
        }

        foreach (var connection in _outboundStreamConnections.Values)
            connection.Dispose();
        foreach (var connection in _inboundStreamConnections.Values)
            connection.Dispose();
        foreach (var connection in _outboundWebSocketConnections.Values)
            connection.Dispose();
        foreach (var connection in _inboundWebSocketConnections.Values)
            connection.Dispose();

        _outboundStreamConnections.Clear();
        _inboundStreamConnections.Clear();
        _outboundWebSocketConnections.Clear();
        _inboundWebSocketConnections.Clear();
        _endpointTransportHints.Clear();
        _requestHandlers.Clear();
        _responseHandlers.Clear();
        _outboundConnectionGate.Dispose();
        _outboundWebSocketConnectionGate.Dispose();
        _stop.Dispose();
    }
}
