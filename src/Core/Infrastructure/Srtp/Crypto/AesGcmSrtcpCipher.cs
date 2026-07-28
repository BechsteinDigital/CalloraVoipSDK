using System.Buffers.Binary;
using System.Security.Cryptography;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;

namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

/// <summary>
/// AEAD-AES-GCM SRTCP packet cipher (RFC 7714 §9). Encrypts the RTCP payload (after the 8-byte header),
/// authenticates the header + <c>E|index</c> word as AAD, and lays the packet out as
/// <c>[8-byte header][ciphertext][16-byte tag][E|index word]</c> — the index trailer sits <b>after</b> the
/// tag, unlike AES-CM. The 96-bit IV (§9.1) carries the 31-bit SRTCP index with the E-flag cleared; the
/// E-flag is set only in the trailer/AAD word (always 1 here — this SDK always encrypts RTCP under GCM).
/// </summary>
internal sealed class AesGcmSrtcpCipher : ISrtcpPacketCipher
{
    private const int TagBytes = 16;
    private const int RtcpHeaderLength = 8;
    private const int SrtcpIndexLength = 4;
    private const int IvLength = 12;
    private const int AadLength = RtcpHeaderLength + SrtcpIndexLength;
    private const uint EncryptionFlag = 0x8000_0000;
    private const uint SrtcpIndexMask = 0x7FFF_FFFF;

    private readonly AesGcm _gcm;
    private readonly byte[] _salt; // 12-byte session salt (RFC 7714 §9.1)

    public AesGcmSrtcpCipher(SrtpSessionKeys keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Salt.Length != IvLength)
            throw new ArgumentException(
                $"AEAD-GCM SRTCP requires a {IvLength}-byte session salt (RFC 7714 §9.1), got {keys.Salt.Length}.",
                nameof(keys));
        _gcm = new AesGcm(keys.CipherKey, TagBytes);
        _salt = keys.Salt;
    }

    public int TagLength => TagBytes;

    public byte[] Protect(uint ssrc, uint index, ReadOnlySpan<byte> rtcpPacket)
    {
        var payloadLen = rtcpPacket.Length - RtcpHeaderLength;
        var indexWord = index | EncryptionFlag; // E-flag = 1 (payload encrypted)

        // Layout: [header (8)][ciphertext (payloadLen)][tag (16)][E|index (4)].
        var result = GC.AllocateUninitializedArray<byte>(rtcpPacket.Length + TagBytes + SrtcpIndexLength);
        rtcpPacket[..RtcpHeaderLength].CopyTo(result);                                 // clear header
        rtcpPacket[RtcpHeaderLength..].CopyTo(result.AsSpan(RtcpHeaderLength));         // payload → encrypted in place

        // AAD = 8-byte header || E|index word (RFC 7714 §9.2).
        Span<byte> aad = stackalloc byte[AadLength];
        result.AsSpan(0, RtcpHeaderLength).CopyTo(aad);
        BinaryPrimitives.WriteUInt32BigEndian(aad[RtcpHeaderLength..], indexWord);

        Span<byte> iv = stackalloc byte[IvLength];
        BuildIv(ssrc, index, iv);

        var payload = result.AsSpan(RtcpHeaderLength, payloadLen);
        var tag = result.AsSpan(RtcpHeaderLength + payloadLen, TagBytes);
        _gcm.Encrypt(iv, payload, payload, tag, aad); // in place

        // Trailer: E|index word after the tag.
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(RtcpHeaderLength + payloadLen + TagBytes), indexWord);

        return result;
    }

    public (byte[] Rtcp, uint Index) Unprotect(uint ssrc, ReadOnlySpan<byte> srtcpPacket)
    {
        // Layout: [header (8)][ciphertext][tag (16)][E|index (4)].
        var indexWord = BinaryPrimitives.ReadUInt32BigEndian(srtcpPacket[^SrtcpIndexLength..]);
        var index = indexWord & SrtcpIndexMask;
        if ((indexWord & EncryptionFlag) == 0)
            throw new NotSupportedException("Unencrypted SRTCP (E-flag = 0) under AEAD-GCM is not supported.");

        var ciphertextLen = srtcpPacket.Length - RtcpHeaderLength - TagBytes - SrtcpIndexLength;
        var header = srtcpPacket[..RtcpHeaderLength];
        var ciphertext = srtcpPacket.Slice(RtcpHeaderLength, ciphertextLen);
        var tag = srtcpPacket.Slice(RtcpHeaderLength + ciphertextLen, TagBytes);

        Span<byte> aad = stackalloc byte[AadLength];
        header.CopyTo(aad);
        BinaryPrimitives.WriteUInt32BigEndian(aad[RtcpHeaderLength..], indexWord);

        Span<byte> iv = stackalloc byte[IvLength];
        BuildIv(ssrc, index, iv);

        var output = GC.AllocateUninitializedArray<byte>(RtcpHeaderLength + ciphertextLen);
        header.CopyTo(output);
        try
        {
            _gcm.Decrypt(iv, ciphertext, tag, output.AsSpan(RtcpHeaderLength), aad);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new SrtpAuthenticationException("SRTCP AEAD-GCM authentication tag mismatch.", ex);
        }

        return (output, index);
    }

    // RFC 7714 §9.1: pre-IV = 0x0000 || SSRC(4) || 0x0000 || (0-bit || 31-bit index), then XOR salt.
    private void BuildIv(uint ssrc, uint index, Span<byte> iv)
    {
        iv.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(iv[2..], ssrc);  // bytes 2..5
        BinaryPrimitives.WriteUInt32BigEndian(iv[8..], index); // bytes 8..11 (top bit already 0 — 31-bit index)
        for (var i = 0; i < IvLength; i++)
            iv[i] ^= _salt[i];
    }

    public void Dispose() => _gcm.Dispose();
}
