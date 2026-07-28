using System.Buffers.Binary;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;

namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Context;

/// <summary>
/// SRTCP context for one direction (RFC 3711 §3.4). Delegates encryption and authentication to an
/// <see cref="ISrtcpPacketCipher"/> — AES-CM+HMAC-SHA1 or AEAD-AES-GCM (RFC 7714 §9) — while owning the
/// per-SSRC 31-bit SRTCP index generation and replay window (RFC 3711 §3.2.3). The 31-bit index is
/// carried explicitly, so — unlike SRTP — no rollover counter feeds the IV or the authentication.
/// </summary>
internal sealed class SrtcpContext : ISrtcpContext
{
    private const int RtcpHeaderLength = 8;
    private const int SrtcpIndexLength = 4;

    private readonly SrtpSessionKeys _keys;
    private readonly ISrtcpPacketCipher _cipher;

    // Per-SSRC SRTCP index and replay window (RFC 3711 §3.2.3): the index and replay state are per
    // synchronisation source, so several RTCP senders multiplexed over one BUNDLE key do not collide in a
    // single shared window (HARD-D1). Only authenticated packets reach GetOrAddState on the receive path
    // (verify-then-decrypt inside the cipher), so the map is bounded by legitimate senders and needs no cap.
    private readonly Dictionary<uint, SrtcpSsrcState> _ssrcState = [];

    // Serializes mutable state (per-SSRC index/replay windows) and key usage so the context is
    // thread-safe on its own.
    private readonly object _sync = new();
    private bool _disposed;

    public SrtcpContext(SrtpKeyMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        _keys = SrtpKeyDerivation.DeriveRtcp(material);
        _cipher = SrtpCryptoSuiteNames.IsAead(material.Suite)
            ? new AesGcmSrtcpCipher(_keys)
            : new AesCmSha1SrtcpCipher(_keys);
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
            return _cipher.Protect(ssrc, index, rtcpPacket);
        }
    }

    /// <inheritdoc />
    public byte[] UnprotectRtcp(ReadOnlySpan<byte> srtcpPacket)
    {
        if (srtcpPacket.Length < RtcpHeaderLength + SrtcpIndexLength + _cipher.TagLength)
            throw new ArgumentException("SRTCP packet too short.", nameof(srtcpPacket));

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Sender SSRC from the clear (unencrypted) RTCP header — keys the per-SSRC replay state.
            var ssrc = BinaryPrimitives.ReadUInt32BigEndian(srtcpPacket[4..]);

            // Verify + decrypt (a failed tag throws before any per-SSRC state is committed).
            var (rtcp, index) = _cipher.Unprotect(ssrc, srtcpPacket);

            // Per-SSRC replay check on the explicit SRTCP index (RFC 3711 §3.2.3/§3.3.2).
            var state = GetOrAddState(ssrc);
            state.CheckReplay(index);
            state.UpdateReplayWindow(index);
            return rtcp;
        }
    }

    // Per-SSRC crypto state (RFC 3711 §3.2.3). Caller holds _sync.
    private SrtcpSsrcState GetOrAddState(uint ssrc)
    {
        if (!_ssrcState.TryGetValue(ssrc, out var state))
            _ssrcState[ssrc] = state = new SrtcpSsrcState();
        return state;
    }

    /// <summary>
    /// Zeroes the derived SRTCP session keys (RFC 3711 §9.4 hygiene) and rejects further use. Idempotent.
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
