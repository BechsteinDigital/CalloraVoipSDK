using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

/// <summary>
/// Owns the lifecycle of accepted inbound connection-oriented SIP connections (TCP/TLS/WS/WSS): it admits
/// each one against the global and per-IP caps, bounds its TLS handshake / WebSocket upgrade against the
/// slowloris deadline, tracks the live connections so a response can be routed back over the exact one a
/// request arrived on, and frees the admission slot when the connection closes (#158 P1-2/P1-3). Extracted
/// from <see cref="SipTransportRuntime"/> as an injected collaborator; the runtime keeps the listeners and
/// dispatch, and delegates acceptance, per-connection lookup and teardown here.
/// </summary>
internal sealed class SipInboundConnectionAcceptor
{
    private readonly SipTransportOptions _options;
    private readonly X509Certificate2? _tlsCertificate;
    private readonly ILogger _logger;
    private readonly Func<IPEndPoint, SipTransportProtocol, ReadOnlyMemory<byte>, int?, Task> _onFrameAsync;
    private readonly SipConnectionAdmissionControl _admissionControl;
    private readonly ConcurrentDictionary<int, SipStreamConnection> _streamConnections = new();
    private readonly ConcurrentDictionary<int, SipWebSocketConnection> _webSocketConnections = new();

    private int _streamConnectionId;
    private int _webSocketConnectionId;

    /// <summary>
    /// Creates an acceptor bound to one transport runtime's options, TLS certificate, logger and inbound
    /// frame callback.
    /// </summary>
    public SipInboundConnectionAcceptor(
        SipTransportOptions options,
        X509Certificate2? tlsCertificate,
        ILogger logger,
        Func<IPEndPoint, SipTransportProtocol, ReadOnlyMemory<byte>, int?, Task> onFrameAsync)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tlsCertificate = tlsCertificate;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _onFrameAsync = onFrameAsync ?? throw new ArgumentNullException(nameof(onFrameAsync));

