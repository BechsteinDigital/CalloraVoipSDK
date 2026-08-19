using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using CalloraVoipSdk;
using CalloraVoipSdk.Core.Infrastructure.Stun.Client;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// Connects a persistent TCP/TLS stream to a configured TURN server and gathers a stream relay candidate over it
/// (ADR-073 slice 4c-iii-b, #240) — the config-to-candidate layer above <see cref="WebRtcStreamRelayGatherer"/>
/// (which allocates over an already-connected stream). Where the UDP relay allocates on the shared media socket,
/// a stream relay needs its own connection (RFC 8656 §2.1), so this owns the connect + TLS handshake for a
/// <see cref="IceTransport.Tcp"/>/<see cref="IceTransport.Tls"/> TURN entry and hands the live connection to the
/// gatherer.
/// <para>
/// The connection is single-owner: the stream is built over a raw socket it fully owns
/// (<see cref="NetworkStream"/> with <c>ownsSocket: true</c>, wrapped by an <see cref="SslStream"/> with
/// <c>leaveInnerStreamOpen: false</c> for TLS), so disposing the stream closes everything. On success the returned
/// <see cref="StreamRelayCandidate"/> owns it; on any failure (unresolved server, connect refused, TLS handshake,
/// no allocation) the stream is disposed here and <see langword="null"/> is returned — one fewer candidate, never
/// a throw, exactly as a failed srflx or UDP relay query.
/// </para>
/// </summary>
internal sealed class WebRtcStreamRelayConnector
{
    private const int DefaultTurnTcpPort = 3478;
    private const int DefaultTurnTlsPort = 5349;

    private readonly WebRtcStreamRelayGatherer _gatherer;
    private readonly RemoteCertificateValidationCallback? _tlsRemoteCertificateValidationCallback;
    private readonly ILogger<WebRtcStreamRelayConnector> _logger;

    /// <summary>Creates the connector.</summary>
    /// <param name="codec">The STUN wire codec.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="tlsRemoteCertificateValidationCallback">
    /// TLS server-certificate validation, or <see langword="null"/> for the platform default (a real deployment's
    /// CA-signed TURN certificate validates without a callback; tests inject an accept-all callback for a
    /// self-signed server cert).
    /// </param>
    /// <param name="gatheringTimeout">The allocation gathering timeout, forwarded to the gatherer.</param>
    public WebRtcStreamRelayConnector(
        IStunMessageCodec codec,
        ILoggerFactory loggerFactory,
        RemoteCertificateValidationCallback? tlsRemoteCertificateValidationCallback = null,
        TimeSpan? gatheringTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _gatherer = new WebRtcStreamRelayGatherer(codec, loggerFactory, gatheringTimeout);
        _tlsRemoteCertificateValidationCallback = tlsRemoteCertificateValidationCallback;
        _logger = loggerFactory.CreateLogger<WebRtcStreamRelayConnector>();
    }

    /// <summary>
    /// Connects to <paramref name="server"/> (a TCP or TLS TURN entry) and gathers a stream relay candidate over
    /// the connection. Returns <see langword="null"/> for a non-stream transport, an unresolvable/unreachable
    /// server, a TLS handshake failure, or a failed allocation — never throws (cancellation aside).
    /// </summary>
    /// <param name="server">The TURN server entry; only <see cref="IceTransport.Tcp"/>/<see cref="IceTransport.Tls"/> are handled.</param>
    /// <param name="addressFamily">The address family to resolve the server in.</param>
    /// <param name="onInboundMedia">Sink for relayed ChannelData (post-nomination media), threaded to the gatherer.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The gathered stream relay candidate, or <see langword="null"/>.</returns>
    public async Task<StreamRelayCandidate?> ConnectAndGatherAsync(
        IceServerConfiguration server,
        AddressFamily addressFamily,
        Action<byte[]> onInboundMedia,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(onInboundMedia);

        if (server.Transport is not (IceTransport.Tcp or IceTransport.Tls))
            return null;

        var endpoint = await ResolveEndPointAsync(server, addressFamily, ct).ConfigureAwait(false);
        if (endpoint is null)
        {
            _logger.LogDebug(
                "Skipping stream relay for TURN server {Host}: no address resolved in family {Family}.",
                server.Host, addressFamily);
            return null;
        }

        Socket? socket = null;
        Stream? stream = null;
        try
        {
            socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(endpoint, ct).ConfigureAwait(false);
            // The stream fully owns the socket from here (ownsSocket: true), so disposing the stream — by the
            // candidate on teardown, or in the finally on any failure below — closes the connection.
            stream = new NetworkStream(socket, ownsSocket: true);
            socket = null;

            if (server.Transport == IceTransport.Tls)
            {
                var targetHost = string.IsNullOrWhiteSpace(server.Host) ? endpoint.Address.ToString() : server.Host;
                // leaveInnerStreamOpen: false — the SslStream now owns the NetworkStream (and thus the socket), so
                // assigning it to `stream` before the handshake means a handshake failure disposes the whole chain.
                var tls = new SslStream(stream, leaveInnerStreamOpen: false, _tlsRemoteCertificateValidationCallback);
                stream = tls;
                await tls.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions { TargetHost = targetHost }, ct).ConfigureAwait(false);
            }

            var candidate = await _gatherer.GatherAsync(
                stream, endpoint, WebRtcTurnServerResolver.BuildCredentials(server),
                lifetimeSeconds: null, onInboundMedia, ct).ConfigureAwait(false);
            // Ownership handed off: on success the candidate owns the stream; on a failed allocation the gatherer
            // already disposed it. Either way this method no longer owns it, so the finally must not touch it.
            stream = null;
            return candidate;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A refused connect or a TLS handshake failure is one fewer candidate, exactly as a failed srflx query.
            _logger.LogDebug(ex, "Could not gather a stream relay over {Transport} for TURN server {Host}.",
                server.Transport, server.Host);
            return null;
        }
        finally
        {
            // Dispose anything still owned here (a failure before the gatherer took the stream).
            if (stream is not null)
                await stream.DisposeAsync().ConfigureAwait(false);
            socket?.Dispose();
        }
    }

    private static async Task<IPEndPoint?> ResolveEndPointAsync(
        IceServerConfiguration server, AddressFamily addressFamily, CancellationToken ct)
    {
        var port = server.Port ?? (server.Transport == IceTransport.Tls ? DefaultTurnTlsPort : DefaultTurnTcpPort);
        if (IPAddress.TryParse(server.Host, out var ip))
            return new IPEndPoint(ip, port);

        var addresses = await Dns.GetHostAddressesAsync(server.Host, ct).ConfigureAwait(false);
        var address = StunIceProbe.PickAddressForFamily(addresses, addressFamily);
        return address is null ? null : new IPEndPoint(address, port);
    }
}
