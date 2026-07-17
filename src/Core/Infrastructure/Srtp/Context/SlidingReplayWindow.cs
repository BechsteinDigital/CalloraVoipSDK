namespace CalloraVoipSdk.Core.Infrastructure.Srtp.Context;

/// <summary>
/// A 64-packet sliding replay window keyed by an extended packet index (RFC 3711 §3.3.2). Shared by
/// <see cref="SrtpSsrcState"/> (48-bit SRTP packet index) and <see cref="SrtcpSsrcState"/> (31-bit
/// SRTCP index) so the identical accept/reject/shift logic lives in one place (HARD-R6).
/// </summary>
/// <remarks>
/// The window index is <see cref="ulong"/>, not <see cref="uint"/>: the SRTP extended index reaches
/// 48 bits (ROC≪16 | seq) and must not be truncated. SRTCP's 31-bit index widens into it without loss.
/// Like the per-SSRC states, this type is <b>not</b> synchronised on its own — the owning context
/// serialises every access under its lock. The <c>label</c> only shapes the diagnostic message.
/// </remarks>
internal sealed class SlidingReplayWindow
{
    private const int WindowSize = 64;

    private readonly string _label;

    // 64-bit bitmap, bit 0 = the highest index seen, bit n = the index n positions older.
    private ulong _highestIndex;
    private ulong _bitmap;

    /// <summary>Creates a replay window whose exceptions describe indices with <paramref name="label"/>.</summary>
    /// <param name="label">Diagnostic prefix, e.g. <c>"SRTP packet index"</c> or <c>"SRTCP index"</c>.</param>
    public SlidingReplayWindow(string label) => _label = label;

    /// <summary>The highest index accepted so far — used by SRTP to estimate the extended index.</summary>
    public ulong HighestIndex => _highestIndex;

    /// <summary>
    /// Rejects an index that falls outside the window or has already been received (RFC 3711 §3.3.2).
    /// Does not mutate state — call <see cref="Update"/> once the packet has been accepted.
    /// </summary>
    /// <exception cref="SrtpReplayException">The index is too old or is a replay.</exception>
    public void Check(ulong index)
    {
        if (index > _highestIndex)
            return; // newer than the window — allowed

        var diff = _highestIndex - index;
        if (diff >= WindowSize)
            throw new SrtpReplayException($"{_label} {index} is outside the replay window.");

        if ((_bitmap & (1UL << (int)diff)) != 0)
            throw new SrtpReplayException($"{_label} {index} has already been received (replay).");
    }

    /// <summary>Records an accepted index in the window (RFC 3711 §3.3.2).</summary>
    public void Update(ulong index)
    {
        if (index > _highestIndex)
        {
            var shift = index - _highestIndex;
            _bitmap = shift >= WindowSize
                ? 0
                : _bitmap << (int)shift;
            _bitmap |= 1;
            _highestIndex = index;
        }
        else
        {
            var diff = _highestIndex - index;
            _bitmap |= 1UL << (int)diff;
        }
    }
}