        // A positive handshake timeout must fit CancellationTokenSource.CancelAfter's timer limit (~49.7 days).
        if (_options.HandshakeTimeout > TimeSpan.Zero
            && _options.HandshakeTimeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), "HandshakeTimeout exceeds the maximum supported deadline (~49.7 days).");
        }

        _admissionControl = new SipConnectionAdmissionControl(
            _options.MaxConcurrentInboundConnections,
            _options.MaxInboundConnectionsPerRemote);
    }

    /// <summary>
    /// Tries to look up a live accepted stream (TCP/TLS) connection by its id.
    /// </summary>
    public bool TryGetStreamConnection(int id, out SipStreamConnection? connection) =>
        _streamConnections.TryGetValue(id, out connection);

    /// <summary>
    /// Tries to look up a live accepted WebSocket (WS/WSS) connection by its id.
    /// </summary>
    public bool TryGetWebSocketConnection(int id, out SipWebSocketConnection? connection) =>
        _webSocketConnections.TryGetValue(id, out connection);

    /// <summary>
    /// Admits, TLS-handshakes (bounded) and registers one accepted inbound TCP/TLS connection.
    /// </summary>
    public async Task AcceptStreamConnectionAsync(
        TcpClient client,
        SipTransportProtocol protocol,
        CancellationToken ct)
    {
        Stream? stream = null;
        IDisposable? admissionLease = null;
        var admitted = false;
        try
        {
            if (client.Client.RemoteEndPoint is not IPEndPoint remote)
                throw new InvalidOperationException("Accepted SIP stream connection has no remote endpoint.");

            // #158 P1-3: admit against the global and per-IP connection caps before doing any work; a
            // connection beyond the cap is dropped so no peer can pin an unbounded number of slots.
            admissionLease = _admissionControl.TryAdmit(remote.Address);
            if (admissionLease is null)
            {
                _logger.LogWarning(
                    "SIP {Transport} connection from {Remote} rejected: inbound connection cap reached.", protocol, remote);
                client.Dispose();
                return;
            }

            stream = client.GetStream();
            if (protocol == SipTransportProtocol.Tls)
            {
                if (_tlsCertificate is null)
                    throw new InvalidOperationException("TLS listener has no certificate.");

                var sslStream = new SslStream(stream, leaveInnerStreamOpen: false);
                // #158 P1-3 (K4): bound the TLS handshake so a peer that dribbles or stalls the ClientHello
                // cannot hold its admission slot indefinitely (slowloris).
                using var handshakeDeadline = CreateDeadline(ct, _options.HandshakeTimeout);
                try
                {
                    await sslStream.AuthenticateAsServerAsync(
                            new SslServerAuthenticationOptions
                            {
                                ServerCertificate = _tlsCertificate,
                                ClientCertificateRequired = false,
                                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                            },
                            handshakeDeadline?.Token ?? ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (handshakeDeadline?.IsCancellationRequested == true && !ct.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "SIP TLS handshake deadline ({Timeout}) exceeded for {Remote}; dropping connection (slowloris).",
                        _options.HandshakeTimeout, remote);
                    throw;
                }
                stream = sslStream;
            }

            var lease = admissionLease;
            var id = Interlocked.Increment(ref _streamConnectionId);
            // Tag every frame from this connection with its id so a response can be routed straight back over
            // it (#158 P1-2). The id is captured before the receive loop starts inside the ctor. The close
            // callback frees the admission slot (#158 P1-3).
            var connection = new SipStreamConnection(
                protocol,
                client,
                stream,
                _logger,
                (remoteEndPoint, transport, payload) => _onFrameAsync(remoteEndPoint, transport, payload, id),
                onClosed: () =>
                {
                    _streamConnections.TryRemove(id, out _);
                    lease.Dispose();
                });
            _streamConnections[id] = connection;
            admitted = true;
            _logger.LogDebug("Accepted SIP {Transport} stream from {Remote}.", protocol, connection.RemoteEndPoint);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to register inbound SIP {Transport} stream connection.", protocol);
            try
            {
                stream?.Dispose();
            }
            catch (Exception disposeEx)
            {
                _logger.LogDebug(disposeEx, "Failed disposing SIP {Transport} stream during cleanup.", protocol);
            }

            try
            {
                client.Dispose();
            }
            catch (Exception disposeEx)
            {
                _logger.LogDebug(disposeEx, "Failed disposing SIP {Transport} client during cleanup.", protocol);
            }
        }
        finally
        {
            // Release the admission slot on any pre-registration failure; once registered, the close callback owns it.
            if (!admitted)
                admissionLease?.Dispose();
        }
    }

    /// <summary>
    /// Admits, upgrades (bounded) and registers one accepted inbound WS/WSS connection.
    /// </summary>
    public async Task AcceptWebSocketConnectionAsync(
        HttpListenerContext context,
        SipTransportProtocol protocol,
        CancellationToken ct)
    {
        IDisposable? admissionLease = null;
        var admitted = false;
        try
        {
            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                context.Response.Close();
                return;
            }

            // RFC 7118 §5: a SIP-over-WebSocket client must offer the "sip" subprotocol. Reject the
            // upgrade when it does not, rather than accepting a WebSocket with no negotiated
            // subprotocol that could not carry SIP framing (HARD-E6).
            var sipSubProtocol = SipTransportRuntimeUtilities.SelectOfferedSipSubProtocol(context.Request);
            if (sipSubProtocol is null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                context.Response.Close();
                return;
            }

            var remoteEndPoint = context.Request.RemoteEndPoint ?? new IPEndPoint(IPAddress.Loopback, 0);

            // #158 P1-3: admit against the global and per-IP connection caps before the upgrade.
            admissionLease = _admissionControl.TryAdmit(remoteEndPoint.Address);
            if (admissionLease is null)
            {
                _logger.LogWarning(
                    "SIP {Transport} WebSocket from {Remote} rejected: inbound connection cap reached.", protocol, remoteEndPoint);
                context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                context.Response.Close();
                return;
            }

            // #158 P1-3 (K4): bound the WebSocket upgrade so a peer that stalls it cannot hold its slot.
            using var handshakeDeadline = CreateDeadline(ct, _options.HandshakeTimeout);
            HttpListenerWebSocketContext wsContext;
            try
            {
                wsContext = await context.AcceptWebSocketAsync(sipSubProtocol)
                    .WaitAsync(handshakeDeadline?.Token ?? ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (handshakeDeadline?.IsCancellationRequested == true && !ct.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "SIP {Transport} WebSocket upgrade deadline ({Timeout}) exceeded for {Remote}; dropping connection (slowloris).",
                    protocol, _options.HandshakeTimeout, remoteEndPoint);
                throw;
            }

            var lease = admissionLease;
            var id = Interlocked.Increment(ref _webSocketConnectionId);
            // Tag every frame from this connection with its id so a response can be routed straight back over
            // it (#158 P1-2). The id is captured before the receive loop starts inside the ctor. The close
            // callback frees the admission slot (#158 P1-3).
            var connection = new SipWebSocketConnection(
                protocol,
                wsContext.WebSocket,
                remoteEndPoint,
                _logger,
                (remote, transport, payload) => _onFrameAsync(remote, transport, payload, id),
                onClosed: () =>
                {
                    _webSocketConnections.TryRemove(id, out _);
                    lease.Dispose();
                });
            _webSocketConnections[id] = connection;
            admitted = true;
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
        finally
        {
            // Release the admission slot on any pre-registration failure; once registered, the close callback owns it.
            if (!admitted)
                admissionLease?.Dispose();
        }
    }

    /// <summary>
    /// Disposes and clears all live accepted connections.
    /// </summary>
    public void DisposeConnections()
    {
        foreach (var connection in _streamConnections.Values)
            connection.Dispose();
        foreach (var connection in _webSocketConnections.Values)
            connection.Dispose();

        _streamConnections.Clear();
        _webSocketConnections.Clear();
    }

    /// <summary>
    /// Builds a linked token source that also cancels after <paramref name="timeout"/>, or null when no
    /// deadline is configured (timeout ≤ 0 or <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>).
    /// </summary>
    private static CancellationTokenSource? CreateDeadline(CancellationToken ct, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            return null;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return cts;
    }
}
