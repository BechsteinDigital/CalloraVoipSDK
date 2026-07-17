namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Context;

/// <summary>
/// Per-SSRC SRTCP index and replay state for one <see cref="SrtcpContext"/>. RFC 3711 §3.2.3/§3.4
/// defines the SRTCP index and replay window as per-SSRC: under one shared SRTCP session key each
/// RTCP sender advances its own 31-bit index and tracks its own replay window. A single
/// <see cref="SrtcpContext"/> keeps one instance of this state per SSRC it protects or unprotects, so
/// a BUNDLE transport (RFC 8843) carrying several RTCP sources over one shared key does not collide
/// their indices in a single shared window (HARD-D1).
/// </summary>
/// <remarks>
/// This type is intentionally <b>not</b> synchronised on its own: the owning
/// <see cref="SrtcpContext"/> serialises every access under its own lock, exactly as it did when this
/// state lived inline as fields. Do not share an instance across contexts or use it without that lock.
/// </remarks>
internal sealed class SrtcpSsrcState
{
    private const uint SrtcpIndexMask = 0x7FFF_FFFF;

    // Sender-side SRTCP index (31-bit), pre-incremented per packet (RFC 3711 §3.4).
    private uint _sendIndex;

    // Receiver replay window (RFC 3711 §3.3.2 applied to the explicit 31-bit SRTCP index), shared
    // with SRTP via SlidingReplayWindow. The 31-bit index widens losslessly into the ulong window.
    private readonly SlidingReplayWindow _replay = new("SRTCP index");

    /// <summary>Pre-increments and returns the next 31-bit sender SRTCP index (RFC 3711 §3.4).</summary>
    public uint NextSendIndex()
    {
        _sendIndex = (_sendIndex + 1) & SrtcpIndexMask;
        return _sendIndex;
    }

    /// <summary>
    /// Rejects an SRTCP index that falls outside the replay window or has already been received
    /// (RFC 3711 §3.3.2). Does not mutate state — call <see cref="UpdateReplayWindow"/> once the
    /// packet has been accepted.
    /// </summary>
    /// <exception cref="SrtpReplayException">The index is stale or a replay.</exception>
    public void CheckReplay(uint index) => _replay.Check(index);

    /// <summary>Records an accepted SRTCP index in the replay window.</summary>
    public void UpdateReplayWindow(uint index) => _replay.Update(index);
}
