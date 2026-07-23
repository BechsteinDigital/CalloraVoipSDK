using System.Buffers.Binary;
using System.Security.Cryptography;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Context;

/// <summary>
/// SRTCP context for one direction (RFC 3711 §3.4). Encrypts the RTCP payload (everything
/// after the 8-byte header) with AES-CM keyed by the SRTCP session keys (KDF labels 3/4/5),
/// appends the 32-bit <c>E</c>-flag/SRTCP-index word and an HMAC-SHA1 auth tag over the
/// encrypted packet including that word. The 31-bit SRTCP index is carried explicitly, so —
/// unlike SRTP — no rollover counter feeds the IV or the authentication.
/// </summary>
internal sealed class SrtcpContext : ISrtcpContext
{
    // First 8 bytes of every RTCP packet (V/P/RC, PT, length, SSRC) stay in the clear.
    private const int RtcpHeaderLength = 8;
    private const int SrtcpIndexLength = 4;
    private const int AuthTagFullLength = 20;

    // SRTCP always carries an 80-bit (10-byte) HMAC-SHA1 tag for every supported suite,
    // including AES_CM_128/256_HMAC_SHA1_32: the 32-bit truncation of RFC 3711 §5.2 applies
    // to SRTP only. RFC 4568 §6.2 and the RFC 5764 §4.1.2 footnote keep SRTCP at 80 bit so the
    // mandatory RTCP authentication is not weakened — a shorter tag breaks interop with
    // libsrtp-based peers on the RTCP path once SHA1_32 is negotiated.
    private const int SrtcpAuthTagLength = 10;

    private const uint EncryptionFlag = 0x8000_0000;
    private const uint SrtcpIndexMask = 0x7FFF_FFFF;

    private readonly SrtpSessionKeys _keys;
    private readonly AesCmCipher _cipher;

    // Per-SSRC SRTCP index and replay window (RFC 3711 §3.2.3): the index and replay state are
    // per synchronisation source, so several RTCP senders multiplexed over one BUNDLE key do not
    // collide in a single shared window (HARD-D1). Only authenticated packets reach the receive
    // path (verify-then-decrypt), so the map is bounded by legitimate senders and needs no cap.
    private readonly Dictionary<uint, SrtcpSsrcState> _ssrcState = [];

    // Serializes mutable state (per-SSRC index/replay windows) and key usage so the context is
    // thread-safe on its own.
    private readonly object _sync = new();
    private bool _disposed;

    public SrtcpContext(SrtpKeyMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        _keys = SrtpKeyDerivation.DeriveRtcp(material);
        _cipher = new AesCmCipher(_keys.CipherKey);
    }

    /// <summary>Derived SRTCP session keys — internal test seam for dispose/zeroing evidence.</summary>
    internal SrtpSessionKeys SessionKeys => _keys;

