using System.Buffers.Binary;
using CalloraVoipSdk.Core.Infrastructure.Rtp;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The RFC 7983 / RFC 5764 §5.1.2 packet-type demux (ADR-011 B2c-in-2), extracted from
/// <c>RtpSession</c> so the bundled transport reuses it: STUN, DTLS, RTP, and RTCP share the media
/// socket and are told apart by first-byte range plus a few confirming bytes.
/// </summary>
public sealed class MediaPacketClassifierTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(3)] // upper edge of the STUN first-byte range
    public void A_low_byte_datagram_with_the_magic_cookie_is_stun(byte firstByte)
    {
        var datagram = new byte[20];
        datagram[0] = firstByte;
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(4), 0x2112A442u);

        Assert.Equal(MediaPacketKind.Stun, MediaPacketClassifier.Classify(datagram));
    }

    [Fact]
    public void A_low_byte_datagram_without_the_magic_cookie_is_not_stun()
    {
        var datagram = new byte[20]; // first byte 0, cookie bytes left zero
        Assert.NotEqual(MediaPacketKind.Stun, MediaPacketClassifier.Classify(datagram));
    }

    [Fact]
    public void A_stun_shaped_datagram_shorter_than_the_header_is_not_stun()
    {
        var datagram = new byte[6]; // < 8 bytes, cannot hold the cookie
        datagram[0] = 1;
        Assert.NotEqual(MediaPacketKind.Stun, MediaPacketClassifier.Classify(datagram));
    }

    [Theory]
    [InlineData(20)] // lower edge of the DTLS content-type range
    [InlineData(63)] // upper edge
    public void A_record_with_a_dtls_content_type_and_full_header_is_dtls(byte contentType)
    {
        var datagram = new byte[13];
        datagram[0] = contentType;
        Assert.Equal(MediaPacketKind.Dtls, MediaPacketClassifier.Classify(datagram));
    }

    [Theory]
    [InlineData(19)] // just below the range
    [InlineData(64)] // just above the range
    public void A_content_type_outside_the_dtls_range_is_not_dtls(byte contentType)
    {
        var datagram = new byte[13];
        datagram[0] = contentType;
        Assert.NotEqual(MediaPacketKind.Dtls, MediaPacketClassifier.Classify(datagram));
    }

    [Fact]
    public void A_dtls_typed_datagram_shorter_than_the_record_header_is_not_dtls()
    {
        var datagram = new byte[12]; // < 13-byte DTLS record header
        datagram[0] = 22;
        Assert.NotEqual(MediaPacketKind.Dtls, MediaPacketClassifier.Classify(datagram));
    }

    [Theory]
    [InlineData(192)] // SR — lower edge
    [InlineData(200)]
    [InlineData(223)] // upper edge of the RTCP packet-type range
    public void A_version_two_packet_with_an_rtcp_type_is_rtcp(byte packetType)
    {
        var datagram = new byte[] { 0x80, packetType, 0x00, 0x00 };
        Assert.Equal(MediaPacketKind.Rtcp, MediaPacketClassifier.Classify(datagram));
    }

    [Theory]
    [InlineData(96)]  // dynamic RTP payload type
    [InlineData(191)] // just below the RTCP range → still RTP
    [InlineData(224)] // just above the RTCP range → RTP
    public void A_version_two_packet_below_or_above_the_rtcp_range_is_rtp(byte payloadType)
    {
        var datagram = new byte[] { 0x80, payloadType, 0x00, 0x00 };
        Assert.Equal(MediaPacketKind.Rtp, MediaPacketClassifier.Classify(datagram));
    }

    [Fact]
    public void An_empty_or_tiny_datagram_falls_back_to_rtp()
    {
        Assert.Equal(MediaPacketKind.Rtp, MediaPacketClassifier.Classify(ReadOnlySpan<byte>.Empty));
        Assert.Equal(MediaPacketKind.Rtp, MediaPacketClassifier.Classify(new byte[] { 0x80 }));
    }
}
