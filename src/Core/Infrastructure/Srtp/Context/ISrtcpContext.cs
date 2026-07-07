namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Context;

/// <summary>
/// Encrypts and decrypts RTCP packets according to SRTCP (RFC 3711 §3.4).
/// One context covers one direction (send or receive) of the RTCP control path.
/// Authentication is mandatory; encryption is signalled per packet via the E flag.
/// </summary>
internal interface ISrtcpContext
{
    /// <summary>
    /// Encrypts and authenticates a plain RTCP compound packet for sending.
    /// Returns the SRTCP packet: the first 8 bytes stay cleartext, the remainder is
    /// encrypted, the 32-bit <c>E||SRTCP index</c> word is appended, and the truncated
    /// HMAC-SHA1 authentication tag is appended last (RFC 3711 §3.4).
    /// </summary>
    byte[] Protect(ReadOnlySpan<byte> rtcpPacket);

    /// <summary>
    /// Authenticates and decrypts an inbound SRTCP packet.
    /// Returns the plain RTCP compound packet (auth tag and <c>E||index</c> word removed).
    /// Throws <see cref="SrtpAuthenticationException"/> when the auth tag is invalid.
    /// Throws <see cref="SrtpReplayException"/> when the SRTCP index has already been seen.
    /// </summary>
    byte[] Unprotect(ReadOnlySpan<byte> srtcpPacket);
}
