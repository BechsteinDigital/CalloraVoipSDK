using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

/// <summary>
/// Owns and reuses outbound SIP stream (TCP/TLS) and WebSocket (WS/WSS) connections and sends
/// payloads over them. Extracted from the transport runtime as an injected collaborator: it takes
/// the logger, resolved TLS-host map, certificate validator and inbound-frame callback it needs and
/// manages the connection pools and their lifetime itself.
/// </summary>
internal sealed class SipOutboundConnectionPool : IDisposable
{
    private readonly ILogger _logger;
    private readonly IReadOnlyDictionary<string, string> _tlsHosts;
    private readonly RemoteCertificateValidationCallback _validateTlsCertificate;
    private readonly Func<IPEndPoint, SipTransportProtocol, ReadOnlyMemory<byte>, Task> _onFrameAsync;

    private readonly ConcurrentDictionary<string, SipStreamConnection> _streamConnections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SipWebSocketConnection> _webSocketConnections = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _streamGate = new(1, 1);
    private readonly SemaphoreSlim _webSocketGate = new(1, 1);

    /// <summary>
    /// Creates an outbound connection pool.
    /// </summary>
    /// <param name="logger">Sink for connection diagnostics.</param>
    /// <param name="tlsHosts">Resolved endpoint-key to SIP-domain map for TLS SNI / certificate validation.</param>
    /// <param name="validateTlsCertificate">Server certificate validation callback for TLS/WSS.</param>
    /// <param name="onFrameAsync">Callback invoked for each inbound SIP frame received on a pooled connection.</param>
    public SipOutboundConnectionPool(
        ILogger logger,
        IReadOnlyDictionary<string, string> tlsHosts,
        RemoteCertificateValidationCallback validateTlsCertificate,
        Func<IPEndPoint, SipTransportProtocol, ReadOnlyMemory<byte>, Task> onFrameAsync)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tlsHosts = tlsHosts ?? throw new ArgumentNullException(nameof(tlsHosts));
        _validateTlsCertificate = validateTlsCertificate ?? throw new ArgumentNullException(nameof(validateTlsCertificate));
        _onFrameAsync = onFrameAsync ?? throw new ArgumentNullException(nameof(onFrameAsync));
    }

    /// <summary>
    /// Sends one payload over a reusable stream (TCP/TLS) connection. RFC 3261 §18.4: on a failed
    /// send the stale connection is removed and the send retried once on a fresh connection.
    /// </summary>
    public async Task SendStreamAsync(IPEndPoint remoteEndPoint, ReadOnlyMemory<byte> payload, SipTransportProtocol transport, CancellationToken ct)
    {
        var targetEndPoint = SipTransportRuntimeUtilities.NormalizeWildcardEndPoint(remoteEndPoint);
        var connection = await GetOrCreateStreamAsync(targetEndPoint, transport, ct).ConfigureAwait(false);
        try
        {
            await connection.SendAsync(payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var staleKey = SipTransportRuntimeUtilities.BuildEndpointKey(transport, targetEndPoint);
            if (_streamConnections.TryRemove(staleKey, out var stale))
            {
                _logger.LogDebug(
                    ex,
                    "SIP {Transport} send to {Remote} failed; retrying on new connection (RFC §18.4).",
                    transport, targetEndPoint);
                stale.Dispose();
            }

            var fresh = await GetOrCreateStreamAsync(targetEndPoint, transport, ct).ConfigureAwait(false);
            await fresh.SendAsync(payload, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends one payload over a reusable WS/WSS connection.
    /// </summary>
    public async Task SendWebSocketAsync(IPEndPoint remoteEndPoint, ReadOnlyMemory<byte> payload, SipTransportProtocol transport, CancellationToken ct)
    {
        var targetEndPoint = SipTransportRuntimeUtilities.NormalizeWildcardEndPoint(remoteEndPoint);
        var connection = await GetOrCreateWebSocketAsync(targetEndPoint, transport, ct).ConfigureAwait(false);
        await connection.SendAsync(payload, ct).ConfigureAwait(false);
    }

    private async Task<SipStreamConnection> GetOrCreateStreamAsync(IPEndPoint remoteEndPoint, SipTransportProtocol transport, CancellationToken ct)
    {
        var key = SipTransportRuntimeUtilities.BuildEndpointKey(transport, remoteEndPoint);
        if (_streamConnections.TryGetValue(key, out var existing))
            return existing;

        await _streamGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_streamConnections.TryGetValue(key, out existing))
                return existing;

            var client = new TcpClient(remoteEndPoint.AddressFamily);
            await client.ConnectAsync(remoteEndPoint, ct).ConfigureAwait(false);
            Stream stream = client.GetStream();
            if (transport == SipTransportProtocol.Tls)
            {
                var targetHost = SipTransportRuntimeUtilities.SelectTlsTargetHost(_tlsHosts, key, remoteEndPoint.Address);
                stream = await SipTransportRuntimeUtilities.AuthenticateOutboundTlsAsync(
                    stream, targetHost, _validateTlsCertificate, ct).ConfigureAwait(false);
            }

            var created = new SipStreamConnection(
                transport,
                client,
                stream,
                _logger,
                _onFrameAsync,
                onClosed: () => _streamConnections.TryRemove(key, out _));
            _streamConnections[key] = created;
            return created;
        }
        finally
        {
            _streamGate.Release();
        }
    }

    private async Task<SipWebSocketConnection> GetOrCreateWebSocketAsync(IPEndPoint remoteEndPoint, SipTransportProtocol transport, CancellationToken ct)
    {
        var key = SipTransportRuntimeUtilities.BuildEndpointKey(transport, remoteEndPoint);
        if (_webSocketConnections.TryGetValue(key, out var existing))
            return existing;

        await _webSocketGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_webSocketConnections.TryGetValue(key, out existing))
                return existing;

            var socket = new ClientWebSocket();
            socket.Options.AddSubProtocol("sip"); // RFC 7118: SIP-over-WebSocket subprotocol
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            if (transport == SipTransportProtocol.Wss)
                socket.Options.RemoteCertificateValidationCallback = _validateTlsCertificate;

            // WSS: use the resolved SIP domain as the URI host so TLS SNI + certificate validation
            // run against the domain, not the IP. WS stays on the IP (no TLS involved).
            var wsHost = transport == SipTransportProtocol.Wss
                ? SipTransportRuntimeUtilities.SelectTlsTargetHost(_tlsHosts, key, remoteEndPoint.Address)
                : remoteEndPoint.Address.ToString();
            var targetUri = SipTransportRuntimeUtilities.BuildWebSocketTargetUri(wsHost, remoteEndPoint.Port, transport);
            await socket.ConnectAsync(targetUri, ct).ConfigureAwait(false);

            var created = new SipWebSocketConnection(
                transport,
                socket,
                remoteEndPoint,
                _logger,
                _onFrameAsync,
                onClosed: () => _webSocketConnections.TryRemove(key, out _));
            _webSocketConnections[key] = created;
            return created;
        }
        finally
        {
            _webSocketGate.Release();
        }
    }

    /// <summary>
    /// Disposes all pooled connections and gates.
    /// </summary>
    public void Dispose()
    {
        foreach (var connection in _streamConnections.Values)
        {
            try { connection.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed disposing outbound stream connection during shutdown."); }
        }
        _streamConnections.Clear();

        foreach (var connection in _webSocketConnections.Values)
        {
            try { connection.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed disposing outbound WebSocket connection during shutdown."); }
        }
        _webSocketConnections.Clear();

        _streamGate.Dispose();
        _webSocketGate.Dispose();
    }
}
