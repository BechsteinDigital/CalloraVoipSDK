using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The per-SSRC RTCP Sender Report counters an outbound bundled track keeps (RFC 3550 §6.4.1):
/// <see cref="BundledOutboundTrack.RecordSent"/> advances the packet and octet totals, flips
/// <see cref="BundledOutboundTrack.HasSent"/>, and tracks the last RTP timestamp for the reporter to build
/// a Sender Report from.
/// </summary>
public sealed class BundledOutboundTrackSenderStatsTests
{
    private const byte MidExtId = 3;

    [Fact]
    public void A_fresh_track_has_not_sent_and_has_zero_counters()
    {
        var track = Track();

        Assert.False(track.HasSent);
        Assert.Equal(0, track.SenderPacketCount);
        Assert.Equal(0, track.SenderOctetCount);
        Assert.Equal(0u, track.LastRtpTimestamp);
    }

    [Fact]
    public void Record_sent_accumulates_packet_and_octet_counts_and_tracks_the_last_timestamp()
    {
        var track = Track();

        track.RecordSent(payloadOctetCount: 160, rtpTimestamp: 5000);
        track.RecordSent(payloadOctetCount: 200, rtpTimestamp: 5160);

        Assert.True(track.HasSent);
        Assert.Equal(2, track.SenderPacketCount);
        Assert.Equal(360, track.SenderOctetCount);
        Assert.Equal(5160u, track.LastRtpTimestamp);
    }

    [Fact]
    public void Record_sent_rejects_a_negative_octet_count()
    {
        var track = Track();

        Assert.Throws<ArgumentOutOfRangeException>(() => track.RecordSent(-1, 0));
    }

    private static BundledOutboundTrack Track() =>
        new(ssrc: 0x0A0A0A0A, defaultPayloadType: 0, samplesPerPacket: 160,
            new RtpOutboundHeaderExtensionStamper(transportWideCcExtensionId: null, MidExtId, "audio"),
            initialSequenceNumber: 1000, initialTimestamp: 5000);
}
