using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #161 RTP P1-2: the transport-cc (TWCC) decoder trusted the 16-bit packet-status count to size two
/// allocations before reading any chunks. Run-length chunks pack up to 8191 symbols into 2 bytes, so a
/// ~40-byte packet could claim 65 535 statuses and force ~800 KB of allocation — an amplification the
/// 8 KiB datagram cap does not stop (K4). The count must be bounded before it sizes anything.
/// </summary>
public sealed class RtcpTransportFeedbackCapTests
{
    [Fact]
    public void Decode_rejects_a_status_count_over_the_cap_before_allocating()
    {
        // 8-byte body header: base seq(2)=0, status count(2)=0xFFFF, reference time(3)=0, fb count(1)=0.
        // The count alone (no chunks) is enough — the cap fires before any allocation or chunk read.
        var fci = new byte[] { 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00 };

        Assert.Throws<ArgumentException>(() => RtcpTransportFeedbackCodec.Decode(senderSsrc: 1, mediaSsrc: 2, fci));
    }

    [Fact]
    public void A_normal_feedback_still_round_trips_through_the_cap()
    {
        var feedback = new RtcpTransportFeedback
        {
            SenderSsrc = 1,
            MediaSsrc = 2,
            ReferenceTimeTicks = 0,
            FeedbackPacketCount = 0,
            Statuses =
            [
                new RtcpTransportFeedbackStatus { SequenceNumber = 100, Received = true, DeltaTicks = 10 },
                new RtcpTransportFeedbackStatus { SequenceNumber = 101, Received = false, DeltaTicks = 0 },
                new RtcpTransportFeedbackStatus { SequenceNumber = 102, Received = true, DeltaTicks = 20 },
            ],
        };

        var encoded = RtcpTransportFeedbackCodec.Encode(feedback);
        // The decode body starts after the RTCP header (4) + sender/media SSRC pair (8).
        var decoded = RtcpTransportFeedbackCodec.Decode(1, 2, encoded.AsSpan(12));

        Assert.Equal(3, decoded.Statuses.Count);
        Assert.True(decoded.Statuses[0].Received);
        Assert.False(decoded.Statuses[1].Received);
        Assert.True(decoded.Statuses[2].Received);
    }
}
