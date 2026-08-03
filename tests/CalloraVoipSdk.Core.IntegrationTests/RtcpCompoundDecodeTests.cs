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

    [Fact]
    public void Compound_exceeding_the_packet_budget_is_rejected()
    {
        // #162 P1 (rule K4): a datagram full of minimal 8-byte sub-packets must not force
        // unbounded object allocation — the codec caps the compound at MaxPacketsPerCompound.
        var compound = MinimalRrCompound(RtcpPacketCodec.MaxPacketsPerCompound + 1);

        Assert.Throws<ArgumentException>(() => { _ = new RtcpPacketCodec().Decode(compound); });
    }

    [Fact]
    public void Compound_at_the_packet_budget_is_accepted()
    {
        var compound = MinimalRrCompound(RtcpPacketCodec.MaxPacketsPerCompound);

        var packets = new RtcpPacketCodec().Decode(compound);

        Assert.Equal(RtcpPacketCodec.MaxPacketsPerCompound, packets.Count);
    }

    [Fact]
    public void Compound_exceeding_the_datagram_byte_budget_is_rejected()
    {
        // #162 P1 (rule K4): an oversized compound is rejected before decode at the shared codec
        // boundary, so the dedicated RTCP socket, BUNDLE and SIP paths all inherit the byte cap.
        var oversized = new byte[RtcpPacketCodec.MaxRtcpDatagramBytes + 4];
        oversized[0] = 0x80;
        oversized[1] = 201; // RR

        Assert.Throws<ArgumentException>(() => { _ = new RtcpPacketCodec().Decode(oversized); });
    }

    private static byte[] MinimalRrCompound(int count)
    {
        var compound = new byte[count * 8];
        for (var i = 0; i < count; i++)
            MinimalReceiverReport((uint)(0x1000 + i)).CopyTo(compound, i * 8);
        return compound;
    }
}
