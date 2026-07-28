using System.Buffers.Binary;
using System.Security.Cryptography;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;

namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

/// <summary>
/// AES-CM + HMAC-SHA1 SRTCP packet cipher (RFC 3711 §3.4). Encrypts the RTCP payload (everything after
/// the 8-byte header) with AES-CM keyed by the SRTCP session keys, appends the 32-bit E-flag/index word
/// and an 80-bit HMAC-SHA1 tag over the encrypted packet including that word. Layout:
/// <c>[clear header + encrypted payload][E|index][tag]</c>. Extracted verbatim from the former inline
/// <c>SrtcpContext</c> crypto so the on-the-wire behaviour is unchanged.
/// </summary>
internal sealed class AesCmSha1SrtcpCipher : ISrtcpPacketCipher
{
    private const int RtcpHeaderLength = 8;
    private const int SrtcpIndexLength = 4;
    private const int AuthTagFullLength = 20;
    private const uint EncryptionFlag = 0x8000_0000;
    private const uint SrtcpIndexMask = 0x7FFF_FFFF;

    private readonly AesCmCipher _cipher;
    private readonly byte[] _salt;
    private readonly byte[] _authKey;

    public AesCmSha1SrtcpCipher(SrtpSessionKeys keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _cipher = new AesCmCipher(keys.CipherKey);
        _salt = keys.Salt;
        _authKey = keys.AuthKey
            ?? throw new ArgumentException("AES-CM SRTCP requires an HMAC auth key.", nameof(keys));
    }

    // SRTCP always carries an 80-bit HMAC tag for every suite (the SHA1_32 truncation is SRTP-only).
    public int TagLength => 10;

    public byte[] Protect(uint ssrc, uint index, ReadOnlySpan<byte> rtcpPacket)
    {
        var encryptedLen = rtcpPacket.Length - RtcpHeaderLength;

        // Layout: [clear header + encrypted payload][E|index (4)][auth tag].
        var result = GC.AllocateUninitializedArray<byte>(rtcpPacket.Length + SrtcpIndexLength + TagLength);
        rtcpPacket.CopyTo(result);

        if (encryptedLen > 0)
        {
            Span<byte> iv = stackalloc byte[16];
            BuildIv(ssrc, index, iv);
            _cipher.Xor(iv, result.AsSpan(RtcpHeaderLength, encryptedLen));
        }

        // E-flag = 1 (payload encrypted) plus the 31-bit index.
        BinaryPrimitives.WriteUInt32BigEndian(
            result.AsSpan(rtcpPacket.Length, SrtcpIndexLength), index | EncryptionFlag);

        // Auth tag over the encrypted packet including the E|index word (RFC 3711 §3.4).
        var authedLen = rtcpPacket.Length + SrtcpIndexLength;
        Span<byte> tag = stackalloc byte[AuthTagFullLength];
        ComputeAuthTag(result.AsSpan(0, authedLen), tag);
        tag[..TagLength].CopyTo(result.AsSpan(authedLen, TagLength));

        return result;
    }

    public (byte[] Rtcp, uint Index) Unprotect(uint ssrc, ReadOnlySpan<byte> srtcpPacket)
    {
        var authedLen = srtcpPacket.Length - TagLength;
        var authedSpan = srtcpPacket[..authedLen];
        var receivedTag = srtcpPacket[authedLen..];

        // Verify auth tag before decryption (RFC 3711 §3.3 — verify-then-decrypt).
        Span<byte> expectedTag = stackalloc byte[AuthTagFullLength];
        ComputeAuthTag(authedSpan, expectedTag);
        if (!CryptographicOperations.FixedTimeEquals(receivedTag, expectedTag[..TagLength]))
            throw new SrtpAuthenticationException("SRTCP authentication tag mismatch.");

        var indexWord = BinaryPrimitives.ReadUInt32BigEndian(authedSpan[(authedLen - SrtcpIndexLength)..]);
        var encrypted = (indexWord & EncryptionFlag) != 0;
        var index = indexWord & SrtcpIndexMask;

        var rtcpLen = authedLen - SrtcpIndexLength;
        var output = GC.AllocateUninitializedArray<byte>(rtcpLen);
        authedSpan[..rtcpLen].CopyTo(output);

        // Decrypt the payload when the E-flag is set.
        var encryptedLen = rtcpLen - RtcpHeaderLength;
        if (encrypted && encryptedLen > 0)
        {
            Span<byte> iv = stackalloc byte[16];
            BuildIv(ssrc, index, iv);
            _cipher.Xor(iv, output.AsSpan(RtcpHeaderLength, encryptedLen));
        }

        return (output, index);
    }

    // IV = (salt XOR (SSRC * 2^64) XOR (index * 2^16)) as 128-bit big-endian (RFC 3711 §4.1). The 31-bit
    // SRTCP index takes the place of the SRTP packet index; no rollover counter feeds the IV.
    private void BuildIv(uint ssrc, uint index, Span<byte> iv)
    {
        iv.Clear();
        _salt.CopyTo(iv);

        iv[4] ^= (byte)(ssrc >> 24);
        iv[5] ^= (byte)(ssrc >> 16);
        iv[6] ^= (byte)(ssrc >>  8);
        iv[7] ^= (byte) ssrc;

        iv[10] ^= (byte)(index >> 24);
        iv[11] ^= (byte)(index >> 16);
        iv[12] ^= (byte)(index >>  8);
        iv[13] ^= (byte) index;
    }

    // RFC 3711 §4.2: HMAC-SHA1 over the encrypted packet + E|index. No ROC — the SRTCP index is already
    // part of the authenticated data.
    private void ComputeAuthTag(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, _authKey);
        hmac.AppendData(data);

        if (!hmac.TryGetHashAndReset(destination, out var bytesWritten) || bytesWritten != AuthTagFullLength)
            throw new CryptographicException("Failed to compute SRTCP HMAC-SHA1 authentication tag.");
    }

    public void Dispose() => _cipher.Dispose();
}
