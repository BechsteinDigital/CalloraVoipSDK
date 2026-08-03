using Org.BouncyCastle.Tls;

namespace CalloraVoipSdk.Core.Infrastructure.Dtls;

/// <summary>
/// Performs the DTLS-SRTP handshake (RFC 5763/5764) over a datagram transport that is
/// multiplexed with RTP on the media socket. Abstracted for dependency injection and
/// deterministic handshake testing.
/// </summary>
internal interface IDtlsSrtpHandshaker
{
    /// <summary>
    /// Runs the handshake in the given role and returns the exported SRTP keys together
    /// with the live DTLS transport. Cancellation closes the transport, which aborts the
    /// handshake.
    /// </summary>
    /// <param name="role">DTLS role from the SDP <c>a=setup</c> negotiation (RFC 5763 §5).</param>
    /// <param name="transport">Datagram transport carrying the DTLS records.</param>
    /// <param name="localCertificate">Local identity; its fingerprint was signaled in SDP.</param>
    /// <param name="expectedRemoteFingerprint">Peer fingerprint from the peer's SDP.</param>
    /// <param name="cancellationToken">Aborts the handshake (e.g. session teardown or timeout).</param>
    /// <param name="serverCookieClientId">
    /// Server role only: opt-in stateless DTLS cookie (RFC 6347 §4.2.1). When non-empty, it is the
    /// opaque identity of the remote peer (e.g. its media IP/port) the cookie MAC binds to, so a
    /// spoofed source cannot reach the amplified certificate flight — for legs without ICE source
    /// validation (SIP). When empty (the default), no cookie exchange runs and the first ClientHello
    /// is answered directly: WebRTC legs are already source-validated by ICE, and a browser DTLS
    /// client never expects a server-initiated HelloVerifyRequest. Ignored for the client role.
    /// </param>
    /// <exception cref="DtlsSrtpHandshakeException">The handshake failed or was aborted.</exception>
    Task<DtlsSrtpHandshakeResult> HandshakeAsync(
        DtlsRole role,
        DatagramTransport transport,
        DtlsCertificate localCertificate,
        DtlsFingerprint expectedRemoteFingerprint,
        CancellationToken cancellationToken = default,
        ReadOnlyMemory<byte> serverCookieClientId = default);
}
