using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The RTP encoder's input is our own model, not wire input, so a field that does not fit the header is a bug
/// on this side and is rejected instead of normalised onto the wire (#161 P3-14). Decode stays lenient — that
/// is the K4 contract for untrusted input — but silently reshaping an invalid model produces a packet that is
/// well-formed and wrong: a payload type above 127 loses its top bit and lands on a different codec (or, at
/// 200/201, inside the RTCP range on a muxed socket), CSRCs past the fifteenth vanish while the survivors still
/// claim to be the whole contributing-source list, and an over-long extension wraps its word count.
/// </summary>
public sealed class RtpPacketCodecEncodeValidationTests
{
    private static readonly RtpPacketCodec Codec = new();

    private static RtpPacket Packet(
        byte payloadType = 0, byte version = 2, IReadOnlyList<uint>? csrc = null, RtpExtension? extension = null) => new()
    {
        Version = version,
        PayloadType = payloadType,
        SequenceNumber = 1,
        Timestamp = 160,
        Ssrc = 0x1234,
        Csrc = csrc ?? [],
        HeaderExtension = extension,
        Payload = new byte[160],
    };

    [Fact]
    public void A_payload_type_above_the_seven_bit_field_is_rejected()
    {
        var packet = Packet(payloadType: 200); // would have gone out as PT 72

        Assert.Throws<ArgumentOutOfRangeException>(() => Codec.Encode(packet));
    }

    [Fact]
    public void The_highest_valid_payload_type_still_encodes()
    {
        var wire = Codec.Encode(Packet(payloadType: 127));

        Assert.Equal(127, wire[1] & 0x7F);
    }

    [Fact]
    public void More_csrcs_than_the_count_field_can_hold_are_rejected()
    {
        var packet = Packet(csrc: Enumerable.Range(1, 16).Select(i => (uint)i).ToArray());

        Assert.Throws<ArgumentException>(() => Codec.Encode(packet));
    }

    [Fact]
    public void A_full_csrc_list_still_encodes_and_round_trips()
    {
        var csrc = Enumerable.Range(1, 15).Select(i => (uint)i).ToArray();

        var decoded = Codec.Decode(Codec.Encode(Packet(csrc: csrc)));

        Assert.Equal(csrc, decoded.Csrc);
    }

    [Fact]
    public void An_unsupported_version_is_rejected_rather_than_rewritten_to_two()
    {
        var packet = Packet(version: 3);

        Assert.Throws<ArgumentException>(() => Codec.Encode(packet));
    }

    [Fact]
    public void A_header_extension_longer_than_its_word_count_can_express_is_rejected()
    {
        // 65536 words + one byte: the 16-bit length field would wrap to 0 and the receiver would read the
        // extension body as payload.
        var packet = Packet(
            extension: new RtpExtension { Profile = 0xBEDE, Data = new byte[(ushort.MaxValue + 1) * 4] });

        Assert.Throws<ArgumentException>(() => Codec.Encode(packet));
    }
}
