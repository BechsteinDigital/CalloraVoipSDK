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
    // The negotiated media parameters resolved per SSRC (kind, mid, clock) the first time that SSRC delivers a
    // packet — remembered so the per-SSRC jitter snapshot can attribute each inbound remote SSRC to a track.
    private readonly ConcurrentDictionary<uint, BundledInboundSourceKind> _sourceKinds = new();
    private readonly Func<DateTimeOffset> _utcNow;

    // The negotiated inbound media clock/kind/mid keyed by RTP payload type (RFC 3550 §A.8 needs the clock; the
    // kind/mid attribute the source to a track). The inbound SSRC is the remote's choice and unknown before its
    // first packet, so the exact negotiated clock is applied by matching the packet's payload type — the audio PT
    // seeds the audio clock, the video PT seeds 90 kHz — rather than by SSRC or by arrival order. This closes the
    // CF-004e video-first gap where the audio clock was handed to whichever source arrived first.
    private readonly IReadOnlyDictionary<byte, BundledInboundClockDescriptor> _clockByPayloadType;

    /// <summary>
    /// Creates the reception tracker.
    /// </summary>
    /// <param name="utcNow">The wall clock read for arrival times (jitter, DLSR); injectable for tests.</param>
    /// <param name="clockByPayloadType">
    /// The negotiated inbound clock/kind/MID keyed by RTP payload type, or <see langword="null"/> for none. A
    /// source's exact §A.8 clock (and its track attribution) is applied by matching the first packet's payload
    /// type against this map — the inbound SSRC is the remote's choice and not known ahead of its first packet, so
    /// payload type is the reliable discriminator. A payload type not in the map (or a null map) falls back to
    /// inferring the clock from the first usable packet pair, with an unknown kind.
    /// </param>
    public BundledInboundReceptionStats(
        Func<DateTimeOffset>? utcNow = null,
        IReadOnlyDictionary<byte, BundledInboundClockDescriptor>? clockByPayloadType = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _clockByPayloadType = clockByPayloadType ?? new Dictionary<byte, BundledInboundClockDescriptor>();
    }

    /// <summary>
    /// Records one inbound RTP packet against its source SSRC, updating that source's sequence tracking
    /// (RFC 3550 §A.1), loss counters (§A.3), and interarrival jitter (§A.8). Called on the receive loop
    /// after SRTP-unprotect and RTP decode. The first packet for an SSRC resolves that source's negotiated
    /// clock and track kind/MID from <paramref name="payloadType"/>.
    /// </summary>
    /// <param name="ssrc">The packet's synchronisation source.</param>
    /// <param name="sequenceNumber">The packet's RTP sequence number.</param>
    /// <param name="rtpTimestamp">The packet's RTP timestamp (for the §A.8 transit estimate).</param>
    /// <param name="payloadType">The packet's RTP payload type, matched against the negotiated clock map.</param>
    public void RecordRtp(uint ssrc, ushort sequenceNumber, uint rtpTimestamp, byte payloadType = 0)
    {
        var state = GetOrAddSource(ssrc, payloadType);
        state.RecordRtp(sequenceNumber, rtpTimestamp, _utcNow());
    }

    // A source seen only via an SR (before any RTP) has no payload type to resolve the negotiated clock from.
    private const int NoPayloadType = -1;

    // The negotiated clock/kind for the packet's payload type seeds the source the first time its SSRC is seen —
    // an audio PT seeds the audio clock, a video PT seeds 90 kHz, keyed by payload type (not arrival order). A PT
    // absent from the map (or NoPayloadType from an SR) creates an inferred-clock, unknown-kind source. Racing
    // GetOrAdd factories can both build a state, but the loser is discarded by the dictionary; a rate handed to a
    // discarded state is harmless.
    private BundledSourceReceptionState GetOrAddSource(uint ssrc, int payloadType)
        => _sources.GetOrAdd(ssrc, _ =>
        {
            if (payloadType != NoPayloadType &&
                _clockByPayloadType.TryGetValue((byte)payloadType, out var descriptor))
            {
                _sourceKinds.TryAdd(ssrc, new BundledInboundSourceKind(descriptor.Kind, descriptor.Mid));
                return new BundledSourceReceptionState(descriptor.ClockRate);
            }

            _sourceKinds.TryAdd(ssrc, new BundledInboundSourceKind(BundledStreamKind.Unknown, Mid: null));
            return new BundledSourceReceptionState();
        });

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
        // An SR carries no payload type; a source seen only via its SR is created with an inferred clock and an
        // unknown kind until its first RTP packet (which resolves the negotiated clock/kind by payload type).
        var state = GetOrAddSource(senderSsrc, NoPayloadType);
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
    /// worst stream), the session-aggregate scalar the stats surface exposes; the per-SSRC breakdown is
    /// <see cref="SnapshotJitterMsPerSsrc"/> (CF-004f). A source whose payload type matched a negotiated clock is
    /// exact; an inferred-clock source contributes once its clock settles.
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

    /// <summary>
    /// Snapshots the local receive-side interarrival jitter (RFC 3550 §A.8) per inbound remote SSRC — one entry
    /// per source that has an established clock — each carrying its jitter in milliseconds and the kind/MID
    /// resolved from the first packet's payload type. The SSRC is the remote's choice; its track kind/MID is
    /// derived from the negotiated payload-type map (audio PT → audio, video PT → video), which is exact when the
    /// payload type is unambiguous. A source seen only via an SR (no RTP), or one whose payload type was not in
    /// the negotiated map, reports <see cref="BundledStreamKind.Unknown"/> with a null MID.
    /// </summary>
    public IReadOnlyList<BundledInboundSsrcJitter> SnapshotJitterMsPerSsrc()
    {
        var result = new List<BundledInboundSsrcJitter>(_sources.Count);
        foreach (var (ssrc, state) in _sources)
        {
            if (state.SnapshotJitterMs() is not { } ms)
                continue; // no established clock yet — no jitter to attribute.

            var kind = _sourceKinds.TryGetValue(ssrc, out var k)
                ? k
                : new BundledInboundSourceKind(BundledStreamKind.Unknown, Mid: null);
            result.Add(new BundledInboundSsrcJitter(ssrc, kind.Kind, kind.Mid, ms));
        }

        return result;
    }

    // RFC 3550 §6.4.1: LSR is the middle 32 bits of the sender's 64-bit NTP timestamp.
    private static uint ToMiddle32Bits(ulong ntpTimestamp) => (uint)((ntpTimestamp >> 16) & 0xFFFFFFFF);
}

