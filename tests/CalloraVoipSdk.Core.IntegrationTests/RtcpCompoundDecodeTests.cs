using System.Buffers.Binary;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RFC 3550 §6.1 compound decoding: a still-unrecognized packet type must be skipped via its
/// length field rather than throwing (which would discard the whole datagram — the regression
/// where a Fritz!Box compound made the quality monitor see zero inbound RTCP). XR (PT=207) is
/// now a recognized type (RFC 3611) and is decoded rather than skipped.
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
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), ssrc);
        return packet;
    }

    private static byte[] ApplicationDefined(uint ssrc)
    {
        // V=2 | PT=204 (APP) — a valid type this codec does not decode, so it must be skipped.
        var packet = new byte[12];
        packet[0] = 0x80;
        packet[1] = 204;
        packet[3] = 2; // length = 2 (12 bytes)
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), ssrc);
        return packet;
    }

    private static byte[] ExtendedReport(uint ssrc)
    {
        // V=2 | PT=207 (XR, RFC 3611) | length=2 (12 bytes) | SSRC | one opaque block word
        var packet = new byte[12];
        packet[0] = 0x80;
        packet[1] = 207;
        packet[3] = 2;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), ssrc);
        return packet;
    }

    [Fact]
    public void Unrecognized_packet_type_is_skipped_and_known_parts_survive()
    {
        var compound = MinimalReceiverReport(0x1111).Concat(ApplicationDefined(0x1111))
            .Concat(MinimalReceiverReport(0x2222)).ToArray();

        var packets = new RtcpPacketCodec().Decode(compound);

        Assert.Equal(2, packets.Count);
        Assert.All(packets, p => Assert.IsType<RtcpReceiverReport>(p));
    }

    [Fact]
    public void Xr_in_a_compound_is_decoded_and_surrounding_reports_survive()
    {
        var compound = MinimalReceiverReport(0x1111).Concat(ExtendedReport(0x1111))
            .Concat(MinimalReceiverReport(0x2222)).ToArray();

        var packets = new RtcpPacketCodec().Decode(compound);

        Assert.Equal(2, packets.OfType<RtcpReceiverReport>().Count());
        Assert.Single(packets.OfType<RtcpExtendedReport>());
    }

    [Fact]
    public void Compound_with_only_unrecognized_types_yields_empty_list()
    {
        var packets = new RtcpPacketCodec().Decode(ApplicationDefined(0x1111));

        Assert.Empty(packets);
    }

    [Fact]
    public void Truncated_packet_still_throws()
    {
        var app = ApplicationDefined(0x1111);
        var truncated = app.AsSpan(0, 8).ToArray(); // claims 12 bytes, delivers 8

        Assert.Throws<ArgumentException>(() => { _ = new RtcpPacketCodec().Decode(truncated); });
    }
}
