namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Consumes the reception report blocks a remote peer sends back about <em>this</em> endpoint's outbound
/// streams (RFC 3550 §6.4.1) and derives the round-trip time and the loss the peer observes on our media
/// (CF-004c, the receive-side counterpart to <see cref="BundledInboundReceptionStats"/>).
/// <para>
/// RTT is computed exactly as the SIP path's <c>CallRtcpQualityMonitor</c> does it (RFC 3550 §6.4.1): each
/// emitted Sender Report is recorded per sending SSRC as its LSR (the middle 32 bits of the SR's NTP
/// timestamp) plus the wall-clock instant it went out; when a peer echoes that LSR back in a report block's
/// <c>LastSR</c> field and reports how long it held it (<c>DLSR</c>), the round trip is
/// <c>arrival − sentAt − DLSR</c>. A block whose <c>LastSR</c> does not match the last SR we recorded for that
/// SSRC (the peer echoed an older report, or none yet) yields no RTT that interval — the peer's reported loss
/// is still captured.
/// </para>
/// <para>
/// Thread-safe: the RTCP receive loop (<see cref="RecordRemoteReportBlock"/>), the reporter's send callback
/// (<see cref="RecordLocalSenderReport"/>), and the stats reader (<see cref="Snapshot"/>) run on different
/// threads and all synchronise on <see cref="_sync"/>.
/// </para>
/// </summary>
internal sealed class BundledOutboundQualityTracker
{
    private readonly object _sync = new();

    // Per local (sending) SSRC: the middle-32 of the last SR we emitted for it and when it went out. The
    // remote echoes that middle-32 back as a report block's LastSR; matching it lets us compute RTT.
    private readonly Dictionary<uint, (uint Middle32, DateTimeOffset SentAtUtc)> _lastLocalSr = new();

    // Per local (sending) SSRC: the most recently derived RTT and the loss the peer last reported on that
    // stream (RFC 3550 §6.4.1). Keyed per SSRC because a report block is per our sending source — a global
    // pair would let audio/video/RTX/simulcast overwrite each other (CF-004f). Each entry is null until its
    // metric arrives; an entry exists only once a report block about that SSRC has been consumed.
    private readonly Dictionary<uint, (double? RoundTripTimeMs, double? RemotePacketLossFraction)> _perSsrc = new();

    /// <summary>
    /// Records that a Sender Report was emitted for <paramref name="ssrc"/>, capturing the LSR the peer will
    /// echo back and the instant it was sent (RFC 3550 §6.4.1). Called by the periodic reporter for each SR.
    /// </summary>
    /// <param name="ssrc">The sending synchronisation source the SR reported for.</param>
    /// <param name="srMiddle32">The middle 32 bits of the SR's NTP timestamp (the value the peer echoes as LSR).</param>
    /// <param name="sentAtUtc">The wall-clock instant the SR was emitted, used with DLSR to derive RTT.</param>
    public void RecordLocalSenderReport(uint ssrc, uint srMiddle32, DateTimeOffset sentAtUtc)
    {
        lock (_sync)
        {
            _lastLocalSr[ssrc] = (srMiddle32, sentAtUtc);
        }
    }

    /// <summary>
    /// Records one inbound reception report block that a peer sent about a stream we are sending (RFC 3550
    /// §6.4.1). Blocks about an SSRC we have not sent a Sender Report for are ignored (they do not describe our
    /// media). For a recognised block the peer's reported loss is captured, and when its <c>LastSR</c> matches
    /// the last SR we recorded for that SSRC, the round-trip time is computed.
    /// </summary>
    /// <param name="aboutLocalSsrc">The report block's SSRC — one of our sending sources.</param>
    /// <param name="fractionLost">The peer's fraction lost on our stream (1/256 fixed point).</param>
    /// <param name="lastSr">The LSR the peer echoed (middle 32 bits of our last SR's NTP timestamp; 0 if none).</param>
    /// <param name="delaySinceLastSr">The peer's DLSR in 1/65536-second units.</param>
    /// <param name="arrivalUtc">The instant this report arrived, used with the recorded send time to derive RTT.</param>
    public void RecordRemoteReportBlock(
        uint aboutLocalSsrc, byte fractionLost, uint lastSr, uint delaySinceLastSr, DateTimeOffset arrivalUtc)
    {
        lock (_sync)
        {
            if (!_lastLocalSr.TryGetValue(aboutLocalSsrc, out var localSr))
                return; // not a stream we are sending Sender Reports for — the block is not about our media.

            _perSsrc.TryGetValue(aboutLocalSsrc, out var current);
            var loss = fractionLost / 256.0;
            var rtt = current.RoundTripTimeMs;

            // No SR echoed, or the peer echoed an older SR than the one we last recorded: RTT is not derivable
            // this interval (matching the SIP path, which also keys RTT off the most recent SR only) — the
            // previously derived RTT for this SSRC is retained; only the loss is refreshed.
            if (lastSr != 0 && localSr.Middle32 == lastSr)
            {
                var dlsr = TimeSpan.FromSeconds(delaySinceLastSr / 65536.0);
                var roundTrip = arrivalUtc - localSr.SentAtUtc - dlsr;
                // A non-positive result means clock skew or a stale/duplicated report — discard it rather than
                // publish a negative or zero RTT (the prior RTT for this SSRC, if any, is kept).
                if (roundTrip > TimeSpan.Zero)
                    rtt = roundTrip.TotalMilliseconds;
            }

            _perSsrc[aboutLocalSsrc] = (rtt, loss);
        }
    }

