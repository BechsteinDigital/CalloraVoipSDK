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
    /// <summary>
    /// Highest sender SRTCP index usable under one key (RFC 3711 §9.2). The 31-bit index must never wrap
    /// — a wrap reuses the AES-CM keystream / GCM nonce — so this is the full 31-bit space (2^31 - 1).
    /// </summary>
    internal const uint MaxSendIndexLimit = 0x7FFF_FFFF;

    private readonly uint _maxSendIndex;

    // Sender-side SRTCP index (31-bit), pre-incremented per packet (RFC 3711 §3.4).
    private uint _sendIndex;

    // Receiver replay window (RFC 3711 §3.3.2 applied to the explicit 31-bit SRTCP index), shared
    // with SRTP via SlidingReplayWindow. The 31-bit index widens losslessly into the ulong window.
    private readonly SlidingReplayWindow _replay = new("SRTCP index");

    /// <param name="maxSendIndex">
    /// Highest sender index allowed before the key is exhausted (RFC 3711 §9.2). Defaults to the full
    /// 31-bit space; a lower value is an injectable test seam for the near-exhaustion boundary.
    /// </param>
    public SrtcpSsrcState(uint maxSendIndex = MaxSendIndexLimit) => _maxSendIndex = maxSendIndex;

    /// <summary>
    /// Pre-increments and returns the next 31-bit sender SRTCP index (RFC 3711 §3.4). Fails closed with
    /// <see cref="SrtpKeyLifetimeExceededException"/> once the index would exceed its per-key lifetime,
    /// rather than wrapping and reusing the keystream/nonce under the same key (RFC 3711 §9.2).
    /// </summary>
    public uint NextSendIndex()
    {
        if (_sendIndex >= _maxSendIndex)
            throw new SrtpKeyLifetimeExceededException(
                $"SRTCP send index reached its per-key lifetime limit ({_maxSendIndex}); refusing to wrap (RFC 3711 §9.2).");
        return ++_sendIndex;
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
