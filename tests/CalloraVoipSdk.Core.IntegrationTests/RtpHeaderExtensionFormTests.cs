using System.Text;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// L0 — #224: the RFC 8285 §4.3 two-byte header-extension form alongside the one-byte form. The two-byte
/// form exists for what the one-byte form cannot express — identifiers above 14, values longer than 16
/// bytes, and empty values — which is why the Dependency Descriptor (#225) needs it. These tests pin the
/// codec, that the wire form is chosen by what the elements need rather than by configuration, and that
/// the receive path reads whichever form arrives.
/// </summary>
public sealed class RtpHeaderExtensionFormTests
{
    // ── the two-byte codec ───────────────────────────────────────────────────────────────────

    /// <summary>Acceptance criterion: a round trip through the two-byte form, across the edge cases.</summary>
    [Theory]
    [InlineData(1, 0)]      // empty value — impossible in the one-byte form
    [InlineData(1, 1)]
    [InlineData(14, 16)]    // the one-byte boundary, carried here anyway
    [InlineData(15, 17)]    // id and length both just past the one-byte range
    [InlineData(16, 255)]   // maximum value length
    [InlineData(255, 3)]    // maximum id
    public void A_two_byte_element_round_trips(byte id, int valueLength)
    {
        var value = Bytes(valueLength);

        var extension = TwoByteRtpHeaderExtensions.Encode([new RtpHeaderExtensionElement(id, value)]);

        Assert.NotNull(extension);
        Assert.Equal(TwoByteRtpHeaderExtensions.Profile, extension!.Profile);
        Assert.Equal(0, extension.Data.Length % 4); // padded to a 32-bit boundary

        var element = Assert.Single(TwoByteRtpHeaderExtensions.Parse(extension));
        Assert.Equal(id, element.Id);
        Assert.Equal(value, element.Value.ToArray());
    }

    [Fact]
    public void Several_two_byte_elements_round_trip_in_wire_order()
    {
        RtpHeaderExtensionElement[] elements =
        [
            new(20, Bytes(17)),
            new(1, Bytes(0)),
            new(255, Bytes(4)),
        ];

        var extension = TwoByteRtpHeaderExtensions.Encode(elements);
        var parsed = TwoByteRtpHeaderExtensions.Parse(extension!);

        Assert.Equal(elements.Length, parsed.Count);
        for (var i = 0; i < elements.Length; i++)
        {
            Assert.Equal(elements[i].Id, parsed[i].Id);
            Assert.Equal(elements[i].Value.ToArray(), parsed[i].Value.ToArray());
        }
    }

