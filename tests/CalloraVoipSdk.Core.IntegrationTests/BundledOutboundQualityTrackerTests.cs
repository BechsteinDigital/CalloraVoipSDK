using CalloraVoipSdk.Core.Infrastructure.Rtp;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The outbound quality tracker (CF-004c, RFC 3550 §6.4.1): it records each Sender Report's LSR + send instant
/// and, when a peer echoes that LSR back in a reception report block about our stream, derives the round-trip
/// time (<c>arrival − sentAt − DLSR</c>) and captures the loss the peer reports on our media. A block about an
/// SSRC we do not send is ignored; a mismatched or absent LSR yields no RTT but still captures loss.
/// </summary>
public sealed class BundledOutboundQualityTrackerTests
{
    private const uint LocalSsrc = 0x0A0A0A0A;
    private static readonly DateTimeOffset SentAt = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Before_any_report_the_snapshot_is_empty()
    {
        var snapshot = new BundledOutboundQualityTracker().Snapshot();
        Assert.Null(snapshot.RoundTripTimeMs);
        Assert.Null(snapshot.RemotePacketLossFraction);
    }

    [Fact]
    public void A_matching_echoed_report_yields_the_round_trip_time_and_the_reported_loss()
    {
        var tracker = new BundledOutboundQualityTracker();
        tracker.RecordLocalSenderReport(LocalSsrc, srMiddle32: 0xAABB_CCDD, SentAt);

        // The peer held our SR for 20 ms (0.020 × 65536 ≈ 1311 DLSR units) and its report arrived 100 ms after
        // we sent the SR: RTT = 100 ms − 20 ms = 80 ms. FractionLost 64/256 = 0.25.
        tracker.RecordRemoteReportBlock(
            aboutLocalSsrc: LocalSsrc, fractionLost: 64, lastSr: 0xAABB_CCDD, delaySinceLastSr: 1311,
            arrivalUtc: SentAt + TimeSpan.FromMilliseconds(100));

        var snapshot = tracker.Snapshot();
        Assert.NotNull(snapshot.RoundTripTimeMs);
        Assert.Equal(80.0, snapshot.RoundTripTimeMs!.Value, precision: 1);
        Assert.Equal(0.25, snapshot.RemotePacketLossFraction!.Value, precision: 6);
    }

    [Fact]
    public void A_block_echoing_no_sender_report_captures_loss_but_no_round_trip_time()
    {
        var tracker = new BundledOutboundQualityTracker();
        tracker.RecordLocalSenderReport(LocalSsrc, srMiddle32: 0xAABB_CCDD, SentAt);

        tracker.RecordRemoteReportBlock(
            aboutLocalSsrc: LocalSsrc, fractionLost: 12, lastSr: 0, delaySinceLastSr: 0,
            arrivalUtc: SentAt + TimeSpan.FromMilliseconds(50));

        var snapshot = tracker.Snapshot();
        Assert.Null(snapshot.RoundTripTimeMs);
        Assert.Equal(12 / 256.0, snapshot.RemotePacketLossFraction!.Value, precision: 6);
    }

    [Fact]
    public void A_block_echoing_a_stale_sender_report_yields_no_round_trip_time()
    {
        var tracker = new BundledOutboundQualityTracker();
        tracker.RecordLocalSenderReport(LocalSsrc, srMiddle32: 0x2222_2222, SentAt);

        // The peer echoes a different (older) LSR than the one we last recorded — RTT cannot be attributed.
        tracker.RecordRemoteReportBlock(
            aboutLocalSsrc: LocalSsrc, fractionLost: 3, lastSr: 0x1111_1111, delaySinceLastSr: 0,
            arrivalUtc: SentAt + TimeSpan.FromMilliseconds(50));

        Assert.Null(tracker.Snapshot().RoundTripTimeMs);
    }

    [Fact]
    public void A_block_about_an_unsent_ssrc_is_ignored()
    {
        var tracker = new BundledOutboundQualityTracker();
        tracker.RecordLocalSenderReport(LocalSsrc, srMiddle32: 0xAABB_CCDD, SentAt);

        // A report about a source we never sent must not move either metric (it does not describe our media).
        tracker.RecordRemoteReportBlock(
            aboutLocalSsrc: 0xDEAD_BEEF, fractionLost: 128, lastSr: 0xAABB_CCDD, delaySinceLastSr: 0,
            arrivalUtc: SentAt + TimeSpan.FromMilliseconds(50));

        var snapshot = tracker.Snapshot();
        Assert.Null(snapshot.RoundTripTimeMs);
        Assert.Null(snapshot.RemotePacketLossFraction);
    }

    [Fact]
    public void A_non_positive_round_trip_is_discarded()
    {
        var tracker = new BundledOutboundQualityTracker();
        tracker.RecordLocalSenderReport(LocalSsrc, srMiddle32: 0xAABB_CCDD, SentAt);

        // DLSR (200 ms) exceeds the observed arrival gap (50 ms) — a skewed/stale report; RTT must stay unset.
        var dlsr200Ms = (uint)Math.Round(0.200 * 65536.0);
        tracker.RecordRemoteReportBlock(
            aboutLocalSsrc: LocalSsrc, fractionLost: 0, lastSr: 0xAABB_CCDD, delaySinceLastSr: dlsr200Ms,
            arrivalUtc: SentAt + TimeSpan.FromMilliseconds(50));

        Assert.Null(tracker.Snapshot().RoundTripTimeMs);
    }
}
