using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RFC 8285 one-byte header-extension codec: the wire foundation for per-packet metadata
/// (transport-wide sequence number for congestion control, etc.). Verifies element packing,
/// 32-bit padding, and lenient parsing (padding skip, id-15 stop, truncation tolerance).
/// </summary>
public sealed class OneByteRtpHeaderExtensionsTests
{
    private static RtpHeaderExtensionElement Element(byte id, params byte[] value)
        => new(id, value);

    [Fact]
    public void Encode_then_parse_round_trips_elements_in_order()
    {
        var extension = OneByteRtpHeaderExtensions.Encode(
            [Element(1, 0xAA, 0xBB), Element(5, 0xCC)]);

        Assert.NotNull(extension);
        Assert.Equal(0xBEDE, extension!.Profile);

        var parsed = OneByteRtpHeaderExtensions.Parse(extension);
        Assert.Equal(2, parsed.Count);
        Assert.Equal(1, parsed[0].Id);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, parsed[0].Value.ToArray());
        Assert.Equal(5, parsed[1].Id);
        Assert.Equal(new byte[] { 0xCC }, parsed[1].Value.ToArray());
    }

    [Fact]
    public void Encoded_body_is_padded_to_a_32bit_boundary()
    {
        // One element of 1 value byte = 2 bytes on the wire → padded to 4.
        var extension = OneByteRtpHeaderExtensions.Encode([Element(3, 0x42)]);

        Assert.NotNull(extension);
        Assert.Equal(0, extension!.Data.Length % 4);
        Assert.Equal(4, extension.Data.Length);
        // The trailing padding is zero and parsing ignores it.
        var parsed = OneByteRtpHeaderExtensions.Parse(extension);
        Assert.Equal(3, Assert.Single(parsed).Id);
    }

    [Fact]
    public void Parse_skips_inter_element_padding_zeros()
    {
        // id=1 len=1 val=0xAA, then a padding zero, then id=2 len=1 val=0xBB, then pad.
        var data = new byte[] { 0x10, 0xAA, 0x00, 0x20, 0xBB, 0x00, 0x00, 0x00 };
        var parsed = OneByteRtpHeaderExtensions.Parse(new RtpExtension { Profile = 0xBEDE, Data = data });

        Assert.Equal(2, parsed.Count);
        Assert.Equal(1, parsed[0].Id);
        Assert.Equal(2, parsed[1].Id);
    }

    [Fact]
    public void Parse_stops_at_reserved_id_15()
    {
        // id=1 len=1 val=0xAA, then 0xF0 (id 15) must stop parsing; trailing bytes ignored.
        var data = new byte[] { 0x10, 0xAA, 0xF0, 0x99, 0x99, 0x99, 0x99, 0x99 };
        var parsed = OneByteRtpHeaderExtensions.Parse(new RtpExtension { Profile = 0xBEDE, Data = data });

        Assert.Equal(1, Assert.Single(parsed).Id);
    }

    [Fact]
    public void Parse_of_non_bede_profile_is_empty()
    {
        var parsed = OneByteRtpHeaderExtensions.Parse(
            new RtpExtension { Profile = 0x1000, Data = new byte[] { 0x10, 0xAA, 0x00, 0x00 } });

        Assert.Empty(parsed);
    }

    [Fact]
    public void Parse_tolerates_a_truncated_trailing_element()
    {
        // id=2 claims len=4 but only 2 bytes remain → the truncated element is dropped, the valid
        // prefix (id=1) is still returned.
        var data = new byte[] { 0x10, 0xAA, 0x23, 0xBB, 0xCC };
        var parsed = OneByteRtpHeaderExtensions.Parse(new RtpExtension { Profile = 0xBEDE, Data = data });

        Assert.Equal(1, Assert.Single(parsed).Id);
    }

    [Theory]
    [InlineData(1)]   // lowest valid id
    [InlineData(14)]  // highest valid id
    public void Valid_boundary_ids_round_trip(byte id)
    {
        var extension = OneByteRtpHeaderExtensions.Encode([Element(id, 0x5A)]);

        var parsed = OneByteRtpHeaderExtensions.Parse(extension!);
        var element = Assert.Single(parsed);
        Assert.Equal(id, element.Id);
        Assert.Equal(new byte[] { 0x5A }, element.Value.ToArray());
    }

    [Fact]
    public void Encode_empty_yields_null()
        => Assert.Null(OneByteRtpHeaderExtensions.Encode([]));

    [Fact]
    public void Sixteen_byte_value_round_trips()
    {
        var value = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var extension = OneByteRtpHeaderExtensions.Encode([Element(7, value)]);

        var parsed = OneByteRtpHeaderExtensions.Parse(extension!);
        Assert.Equal(value, Assert.Single(parsed).Value.ToArray());
    }

    [Theory]
    [InlineData(0)]   // padding id
    [InlineData(15)]  // reserved id
    [InlineData(16)]  // out of nibble range
    public void Encode_rejects_out_of_range_ids(byte id)
        => Assert.Throws<ArgumentException>(() => OneByteRtpHeaderExtensions.Encode([Element(id, 0xAA)]));

    [Fact]
    public void Encode_rejects_empty_value()
        => Assert.Throws<ArgumentException>(() => OneByteRtpHeaderExtensions.Encode([Element(1)]));

    [Fact]
    public void Encode_rejects_value_longer_than_16_bytes()
    {
        var tooLong = new byte[17];
        Assert.Throws<ArgumentException>(
            () => OneByteRtpHeaderExtensions.Encode([new RtpHeaderExtensionElement(1, tooLong)]));
    }
}