    /// <summary>Padding is skipped, not read as an element: id 0 is padding in the two-byte form too.</summary>
    [Fact]
    public void Two_byte_parsing_skips_padding()
    {
        // padding, then id 7 with a 2-byte value, then trailing padding.
        var data = new byte[] { 0x00, 0x00, 0x07, 0x02, 0xAA, 0xBB, 0x00, 0x00 };

        var element = Assert.Single(TwoByteRtpHeaderExtensions.Parse(Extension(TwoByteRtpHeaderExtensions.Profile, data)));

        Assert.Equal(7, element.Id);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, element.Value.ToArray());
    }

    /// <summary>K4: malformed remote input is tolerated to the valid prefix, never thrown on.</summary>
    [Theory]
    [InlineData(new byte[] { 0x07, 0x02, 0xAA, 0xBB, 0x09 })]              // truncated header
    [InlineData(new byte[] { 0x07, 0x02, 0xAA, 0xBB, 0x09, 0x04, 0x01 })]  // truncated value
    public void Two_byte_parsing_drops_a_truncated_tail(byte[] data)
    {
        var element = Assert.Single(TwoByteRtpHeaderExtensions.Parse(Extension(TwoByteRtpHeaderExtensions.Profile, data)));

        Assert.Equal(7, element.Id);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, element.Value.ToArray());
    }

    /// <summary>The appbits in the profile's low nibble carry no framing meaning (RFC 8285 §4.3).</summary>
    [Theory]
    [InlineData((ushort)0x1000)]
    [InlineData((ushort)0x1001)]
    [InlineData((ushort)0x100F)]
    public void Any_appbits_variant_is_recognised_as_the_two_byte_form(ushort profile)
    {
        Assert.True(TwoByteRtpHeaderExtensions.IsProfile(profile));

        var element = Assert.Single(
            RtpHeaderExtensions.Parse(Extension(profile, [0x07, 0x02, 0xAA, 0xBB])));
        Assert.Equal(7, element.Id);
    }

    [Theory]
    [InlineData((ushort)0xBEDE)] // the one-byte profile is not the two-byte one
    [InlineData((ushort)0x2000)]
    public void A_foreign_profile_is_not_the_two_byte_form(ushort profile)
        => Assert.False(TwoByteRtpHeaderExtensions.IsProfile(profile));

    [Fact]
    public void An_unknown_profile_yields_no_elements_rather_than_a_guess()
        => Assert.Empty(RtpHeaderExtensions.Parse(Extension(0x2000, [0x07, 0x02, 0xAA, 0xBB])));

    [Theory]
    [InlineData(0, 4)]     // id 0 is padding, never an element
    [InlineData(3, 256)]   // value beyond the 8-bit length field
    public void The_two_byte_encoder_rejects_an_unencodable_element(byte id, int valueLength)
        => Assert.Throws<ArgumentException>(
            () => TwoByteRtpHeaderExtensions.Encode([new RtpHeaderExtensionElement(id, Bytes(valueLength))]));

    // ── the send-side form choice ────────────────────────────────────────────────────────────

    /// <summary>
    /// Acceptance criterion: one-byte while everything fits, two-byte otherwise (RFC 8285 §4.3, final
    /// paragraph). The boundary cases are the point — id 14 with a 16-byte value is still one-byte, and
    /// one step past on either axis flips the form.
    /// </summary>
    [Theory]
    [InlineData(14, 16, OneByteRtpHeaderExtensions.Profile)]   // both at the one-byte limit
    [InlineData(1, 1, OneByteRtpHeaderExtensions.Profile)]
    [InlineData(15, 4, TwoByteRtpHeaderExtensions.Profile)]    // id past the limit
    [InlineData(16, 4, TwoByteRtpHeaderExtensions.Profile)]
    [InlineData(3, 17, TwoByteRtpHeaderExtensions.Profile)]    // value past the limit
    [InlineData(3, 0, TwoByteRtpHeaderExtensions.Profile)]     // empty value: one-byte cannot express it
    public void The_form_follows_what_the_elements_need(byte id, int valueLength, ushort expectedProfile)
    {
        var extension = RtpHeaderExtensions.Encode([new RtpHeaderExtensionElement(id, Bytes(valueLength))]);

        Assert.NotNull(extension);
        Assert.Equal(expectedProfile, extension!.Profile);
    }

    /// <summary>One element that needs the two-byte form puts the whole packet's extension in it.</summary>
    [Fact]
    public void A_single_oversized_element_moves_the_whole_extension_to_the_two_byte_form()
    {
        RtpHeaderExtensionElement[] elements = [new(1, Bytes(2)), new(20, Bytes(3))];

        var extension = RtpHeaderExtensions.Encode(elements);

        Assert.Equal(TwoByteRtpHeaderExtensions.Profile, extension!.Profile);
        Assert.Equal(2, RtpHeaderExtensions.Parse(extension).Count);
    }

    // ── the receive path reads whichever form arrives ────────────────────────────────────────

    /// <summary>
    /// The cross-form regression this slice is really about: a peer that needs the two-byte form for one
    /// extension writes *all* of that packet's elements in it. A reader gated on <c>0xBEDE</c> would drop
    /// transport-cc, MID and RID for exactly those packets — losing congestion feedback and, on a BUNDLE,
    /// the routing token.
    /// </summary>
    [Fact]
    public void Transport_cc_mid_and_rid_are_read_from_a_two_byte_extension()
    {
        var extension = TwoByteRtpHeaderExtensions.Encode(
        [
            OneByteRtpHeaderExtensions.TransportSequenceNumber(20, 0x1234),
            new RtpHeaderExtensionElement(21, Encoding.ASCII.GetBytes("video")),
            new RtpHeaderExtensionElement(22, Encoding.ASCII.GetBytes("hi")),
        ]);

        Assert.True(OneByteRtpHeaderExtensions.TryReadTransportSequenceNumber(extension, 20, out var sequence));
        Assert.Equal(0x1234, sequence);
        Assert.True(RtpMidHeaderExtension.TryRead(extension, 21, out var mid));
        Assert.Equal("video", mid);
        Assert.True(RtpRidHeaderExtension.TryRead(extension, 22, out var rid));
        Assert.Equal("hi", rid);
    }

    /// <summary>...and the one-byte form still reads exactly as before.</summary>
    [Fact]
    public void The_one_byte_receive_path_is_unchanged()
    {
        var extension = OneByteRtpHeaderExtensions.Encode(
        [
            OneByteRtpHeaderExtensions.TransportSequenceNumber(3, 0x9ABC),
            new RtpHeaderExtensionElement(4, Encoding.ASCII.GetBytes("0")),
        ]);

        Assert.True(OneByteRtpHeaderExtensions.TryReadTransportSequenceNumber(extension, 3, out var sequence));
        Assert.Equal(0x9ABC, sequence);
        Assert.True(RtpMidHeaderExtension.TryRead(extension, 4, out var mid));
        Assert.Equal("0", mid);
    }

    [Fact]
    public void A_missing_id_is_reported_as_absent_in_both_forms()
    {
        var oneByte = OneByteRtpHeaderExtensions.Encode([new RtpHeaderExtensionElement(3, Bytes(2))]);
        var twoByte = TwoByteRtpHeaderExtensions.Encode([new RtpHeaderExtensionElement(30, Bytes(2))]);

        Assert.False(RtpHeaderExtensions.TryFindValue(oneByte, 9, out _));
        Assert.False(RtpHeaderExtensions.TryFindValue(twoByte, 9, out _));
        Assert.False(RtpHeaderExtensions.TryFindValue(extension: null, 3, out _));
    }

    // ── the send path in the session ─────────────────────────────────────────────────────────

    /// <summary>
    /// The stamper has an allocation-lean one-byte writer for the common case; a negotiated id above 14
    /// must take the general encoder instead of throwing. Both directions are checked on the wire.
    /// </summary>
    [Theory]
    [InlineData((byte)5, OneByteRtpHeaderExtensions.Profile)]
    [InlineData((byte)20, TwoByteRtpHeaderExtensions.Profile)]
    public void The_stamper_writes_the_form_the_negotiated_id_requires(byte transportCcId, ushort expectedProfile)
    {
        var stamper = new RtpOutboundHeaderExtensionStamper(transportCcId, midExtensionId: null, mid: null);

        var extension = stamper.Build(transportCcSequence: 0x4242);

        Assert.NotNull(extension);
        Assert.Equal(expectedProfile, extension!.Profile);
        Assert.True(OneByteRtpHeaderExtensions.TryReadTransportSequenceNumber(extension, transportCcId, out var sequence));
        Assert.Equal(0x4242, sequence);
    }

    /// <summary>A MID stamped under a two-byte id survives the round trip the BUNDLE router depends on.</summary>
    [Fact]
    public void The_stamper_carries_a_two_byte_mid_and_transport_cc_together()
    {
        var stamper = new RtpOutboundHeaderExtensionStamper(
            transportWideCcExtensionId: 20, midExtensionId: 21, mid: "video");

        var extension = stamper.Build(transportCcSequence: 7);

        Assert.Equal(TwoByteRtpHeaderExtensions.Profile, extension!.Profile);
        Assert.True(RtpMidHeaderExtension.TryRead(extension, 21, out var mid));
        Assert.Equal("video", mid);
        Assert.True(OneByteRtpHeaderExtensions.TryReadTransportSequenceNumber(extension, 20, out var sequence));
        Assert.Equal(7, sequence);
    }

    // ── extmap negotiation beyond id 14 ──────────────────────────────────────────────────────

    /// <summary>
    /// Acceptance criterion: ids above 14 are usable. The first fourteen still land in the one-byte range,
    /// so the SDP of every peer with at most that many extensions is unchanged.
    /// </summary>
    [Fact]
    public void The_offer_assigns_ids_beyond_the_one_byte_range_instead_of_dropping_extensions()
    {
        var uris = Enumerable.Range(1, 20).Select(i => $"urn:test:ext:{i}").ToArray();

        var extmaps = SdpExtmapNegotiation.BuildOffer(uris);

        Assert.Equal(20, extmaps.Count);
        Assert.Equal(1, extmaps[0].Id);
        Assert.Equal(14, extmaps[13].Id); // the one-byte range is used first
        Assert.Equal(20, extmaps[19].Id);
    }

    [Fact]
    public void An_offer_using_a_two_byte_id_is_answered_under_that_id()
    {
        IReadOnlyList<SdpExtmap> offered = [new() { Id = 20, Uri = "urn:test:ext:a" }];

        var answer = SdpExtmapNegotiation.BuildAnswer(offered, ["urn:test:ext:a"]);

        var echoed = Assert.Single(answer);
        Assert.Equal(20, echoed.Id);
        Assert.Equal("urn:test:ext:a", echoed.Uri);
    }

    [Fact]
    public void An_offer_using_an_id_outside_rfc_8285_is_still_dropped()
    {
        IReadOnlyList<SdpExtmap> offered = [new() { Id = 256, Uri = "urn:test:ext:a" }];

        Assert.Empty(SdpExtmapNegotiation.BuildAnswer(offered, ["urn:test:ext:a"]));
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────

    private static byte[] Bytes(int length)
    {
        var value = new byte[length];
        for (var i = 0; i < length; i++)
            value[i] = (byte)(i + 1);
        return value;
    }

    private static RtpExtension Extension(ushort profile, byte[] data) => new() { Profile = profile, Data = data };
}
