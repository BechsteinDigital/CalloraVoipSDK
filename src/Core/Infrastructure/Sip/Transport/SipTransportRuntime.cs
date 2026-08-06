using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Application.Ports.Security;
using CalloraVoipSdk.Core.Infrastructure.Common.Disposal;
using CalloraVoipSdk.Core.Infrastructure.Common.Network;
using CalloraVoipSdk.Core.Infrastructure.Sip.Routing;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using CalloraVoipSdk.Core.Infrastructure.Security;
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
    private const int WebSocketListenerBindAttempts = 5;
    private readonly IPEndPoint _wssLocalEndPoint;
    private readonly TlsConfiguration? _tlsConfiguration;
    private readonly SipTlsCertificateProvider? _tlsCertificateProvider;
    private readonly X509Certificate2? _tlsCertificate;
    private readonly ILogger<SipTransportRuntime> _logger;
    private readonly ISipWireCodec _wireCodec;
    private readonly ISipRouteResolver _routeResolver;
    private readonly SipTransportProtocol _defaultTransport;
    private readonly SipInboundConnectionAcceptor _acceptor;
    private readonly int _maxEndpointHintEntries;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<int, Action<SipInboundRequestContext, SipRequest>> _requestHandlers = new();
    private readonly ConcurrentDictionary<int, Action<IPEndPoint, SipResponse>> _responseHandlers = new();
    private readonly ConcurrentDictionary<string, SipTransportProtocol> _endpointTransportHints = new();
    // Maps a resolved endpoint (transport+addr:port key) to the SIP domain it was resolved from, so
    // outbound TLS uses the domain for SNI and certificate name validation, not the literal IP.
    private readonly ConcurrentDictionary<string, string> _endpointTlsHosts = new();
    private readonly SipOutboundConnectionPool _outboundPool;
    private readonly Task _udpReceiveLoop;
    private readonly Task _tcpAcceptLoop;
    private readonly Task _tlsAcceptLoop;
    private readonly Task _wsAcceptLoop;
    private readonly Task _wssAcceptLoop;

    private int _handlerIdSequence;
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
        ISipRouteResolver? routeResolver,
        SipTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _logger = loggerFactory.CreateLogger<SipTransportRuntime>();
        _wireCodec = wireCodec ?? throw new ArgumentNullException(nameof(wireCodec));
        _routeResolver = routeResolver ?? new SipDnsRouteResolver(loggerFactory);
        _tlsConfiguration = tlsConfiguration;
        _tlsCertificateProvider = tlsConfiguration is null ? null : new SipTlsCertificateProvider(tlsConfiguration);
        _tlsCertificate = _tlsCertificateProvider?.GetCertificate();
        _defaultTransport = defaultTransport;
        var effectiveOptions = options ?? SipTransportOptions.Default;
        _maxEndpointHintEntries = effectiveOptions.MaxEndpointHintEntries;
        _acceptor = new SipInboundConnectionAcceptor(
            effectiveOptions,
            _tlsCertificate,
            _logger,
            HandleInboundPayloadAsync);
        // Frames received on an outbound (pooled) connection carry no accepted inbound connection id — a
        // response to them can only ever go back out through the pool, so the id is null here (#158 P1-2).
        _outboundPool = new SipOutboundConnectionPool(
            _logger,
            _endpointTlsHosts,
            ValidateTlsServerCertificate,
            _tlsCertificate,
            (remote, transport, payload) => HandleInboundPayloadAsync(remote, transport, payload, inboundConnectionId: null));

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
    public IDisposable SubscribeRequests(Action<SipInboundRequestContext, SipRequest> handler)
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
        return SendResponseAsync(statusCode, reasonPhrase, headers, body, remoteEndPoint, transport, inboundConnectionId: null, ct);
    }

    /// <summary>
    /// Sends a SIP response over an explicit transport. For a connection-oriented transport the response is
    /// sent back over the accepted inbound connection identified by <paramref name="inboundConnectionId"/>
    /// when that connection is still live; otherwise it falls back to the outbound connection pool (or the
    /// UDP socket for connectionless transport). This keeps a TCP/TLS/WS/WSS response on the connection the
    /// request actually arrived on instead of dialling the peer's ephemeral source port (#158 P1-2).
    /// </summary>
    public async Task SendResponseAsync(
        int statusCode,
        string reasonPhrase,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        IPEndPoint remoteEndPoint,
        SipTransportProtocol transport,
        int? inboundConnectionId = null,
        CancellationToken ct = default)
    {
        SipWireTraceLogger.ResponseSent(_logger, statusCode, reasonPhrase, headers, body, remoteEndPoint, transport);
        var bytes = _wireCodec.SerializeResponse(statusCode, reasonPhrase, headers, body);

        if (inboundConnectionId is { } connectionId
            && await TrySendOverInboundConnectionAsync(connectionId, transport, bytes, ct).ConfigureAwait(false))
        {
            return;
        }

        await SendPayloadAsync(remoteEndPoint, bytes, transport, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Attempts to send one payload back over the accepted inbound connection identified by
    /// <paramref name="connectionId"/>. Returns false — so the caller falls back to the outbound path — when
    /// the transport is connectionless, no such connection is tracked, or the connection was already closed.
    /// A send failure over a live connection is propagated (the caller's transaction handles it per §17.2.4).
    /// </summary>
    private async Task<bool> TrySendOverInboundConnectionAsync(
        int connectionId,
        SipTransportProtocol transport,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct)
    {
        switch (transport)
        {
            case SipTransportProtocol.Tcp:
            case SipTransportProtocol.Tls:
                if (!_acceptor.TryGetStreamConnection(connectionId, out var streamConnection) || streamConnection is null)
                    return false;
                await streamConnection.SendAsync(payload, ct).ConfigureAwait(false);
                return true;

            case SipTransportProtocol.Ws:
            case SipTransportProtocol.Wss:
                if (!_acceptor.TryGetWebSocketConnection(connectionId, out var webSocketConnection) || webSocketConnection is null)
                    return false;
                await webSocketConnection.SendAsync(payload, ct).ConfigureAwait(false);
                return true;

            default:
                return false;
        }
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
                PutBounded(_endpointTransportHints, endpointKey, candidate.Transport);
                PutBounded(_endpointTransportHints, transportEndpointKey, candidate.Transport);
                PutBounded(_endpointTlsHosts, transportEndpointKey, host);
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
            PutBounded(_endpointTlsHosts, SipTransportRuntimeUtilities.BuildEndpointKey(transport, endpoint), host);
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
        var transportName = secure ? SipTransportProtocol.Wss : SipTransportProtocol.Ws;

        // HttpListener needs a concrete port in its prefix, so an ephemeral port is probed and then bound — an
        // unavoidable TOCTOU window, and the probe is per-socket while the "+:" bind is system-wide. Retry on a
        // fresh port so a port that raced away (or is taken on another interface) does not fail startup outright.
        for (var attempt = 1; attempt <= WebSocketListenerBindAttempts; attempt++)
        {
            var port = SipTransportRuntimeUtilities.AllocateEphemeralPort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"{scheme}://+:{port}/");
            try
            {
                listener.Start();
                localEndPoint = new IPEndPoint(IPAddress.Any, port);
                _logger.LogInformation("SIP {Transport} listener started on {EndPoint}.", transportName, localEndPoint);
                return listener;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "SIP {Transport} listener failed to start on port {Port} (attempt {Attempt}/{Max}).",
                    transportName,
                    port,
                    attempt,
                    WebSocketListenerBindAttempts);
                try
                {
                    listener.Close();
                }
                catch (Exception closeEx)
                {
                    _logger.LogDebug(closeEx, "Failed closing SIP {Transport} listener.", transportName);
                }
            }
        }

        return null;
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

            await HandleInboundPayloadAsync(packet.RemoteEndPoint, SipTransportProtocol.Udp, packet.Buffer, inboundConnectionId: null)
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

            _ = _acceptor.AcceptStreamConnectionAsync(client, protocol, ct);
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

            _ = _acceptor.AcceptWebSocketConnectionAsync(context, protocol, ct);
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
                await _outboundPool.SendStreamAsync(targetEndPoint, payload, transport, ct).ConfigureAwait(false);
                break;

            case SipTransportProtocol.Ws:
            case SipTransportProtocol.Wss:
                await _outboundPool.SendWebSocketAsync(targetEndPoint, payload, transport, ct).ConfigureAwait(false);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(transport), transport, "Unknown SIP transport.");
        }
    }

    /// <summary>
    /// Handles one inbound payload from any transport and dispatches parsed messages.
    /// </summary>
    private Task HandleInboundPayloadAsync(
        IPEndPoint remoteEndPoint,
        SipTransportProtocol transport,
        ReadOnlyMemory<byte> payload,
        int? inboundConnectionId)
    {
        try
        {
            if (_wireCodec.TryParseRequest(payload.Span, out var request) && request is not null)
            {
                // #158 P1-4: learn the transport hint only after the payload parses as a real SIP message.
                // Writing it up-front (as before) let a spoofed/garbage datagram plant hint state for any
                // source address it forged, growing the map without bound and skewing outbound transport
                // selection for that address.
                RememberTransportHint(remoteEndPoint, transport);
                SipWireTraceLogger.RequestReceived(_logger, request, remoteEndPoint, transport);
                DispatchRequest(new SipInboundRequestContext(remoteEndPoint, transport, inboundConnectionId), request);
                return Task.CompletedTask;
            }

            if (_wireCodec.TryParseResponse(payload.Span, out var response) && response is not null)
            {
                RememberTransportHint(remoteEndPoint, transport);
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
    /// Records the transport a validated inbound SIP message arrived on, keyed by remote endpoint, so a later
    /// outbound message to that endpoint reuses the same transport. Bounded (#158 P1-4).
    /// </summary>
    private void RememberTransportHint(IPEndPoint remoteEndPoint, SipTransportProtocol transport)
    {
        PutBounded(_endpointTransportHints, SipTransportRuntimeUtilities.BuildEndpointKey(transport, remoteEndPoint), transport);
        PutBounded(_endpointTransportHints, SipTransportRuntimeUtilities.BuildEndpointKey(null, remoteEndPoint), transport);
    }

    /// <summary>
    /// Writes one hint-map entry and evicts arbitrary entries when the map exceeds
    /// <see cref="_maxEndpointHintEntries"/>. The maps are optimisation caches — a missing entry falls back to
    /// the default transport / literal-IP TLS host — so an approximate, best-effort bound is sufficient to
    /// deny a source-spoofing peer unbounded growth (#158 P1-4).
    /// </summary>
    private void PutBounded<TValue>(ConcurrentDictionary<string, TValue> map, string key, TValue value)
    {
        map[key] = value;
        if (_maxEndpointHintEntries <= 0 || map.Count <= _maxEndpointHintEntries)
            return;

        foreach (var evictKey in map.Keys)
        {
            if (map.Count <= _maxEndpointHintEntries)
                break;
            map.TryRemove(evictKey, out _);
        }
    }

    /// <summary>
    /// Dispatches parsed SIP requests to subscribed handlers.
    /// Uses a snapshot of the handler collection via <c>.ToArray()</c> to guard against
    /// concurrent handler removal during iteration (e.g., a handler unsubscribing itself).
    /// </summary>
    private void DispatchRequest(SipInboundRequestContext context, SipRequest request)
    {
        foreach (var handler in _requestHandlers.Values.ToArray()) // snapshot before iterating
        {
            try
            {
                handler(context, request);
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
        var (accepted, reason) = SipTlsServerTrustEvaluator.Evaluate(
            _tlsConfiguration?.TrustMode ?? SipTlsTrustMode.System,
            _tlsConfiguration?.ExpectedSipDomain,
            certificate,
            sslPolicyErrors,
            _tlsCertificateProvider is not null ? _tlsCertificateProvider.ValidatePeerCertificateSipDomain : null);

        if (!accepted)
        {
            _logger.LogWarning("SIP TLS server certificate rejected: {Reason}.", reason);
            return false;
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

        _outboundPool.Dispose();
        _acceptor.DisposeConnections();
        _endpointTransportHints.Clear();
        _endpointTlsHosts.Clear();
        _requestHandlers.Clear();
        _responseHandlers.Clear();
        _stop.Dispose();
    }
}
