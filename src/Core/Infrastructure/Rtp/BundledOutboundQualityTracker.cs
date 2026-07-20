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

    private double? _roundTripTimeMs;
    private double? _remotePacketLossFraction;

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

            _remotePacketLossFraction = fractionLost / 256.0;

            // No SR echoed, or the peer echoed an older SR than the one we last recorded: RTT is not derivable
            // this interval (matching the SIP path, which also keys RTT off the most recent SR only).
            if (lastSr == 0 || localSr.Middle32 != lastSr)
                return;

            var dlsr = TimeSpan.FromSeconds(delaySinceLastSr / 65536.0);
            var roundTrip = arrivalUtc - localSr.SentAtUtc - dlsr;
            // A non-positive result means clock skew or a stale/duplicated report — discard it rather than
            // publish a negative or zero RTT.
            if (roundTrip > TimeSpan.Zero)
                _roundTripTimeMs = roundTrip.TotalMilliseconds;
        }
    }

    /// <summary>
    /// Snapshots the latest outbound quality: the most recently derived round-trip time and the peer's most
    /// recently reported loss on our media. Both are <see langword="null"/> until a matching report arrives.
    /// </summary>
    public BundledMediaQuality Snapshot()
    {
        lock (_sync)
        {
            return new BundledMediaQuality(_roundTripTimeMs, _remotePacketLossFraction);
        }
    }
}

/// <summary>
/// A snapshot of the RTCP-derived outbound quality of a bundled media session (RFC 3550 §6.4.1): the
/// round-trip time and the loss the peer reports on our media. Both are <see langword="null"/> until the peer
/// has returned a matching reception report.
/// </summary>
/// <param name="RoundTripTimeMs">
/// The round-trip time in milliseconds derived from the peer's echoed LSR/DLSR, or <see langword="null"/>
/// before a report block echoing one of our Sender Reports has arrived.
/// </param>
/// <param name="RemotePacketLossFraction">
/// The fraction of our packets the peer reports lost (0..1), or <see langword="null"/> before the peer has
/// reported on our media.
/// </param>
internal readonly record struct BundledMediaQuality(
    double? RoundTripTimeMs,
    double? RemotePacketLossFraction);
