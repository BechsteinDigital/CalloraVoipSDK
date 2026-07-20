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
    private const byte AudioPayloadType = 0;   // PCMU
    private const byte VideoPayloadType = 96;  // dynamic H.264/VP8

    // The negotiated inbound clock/kind map used by the seeded-clock tests: audio PT → 8 kHz audio, video PT →
    // 90 kHz video, attributed by payload type (CF-004f) rather than by arrival order.
    private static Dictionary<byte, BundledInboundClockDescriptor> AudioVideoClockMap(uint audioClockRate = 8000) => new()
    {
        [AudioPayloadType] = new BundledInboundClockDescriptor(audioClockRate, BundledStreamKind.Audio, "0"),
        [VideoPayloadType] = new BundledInboundClockDescriptor(90000, BundledStreamKind.Video, "1"),
    };

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

    [Fact]
    public void Jitter_ms_uses_the_negotiated_clock_rate_when_supplied()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new StepClock(start);
        // 8 kHz negotiated for the audio payload type; the source is seeded by matching its PT (not arrival order).
        var stats = new BundledInboundReceptionStats(clock.Now, AudioVideoClockMap());

        // Nominal 20 ms cadence (RTP +160 per packet at 8 kHz), but every second arrival is 10 ms late, so the
        // transit alternates 0 / +10 ms = 0 / +80 RTP units. With the negotiated clock the §A.8 estimate settles
        // toward a small, well-defined jitter, and the ms conversion is jitterRtpUnits × 1000 / 8000.
        var rtp = 0u;
        for (var i = 0; i < 200; i++)
        {
            stats.RecordRtp(SsrcA, (ushort)(1 + i), rtp, AudioPayloadType);
            clock.Advance(TimeSpan.FromMilliseconds(i % 2 == 0 ? 20 : 30));
            rtp += 160;
        }

        var block = Assert.Single(stats.SnapshotReportBlocks());
        var jitterMs = stats.SnapshotJitterMs();
        Assert.NotNull(jitterMs);
        // The ms value converts the smoothed §A.8 jitter with the 8 kHz clock (J × 1000 / 8000). The wire block
        // carries the SAME jitter truncated to an integer RTP unit, so the ms value agrees with the wire value's
        // ms equivalent to within one RTP unit's worth of milliseconds (1000/8000 = 0.125 ms).
        var wireJitterMs = block.InterarrivalJitter * 1000.0 / 8000.0;
        Assert.Equal(wireJitterMs, jitterMs.Value, tolerance: 1000.0 / 8000.0);
        // A ±10 ms transit swing settles well under 10 ms of smoothed jitter — a plausible, non-trivial value.
        Assert.InRange(jitterMs.Value, 0.01, 10.0);
    }

    [Fact]
    public void Jitter_ms_is_null_before_any_rtp_is_received()
    {
        var stats = new BundledInboundReceptionStats(clockByPayloadType: AudioVideoClockMap(48000));
        Assert.Null(stats.SnapshotJitterMs());
        Assert.Empty(stats.SnapshotJitterMsPerSsrc());
    }

    [Fact]
    public void The_negotiated_clock_is_keyed_by_payload_type_not_arrival_order_so_video_first_still_gets_90khz()
    {
        // CF-004e bug: the audio clock was seeded into whichever source arrived first. In a video-first bundle the
        // video source would then wrongly get the 8 kHz audio clock. Keying by payload type fixes this: the video
        // source (PT 96) gets 90 kHz and the audio source (PT 0) gets 8 kHz regardless of who arrives first.
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new StepClock(start);
        var stats = new BundledInboundReceptionStats(clock.Now, AudioVideoClockMap());

        // Video arrives FIRST at 90 kHz / 30 fps: RTP +3000 per frame, arrival +33.333 ms. A constant cadence
        // means the §A.8 estimate stays ~0, but the clock must be exactly 90 kHz for the ms conversion.
        var videoRtp = 0u;
        for (var i = 0; i < 30; i++)
        {
            stats.RecordRtp(SsrcB, (ushort)(1 + i), videoRtp, VideoPayloadType);
            clock.Advance(TimeSpan.FromMilliseconds(1000.0 / 30));
            videoRtp += 3000;
        }

        // Then audio (PT 0) at 8 kHz / 20 ms, jittered so it produces a non-zero, clock-dependent ms value.
        var audioRtp = 0u;
        for (var i = 0; i < 60; i++)
        {
            stats.RecordRtp(SsrcA, (ushort)(1 + i), audioRtp, AudioPayloadType);
            clock.Advance(TimeSpan.FromMilliseconds(i % 2 == 0 ? 20 : 30));
            audioRtp += 160;
        }

        var perSsrc = stats.SnapshotJitterMsPerSsrc();
        var video = perSsrc.Single(s => s.Ssrc == SsrcB);
        var audio = perSsrc.Single(s => s.Ssrc == SsrcA);

        // The video source is attributed to video and its wire jitter converts against 90 kHz (≈0 under a
        // constant cadence): jitter_ms = wireJitter × 1000 / 90000.
        Assert.Equal(BundledStreamKind.Video, video.Kind);
        Assert.Equal("1", video.Mid);
        var videoBlock = stats.SnapshotReportBlocks().Single(b => b.Ssrc == SsrcB);
        Assert.Equal(videoBlock.InterarrivalJitter * 1000.0 / 90000.0, video.JitterMs, tolerance: 1000.0 / 90000.0);

        // The audio source is attributed to audio and converts against 8 kHz — the video-first arrival did NOT
        // steal the audio clock, and audio's ms value is a plausible non-trivial jitter.
        Assert.Equal(BundledStreamKind.Audio, audio.Kind);
        Assert.Equal("0", audio.Mid);
        var audioBlock = stats.SnapshotReportBlocks().Single(b => b.Ssrc == SsrcA);
        Assert.Equal(audioBlock.InterarrivalJitter * 1000.0 / 8000.0, audio.JitterMs, tolerance: 1000.0 / 8000.0);
        Assert.InRange(audio.JitterMs, 0.01, 10.0);
    }

    [Fact]
    public void An_unmapped_payload_type_source_is_reported_with_an_unknown_kind_and_no_mid()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new StepClock(start);
        var stats = new BundledInboundReceptionStats(clock.Now, AudioVideoClockMap());

        // PT 111 is not in the negotiated map → inferred clock, unknown kind, null MID. Feed a clean cadence so
        // the inferred clock settles and a jitter value is produced.
        const byte unmappedPt = 111;
        stats.RecordRtp(SsrcA, 1, rtpTimestamp: 0, unmappedPt);
        clock.Advance(TimeSpan.FromMilliseconds(20));
        var rtp = 160u;
        for (var i = 0; i < 40; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(i % 2 == 0 ? 5 : 35));
            stats.RecordRtp(SsrcA, (ushort)(2 + i), rtp, unmappedPt);
            rtp += 160;
        }

        var entry = Assert.Single(stats.SnapshotJitterMsPerSsrc());
        Assert.Equal(SsrcA, entry.Ssrc);
        Assert.Equal(BundledStreamKind.Unknown, entry.Kind);
        Assert.Null(entry.Mid);
        Assert.True(entry.JitterMs > 0);
    }

    [Fact]
    public void Jitter_ms_falls_back_to_the_inferred_clock_when_no_rate_is_negotiated()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new StepClock(start);
        // No negotiated rate (default 0) → the clock is inferred from the first usable packet pair.
        var stats = new BundledInboundReceptionStats(clock.Now);

        // First pair at a clean 20 ms / +160 RTP cadence establishes the inferred 8 kHz clock.
        stats.RecordRtp(SsrcA, 1, rtpTimestamp: 0);
        clock.Advance(TimeSpan.FromMilliseconds(20));
        stats.RecordRtp(SsrcA, 2, rtpTimestamp: 160);

        // Then jitter the arrivals so the estimate is non-zero and a ms value is produced against the inferred clock.
        var rtp = 160u;
        for (var i = 0; i < 40; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(i % 2 == 0 ? 5 : 35));
            rtp += 160;
            stats.RecordRtp(SsrcA, (ushort)(3 + i), rtp);
        }

        var jitterMs = stats.SnapshotJitterMs();
        Assert.NotNull(jitterMs);
        Assert.True(jitterMs.Value > 0, "An inferred clock must still yield a positive jitter-ms once it settles.");
    }

    private sealed class StepClock(DateTimeOffset start)
    {
        private DateTimeOffset _now = start;
        public DateTimeOffset Now() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
