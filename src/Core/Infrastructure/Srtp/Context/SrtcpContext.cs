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
    // (verify-then-decrypt inside the cipher), so a forged flood cannot grow the map — but a keyed peer
    // still could, so the map is hard-capped at _maxTrackedSsrcs (#157 P1-2, K4 wire-DoS).
    private readonly Dictionary<uint, SrtcpSsrcState> _ssrcState = [];
    private readonly int _maxTrackedSsrcs;
    private long _discardedSourceCount;

    // Serializes mutable state (per-SSRC index/replay windows) and key usage so the context is
    // thread-safe on its own.
    private readonly object _sync = new();
    private bool _disposed;

    public SrtcpContext(SrtpKeyMaterial material, int maxTrackedSsrcs = SrtpContext.DefaultMaxTrackedSsrcs)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTrackedSsrcs);
        _keys = SrtpKeyDerivation.DeriveRtcp(material);
        _cipher = SrtpCryptoSuiteNames.IsAead(material.Suite)
            ? new AesGcmSrtcpCipher(_keys)
            : new AesCmSha1SrtcpCipher(_keys);
        _maxTrackedSsrcs = maxTrackedSsrcs;
    }

    /// <summary>Derived SRTCP session keys — internal test seam for dispose/zeroing evidence.</summary>
    internal SrtpSessionKeys SessionKeys => _keys;

    /// <summary>
    /// Number of SSRCs with committed per-SSRC SRTCP state — internal test seam proving inbound state
    /// is created only for authenticated sources and stays within the tracked-source cap (#157 P1-2).
    /// </summary>
    internal int TrackedSourceCount
    {
        get { lock (_sync) { return _ssrcState.Count; } }
    }

    /// <summary>
    /// Number of authenticated packets discarded because admitting their new SSRC would exceed the
    /// tracked-source cap — internal telemetry/test seam proving the cap rejects rather than evicts.
    /// Monotonic for the context's lifetime.
    /// </summary>
    internal long DiscardedSourceCount
    {
        get { lock (_sync) { return _discardedSourceCount; } }
    }

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

            // Per-SSRC replay check on the explicit SRTCP index (RFC 3711 §3.2.3/§3.3.2). Admitting a
            // new inbound SSRC is subject to the receive-side tracked-source cap (#157 P1-2).
            var state = GetOrAdmitInboundState(ssrc);
            state.CheckReplay(index);
            state.UpdateReplayWindow(index);
            return rtcp;
        }
    }

    // Send-side state (ProtectRtcp). A sender controls its own SSRCs, so this path is deliberately NOT
    // capped — the tracked-source cap is a receive-side defense and must never make a legitimate
    // multi-stream Protect throw. RFC 3711 §3.2.3. Caller holds _sync.
    private SrtcpSsrcState GetOrAddState(uint ssrc)
    {
        if (!_ssrcState.TryGetValue(ssrc, out var state))
            _ssrcState[ssrc] = state = new SrtcpSsrcState();
        return state;
    }

    // Receive-side admission (UnprotectRtcp). A new authenticated SSRC at the tracked-source cap is
    // refused with a typed, fail-closed discard (#157 P1-2, K4) rather than evicting an already-admitted
    // source's replay window — eviction would let its earlier indices be replayed. Caller holds _sync.
    private SrtcpSsrcState GetOrAdmitInboundState(uint ssrc)
    {
        if (_ssrcState.TryGetValue(ssrc, out var state))
            return state;
        if (_ssrcState.Count >= _maxTrackedSsrcs)
        {
            _discardedSourceCount++;
            throw new SrtpSourceLimitException(
                $"SRTCP tracked-source cap ({_maxTrackedSsrcs}) reached; refusing a new SSRC (RFC 3711 §3.2.3 state).");
        }
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
