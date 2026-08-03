namespace CalloraVoipSdk.Core.Infrastructure.Dtls;

/// <summary>
/// Fixed wire-safety caps applied to both DTLS-SRTP peers (#163 P1-1, rule K4). A DTLS-SRTP
/// association authenticates a single self-signed certificate (RFC 5763 §6.7.1) over a handful
/// of small flights, so these bounds sit far above any legitimate WebRTC/SIP handshake yet deny
/// a peer the ability to inflate per-handshake memory with an oversized message or a deep chain.
/// Pinned here so the bounds are explicit rather than silently inherited from BouncyCastle.
/// </summary>
internal static class DtlsHandshakeLimits
{
    /// <summary>
    /// Cap on a single reassembled handshake message (RFC 6347 §4.2.3). 32 KiB dwarfs a real
    /// ClientHello/Certificate flight but bounds reassembly memory per handshake.
    /// </summary>
    public const int MaxHandshakeMessageSize = 32 * 1024;

    /// <summary>
    /// Cap on the peer certificate chain length. Authentication is by SDP fingerprint over a
    /// single self-signed certificate, so a legitimate chain is length one; 10 leaves generous
    /// headroom while bounding chain-processing work. Matches the BouncyCastle default, pinned
    /// explicitly so the bound cannot drift with the library.
    /// </summary>
    public const int MaxCertificateChainLength = 10;
}
