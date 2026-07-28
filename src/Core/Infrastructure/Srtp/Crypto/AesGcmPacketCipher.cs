using System.Security.Cryptography;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;

namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

/// <summary>
/// AEAD-AES-GCM SRTP packet cipher (RFC 7714): authenticates the clear-text RTP header as AAD, encrypts
/// the payload in place and appends the full 16-byte tag. Adapts the low-level <see cref="AesGcmSrtpCipher"/>
/// to the per-packet <see cref="ISrtpPacketCipher"/> seam, splitting the extended packet index into the
/// (ROC, SEQ) the §8.1 IV needs.
/// </summary>
internal sealed class AesGcmPacketCipher : ISrtpPacketCipher
{
    private readonly AesGcmSrtpCipher _gcm;

    public AesGcmPacketCipher(SrtpSessionKeys keys) => _gcm = new AesGcmSrtpCipher(keys);

    public int TagLength => AesGcmSrtpCipher.TagLength;

    public void Protect(uint ssrc, ulong packetIndex, Span<byte> rtpRegion, int headerLength, Span<byte> tag)
    {
        var header = rtpRegion[..headerLength];
        var payload = rtpRegion[headerLength..];
        // In-place: AesGcm allows plaintext and ciphertext to be the same buffer.
        _gcm.Encrypt(ssrc, Roc(packetIndex), Seq(packetIndex), header, payload, payload, tag);
    }

    public void Unprotect(uint ssrc, ulong packetIndex, Span<byte> rtpRegion, int headerLength, ReadOnlySpan<byte> tag)
    {
        var header = rtpRegion[..headerLength];
        var payload = rtpRegion[headerLength..];
        try
        {
            _gcm.Decrypt(ssrc, Roc(packetIndex), Seq(packetIndex), header, payload, tag, payload);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            // Normalise onto the same failure type the AES-CM path raises, so callers handle one exception.
            throw new SrtpAuthenticationException("SRTP AEAD-GCM authentication tag mismatch.", ex);
        }
    }

    private static uint Roc(ulong packetIndex) => (uint)(packetIndex >> 16);
    private static ushort Seq(ulong packetIndex) => (ushort)packetIndex;

    public void Dispose() => _gcm.Dispose();
}
