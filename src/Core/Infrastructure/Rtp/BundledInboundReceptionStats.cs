using System.Collections.Concurrent;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Per-SSRC inbound reception statistics for a BUNDLE media session (CF-004b, RFC 3550 §6.4.1 / A.1 / A.3 /
/// A.8). Every decrypted, decoded inbound RTP packet on the shared 5-tuple is recorded here keyed by its
/// SSRC; the periodic RTCP reporter then snapshots one reception report block per active source (fraction
/// lost, cumulative lost, extended highest sequence, interarrival jitter, LSR/DLSR).
/// <para>
/// The BUNDLE inbound path routes decoded packets straight to their track sinks with no jitter buffer, so —
/// unlike the SIP path, which derives jitter from the jitter buffer's playout estimate — the interarrival
/// jitter (RFC 3550 §A.8) is accumulated here directly from each packet's RTP timestamp and arrival instant.
/// The sequence/loss bookkeeping mirrors <see cref="InboundRtpStatistics"/> (RFC 3550 §A.1/A.3) but is kept
/// per SSRC because one bundled transport carries several inbound streams.
/// </para>
/// <para>
/// Thread-safe: the per-SSRC map is a <see cref="ConcurrentDictionary{TKey,TValue}"/>; each source's state is
/// mutated under its own lock. The RTP receive path (<see cref="RecordRtp"/>) and the RTCP receive path
/// (<see cref="RecordSenderReport"/>) run on different threads from the reporter's snapshot
/// (<see cref="SnapshotReportBlocks"/>), and all three synchronise on the per-source lock.
/// </para>
/// </summary>
internal sealed class BundledInboundReceptionStats
{
    private readonly ConcurrentDictionary<uint, BundledSourceReceptionState> _sources = new();
    private readonly Func<DateTimeOffset> _utcNow;

    // The primary audio source and its negotiated clock rate (Hz): that SSRC's reception state is seeded with
    // the negotiated rate so its §A.8 jitter is exact (not inferred) and convertible to milliseconds. Other
    // SSRCs (e.g. an inbound video source) are created without a negotiated rate and fall back to inference —
    // per-SSRC negotiated clocks are CF-004f.
    private readonly uint _audioSsrc;
    private readonly uint _audioClockRate;

    /// <summary>
    /// Creates the reception tracker.
    /// </summary>
    /// <param name="utcNow">The wall clock read for arrival times (jitter, DLSR); injectable for tests.</param>
    /// <param name="audioSsrc">
    /// The local peer's negotiated inbound audio SSRC, or 0 when not known. Reserved: the inbound audio source's
    /// SSRC is chosen by the remote and not known ahead of the first packet, so <paramref name="audioClockRate"/>
    /// is applied to whichever source first delivers RTP (the audio stream, in an audio-first bundle) rather than
    /// keyed by this value today. Retained for a future per-SSRC clock map (CF-004f).
    /// </param>
    /// <param name="audioClockRate">
    /// The negotiated audio RTP clock rate (Hz), or 0 when unknown. Seeds the primary audio source's §A.8 jitter
    /// so it is exact under network jitter (the per-pair inference is not) and convertible to milliseconds.
    /// </param>
    public BundledInboundReceptionStats(
        Func<DateTimeOffset>? utcNow = null, uint audioSsrc = 0, uint audioClockRate = 0)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _audioSsrc = audioSsrc;
        _audioClockRate = audioClockRate;
    }

    /// <summary>
    /// Records one inbound RTP packet against its source SSRC, updating that source's sequence tracking
    /// (RFC 3550 §A.1), loss counters (§A.3), and interarrival jitter (§A.8). Called on the receive loop
    /// after SRTP-unprotect and RTP decode.
    /// </summary>
    /// <param name="ssrc">The packet's synchronisation source.</param>
    /// <param name="sequenceNumber">The packet's RTP sequence number.</param>
    /// <param name="rtpTimestamp">The packet's RTP timestamp (for the §A.8 transit estimate).</param>
    public void RecordRtp(uint ssrc, ushort sequenceNumber, uint rtpTimestamp)
    {
        var state = GetOrAddSource(ssrc);
        state.RecordRtp(sequenceNumber, rtpTimestamp, _utcNow());
    }

    // The negotiated audio clock seeds only the first source created — in an audio-first bundle that is the
    // inbound audio stream (its SSRC is the remote's choice, unknown before its first packet). Any later source
    // (e.g. video) is created without a negotiated rate and infers it. Racing GetOrAdd factories can both build a
    // state, but the loser is discarded by the dictionary; a rate handed to a discarded state is harmless.
    private BundledSourceReceptionState GetOrAddSource(uint ssrc)
        => _sources.GetOrAdd(ssrc, _ =>
            _sources.IsEmpty ? new BundledSourceReceptionState(_audioClockRate) : new BundledSourceReceptionState());

    /// <summary>
    /// Records that a Sender Report was received from <paramref name="senderSsrc"/>, capturing the LSR (the
    /// middle 32 bits of the SR's NTP timestamp) and the arrival instant used to derive DLSR at report time
    /// (RFC 3550 §6.4.1). A source seen only via its SR (before any RTP) is tracked so its LSR/DLSR still feed
    /// a report block once RTP arrives; if no RTP ever arrives it contributes no report block (no loss/jitter).
    /// </summary>
    /// <param name="senderSsrc">The SSRC that sent the report.</param>
    /// <param name="senderReportNtpTimestamp">The 64-bit NTP timestamp carried in the SR.</param>
    public void RecordSenderReport(uint senderSsrc, ulong senderReportNtpTimestamp)
    {
        var state = GetOrAddSource(senderSsrc);
        state.RecordSenderReport(ToMiddle32Bits(senderReportNtpTimestamp), _utcNow());
    }

    /// <summary>
    /// Snapshots one <see cref="BundledReceptionReportBlock"/> per active source for the reporter to build
    /// SR/RR report blocks from. Stateful per RFC 3550 §A.3: capturing advances each source's fraction-lost
    /// interval baseline, so call exactly once per emitted report. Sources that have not yet delivered a
    /// countable RTP packet are omitted (no reception is described).
    /// </summary>
    public IReadOnlyList<BundledReceptionReportBlock> SnapshotReportBlocks()
    {
        var now = _utcNow();
        var blocks = new List<BundledReceptionReportBlock>(_sources.Count);
        foreach (var (ssrc, state) in _sources)
        {
            if (state.CaptureReportBlock(ssrc, now) is { } block)
                blocks.Add(block);
        }

        return blocks;
    }

    /// <summary>
    /// The current local receive-side interarrival jitter in milliseconds (RFC 3550 §A.8), or
    /// <see langword="null"/> before any source has an established clock rate. This is our own inbound jitter —
    /// the browser <c>getStats</c> inbound-rtp jitter — distinct from the peer-reported jitter that rides the
    /// reception report blocks in RTP units. When several inbound sources are active the maximum is returned (the
    /// worst stream), a simple aggregation for the single scalar the stats surface exposes today; per-SSRC jitter
    /// is CF-004f. The primary audio source's value is exact (seeded with the negotiated clock); an inferred-clock
    /// source contributes once its clock settles.
    /// </summary>
    public double? SnapshotJitterMs()
    {
        double? worst = null;
        foreach (var state in _sources.Values)
        {
            if (state.SnapshotJitterMs() is { } ms && (worst is null || ms > worst))
                worst = ms;
        }

        return worst;
    }

    // RFC 3550 §6.4.1: LSR is the middle 32 bits of the sender's 64-bit NTP timestamp.
    private static uint ToMiddle32Bits(ulong ntpTimestamp) => (uint)((ntpTimestamp >> 16) & 0xFFFFFFFF);
}