    /// <summary>
    /// Snapshots the session-aggregate outbound quality across all our sending SSRCs: the worst (maximum)
    /// round-trip time and the worst (maximum) loss the peer reports on any of our streams. This is the
    /// single scalar surfaced to the stats fassade — the per-stream breakdown is <see cref="SnapshotPerSsrc"/>.
    /// Both are <see langword="null"/> until a matching report arrives for at least one stream.
    /// </summary>
    public BundledMediaQuality Snapshot()
    {
        lock (_sync)
        {
            double? worstRtt = null;
            double? worstLoss = null;
            foreach (var (rtt, loss) in _perSsrc.Values)
            {
                if (rtt is { } r && (worstRtt is null || r > worstRtt))
                    worstRtt = r;
                if (loss is { } l && (worstLoss is null || l > worstLoss))
                    worstLoss = l;
            }

            return new BundledMediaQuality(worstRtt, worstLoss);
        }
    }

    /// <summary>
    /// Snapshots the outbound quality per our sending SSRC (RFC 3550 §6.4.1): one entry per stream we have
    /// consumed a report block for, each carrying the RTT and the loss the peer reports on that specific
    /// stream. Empty until the peer has reported on at least one of our streams. The caller maps each SSRC to
    /// its MID/kind via the negotiated track configuration.
    /// </summary>
    public IReadOnlyList<BundledOutboundSsrcQuality> SnapshotPerSsrc()
    {
        lock (_sync)
        {
            var result = new List<BundledOutboundSsrcQuality>(_perSsrc.Count);
            foreach (var (ssrc, metrics) in _perSsrc)
                result.Add(new BundledOutboundSsrcQuality(ssrc, metrics.RoundTripTimeMs, metrics.RemotePacketLossFraction));

            return result;
        }
    }
}

/// <summary>
/// The RTCP-derived outbound quality of one of our sending SSRCs (RFC 3550 §6.4.1): the round-trip time and
/// the loss the peer reports on that specific stream. Each field is <see langword="null"/> until its metric
/// is available — RTT until the peer echoes a matching Sender Report, loss until the peer reports on the
/// stream (an entry exists only once at least one report block about the SSRC has been consumed).
/// </summary>
/// <param name="Ssrc">The local sending synchronisation source this quality describes.</param>
/// <param name="RoundTripTimeMs">The round-trip time in milliseconds for this stream, or <see langword="null"/>.</param>
/// <param name="RemotePacketLossFraction">The fraction (0..1) of this stream's packets the peer reports lost, or <see langword="null"/>.</param>
internal readonly record struct BundledOutboundSsrcQuality(
    uint Ssrc,
    double? RoundTripTimeMs,
    double? RemotePacketLossFraction);

/// <summary>
/// A snapshot of a bundled media session's derived quality: the RTCP outbound metrics (RFC 3550 §6.4.1 —
/// round-trip time and the loss the peer reports on our media) plus our own local receive-side interarrival
/// jitter (RFC 3550 §A.8). Every field is <see langword="null"/> until its metric is available: RTT/loss until
/// the peer returns a matching reception report, jitter until an inbound clock rate is established.
/// </summary>
/// <param name="RoundTripTimeMs">
/// The round-trip time in milliseconds derived from the peer's echoed LSR/DLSR, or <see langword="null"/>
/// before a report block echoing one of our Sender Reports has arrived.
/// </param>
/// <param name="RemotePacketLossFraction">
/// The fraction of our packets the peer reports lost (0..1), or <see langword="null"/> before the peer has
/// reported on our media.
/// </param>
/// <param name="JitterMs">
/// Our local receive-side interarrival jitter in milliseconds (RFC 3550 §A.8) — the browser
/// <c>getStats</c> inbound-rtp jitter — or <see langword="null"/> before an inbound clock rate is established.
/// Aggregated across inbound sources (the worst) for this single scalar; the per-SSRC breakdown is on
/// <c>BundledInboundReceptionStats.SnapshotJitterMsPerSsrc</c> (CF-004f).
/// </param>
internal readonly record struct BundledMediaQuality(
    double? RoundTripTimeMs,
    double? RemotePacketLossFraction,
    double? JitterMs = null);
