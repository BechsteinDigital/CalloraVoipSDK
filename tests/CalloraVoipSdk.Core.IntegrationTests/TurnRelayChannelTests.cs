using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using CalloraVoipSdk.Core.Infrastructure.Turn.Wire;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The TURN relay data-path primitive (Slice 4a): <see cref="TurnRelayChannel"/> frames outbound media as
/// ChannelData addressed to the relay server (RFC 8656 §11–12) and recovers the media payload from inbound
/// ChannelData relayed back from that same server. It does no I/O — it only translates datagrams — so a
/// media transport can drop it in below its packet demux. These tests pin the wire round-trip and the
/// source/channel gating that makes it safe to sit at the socket boundary.
/// </summary>
public sealed class TurnRelayChannelTests
{
    private static readonly IPEndPoint Relay = new(IPAddress.Parse("203.0.113.7"), 3478);
    private const ushort Channel = 0x4001;

    [Fact]
    public void Wrap_frames_the_payload_as_channel_data_for_this_channel()
    {
        var channel = new TurnRelayChannel(Relay, Channel);
        var media = new byte[] { 0x80, 0x00, 0x12, 0x34, 0xDE, 0xAD, 0xBE, 0xEF };

        var framed = channel.Wrap(media);

        Assert.True(TurnChannelDataCodec.TryParse(framed, out var parsedChannel, out var parsedData));
        Assert.Equal(Channel, parsedChannel);
        Assert.Equal(media, parsedData);
    }

    [Fact]
    public void Wrap_then_unwrap_round_trips_the_media_payload()
    {
        var channel = new TurnRelayChannel(Relay, Channel);
        var media = new byte[] { 0x90, 0x64, 0x00, 0x01, 0x11, 0x22, 0x33 };

        var framed = channel.Wrap(media);

        // The relayed ChannelData arrives back from the relay server's 5-tuple.
        Assert.True(channel.TryUnwrap(framed, Relay, out var recovered));
        Assert.Equal(media, recovered);
    }

    [Fact]
    public void TryUnwrap_rejects_channel_data_from_a_source_other_than_the_relay_server()
    {
        var channel = new TurnRelayChannel(Relay, Channel);
        var framed = channel.Wrap(new byte[] { 1, 2, 3, 4 });
        var spoofedSource = new IPEndPoint(IPAddress.Parse("198.51.100.9"), 3478);

        Assert.False(channel.TryUnwrap(framed, spoofedSource, out var recovered));
        Assert.Empty(recovered);
    }

    [Fact]
    public void TryUnwrap_rejects_channel_data_for_a_different_channel()
    {
        var channel = new TurnRelayChannel(Relay, Channel);
        var otherChannelFrame = TurnChannelDataCodec.Encode(0x4002, new byte[] { 9, 9, 9 });

        Assert.False(channel.TryUnwrap(otherChannelFrame, Relay, out var recovered));
        Assert.Empty(recovered);
    }

    [Fact]
    public void TryUnwrap_rejects_a_datagram_that_is_not_channel_data()
    {
        var channel = new TurnRelayChannel(Relay, Channel);
        // A raw RTP packet (first byte 0x80) is not ChannelData: its leading u16 is below the 0x4000 range.
        var rawRtp = new byte[] { 0x80, 0x60, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 };

        Assert.False(channel.TryUnwrap(rawRtp, Relay, out var recovered));
        Assert.Empty(recovered);
    }

    [Theory]
    [InlineData(0x3FFF)]
    [InlineData(0x8000)]
    public void Constructor_rejects_a_channel_number_outside_the_turn_range(int channelNumber)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TurnRelayChannel(Relay, (ushort)channelNumber));
    }

    [Fact]
    public void Constructor_rejects_a_null_relay_server()
    {
        Assert.Throws<ArgumentNullException>(() => new TurnRelayChannel(null!, Channel));
    }
}
