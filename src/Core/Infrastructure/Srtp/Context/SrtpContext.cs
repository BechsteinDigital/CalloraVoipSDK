using System.Buffers.Binary;
using System.Security.Cryptography;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Context;

/// <summary>
/// SRTP context for one direction (RFC 3711) under one shared master key. Encryption and authentication
/// are delegated to an <see cref="ISrtpPacketCipher"/> — AES-CM+HMAC-SHA1 (RFC 3711 §4.1/§4.2) or
/// AEAD-AES-GCM (RFC 7714) — while this context owns replay protection via a 64-packet sliding window
/// (§3.3.2) and the per-SSRC rollover counter. The session keys are shared across SSRCs (RFC 3711 §4.3
/// derives them from the master key, not the SSRC — the SSRC only feeds the IV), while the rollover
/// counter and replay window are per-SSRC (§3.2.1), so one context serves every SSRC a BUNDLE transport
/// (RFC 8843) carries.
/// </summary>
internal sealed class SrtpContext : ISrtpContext
{
    private readonly SrtpSessionKeys _keys;
    private readonly ISrtpPacketCipher _cipher;
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
        _keys          = SrtpKeyDerivation.Derive(material);
        _cipher        = CreateCipher(material.Suite, _keys);
        _authTagLength = _cipher.TagLength;
    }

    // Picks the packet cipher for the negotiated suite: AEAD-GCM (RFC 7714, 16-byte tag) or the classic
    // AES-CM + HMAC-SHA1 (RFC 3711, 10- or 4-byte tag). The context is otherwise suite-agnostic.
    private static ISrtpPacketCipher CreateCipher(SrtpCryptoSuite suite, SrtpSessionKeys keys)
    {
        if (SrtpCryptoSuiteNames.IsAead(suite))
            return new AesGcmPacketCipher(keys);

        var tagLength = suite is SrtpCryptoSuite.AesCm128HmacSha1_32 or SrtpCryptoSuite.AesCm256HmacSha1_32
            ? 4 : 10;
        return new AesCmSha1PacketCipher(keys, tagLength);
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
        var ssrc        = BinaryPrimitives.ReadUInt32BigEndian(rtpPacket[8..]);
        var seq         = BinaryPrimitives.ReadUInt16BigEndian(rtpPacket[2..]);
        var state       = GetOrAddState(ssrc);
        var packetIndex = state.ComputeSenderIndex(seq);

        // Copy into the final SRTP buffer, then encrypt the payload in place and append the tag.
        var result = GC.AllocateUninitializedArray<byte>(rtpPacket.Length + _authTagLength);
        rtpPacket.CopyTo(result);
        _cipher.Protect(ssrc, packetIndex, result.AsSpan(0, rtpPacket.Length), headerLen,
            result.AsSpan(rtpPacket.Length, _authTagLength));

        // Advance this SSRC's sender-side index so ROC is correct for subsequent packets.
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
        var headerLen = GetRtpHeaderLength(rtpSpan); // AEAD-GCM needs the header (AAD) to authenticate.

        // Copy the encrypted RTP region out, then verify + decrypt it in place. A failed tag throws
        // before any per-SSRC state is committed (RFC 3711 §3.3 — discard unauthenticated packets):
        // AES-CM verifies then decrypts, AEAD-GCM verifies and decrypts atomically.
        var output = GC.AllocateUninitializedArray<byte>(rtpLen);
        rtpSpan.CopyTo(output);
        _cipher.Unprotect(ssrc, packetIndex, output, headerLen, receivedTag);

        // Authenticated: commit the state for this SSRC so its ROC/replay window persists.
        if (existing is null)
            _ssrcState[ssrc] = state;

        // Replay check + window update (RFC 3711 §3.3.2).
        state.CheckReplay(packetIndex);
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
            _cipher.Dispose();
            _keys.Zero();
        }
    }
}
