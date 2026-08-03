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
    /// Server role only: opaque identity of the remote peer (e.g. its media IP/port) that the
    /// stateless DTLS cookie is bound to (RFC 6347 §4.2.1). A spoofed source cannot echo a valid
    /// cookie, so it never reaches the amplified certificate flight. <b>Required (non-empty) for
    /// the server role</b> — an empty value throws, since a cookie without source binding is a
    /// wiring error. Ignored for the client role.
    /// </param>
    /// <exception cref="DtlsSrtpHandshakeException">The handshake failed or was aborted.</exception>
    /// <exception cref="ArgumentException">
    /// Server role with an empty <paramref name="serverCookieClientId"/>.
    /// </exception>
    Task<DtlsSrtpHandshakeResult> HandshakeAsync(
        DtlsRole role,
        DatagramTransport transport,
        DtlsCertificate localCertificate,
        DtlsFingerprint expectedRemoteFingerprint,
        CancellationToken cancellationToken = default,
        ReadOnlyMemory<byte> serverCookieClientId = default);
}
