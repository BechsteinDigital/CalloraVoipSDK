using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RFC 3550 §6.1 compound decoding: unrecognized packet types must be skipped via their
/// length field. Regression: a Fritz!Box compound (RR + SDES + XR/207 per RFC 3611) made
/// Decode throw on the XR part, discarding the whole datagram — the quality monitor saw
/// zero inbound RTCP for the entire call (remote jitter/loss/RTT all stuck at 0).
/// </summary>
public sealed class RtcpCompoundDecodeTests
{
    private static byte[] MinimalReceiverReport(uint ssrc)
    {
        // V=2, P=0, RC=0 | PT=201 | length=1 (8 bytes total) | SSRC
        var packet = new byte[8];
        packet[0] = 0x80;
        packet[1] = 201;
        packet[3] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), ssrc);
        return packet;
    }

    private static byte[] ExtendedReport(uint ssrc)
    {
        // V=2 | PT=207 (XR, RFC 3611) | length=2 (12 bytes) | SSRC | one opaque block word
        var packet = new byte[12];
        packet[0] = 0x80;
        packet[1] = 207;
        packet[3] = 2;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), ssrc);
        return packet;
    }

    [Fact]
    public void Unknown_packet_type_is_skipped_and_known_parts_survive()
    {
        var compound = MinimalReceiverReport(0x1111).Concat(ExtendedReport(0x1111))
            .Concat(MinimalReceiverReport(0x2222)).ToArray();

        var packets = new RtcpPacketCodec().Decode(compound);

        Assert.Equal(2, packets.Count);
        Assert.All(packets, p => Assert.IsType<RtcpReceiverReport>(p));
    }

    [Fact]
    public void Compound_with_only_unknown_types_yields_empty_list()
    {
        var packets = new RtcpPacketCodec().Decode(ExtendedReport(0x1111));

        Assert.Empty(packets);
    }

    [Fact]
    public void Truncated_unknown_packet_still_throws()
    {
        var xr = ExtendedReport(0x1111);
        var truncated = xr.AsSpan(0, 8).ToArray(); // claims 12 bytes, delivers 8

        Assert.Throws<ArgumentException>(() => { _ = new RtcpPacketCodec().Decode(truncated); });
    }
}
