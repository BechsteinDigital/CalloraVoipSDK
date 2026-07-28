using System.Buffers.Binary;
using System.Security.Cryptography;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;

namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

/// <summary>
/// AES-CM + HMAC-SHA1 SRTP packet cipher (RFC 3711 §4.1/§4.2): the classic non-AEAD transform. Encrypts
/// the payload with AES counter mode and authenticates header + encrypted payload + ROC with a truncated
/// HMAC-SHA1 tag (10 or 4 bytes). Extracted verbatim from the former inline <c>SrtpContext</c> crypto so
/// the on-the-wire behaviour is unchanged.
/// </summary>
internal sealed class AesCmSha1PacketCipher : ISrtpPacketCipher
{
    private const int AuthTagFullLength = 20; // full HMAC-SHA1 output before truncation

    private readonly AesCmCipher _cipher;
    private readonly byte[] _salt;    // 14-byte session salt (RFC 3711 §4.1 IV)
    private readonly byte[] _authKey; // 20-byte HMAC-SHA1 session key

    public AesCmSha1PacketCipher(SrtpSessionKeys keys, int tagLength)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _cipher = new AesCmCipher(keys.CipherKey);
        _salt = keys.Salt;
        _authKey = keys.AuthKey
            ?? throw new ArgumentException("AES-CM SRTP requires an HMAC auth key.", nameof(keys));
        TagLength = tagLength;
    }

    public int TagLength { get; }

    public void Protect(uint ssrc, ulong packetIndex, Span<byte> rtpRegion, int headerLength, Span<byte> tag)
    {
        // 1. Encrypt payload in place.
        var payloadLength = rtpRegion.Length - headerLength;
        if (payloadLength > 0)
        {
            Span<byte> iv = stackalloc byte[16];
            BuildIv(ssrc, packetIndex, iv);
            _cipher.Xor(iv, rtpRegion[headerLength..]);
        }

        // 2. Auth over header + encrypted payload + ROC, truncated to the tag length.
        Span<byte> full = stackalloc byte[AuthTagFullLength];
        ComputeAuthTag(rtpRegion, Roc(packetIndex), full);
        full[..TagLength].CopyTo(tag);
    }

    public void Unprotect(uint ssrc, ulong packetIndex, Span<byte> rtpRegion, int headerLength, ReadOnlySpan<byte> tag)
    {
        // 1. Verify auth over the still-encrypted region before releasing plaintext (RFC 3711 §3.3).
        Span<byte> expected = stackalloc byte[AuthTagFullLength];
        ComputeAuthTag(rtpRegion, Roc(packetIndex), expected);
        if (!CryptographicOperations.FixedTimeEquals(tag, expected[..TagLength]))
            throw new SrtpAuthenticationException("SRTP authentication tag mismatch.");

        // 2. Decrypt payload in place.
        var payloadLength = rtpRegion.Length - headerLength;
        if (payloadLength > 0)
        {
            Span<byte> iv = stackalloc byte[16];
            BuildIv(ssrc, packetIndex, iv);
            _cipher.Xor(iv, rtpRegion[headerLength..]);
        }
    }

    // RFC 3711 §4.1: IV = (salt XOR (SSRC * 2^64) XOR (index * 2^16)) as 128-bit big-endian.
    private void BuildIv(uint ssrc, ulong index, Span<byte> iv)
    {
        iv.Clear();
        _salt.CopyTo(iv); // k_s * 2^16 leaves bytes 14..15 for the block counter.

        iv[4] ^= (byte)(ssrc >> 24);
        iv[5] ^= (byte)(ssrc >> 16);
        iv[6] ^= (byte)(ssrc >>  8);
        iv[7] ^= (byte) ssrc;

        iv[ 8] ^= (byte)(index >> 40);
        iv[ 9] ^= (byte)(index >> 32);
        iv[10] ^= (byte)(index >> 24);
        iv[11] ^= (byte)(index >> 16);
        iv[12] ^= (byte)(index >>  8);
        iv[13] ^= (byte) index;
    }

    // RFC 3711 §4.2: HMAC-SHA1 over the packet plus the 32-bit ROC.
    private void ComputeAuthTag(ReadOnlySpan<byte> data, uint roc, Span<byte> destination)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, _authKey);
        hmac.AppendData(data);

        Span<byte> rocBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(rocBytes, roc);
        hmac.AppendData(rocBytes);

        if (!hmac.TryGetHashAndReset(destination, out var bytesWritten) || bytesWritten != AuthTagFullLength)
            throw new CryptographicException("Failed to compute SRTP HMAC-SHA1 authentication tag.");
    }

    private static uint Roc(ulong packetIndex) => (uint)(packetIndex >> 16);

    public void Dispose() => _cipher.Dispose();
}
