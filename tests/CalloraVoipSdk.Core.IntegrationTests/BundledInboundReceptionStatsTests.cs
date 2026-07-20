using CalloraVoipSdk.Core.Infrastructure.Rtp;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Per-SSRC inbound reception statistics for a BUNDLE session (CF-004b, RFC 3550 §6.4.1 / A.1 / A.3 / A.8):
/// each decoded inbound RTP packet updates its source's sequence tracking, loss counters, and interarrival
/// jitter; each inbound Sender Report captures the LSR/arrival for DLSR. The reporter snapshots one reception
/// report block per active source.
/// </summary>
public sealed class BundledInboundReceptionStatsTests
{
    private const uint SsrcA = 0x0A0A0A0A;
    private const uint SsrcB = 0x0B0B0B0B;

    [Fact]
    public void A_perfect_stream_reports_no_loss_and_the_extended_highest_sequence()
    {
        var stats = new BundledInboundReceptionStats();

        for (ushort seq = 100; seq <= 109; seq++)
            stats.RecordRtp(SsrcA, seq, rtpTimestamp: (uint)(seq * 160));

        var block = Assert.Single(stats.SnapshotReportBlocks());
        Assert.Equal(SsrcA, block.Ssrc);
        Assert.Equal(0, block.FractionLost);
        Assert.Equal(0, block.CumulativePacketsLost);
        Assert.Equal(109u, block.ExtendedHighestSequenceNumber);
    }

    [Fact]
    public void A_gap_produces_cumulative_and_fraction_loss()
    {
        var stats = new BundledInboundReceptionStats();

        // Send seq 1..10 but skip 4, 5 (2 of 10 expected lost).
        foreach (ushort seq in new ushort[] { 1, 2, 3, 6, 7, 8, 9, 10 })
            stats.RecordRtp(SsrcA, seq, rtpTimestamp: (uint)(seq * 160));

        var block = Assert.Single(stats.SnapshotReportBlocks());
        Assert.Equal(10u, block.ExtendedHighestSequenceNumber);
        // expected = 10 (seq 1..10), received = 8 → cumulative lost 2.
        Assert.Equal(2, block.CumulativePacketsLost);
        // fraction over the interval: lost 2 of 10 expected → (2<<8)/10 = 51.
        Assert.Equal(51, block.FractionLost);
    }

    [Fact]
    public void Reorder_within_the_stream_is_not_counted_as_loss()
    {
        var stats = new BundledInboundReceptionStats();

        // 1,2,4,3,5 — a reorder of 3 and 4; all five arrive, nothing is lost.
        foreach (ushort seq in new ushort[] { 1, 2, 4, 3, 5 })
            stats.RecordRtp(SsrcA, seq, rtpTimestamp: (uint)(seq * 160));

        var block = Assert.Single(stats.SnapshotReportBlocks());
        Assert.Equal(5u, block.ExtendedHighestSequenceNumber);
        Assert.Equal(0, block.CumulativePacketsLost);
        Assert.Equal(0, block.FractionLost);
    }

    [Fact]
    public void Fraction_lost_is_measured_per_report_interval()
    {
        var stats = new BundledInboundReceptionStats();

        // First interval: clean 1..5.
        for (ushort seq = 1; seq <= 5; seq++)
            stats.RecordRtp(SsrcA, seq, rtpTimestamp: (uint)(seq * 160));
        var first = Assert.Single(stats.SnapshotReportBlocks());
        Assert.Equal(0, first.FractionLost);
        Assert.Equal(0, first.CumulativePacketsLost);

        // Second interval: 6,7,9,10 — skip 8 (1 lost of 5 in this interval).
        foreach (ushort seq in new ushort[] { 6, 7, 9, 10 })
            stats.RecordRtp(SsrcA, seq, rtpTimestamp: (uint)(seq * 160));
        var second = Assert.Single(stats.SnapshotReportBlocks());
        // interval expected = 5 (6..10), received = 4 → (1<<8)/5 = 51.
        Assert.Equal(51, second.FractionLost);
        Assert.Equal(1, second.CumulativePacketsLost);
    }