    /// <inheritdoc />
    public byte[] ProtectRtcp(ReadOnlySpan<byte> rtcpPacket)
    {
        if (rtcpPacket.Length < RtcpHeaderLength)
            throw new ArgumentException("RTCP packet too short (minimum 8 bytes).", nameof(rtcpPacket));

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var ssrc = BinaryPrimitives.ReadUInt32BigEndian(rtcpPacket[4..]);
            var index = GetOrAddState(ssrc).NextSendIndex();
            var encryptedLen = rtcpPacket.Length - RtcpHeaderLength;

            // Layout: [clear header + encrypted payload][E|index (4)][auth tag].
            var result = GC.AllocateUninitializedArray<byte>(
                rtcpPacket.Length + SrtcpIndexLength + SrtcpAuthTagLength);
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
            tag[..SrtcpAuthTagLength].CopyTo(result.AsSpan(authedLen, SrtcpAuthTagLength));

            return result;
        }
    }

    /// <inheritdoc />
    public byte[] UnprotectRtcp(ReadOnlySpan<byte> srtcpPacket)
    {
        if (srtcpPacket.Length < RtcpHeaderLength + SrtcpIndexLength + SrtcpAuthTagLength)
            throw new ArgumentException("SRTCP packet too short.", nameof(srtcpPacket));

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var authedLen = srtcpPacket.Length - SrtcpAuthTagLength;
            var authedSpan = srtcpPacket[..authedLen];
            var receivedTag = srtcpPacket[authedLen..];

            // 1. Verify auth tag before decryption (RFC 3711 §3.3 — verify-then-decrypt).
            Span<byte> expectedTag = stackalloc byte[AuthTagFullLength];
            ComputeAuthTag(authedSpan, expectedTag);
            if (!CryptographicOperations.FixedTimeEquals(receivedTag, expectedTag[..SrtcpAuthTagLength]))
                throw new SrtpAuthenticationException("SRTCP authentication tag mismatch.");

            var indexWord = BinaryPrimitives.ReadUInt32BigEndian(authedSpan[(authedLen - SrtcpIndexLength)..]);
            var encrypted = (indexWord & EncryptionFlag) != 0;
            var index = indexWord & SrtcpIndexMask;
            // Sender SSRC from the clear (unencrypted) RTCP header — keys the per-SSRC replay state.
            var ssrc = BinaryPrimitives.ReadUInt32BigEndian(authedSpan[4..]);

            // 2. Per-SSRC replay check on the explicit SRTCP index (RFC 3711 §3.2.3/§3.3.2).
            var state = GetOrAddState(ssrc);
            state.CheckReplay(index);

            var rtcpLen = authedLen - SrtcpIndexLength;
            var output = GC.AllocateUninitializedArray<byte>(rtcpLen);
            authedSpan[..rtcpLen].CopyTo(output);

            // 3. Decrypt the payload when the E-flag is set.
            var encryptedLen = rtcpLen - RtcpHeaderLength;
            if (encrypted && encryptedLen > 0)
            {
                Span<byte> iv = stackalloc byte[16];
                BuildIv(ssrc, index, iv);
                _cipher.Xor(iv, output.AsSpan(RtcpHeaderLength, encryptedLen));
            }

            // 4. Update the SSRC's replay window.
            state.UpdateReplayWindow(index);
            return output;
        }
    }

    // IV = (salt XOR (SSRC * 2^64) XOR (index * 2^16)) as 128-bit big-endian (RFC 3711 §4.1).
    // For SRTCP the 31-bit SRTCP index takes the place of the SRTP packet index.
    private void BuildIv(uint ssrc, uint index, Span<byte> iv)
    {
        iv.Clear();
        _keys.Salt.CopyTo(iv);

        iv[4] ^= (byte)(ssrc >> 24);
        iv[5] ^= (byte)(ssrc >> 16);
        iv[6] ^= (byte)(ssrc >>  8);
        iv[7] ^= (byte) ssrc;

        // index occupies bits 16..63; a 31-bit value only reaches bytes 10..13.
        iv[10] ^= (byte)(index >> 24);
        iv[11] ^= (byte)(index >> 16);
        iv[12] ^= (byte)(index >>  8);
        iv[13] ^= (byte) index;
    }

    // -------------------------------------------------------------------------
    // Authentication (RFC 3711 §4.2) — HMAC-SHA1 over the encrypted packet + E|index.
    // No ROC: the SRTCP index is already part of the authenticated data.
    // -------------------------------------------------------------------------

    private void ComputeAuthTag(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, _keys.AuthKey);
        hmac.AppendData(data);

        if (!hmac.TryGetHashAndReset(destination, out var bytesWritten)
            || bytesWritten != AuthTagFullLength)
        {
            throw new CryptographicException("Failed to compute SRTCP HMAC-SHA1 authentication tag.");
        }
    }

    // -------------------------------------------------------------------------
    // Per-SSRC crypto state (RFC 3711 §3.2.3). Caller holds _sync.
    // -------------------------------------------------------------------------

    private SrtcpSsrcState GetOrAddState(uint ssrc)
    {
        if (!_ssrcState.TryGetValue(ssrc, out var state))
            _ssrcState[ssrc] = state = new SrtcpSsrcState();
        return state;
    }

    /// <summary>
    /// Zeroes the derived SRTCP session keys (RFC 3711 §9.4 hygiene) and rejects further use.
    /// Idempotent.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _cipher.Dispose();
            _keys.Zero();
        }
    }
}
