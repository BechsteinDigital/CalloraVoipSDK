using System.Buffers.Binary;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RTCP-XR (PT=207, RFC 3611) decoding. The compound decoder previously skipped XR entirely;
/// it now surfaces the VoIP Metrics block (§4.7) so call-quality data can be consumed, while
/// still tolerating XR inside a compound packet next to SR/RR.
/// </summary>
public sealed class RtcpExtendedReportDecodeTests
{
    private const uint ReportSsrc = 0xAABBCCDDu;
    private const uint SourceSsrc = 0x11223344u;

    // Builds a standalone XR packet carrying one VoIP Metrics block with known field values.
    private static byte[] BuildXrWithVoipMetrics(byte blockType = 7)
    {
        var packet = new byte[44];
        packet[0] = 0x80;                                           // V=2, P=0, reserved=0
        packet[1] = 207;                                            // PT = XR
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 10); // length = words - 1 (44/4 - 1)
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), ReportSsrc);

        packet[8] = blockType;                                      // BT
        packet[9] = 0;                                              // type-specific
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10), 8); // block length = 8 words (32 bytes)

        var c = packet.AsSpan(12);
        BinaryPrimitives.WriteUInt32BigEndian(c, SourceSsrc);
        c[4] = 5;   // loss rate
        c[5] = 2;   // discard rate
        c[6] = 10;  // burst density
        c[7] = 1;   // gap density
        BinaryPrimitives.WriteUInt16BigEndian(c[8..], 300);   // burst duration
        BinaryPrimitives.WriteUInt16BigEndian(c[10..], 5000); // gap duration
        BinaryPrimitives.WriteUInt16BigEndian(c[12..], 120);  // round trip delay
        BinaryPrimitives.WriteUInt16BigEndian(c[14..], 40);   // end system delay
        c[20] = 93;  // R factor
        c[21] = 127; // ext R factor (unavailable)
        c[22] = 43;  // MOS-LQ ×10 = 4.3
        c[23] = 41;  // MOS-CQ ×10 = 4.1
        BinaryPrimitives.WriteUInt16BigEndian(c[26..], 60);  // JB nominal
        BinaryPrimitives.WriteUInt16BigEndian(c[28..], 120); // JB maximum
        BinaryPrimitives.WriteUInt16BigEndian(c[30..], 200); // JB abs max
        return packet;
    }

    private static byte[] BuildEmptyReceiverReport(uint ssrc)
    {
        var rr = new byte[8];
        rr[0] = 0x80;                                          // V=2, RC=0
        rr[1] = 201;                                           // PT = RR
        BinaryPrimitives.WriteUInt16BigEndian(rr.AsSpan(2), 1); // length = words - 1 (8/4 - 1)
        BinaryPrimitives.WriteUInt32BigEndian(rr.AsSpan(4), ssrc);
        return rr;
    }

    [Fact]
    public void Voip_metrics_block_fields_are_decoded()
    {
        var report = Assert.IsType<RtcpExtendedReport>(
            Assert.Single(new RtcpPacketCodec().Decode(BuildXrWithVoipMetrics())));

        Assert.Equal(ReportSsrc, report.Ssrc);
        var m = Assert.Single(report.VoipMetrics);
        Assert.Equal(SourceSsrc, m.SourceSsrc);
        Assert.Equal(5, m.LossRate);
        Assert.Equal(2, m.DiscardRate);
        Assert.Equal(10, m.BurstDensity);
        Assert.Equal(1, m.GapDensity);
        Assert.Equal(300, m.BurstDurationMs);
        Assert.Equal(5000, m.GapDurationMs);
        Assert.Equal(120, m.RoundTripDelayMs);
        Assert.Equal(40, m.EndSystemDelayMs);
        Assert.Equal(93, m.RFactor);
        Assert.Equal(127, m.ExternalRFactor);
        Assert.Equal(43, m.MosLq);
        Assert.Equal(41, m.MosCq);
        Assert.Equal(60, m.JitterBufferNominalMs);
        Assert.Equal(120, m.JitterBufferMaximumMs);
        Assert.Equal(200, m.JitterBufferAbsoluteMaxMs);
    }

    [Fact]
    public void Xr_is_parsed_alongside_an_rr_in_a_compound_packet()
    {
        var compound = BuildEmptyReceiverReport(0x01020304).Concat(BuildXrWithVoipMetrics()).ToArray();

        var packets = new RtcpPacketCodec().Decode(compound);

        Assert.Contains(packets, p => p is RtcpReceiverReport);
        Assert.Contains(packets, p => p is RtcpExtendedReport { VoipMetrics.Count: 1 });
    }

    [Fact]
    public void Unknown_xr_block_types_are_skipped_but_the_report_is_still_surfaced()
    {
        // Block type 4 (Receiver Reference Time) is not VoIP Metrics — it must be skipped
        // without discarding the XR envelope.
        var report = Assert.IsType<RtcpExtendedReport>(
            Assert.Single(new RtcpPacketCodec().Decode(BuildXrWithVoipMetrics(blockType: 4))));

        Assert.Equal(ReportSsrc, report.Ssrc);
        Assert.Empty(report.VoipMetrics);
    }
}
