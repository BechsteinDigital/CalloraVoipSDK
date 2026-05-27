using System.Buffers.Binary;
using System.Security.Cryptography;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Context;

/// <summary>
/// SRTP context for one SSRC and one direction (RFC 3711).
/// Implements AES-CM encryption (§4.1), HMAC-SHA1 authentication (§4.2),
/// and replay protection via a 64-packet sliding window (§3.3.2).
/// </summary>
internal sealed class SrtpContext : ISrtpContext
{
    private const int AuthTagFullLength = 20;
    private const int AesCmBlockLength = 16;
    private const int MaxAesCmKeystreamBytes = 1 << 20;
    private readonly SrtpSessionKeys _keys;
    private readonly SrtpCryptoSuite _suite;
    private readonly int _authTagLength;

    // Sender-side index: tracks the last outbound packet index for ROC advancement.
    // Separate from the receiver replay window so Protect and Unprotect can safely
    // share one context instance (though RFC 3711 recommends per-direction contexts).
    private ulong _senderIndex;

    // Receiver replay window: 64-bit bitmap, high bit = newest packet (RFC 3711 §3.3.2)
    private ulong _replayWindowIndex;
    private ulong _replayWindowBitmap;
    private const int ReplayWindowSize = 64;

    public SrtpContext(SrtpKeyMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        _suite         = material.Suite;
        _keys          = SrtpKeyDerivation.Derive(material);
        _authTagLength = material.Suite is SrtpCryptoSuite.AesCm128HmacSha1_32
                                       or SrtpCryptoSuite.AesCm256HmacSha1_32
            ? 4 : 10;
    }

    // -------------------------------------------------------------------------
    // Protect (encrypt outbound)
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public byte[] Protect(ReadOnlySpan<byte> rtpPacket)
    {
        if (rtpPacket.Length < 12)
            throw new ArgumentException("RTP packet too short (minimum 12 bytes).", nameof(rtpPacket));

        var headerLen   = GetRtpHeaderLength(rtpPacket);
        var payloadLen  = rtpPacket.Length - headerLen;
        var ssrc        = BinaryPrimitives.ReadUInt32BigEndian(rtpPacket[8..]);
        var seq         = BinaryPrimitives.ReadUInt16BigEndian(rtpPacket[2..]);
        var packetIndex = ComputeSenderIndex(seq);

        // Encrypt payload in-place in final SRTP buffer.
        var result = GC.AllocateUninitializedArray<byte>(rtpPacket.Length + _authTagLength);
        rtpPacket.CopyTo(result);
        if (payloadLen > 0)
        {
            Span<byte> iv = stackalloc byte[16];
            BuildIv(ssrc, packetIndex, iv);
            AesCmXor(_keys.CipherKey, iv, result.AsSpan(headerLen, payloadLen));
        }

        // Append auth tag over header + encrypted payload
        Span<byte> tag = stackalloc byte[AuthTagFullLength];
        ComputeAuthTag(result.AsSpan(0, rtpPacket.Length), GetRoc(packetIndex), tag);
        tag[.._authTagLength].CopyTo(result.AsSpan(rtpPacket.Length, _authTagLength));

        // Advance sender-side index so ROC is correct for subsequent packets
        if (packetIndex >= _senderIndex)
            _senderIndex = packetIndex;

        return result;
    }

    // -------------------------------------------------------------------------
    // Unprotect (decrypt inbound)
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public byte[] Unprotect(ReadOnlySpan<byte> srtpPacket)
    {
        if (srtpPacket.Length < 12 + _authTagLength)
            throw new ArgumentException("SRTP packet too short.", nameof(srtpPacket));

        var rtpLen     = srtpPacket.Length - _authTagLength;
        var rtpSpan    = srtpPacket[..rtpLen];
        var receivedTag = srtpPacket[rtpLen..];
        var seq  = BinaryPrimitives.ReadUInt16BigEndian(rtpSpan[2..]);
        var ssrc = BinaryPrimitives.ReadUInt32BigEndian(rtpSpan[8..]);
        var packetIndex = ComputePacketIndex(seq);

        // 1. Verify auth tag before decryption (RFC 3711 §3.3 — verify-then-decrypt)
        Span<byte> expectedTag = stackalloc byte[AuthTagFullLength];
        ComputeAuthTag(rtpSpan, GetRoc(packetIndex), expectedTag);
        if (!CryptographicOperations.FixedTimeEquals(receivedTag, expectedTag[.._authTagLength]))
            throw new SrtpAuthenticationException("SRTP authentication tag mismatch.");

        // 2. Replay check (RFC 3711 §3.3.2)
        CheckReplay(packetIndex);

        // 3. Decrypt
        var output = GC.AllocateUninitializedArray<byte>(rtpLen);
        rtpSpan.CopyTo(output);
        var headerLen = GetRtpHeaderLength(rtpSpan);
        var payloadLen = output.Length - headerLen;

        if (payloadLen > 0)
        {
            Span<byte> iv = stackalloc byte[16];
            BuildIv(ssrc, packetIndex, iv);
            AesCmXor(_keys.CipherKey, iv, output.AsSpan(headerLen, payloadLen));
        }

        // 4. Update replay window
        UpdateReplayWindow(packetIndex);

        return output;
    }

    // -------------------------------------------------------------------------
    // AES-CM encryption/decryption (symmetric, RFC 3711 §4.1)
    // -------------------------------------------------------------------------

