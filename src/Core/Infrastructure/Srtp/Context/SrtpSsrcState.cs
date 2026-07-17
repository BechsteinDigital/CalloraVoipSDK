namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Context;

/// <summary>
/// Per-SSRC sequence, rollover-counter (ROC), and replay state for an SRTP cryptographic context.
/// RFC 3711 §3.2.1 defines the cryptographic context — including the ROC and the replay window —
/// as per-SSRC: under one shared master key each synchronisation source advances its own extended
/// packet index and tracks its own replay window. A single <see cref="SrtpContext"/> keeps one
/// instance of this state per SSRC it protects or unprotects, so a BUNDLE transport (RFC 8843)
/// carrying multiple SSRCs over one shared key demultiplexes their crypto state cleanly.
/// </summary>
/// <remarks>
/// This type is intentionally <b>not</b> synchronised on its own: the owning
/// <see cref="SrtpContext"/> serialises every access under its own lock, exactly as it did when this
/// state lived inline as fields. Do not share an instance across contexts or use it without that lock.
/// </remarks>
internal sealed class SrtpSsrcState
{
    // Sender-side index: tracks the last outbound packet index for ROC advancement (RFC 3711 §3.3.1).
    private ulong _senderIndex;

    // Receiver replay window (RFC 3711 §3.3.2), shared with SRTCP via SlidingReplayWindow.
    private readonly SlidingReplayWindow _replay = new("SRTP packet index");

    // RFC 3711 §3.3.1: estimate the extended packet index from the 16-bit sequence number using a
    // signed 16-bit delta, which handles wrap-around naturally (positive = ahead, negative = behind).
    // Equivalent to the libsrtp reference implementation.

    /// <summary>Estimates the outbound extended packet index for the given sequence number.</summary>
    public ulong ComputeSenderIndex(ushort seq)
    {
        var sL        = (ushort)(_senderIndex & 0xFFFF);
        var delta     = (short)(seq - sL);
        var estimated = (long)_senderIndex + delta;
        return estimated >= 0 ? (ulong)estimated : (ulong)seq;
    }

    /// <summary>Advances the sender-side index so the ROC is correct for subsequent packets.</summary>
    public void AdvanceSender(ulong packetIndex)
    {
        if (packetIndex >= _senderIndex)
            _senderIndex = packetIndex;
    }

    /// <summary>Estimates the inbound extended packet index for the given sequence number.</summary>
    public ulong ComputePacketIndex(ushort seq)
    {
        var windowIndex = _replay.HighestIndex;
        var sL          = (ushort)(windowIndex & 0xFFFF);
        var delta       = (short)(seq - sL);
        var estimated   = (long)windowIndex + delta;
        return estimated >= 0 ? (ulong)estimated : (ulong)seq;
    }

    /// <summary>
    /// Rejects a packet index that falls outside the window or has already been received
    /// (RFC 3711 §3.3.2). Does not mutate state — call <see cref="UpdateReplayWindow"/> after the
    /// packet has been accepted.
    /// </summary>
    /// <exception cref="SrtpReplayException">The index is too old or is a replay.</exception>
    public void CheckReplay(ulong index) => _replay.Check(index);

    /// <summary>Records an accepted packet index in the replay window (RFC 3711 §3.3.2).</summary>
    public void UpdateReplayWindow(ulong index) => _replay.Update(index);
}
