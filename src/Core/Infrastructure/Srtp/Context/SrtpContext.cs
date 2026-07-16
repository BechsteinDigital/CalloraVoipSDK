using System.Buffers.Binary;
using System.Security.Cryptography;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Context;

/// <summary>
/// SRTP context for one direction (RFC 3711) under one shared master key.
/// Implements AES-CM encryption (§4.1), HMAC-SHA1 authentication (§4.2),
/// and replay protection via a 64-packet sliding window (§3.3.2).
/// The session keys are shared across SSRCs (RFC 3711 §4.3 derives them from the master key,
/// not the SSRC — the SSRC only feeds the IV), while the rollover counter and replay window are
/// per-SSRC (§3.2.1), so one context serves every SSRC a BUNDLE transport (RFC 8843) carries.
/// </summary>
internal sealed class SrtpContext : ISrtpContext
{
    private const int AuthTagFullLength = 20;
    private const int AesCmBlockLength = 16;
    private const int MaxAesCmKeystreamBytes = 1 << 20;
    private readonly SrtpSessionKeys _keys;
    private readonly SrtpCryptoSuite _suite;
    private readonly int _authTagLength;

    // Per-SSRC ROC + replay state (RFC 3711 §3.2.1). One entry per synchronisation source seen on
    // this direction; a single-stream context simply holds one. Inbound state is created only once a
    // packet from an SSRC authenticates, so an unauthenticated SSRC spray cannot grow the map.
    private readonly Dictionary<uint, SrtpSsrcState> _ssrcState = [];

    // Serializes all mutable state (per-SSRC indices, replay windows) and key usage so the context
    // is thread-safe on its own — concurrent Protect calls would otherwise race a stream's ROC
    // advancement and concurrent Unprotect calls its replay window.
    private readonly object _sync = new();
    private bool _disposed;

    public SrtpContext(SrtpKeyMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        _suite         = material.Suite;
        _keys          = SrtpKeyDerivation.Derive(material);
        _authTagLength = material.Suite is SrtpCryptoSuite.AesCm128HmacSha1_32
                                       or SrtpCryptoSuite.AesCm256HmacSha1_32
            ? 4 : 10;
    }

    /// <summary>Derived session keys — internal test seam for dispose/zeroing evidence.</summary>
    internal SrtpSessionKeys SessionKeys => _keys;

    /// <summary>
    /// Number of SSRCs with committed per-SSRC state — internal test seam proving that inbound state
    /// is created only for authenticated sources (a forged-SSRC flood leaves this at zero).
    /// </summary>
    internal int TrackedSourceCount
    {
        get { lock (_sync) { return _ssrcState.Count; } }
    }

