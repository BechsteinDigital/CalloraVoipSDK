using System.Text;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// P2 [RTP/RTCP] #14: an SDES item value and a BYE reason are length-prefixed by a single byte (RFC 3550
/// §6.5/§6.6), so a value longer than 255 bytes must be clamped rather than emitting a wrapped length byte that
/// no longer matches the written content — which would corrupt the packet on the wire.
/// </summary>
public sealed class RtcpSdesByeLengthClampTests
{
    [Fact]
    public void An_over_long_sdes_value_is_clamped_to_255_bytes_and_round_trips()
    {
        var packet = new RtcpSdesPacket
        {
            Chunks =
            [
                new RtcpSdesChunk
                {
                    Ssrc = 0x1234_5678,
                    Items = [new RtcpSdesItem { ItemType = RtcpSdesItemType.CName, Value = new string('a', 300) }],
                },
            ],
        };

        var codec = new RtcpPacketCodec();
        var decoded = codec.Decode(codec.Encode([packet]));

        var sdes = Assert.IsType<RtcpSdesPacket>(Assert.Single(decoded));
        var item = Assert.Single(Assert.Single(sdes.Chunks).Items);
        Assert.Equal(255, Encoding.UTF8.GetByteCount(item.Value));
        Assert.Equal(new string('a', 255), item.Value);
    }

    [Fact]
    public void An_over_long_bye_reason_is_clamped_to_255_bytes_and_round_trips()
    {
        var packet = new RtcpByePacket { Sources = [0x1234_5678], Reason = new string('b', 300) };

        var codec = new RtcpPacketCodec();
        var decoded = codec.Decode(codec.Encode([packet]));

        var bye = Assert.IsType<RtcpByePacket>(Assert.Single(decoded));
        Assert.Equal(new string('b', 255), bye.Reason);
    }

    [Fact]
    public void A_normal_length_sdes_value_round_trips_unchanged()
    {
        var packet = new RtcpSdesPacket
        {
            Chunks =
            [
                new RtcpSdesChunk
                {
                    Ssrc = 0x1234_5678,
                    Items = [new RtcpSdesItem { ItemType = RtcpSdesItemType.CName, Value = "alice@example.test" }],
                },
            ],
        };

        var codec = new RtcpPacketCodec();
        var decoded = codec.Decode(codec.Encode([packet]));

        var sdes = Assert.IsType<RtcpSdesPacket>(Assert.Single(decoded));
        Assert.Equal("alice@example.test", Assert.Single(Assert.Single(sdes.Chunks).Items).Value);
    }
}
