namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Context;

/// <summary>
/// Encrypts and decrypts RTP packets according to SRTP (RFC 3711).
/// One context covers one direction (send or receive) under one shared master key; it tracks the
/// rollover counter and replay window per SSRC (RFC 3711 §3.2.1) internally, so it serves every
/// SSRC a BUNDLE transport (RFC 8843) carries — not just a single stream.
/// Disposing a context zeroes its derived session keys; the owner that created the
/// context is responsible for disposing it once the media session ends.
/// </summary>
internal interface ISrtpContext : IDisposable
{
    /// <summary>
    /// Encrypts and authenticates a plain RTP packet for sending.
    /// Returns the SRTP packet (header unchanged, payload encrypted, auth tag appended).
    /// </summary>
    byte[] Protect(ReadOnlySpan<byte> rtpPacket);

    /// <summary>
    /// Authenticates and decrypts an inbound SRTP packet.
    /// Returns the plain RTP packet (header unchanged, payload decrypted, auth tag removed).
    /// Throws <see cref="SrtpAuthenticationException"/> when the auth tag is invalid.
    /// Throws <see cref="SrtpReplayException"/> when the packet index has already been seen.
    /// </summary>
    byte[] Unprotect(ReadOnlySpan<byte> srtpPacket);
}
