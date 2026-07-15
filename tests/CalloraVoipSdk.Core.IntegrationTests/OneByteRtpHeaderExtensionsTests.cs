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

    // ── Transport-wide sequence number read-back (receive side) ─────────────────────

    [Fact]
    public void TryReadTransportSequenceNumber_round_trips_the_stamped_value()
    {
        var extension = OneByteRtpHeaderExtensions.Encode(
            [OneByteRtpHeaderExtensions.TransportSequenceNumber(5, 40000)]);

        Assert.True(OneByteRtpHeaderExtensions.TryReadTransportSequenceNumber(extension, 5, out var seq));
        Assert.Equal(40000, seq);
    }

    [Fact]
    public void TryReadTransportSequenceNumber_returns_false_for_a_missing_extension()
        => Assert.False(OneByteRtpHeaderExtensions.TryReadTransportSequenceNumber(null, 5, out _));

    [Fact]
    public void TryReadTransportSequenceNumber_returns_false_for_a_different_id()
    {
        var extension = OneByteRtpHeaderExtensions.Encode(
            [OneByteRtpHeaderExtensions.TransportSequenceNumber(5, 123)]);

        Assert.False(OneByteRtpHeaderExtensions.TryReadTransportSequenceNumber(extension, 6, out _));
    }

    [Fact]
    public void TryReadTransportSequenceNumber_returns_false_when_the_element_is_not_two_bytes()
    {
        // Same id, but a one-byte value — not a transport sequence number.
        var extension = OneByteRtpHeaderExtensions.Encode([Element(5, 0xAA)]);

        Assert.False(OneByteRtpHeaderExtensions.TryReadTransportSequenceNumber(extension, 5, out _));
    }

    [Fact]
    public void TryReadTransportSequenceNumber_returns_false_for_a_non_bede_profile()
    {
        var extension = new RtpExtension { Profile = 0x1000, Data = new byte[] { 0x50, 0x9C, 0x40, 0x00 } };

        Assert.False(OneByteRtpHeaderExtensions.TryReadTransportSequenceNumber(extension, 5, out _));
    }

    // ── Allocation-lean transport-cc stamp (must match the generic encode byte for byte) ──────────

    [Theory]
    [InlineData((byte)1, (ushort)0)]
    [InlineData((byte)5, (ushort)40_000)]
    [InlineData((byte)14, (ushort)65_535)]
    public void EncodeTransportSequenceNumber_matches_the_generic_encode(byte id, ushort seq)
    {
        var lean = OneByteRtpHeaderExtensions.EncodeTransportSequenceNumber(id, seq);
        var generic = OneByteRtpHeaderExtensions.Encode([OneByteRtpHeaderExtensions.TransportSequenceNumber(id, seq)]);

        Assert.NotNull(generic);
        Assert.Equal(generic!.Profile, lean.Profile);
        Assert.Equal(generic.Data.ToArray(), lean.Data.ToArray());
    }

    [Fact]
    public void EncodeTransportSequenceNumber_round_trips_via_read()
    {
        var extension = OneByteRtpHeaderExtensions.EncodeTransportSequenceNumber(7, 12_345);

        Assert.True(OneByteRtpHeaderExtensions.TryReadTransportSequenceNumber(extension, 7, out var seq));
        Assert.Equal(12_345, seq);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    public void EncodeTransportSequenceNumber_rejects_out_of_range_ids(byte id)
        => Assert.Throws<ArgumentException>(() => OneByteRtpHeaderExtensions.EncodeTransportSequenceNumber(id, 1));
}
