namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

/// <summary>
/// The per-packet cryptographic transform of an SRTP context: builds the IV, encrypts/decrypts the payload
/// and produces/verifies the authentication tag. The owning <c>SrtpContext</c> keeps everything else
/// (header parsing, rollover counter, replay window, per-SSRC state); this abstraction is only the varying
/// cipher, so AES-CM+HMAC (RFC 3711) and AEAD-AES-GCM (RFC 7714) plug in behind one seam.
/// </summary>
/// <remarks>
/// Not thread-safe: the owning context serialises every call under its own lock, as it did when the
/// AES-CM cipher and HMAC were inlined.
/// </remarks>
internal interface ISrtpPacketCipher : IDisposable
{
    /// <summary>Bytes appended to each packet for authentication (10 or 4 for AES-CM-HMAC, 16 for AEAD-GCM).</summary>
    int TagLength { get; }

    /// <summary>
    /// Encrypts the payload of <paramref name="rtpRegion"/> (the bytes at and after
    /// <paramref name="headerLength"/>) in place and writes the <see cref="TagLength"/>-byte
    /// <paramref name="tag"/>. The header stays clear-text (it is authenticated, not encrypted).
    /// </summary>
    /// <param name="ssrc">Synchronisation source of the packet (feeds the IV).</param>
    /// <param name="packetIndex">Extended 48-bit packet index (ROC &lt;&lt; 16 | SEQ).</param>
    void Protect(uint ssrc, ulong packetIndex, Span<byte> rtpRegion, int headerLength, Span<byte> tag);

    /// <summary>
    /// Verifies <paramref name="tag"/> over the header + encrypted payload and decrypts the payload of
    /// <paramref name="rtpRegion"/> in place. Throws <see cref="Context.SrtpAuthenticationException"/> when
    /// authentication fails — the caller must not trust <paramref name="rtpRegion"/> after a throw.
    /// </summary>
    void Unprotect(uint ssrc, ulong packetIndex, Span<byte> rtpRegion, int headerLength, ReadOnlySpan<byte> tag);
}