/// <summary>
/// A snapshot of one inbound source's reception quality (RFC 3550 §6.4.1), captured by
/// <see cref="BundledInboundReceptionStats.SnapshotReportBlocks"/> for the reporter to turn into a
/// reception report block.
/// </summary>
/// <param name="Ssrc">The source this block reports on.</param>
/// <param name="FractionLost">Packets lost since the last report, as a 1/256 fixed-point fraction.</param>
/// <param name="CumulativePacketsLost">Total packets lost since reception began (24-bit signed).</param>
/// <param name="ExtendedHighestSequenceNumber">Rollover count (high 16) plus highest sequence (low 16).</param>
/// <param name="InterarrivalJitter">Smoothed interarrival jitter in RTP timestamp units (RFC 3550 §A.8).</param>
/// <param name="LastSr">Middle 32 bits of the last SR's NTP timestamp from this source (0 if none).</param>
/// <param name="DelaySinceLastSr">Delay since that SR in 1/65536-second units (0 if none).</param>
internal readonly record struct BundledReceptionReportBlock(
    uint Ssrc,
    byte FractionLost,
    int CumulativePacketsLost,
    uint ExtendedHighestSequenceNumber,
    uint InterarrivalJitter,
    uint LastSr,
    uint DelaySinceLastSr);
