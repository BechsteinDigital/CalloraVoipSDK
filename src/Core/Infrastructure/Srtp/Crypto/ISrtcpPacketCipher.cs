namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

/// <summary>
/// The cryptographic transform of an SRTCP context. Unlike SRTP, the AES-CM and AEAD-GCM SRTCP layouts
/// differ (AES-CM appends <c>[E|index][tag]</c>, GCM appends <c>[tag][E|index]</c> and folds the index
/// word into the AAD), so this seam owns the whole packet assembly, not just the cipher. The owning
/// <c>SrtcpContext</c> keeps the per-SSRC index generation and replay window.
/// </summary>
/// <remarks>Not thread-safe: the owning context serialises every call under its own lock.</remarks>
internal interface ISrtcpPacketCipher : IDisposable
{
    /// <summary>Authentication tag length (10 for AES-CM-HMAC-SHA1, 16 for AEAD-GCM).</summary>
    int TagLength { get; }

    /// <summary>Assembles the full SRTCP packet from an RTCP packet and this sender's 31-bit SRTCP index.</summary>
    byte[] Protect(uint ssrc, uint index, ReadOnlySpan<byte> rtcpPacket);

    /// <summary>
    /// Verifies and decrypts an SRTCP packet, returning the recovered RTCP packet and the SRTCP index it
    /// carried (for the caller's replay check). Throws <see cref="Context.SrtpAuthenticationException"/>
    /// when authentication fails.
    /// </summary>
    (byte[] Rtcp, uint Index) Unprotect(uint ssrc, ReadOnlySpan<byte> srtcpPacket);
}
