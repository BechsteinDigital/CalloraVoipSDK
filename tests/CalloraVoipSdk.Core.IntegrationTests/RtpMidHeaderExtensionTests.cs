using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RFC 9143 / RFC 8843 §15 MID SDES header-extension codec (BUNDLE routing foundation, ADR-010 B1).
/// Pins the one-byte wire layout, round-trip, coexistence with the transport-cc element, and the
/// one-byte-form limits.
/// </summary>
public sealed class RtpMidHeaderExtensionTests
{
    [Theory]
    [InlineData("0")]      // browser-style numeric MID
    [InlineData("audio")]
    [InlineData("video")]
    [InlineData("0123456789abcdef")] // 16 bytes — the one-byte-form maximum
    public void Encode_then_read_round_trips_the_mid(string mid)
    {
        var extension = RtpMidHeaderExtension.Encode(id: 4, mid);

        Assert.Equal(0xBEDE, extension.Profile);
        Assert.Equal(0, extension.Data.Length % 4); // padded to a 32-bit boundary
        Assert.True(RtpMidHeaderExtension.TryRead(extension, id: 4, out var read));
        Assert.Equal(mid, read);
    }

    [Fact]
    public void Element_coexists_with_the_transport_cc_element_and_both_read_back()
    {
        var extension = OneByteRtpHeaderExtensions.Encode(
        [
            RtpMidHeaderExtension.Element(id: 3, "video"),
            OneByteRtpHeaderExtensions.TransportSequenceNumber(id: 5, sequenceNumber: 40_000),
        ]);

        Assert.NotNull(extension);
        Assert.True(RtpMidHeaderExtension.TryRead(extension, id: 3, out var mid));
        Assert.Equal("video", mid);
        Assert.True(OneByteRtpHeaderExtensions.TryReadTransportSequenceNumber(extension, id: 5, out var seq));
        Assert.Equal(40_000, seq);
    }

    [Fact]
    public void Direct_encode_matches_the_generic_element_encoding()
    {
        var direct = RtpMidHeaderExtension.Encode(id: 7, "audio");
        var generic = OneByteRtpHeaderExtensions.Encode([RtpMidHeaderExtension.Element(id: 7, "audio")]);

        Assert.NotNull(generic);
        Assert.Equal(generic!.Data.ToArray(), direct.Data.ToArray());
    }

    [Fact]
    public void Read_returns_false_when_no_element_has_the_id()
    {
        var extension = RtpMidHeaderExtension.Encode(id: 4, "audio");

        Assert.False(RtpMidHeaderExtension.TryRead(extension, id: 6, out var mid));
        Assert.Equal(string.Empty, mid);
    }

    [Fact]
    public void Read_returns_false_for_a_missing_or_non_bede_extension()
    {
        Assert.False(RtpMidHeaderExtension.TryRead(null, id: 4, out _));
        Assert.False(RtpMidHeaderExtension.TryRead(
            new RtpExtension { Profile = 0x1000, Data = new byte[] { 0x41, 0x30, 0, 0 } }, id: 4, out _));
    }

    [Fact]
    public void Encode_rejects_a_mid_longer_than_the_one_byte_form_allows()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => RtpMidHeaderExtension.Encode(id: 4, "0123456789abcdefg")); // 17 bytes
        Assert.Contains("two-byte form", ex.Message);
    }

    [Fact]
    public void Encode_rejects_an_empty_mid()
    {
        Assert.Throws<ArgumentException>(() => RtpMidHeaderExtension.Encode(id: 4, ""));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)15)]
    public void Encode_rejects_an_out_of_range_id(byte id)
    {
        Assert.Throws<ArgumentException>(() => RtpMidHeaderExtension.Encode(id, "audio"));
    }
}