    [Fact]
    public void Multiple_sources_are_tracked_independently()
    {
        var stats = new BundledInboundReceptionStats();

        for (ushort seq = 1; seq <= 5; seq++)
            stats.RecordRtp(SsrcA, seq, rtpTimestamp: (uint)(seq * 160));
        // B loses one: 1,2,4,5 (skip 3).
        foreach (ushort seq in new ushort[] { 1, 2, 4, 5 })
            stats.RecordRtp(SsrcB, seq, rtpTimestamp: (uint)(seq * 90));

        var blocks = stats.SnapshotReportBlocks();
        Assert.Equal(2, blocks.Count);

        var a = blocks.Single(b => b.Ssrc == SsrcA);
        var b = blocks.Single(x => x.Ssrc == SsrcB);
        Assert.Equal(0, a.CumulativePacketsLost);
        Assert.Equal(1, b.CumulativePacketsLost);
    }

    [Fact]
    public void Constant_spacing_yields_zero_jitter()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new StepClock(start);
        var stats = new BundledInboundReceptionStats(clock.Now);

        // 8 kHz clock, 20 ms packets: RTP +160, arrival +20 ms — transit is constant, so jitter decays to 0.
        for (var i = 0; i < 30; i++)
        {
            stats.RecordRtp(SsrcA, (ushort)(1 + i), rtpTimestamp: (uint)(160 * i));
            clock.Advance(TimeSpan.FromMilliseconds(20));
        }

        var block = Assert.Single(stats.SnapshotReportBlocks());
        Assert.Equal(0u, block.InterarrivalJitter);
    }

    [Fact]
    public void Variable_spacing_produces_nonzero_jitter()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new StepClock(start);
        var stats = new BundledInboundReceptionStats(clock.Now);

        // First two packets establish the 8 kHz clock at a nominal 20 ms cadence.
        stats.RecordRtp(SsrcA, 1, rtpTimestamp: 0);
        clock.Advance(TimeSpan.FromMilliseconds(20));
        stats.RecordRtp(SsrcA, 2, rtpTimestamp: 160);

        // Then jitter the arrivals: RTP keeps +160 but arrival alternates 5 ms / 35 ms — transit varies.
        var rtp = 160u;
        for (var i = 0; i < 20; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(i % 2 == 0 ? 5 : 35));
            rtp += 160;
            stats.RecordRtp(SsrcA, (ushort)(3 + i), rtp);
        }

        var block = Assert.Single(stats.SnapshotReportBlocks());
        Assert.True(block.InterarrivalJitter > 0u, "Variable arrival spacing must produce a non-zero jitter estimate.");
    }

    [Fact]
    public void A_received_sender_report_sets_lsr_and_a_plausible_dlsr()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new StepClock(start);
        var stats = new BundledInboundReceptionStats(clock.Now);

        stats.RecordRtp(SsrcA, 1, rtpTimestamp: 0);

        // A Sender Report with a known NTP timestamp; LSR is its middle 32 bits.
        const ulong ntp = 0x1122_3344_5566_7788UL;
        const uint expectedLsr = (uint)((ntp >> 16) & 0xFFFFFFFF); // 0x3344_5566
        stats.RecordSenderReport(SsrcA, ntp);

        // 2 seconds pass before the report is emitted → DLSR ≈ 2 × 65536.
        clock.Advance(TimeSpan.FromSeconds(2));

        var block = Assert.Single(stats.SnapshotReportBlocks());
        Assert.Equal(expectedLsr, block.LastSr);
        Assert.Equal((uint)(2 * 65536), block.DelaySinceLastSr);
    }

    [Fact]
    public void No_sender_report_leaves_lsr_and_dlsr_zero()
    {
        var stats = new BundledInboundReceptionStats();
        stats.RecordRtp(SsrcA, 1, rtpTimestamp: 0);

        var block = Assert.Single(stats.SnapshotReportBlocks());
        Assert.Equal(0u, block.LastSr);
        Assert.Equal(0u, block.DelaySinceLastSr);
    }

    [Fact]
    public void A_source_seen_only_via_its_sender_report_contributes_no_block_until_rtp_arrives()
    {
        var stats = new BundledInboundReceptionStats();
        stats.RecordSenderReport(SsrcA, 0x1122_3344_5566_7788UL);

        // No RTP counted yet → no reception is described.
        Assert.Empty(stats.SnapshotReportBlocks());

        stats.RecordRtp(SsrcA, 1, rtpTimestamp: 0);
        var block = Assert.Single(stats.SnapshotReportBlocks());
        Assert.Equal((uint)0x3344_5566, block.LastSr);
    }

    private sealed class StepClock(DateTimeOffset start)
    {
        private DateTimeOffset _now = start;
        public DateTimeOffset Now() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
