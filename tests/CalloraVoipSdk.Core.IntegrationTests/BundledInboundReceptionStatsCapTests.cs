using CalloraVoipSdk.Core.Infrastructure.Rtp;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #161 RTP P1-3: BundledInboundReceptionStats created persistent, never-expiring per-SSRC state for every
/// distinct inbound SSRC — via RTP and via Sender Reports, even before routing. A peer that sprays distinct
/// SSRCs (5000 SRs → 5000 sources in the probe) grew the tracking table without bound (K4). A hard cap now
/// refuses a new SSRC once the table is full, never evicting an active source.
/// </summary>
public sealed class BundledInboundReceptionStatsCapTests
{
    [Fact]
    public void Rtp_sources_over_the_cap_are_refused_without_evicting_active_ones()
    {
        var stats = new BundledInboundReceptionStats(maxTrackedSources: 4);

        for (uint ssrc = 1; ssrc <= 10; ssrc++)
            stats.RecordRtp(ssrc, sequenceNumber: 100, rtpTimestamp: 1000);

        Assert.Equal(6, stats.RejectedSourceCount); // 4 admitted, 6 refused

        // An already-admitted source keeps its state — a further packet is not a refusal.
        stats.RecordRtp(1, sequenceNumber: 200, rtpTimestamp: 2000);
        Assert.Equal(6, stats.RejectedSourceCount);
    }

    [Fact]
    public void Sender_reports_over_the_cap_are_refused()
    {
        var stats = new BundledInboundReceptionStats(maxTrackedSources: 3);

        for (uint ssrc = 1; ssrc <= 8; ssrc++)
            stats.RecordSenderReport(ssrc, senderReportNtpTimestamp: 0);

        Assert.Equal(5, stats.RejectedSourceCount); // 3 admitted, 5 refused
    }

    [Fact]
    public void Sources_within_the_cap_are_tracked()
    {
        var stats = new BundledInboundReceptionStats(maxTrackedSources: 4);

        stats.RecordRtp(1, sequenceNumber: 100, rtpTimestamp: 1000);
        stats.RecordRtp(2, sequenceNumber: 100, rtpTimestamp: 1000);
        stats.RecordSenderReport(3, senderReportNtpTimestamp: 0);

        Assert.Equal(0, stats.RejectedSourceCount);
    }

    [Fact]
    public void A_non_positive_cap_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BundledInboundReceptionStats(maxTrackedSources: 0));
    }
}