    // -------------------------------------------------------------------------
    // Protect (encrypt outbound)
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public byte[] Protect(ReadOnlySpan<byte> rtpPacket)
    {
        if (rtpPacket.Length < 12)
            throw new ArgumentException("RTP packet too short (minimum 12 bytes).", nameof(rtpPacket));

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ProtectLocked(rtpPacket);
        }
    }

    private byte[] ProtectLocked(ReadOnlySpan<byte> rtpPacket)
    {
        var headerLen   = GetRtpHeaderLength(rtpPacket);
        var payloadLen  = rtpPacket.Length - headerLen;
        var ssrc        = BinaryPrimitives.ReadUInt32BigEndian(rtpPacket[8..]);
        var seq         = BinaryPrimitives.ReadUInt16BigEndian(rtpPacket[2..]);
        var state       = GetOrAddState(ssrc);
        var packetIndex = state.ComputeSenderIndex(seq);

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

        // Advance this SSRC's sender-side index so ROC is correct for subsequent packets
        state.AdvanceSender(packetIndex);

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

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return UnprotectLocked(srtpPacket);
        }
    }

    private byte[] UnprotectLocked(ReadOnlySpan<byte> srtpPacket)
    {
        var rtpLen     = srtpPacket.Length - _authTagLength;
        var rtpSpan    = srtpPacket[..rtpLen];
        var receivedTag = srtpPacket[rtpLen..];
        var seq  = BinaryPrimitives.ReadUInt16BigEndian(rtpSpan[2..]);
        var ssrc = BinaryPrimitives.ReadUInt32BigEndian(rtpSpan[8..]);

        // Look up this SSRC's state, or start from a fresh (ROC 0) estimate for an unseen SSRC. The
        // fresh state stays a local until the packet authenticates, so a forged-SSRC flood cannot
        // create per-SSRC entries without holding the master key.
        _ssrcState.TryGetValue(ssrc, out var existing);
        var state = existing ?? new SrtpSsrcState();
        var packetIndex = state.ComputePacketIndex(seq);

        // 1. Verify auth tag before decryption (RFC 3711 §3.3 — verify-then-decrypt)
        Span<byte> expectedTag = stackalloc byte[AuthTagFullLength];
        ComputeAuthTag(rtpSpan, GetRoc(packetIndex), expectedTag);
        if (!CryptographicOperations.FixedTimeEquals(receivedTag, expectedTag[.._authTagLength]))
            throw new SrtpAuthenticationException("SRTP authentication tag mismatch.");

        // Authenticated: commit the state for this SSRC so its ROC/replay window persists.
        if (existing is null)
            _ssrcState[ssrc] = state;

        // 2. Replay check (RFC 3711 §3.3.2)
        state.CheckReplay(packetIndex);

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

        // 4. Update this SSRC's replay window
        state.UpdateReplayWindow(packetIndex);

        return output;
    }

    private SrtpSsrcState GetOrAddState(uint ssrc)
    {
        if (!_ssrcState.TryGetValue(ssrc, out var state))
            _ssrcState[ssrc] = state = new SrtpSsrcState();
        return state;
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
    // Packet index (RFC 3711 §3.3.1) — the extended index estimation and the replay window
    // now live per-SSRC on SrtpSsrcState; the context only maps the index to its ROC here.
    // -------------------------------------------------------------------------

    private static uint GetRoc(ulong packetIndex) => (uint)(packetIndex >> 16);

    // -------------------------------------------------------------------------
    // RTP header length (fixed + CSRC + optional extension)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Computes the RTP header length including CSRC list and header extension
    /// (RFC 3550 §5.1/§5.3.1), validating every step against the packet length.
    /// A header claiming more CSRCs or extension words than the packet holds is
    /// malformed — without these checks the computed payload length turns negative
    /// and an uncontrolled <see cref="ArgumentOutOfRangeException"/> would escape
    /// past the media path's SRTP error handling and kill the receive loop.
    /// </summary>
    internal static int GetRtpHeaderLength(ReadOnlySpan<byte> packet)
    {
        var csrcCount    = packet[0] & 0x0F;
        var hasExtension = (packet[0] & 0x10) != 0;
        var offset       = 12 + csrcCount * 4;

        if (offset > packet.Length)
            throw new CryptographicException(
                $"Malformed RTP header: CSRC list ({csrcCount} entries) exceeds the {packet.Length}-byte packet.");

        if (hasExtension)
        {
            if (packet.Length < offset + 4)
                throw new CryptographicException(
                    "Malformed RTP header: extension flag set but the extension header is truncated.");

            var extWords = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]);
            offset += 4 + extWords * 4;

            if (offset > packet.Length)
                throw new CryptographicException(
                    $"Malformed RTP header: extension ({extWords} words) exceeds the {packet.Length}-byte packet.");
        }

        return offset;
    }

    /// <summary>
    /// Zeroes the derived session keys (RFC 3711 §9.4 hygiene) and rejects further use.
    /// Idempotent; safe to call while another thread is mid-Protect/Unprotect (the
    /// operation in flight completes, subsequent calls throw).
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _keys.Zero();
        }
    }
}