/// <summary>
/// The media kind of a BUNDLE stream (RFC 8843), used to attribute a per-SSRC quality metric to audio or video.
/// <see cref="Unknown"/> is used when the kind could not be resolved (an inbound source whose payload type was
/// not in the negotiated map, or one seen only via an RTCP Sender Report before any RTP).
/// </summary>
internal enum BundledStreamKind
{
    /// <summary>The kind is not known (unmapped payload type, or SR-only source before RTP).</summary>
    Unknown = 0,

    /// <summary>An audio stream.</summary>
    Audio = 1,

    /// <summary>A video stream.</summary>
    Video = 2,
}

/// <summary>
/// The negotiated inbound media parameters for a given RTP payload type: the clock rate seeded into a source's
/// §A.8 jitter and the track kind/MID the source is attributed to. Built from the negotiated track configuration
/// and keyed by payload type because the inbound SSRC is the remote's choice, unknown before its first packet.
/// </summary>
/// <param name="ClockRate">The negotiated RTP clock rate (Hz) for this payload type.</param>
/// <param name="Kind">The media kind (audio/video) this payload type belongs to.</param>
/// <param name="Mid">The MID of the track this payload type belongs to, or <see langword="null"/> when unknown.</param>
internal readonly record struct BundledInboundClockDescriptor(uint ClockRate, BundledStreamKind Kind, string? Mid);

/// <summary>
/// The resolved kind/MID of one inbound source, remembered from the first packet's payload type so the per-SSRC
/// jitter snapshot can attribute the source to a track.
/// </summary>
/// <param name="Kind">The media kind (audio/video/unknown) of the source.</param>
/// <param name="Mid">The MID the source is attributed to, or <see langword="null"/> when unknown.</param>
internal readonly record struct BundledInboundSourceKind(BundledStreamKind Kind, string? Mid);

/// <summary>
/// The local receive-side interarrival jitter (RFC 3550 §A.8) of one inbound remote SSRC, in milliseconds, with
/// the track kind/MID resolved from the first packet's payload type.
/// </summary>
/// <param name="Ssrc">The inbound (remote) synchronisation source.</param>
/// <param name="Kind">The media kind (audio/video/unknown) of the source.</param>
/// <param name="Mid">The MID the source is attributed to, or <see langword="null"/> when unknown.</param>
/// <param name="JitterMs">The source's §A.8 interarrival jitter in milliseconds.</param>
internal readonly record struct BundledInboundSsrcJitter(uint Ssrc, BundledStreamKind Kind, string? Mid, double JitterMs);

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
