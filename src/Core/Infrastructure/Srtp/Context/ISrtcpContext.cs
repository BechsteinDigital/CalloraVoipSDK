namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Context;

/// <summary>
/// Encrypts and decrypts RTCP packets according to SRTCP (RFC 3711 §3.4).
/// One context covers one direction (send or receive); it keeps its own SRTCP index
/// (sender) or replay window (receiver). Disposing a context zeroes its derived session
/// keys; the owner that created the context is responsible for disposing it.
/// </summary>
internal interface ISrtcpContext : IDisposable
{
    /// <summary>
    /// Encrypts and authenticates a plain RTCP compound packet for sending. Returns the
    /// SRTCP packet: the 8-byte header stays clear, the remainder is encrypted, and the
    /// 4-byte <c>E</c>-flag/SRTCP-index word plus the auth tag are appended (RFC 3711 §3.4).
    /// </summary>
    byte[] ProtectRtcp(ReadOnlySpan<byte> rtcpPacket);

    /// <summary>
    /// Authenticates and decrypts an inbound SRTCP packet. Returns the plain RTCP packet
    /// (index word and auth tag removed, encrypted portion decrypted).
    /// Throws <see cref="SrtpAuthenticationException"/> when the auth tag is invalid.
    /// Throws <see cref="SrtpReplayException"/> when the SRTCP index has already been seen.
    /// </summary>
    byte[] UnprotectRtcp(ReadOnlySpan<byte> srtcpPacket);
}
