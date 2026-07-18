using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RFC 8852 / RFC 8851 RID SDES header-extension codec (simulcast layer identity). Pins the one-byte wire
/// layout, round-trip, coexistence with the MID and transport-cc elements, and the one-byte-form limits.
/// </summary>
public sealed class RtpRidHeaderExtensionTests
{
    [Theory]
    [InlineData("h")]
    [InlineData("hi")]
    [InlineData("mid")]
    [InlineData("0123456789abcdef")] // 16 bytes — the one-byte-form maximum
    public void Encode_then_read_round_trips_the_rid(string rid)
    {
        var extension = RtpRidHeaderExtension.Encode(id: 4, rid);

        Assert.Equal(0xBEDE, extension.Profile);
        Assert.Equal(0, extension.Data.Length % 4); // padded to a 32-bit boundary
        Assert.True(RtpRidHeaderExtension.TryRead(extension, id: 4, out var read));
        Assert.Equal(rid, read);
    }

    [Fact]
    public void Rid_coexists_with_mid_and_transport_cc_and_all_read_back()
    {
        var extension = OneByteRtpHeaderExtensions.Encode(
        [
            RtpMidHeaderExtension.Element(id: 3, "video"),
            RtpRidHeaderExtension.Element(id: 6, "hi"),
            OneByteRtpHeaderExtensions.TransportSequenceNumber(id: 5, sequenceNumber: 40_000),
        ]);

        Assert.NotNull(extension);
        Assert.True(RtpMidHeaderExtension.TryRead(extension, id: 3, out var mid));
        Assert.Equal("video", mid);
        Assert.True(RtpRidHeaderExtension.TryRead(extension, id: 6, out var rid));
        Assert.Equal("hi", rid);
        Assert.True(OneByteRtpHeaderExtensions.TryReadTransportSequenceNumber(extension, id: 5, out var seq));
        Assert.Equal(40_000, seq);
    }

    [Fact]
    public void Direct_encode_matches_the_generic_element_encoding()
    {
        var direct = RtpRidHeaderExtension.Encode(id: 7, "lo");
        var generic = OneByteRtpHeaderExtensions.Encode([RtpRidHeaderExtension.Element(id: 7, "lo")]);

        Assert.NotNull(generic);
        Assert.Equal(generic!.Data.ToArray(), direct.Data.ToArray());
    }

    [Fact]
    public void Read_returns_false_when_no_element_has_the_id()
    {
        var extension = RtpRidHeaderExtension.Encode(id: 4, "hi");

        Assert.False(RtpRidHeaderExtension.TryRead(extension, id: 6, out var rid));
        Assert.Equal(string.Empty, rid);
    }

    [Fact]
    public void Read_returns_false_for_a_missing_or_non_bede_extension()
    {
        Assert.False(RtpRidHeaderExtension.TryRead(null, id: 4, out _));
        Assert.False(RtpRidHeaderExtension.TryRead(
            new RtpExtension { Profile = 0x1000, Data = new byte[] { 0x41, 0x30, 0, 0 } }, id: 4, out _));
    }

    [Fact]
    public void Encode_rejects_a_rid_longer_than_the_one_byte_form_allows()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => RtpRidHeaderExtension.Encode(id: 4, "0123456789abcdefg")); // 17 bytes
        Assert.Contains("two-byte form", ex.Message);
    }

    [Fact]
    public void Encode_rejects_an_empty_rid()
    {
        Assert.Throws<ArgumentException>(() => RtpRidHeaderExtension.Encode(id: 4, ""));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)15)]
    public void Encode_rejects_an_out_of_range_id(byte id)
    {
        Assert.Throws<ArgumentException>(() => RtpRidHeaderExtension.Encode(id, "hi"));
    }
}