    private static void AesCmXor(byte[] key, ReadOnlySpan<byte> iv, Span<byte> data)
    {
        if (data.Length > MaxAesCmKeystreamBytes)
            throw new CryptographicException("SRTP AES-CM payload exceeds the RFC3711 2^16-block keystream limit.");

        using var aes  = Aes.Create();
        aes.Key        = key;
        aes.Mode       = CipherMode.ECB;
        aes.Padding    = PaddingMode.None;
        using var enc = aes.CreateEncryptor();

        var block = new byte[16];
        var counterIv = new byte[16];
        iv.CopyTo(counterIv);
        var offset = 0;
        var counter = 0;

        while (offset < data.Length)
        {
            counterIv[14] = (byte)(counter >> 8);
            counterIv[15] = (byte)counter;

            enc.TransformBlock(counterIv, 0, AesCmBlockLength, block, 0);

            var chunk = Math.Min(AesCmBlockLength, data.Length - offset);
            for (var i = 0; i < chunk; i++)
                data[offset + i] ^= block[i];

            offset += chunk;
            counter++;
        }
    }

    // -------------------------------------------------------------------------
    // IV construction (RFC 3711 §4.1)
    // IV = (salt XOR (SSRC * 2^64) XOR (index * 2^16)) as 128-bit big-endian
    // -------------------------------------------------------------------------

    private void BuildIv(uint ssrc, ulong index, Span<byte> iv)
    {
        iv.Clear();
        _keys.Salt.CopyTo(iv); // k_s * 2^16 leaves bytes 14..15 reserved for the block counter.

        // XOR SSRC into bytes 4..7 (SSRC * 2^64 means bits 64..95)
        iv[4] ^= (byte)(ssrc >> 24);
        iv[5] ^= (byte)(ssrc >> 16);
        iv[6] ^= (byte)(ssrc >>  8);
        iv[7] ^= (byte) ssrc;

        // XOR packet index into bytes 8..13 (index * 2^16 means bits 16..63)
        iv[ 8] ^= (byte)(index >> 40);
        iv[ 9] ^= (byte)(index >> 32);
        iv[10] ^= (byte)(index >> 24);
        iv[11] ^= (byte)(index >> 16);
        iv[12] ^= (byte)(index >>  8);
        iv[13] ^= (byte) index;
    }

    // -------------------------------------------------------------------------
    // Authentication (RFC 3711 §4.2) — HMAC-SHA1 over RTP packet plus ROC
    // -------------------------------------------------------------------------

    private void ComputeAuthTag(ReadOnlySpan<byte> data, uint roc, Span<byte> destination)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, _keys.AuthKey);
        hmac.AppendData(data);

        Span<byte> rocBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(rocBytes, roc);
        hmac.AppendData(rocBytes);

        if (!hmac.TryGetHashAndReset(destination, out var bytesWritten)
            || bytesWritten != AuthTagFullLength)
        {
            throw new CryptographicException("Failed to compute SRTP HMAC-SHA1 authentication tag.");
        }
    }

    // -------------------------------------------------------------------------
    // Packet index (RFC 3711 §3.3.1) — extended 48-bit index from 16-bit seq
    // -------------------------------------------------------------------------

    // RFC 3711 §3.3.1: estimate packet index using signed 16-bit delta.
    // delta = (seq - s_l) as a signed 16-bit integer handles wrap-around naturally:
    // positive = seq is ahead, negative = seq is behind the reference.
    // Equivalent to the libsrtp reference implementation.

    private ulong ComputeSenderIndex(ushort seq)
    {
        var sL        = (ushort)(_senderIndex & 0xFFFF);
        var delta     = (short)(seq - sL);
        var estimated = (long)_senderIndex + delta;
        return estimated >= 0 ? (ulong)estimated : (ulong)seq;
    }

    private static uint GetRoc(ulong packetIndex) => (uint)(packetIndex >> 16);

    private ulong ComputePacketIndex(ushort seq)
    {
        var sL        = (ushort)(_replayWindowIndex & 0xFFFF);
        var delta     = (short)(seq - sL);
        var estimated = (long)_replayWindowIndex + delta;
        return estimated >= 0 ? (ulong)estimated : (ulong)seq;
    }

    // -------------------------------------------------------------------------
    // Replay protection (RFC 3711 §3.3.2)
    // -------------------------------------------------------------------------

    private void CheckReplay(ulong index)
    {
        if (index > _replayWindowIndex)
            return; // newer than window — allowed

        var diff = _replayWindowIndex - index;
        if (diff >= ReplayWindowSize)
            throw new SrtpReplayException($"SRTP packet index {index} is outside the replay window.");

        if ((_replayWindowBitmap & (1UL << (int)diff)) != 0)
            throw new SrtpReplayException($"SRTP packet index {index} has already been received (replay).");
    }

    private void UpdateReplayWindow(ulong index)
    {
        if (index > _replayWindowIndex)
        {
            var shift = index - _replayWindowIndex;
            _replayWindowBitmap = shift >= ReplayWindowSize
                ? 0
                : _replayWindowBitmap << (int)shift;
            _replayWindowBitmap |= 1;
            _replayWindowIndex   = index;
        }
        else
        {
            var diff = _replayWindowIndex - index;
            _replayWindowBitmap |= 1UL << (int)diff;
        }
    }

    // -------------------------------------------------------------------------
    // RTP header length (fixed + CSRC + optional extension)
    // -------------------------------------------------------------------------

    private static int GetRtpHeaderLength(ReadOnlySpan<byte> packet)
    {
        var csrcCount    = packet[0] & 0x0F;
        var hasExtension = (packet[0] & 0x10) != 0;
        var offset       = 12 + csrcCount * 4;

        if (hasExtension && packet.Length >= offset + 4)
        {
            var extWords = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]);
            offset += 4 + extWords * 4;
        }

        return offset;
    }
}
