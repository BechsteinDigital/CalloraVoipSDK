using System.Buffers.Binary;
using System.Linq;
using System.Text;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #162 P2-4: tolerant skipping is right — a malformed sub-packet must not discard the compound it travels
/// in (RFC 3550 §6.1). Silently repairing one is not. Each case here used to produce a well-formed-looking
/// result from input we could not actually read, which is worse than rejecting it: the caller has no way to
/// tell the difference and no reason to look.
/// </summary>
public sealed class RtcpParseStrictnessTests
{
    private static readonly RtcpPacketCodec Codec = new();

    // A minimal RTCP packet: V=2, the given count/FMT in the low 5 bits, PT, and a length field derived
    // from the body. Padding is not used, so the header length is authoritative.
    private static byte[] Packet(RtcpPacketType type, int countOrFmt, byte[] body)
    {
        var total = 4 + body.Length;
        var buffer = new byte[total];
        buffer[0] = (byte)(0x80 | (countOrFmt & 0x1F));
        buffer[1] = (byte)type;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2), (ushort)(total / 4 - 1));
        body.CopyTo(buffer, 4);
        return buffer;
    }

    // ── BYE: a reason we cannot read is not "no reason" ──────────────────────

    [Fact]
    public void A_bye_whose_reason_runs_past_the_body_is_rejected()
    {
        // One source, then a reason length of 8 with only 4 bytes behind it.
        var body = new byte[4 + 1 + 4 + 3];
        BinaryPrimitives.WriteUInt32BigEndian(body, 0x0A0B0C0D);
        body[4] = 8;
        Encoding.UTF8.GetBytes("byee").CopyTo(body, 5);

        var ex = Assert.Throws<ArgumentException>(() => Codec.Decode(Packet(RtcpPacketType.Bye, 1, body)));
        Assert.Contains("reason", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_bye_with_a_well_formed_reason_still_decodes()
    {
        var reason = Encoding.UTF8.GetBytes("leaving");
        var body = new byte[4 + 1 + reason.Length];
        BinaryPrimitives.WriteUInt32BigEndian(body, 0x0A0B0C0D);
        body[4] = (byte)reason.Length;
        reason.CopyTo(body, 5);

        var bye = Assert.IsType<RtcpByePacket>(Codec.Decode(Packet(RtcpPacketType.Bye, 1, PadTo4(body))).Single());
        Assert.Equal("leaving", bye.Reason);
    }

    [Fact]
    public void A_bye_with_no_reason_at_all_still_decodes()
    {
        var body = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(body, 0x0A0B0C0D);

        var bye = Assert.IsType<RtcpByePacket>(Codec.Decode(Packet(RtcpPacketType.Bye, 1, body)).Single());
        Assert.Null(bye.Reason);
    }

    // ── UTF-8: invalid bytes are not laundered into replacement characters ───

    [Fact]
    public void A_bye_reason_that_is_not_valid_utf8_is_dropped_but_the_departure_stands()
    {
        // 0xFF is never valid in UTF-8. Encoding.UTF8.GetString would return "�" and look fine.
        var body = new byte[4 + 1 + 3];
        BinaryPrimitives.WriteUInt32BigEndian(body, 0x0A0B0C0D);
        body[4] = 3;
        body[5] = 0xFF;
        body[6] = 0xFE;
        body[7] = 0xFD;

        // The departure itself is readable, so it stands — only the text field is dropped. No reference
        // stack discards the packet over its text, and the sources are what a BYE is for.
        var bye = Assert.IsType<RtcpByePacket>(Codec.Decode(Packet(RtcpPacketType.Bye, 1, body)).Single());

        Assert.Equal(0x0A0B0C0Du, bye.Sources.Single());
        Assert.Null(bye.Reason);   // dropped, not laundered into replacement characters
    }

    [Fact]
    public void An_sdes_item_that_is_not_valid_utf8_is_dropped_but_the_chunk_stands()
    {
        // A CNAME laundered into replacement characters compares unequal to whatever the peer meant, and
        // nothing would say why. Dropping the item is stricter than every reference stack (none of them
        // validates UTF-8) without discarding the chunk's SSRC or its other items.
        var body = new byte[4 + 1 + 1 + 3 + 3];
        BinaryPrimitives.WriteUInt32BigEndian(body, 0x0A0B0C0D);
        body[4] = 1;      // CNAME
        body[5] = 3;      // length
        body[6] = 0xC3;   // truncated multi-byte sequence
        body[7] = 0x28;
        body[8] = 0xFF;

        var sdes = Assert.IsType<RtcpSdesPacket>(Codec.Decode(Packet(RtcpPacketType.Sdes, 1, body)).Single());
        var chunk = sdes.Chunks.Single();

        Assert.Equal(0x0A0B0C0Du, chunk.Ssrc);
        Assert.Empty(chunk.Items);   // the undecodable CNAME is absent, not mangled
    }

    [Fact]
    public void A_compound_survives_an_sdes_item_that_is_not_valid_utf8()
    {
        // The point of dropping the field rather than the packet: this interval's loss statistics must not
        // be lost because a peer's CNAME is malformed. SIPSorcery, libwebrtc and Pion all keep the compound
        // here — being stricter than them about the text must not mean being worse than them about the data.
        var rrBody = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(rrBody, 0x0A0A0A0A);
        var rr = Packet(RtcpPacketType.ReceiverReport, 0, rrBody);

        var sdesBody = new byte[4 + 1 + 1 + 3 + 3];
        BinaryPrimitives.WriteUInt32BigEndian(sdesBody, 0x0A0B0C0D);
        sdesBody[4] = 1;
        sdesBody[5] = 3;
        sdesBody[6] = 0xC3;
        sdesBody[7] = 0x28;
        sdesBody[8] = 0xFF;
        var sdes = Packet(RtcpPacketType.Sdes, 1, sdesBody);

        var compound = new byte[rr.Length + sdes.Length];
        rr.CopyTo(compound, 0);
        sdes.CopyTo(compound, rr.Length);

        var decoded = Codec.Decode(compound);

        Assert.Single(decoded.OfType<RtcpReceiverReport>());
        Assert.Empty(decoded.OfType<RtcpSdesPacket>().Single().Chunks.Single().Items);
    }

    [Fact]
    public void An_sdes_item_with_valid_utf8_including_non_ascii_still_decodes()
    {
        // Strictness must not mean ASCII-only: a CNAME may legitimately carry any UTF-8 (RFC 3550 §6.5).
        var value = Encoding.UTF8.GetBytes("grüße-ü");
        var body = new byte[4 + 1 + 1 + value.Length + 1];
        BinaryPrimitives.WriteUInt32BigEndian(body, 0x0A0B0C0D);
        body[4] = 1;
        body[5] = (byte)value.Length;
        value.CopyTo(body, 6);

        var sdes = Assert.IsType<RtcpSdesPacket>(Codec.Decode(Packet(RtcpPacketType.Sdes, 1, PadTo4(body))).Single());
        Assert.Equal("grüße-ü", sdes.Chunks.Single().Items.Single().Value);
    }

    // ── XR: a partial parse must not look complete ───────────────────────────

    [Fact]
    public void An_xr_whose_block_length_runs_past_the_body_is_marked_truncated()
    {
        // SSRC, then a block claiming 40 bytes of content with far fewer behind it.
        var body = new byte[4 + 4 + 8];
        BinaryPrimitives.WriteUInt32BigEndian(body, 0x0A0B0C0D);
        body[4] = 7;   // VoIP metrics block type
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(6), 10);   // 10 words = 40 bytes

        var xr = Assert.IsType<RtcpExtendedReport>(Codec.Decode(Packet(RtcpPacketType.ExtendedReport, 0, body)).Single());

        Assert.True(xr.IsTruncated);
        Assert.Empty(xr.VoipMetrics);   // nothing readable was read
    }

    [Fact]
    public void A_well_formed_xr_is_not_marked_truncated()
    {
        var body = new byte[4 + 4 + 32];
        BinaryPrimitives.WriteUInt32BigEndian(body, 0x0A0B0C0D);
        body[4] = 7;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(6), 8);   // 8 words = 32 bytes

        var xr = Assert.IsType<RtcpExtendedReport>(Codec.Decode(Packet(RtcpPacketType.ExtendedReport, 0, body)).Single());

        Assert.False(xr.IsTruncated);
        Assert.Single(xr.VoipMetrics);
    }

    // ── PLI: a packet with an FCI is not a PLI ───────────────────────────────

    [Fact]
    public void A_pli_carrying_fci_bytes_is_dropped_rather_than_accepted()
    {
        // RFC 4585 §6.3.1 fixes the PLI length at the SSRC pair; trailing bytes mean the sender and we
        // disagree about what this packet is. Feedback packets decode tolerantly by design, so the right
        // outcome is a dropped sub-packet — not a thrown compound, and not a PLI conjured from input we
        // could not read. It used to be the last of those: a key-frame request nobody asked for.
        var body = new byte[8 + 4];
        BinaryPrimitives.WriteUInt32BigEndian(body, 0x11111111);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4), 0x22222222);
        body[8] = 0xDE;

        var decoded = Codec.Decode(Packet(RtcpPacketType.PayloadFeedback, RtcpPictureLossIndication.FeedbackMessageType, body));

        Assert.Empty(decoded.OfType<RtcpPictureLossIndication>());
    }

    [Fact]
    public void A_malformed_pli_does_not_take_the_rest_of_the_compound_with_it()
    {
        // The reason feedback decoding is tolerant: an RR carrying this interval's loss statistics must
        // survive a peer's malformed feedback packet (RFC 3550 §6.1).
        var rrBody = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(rrBody, 0x0A0A0A0A);
        var rr = Packet(RtcpPacketType.ReceiverReport, 0, rrBody);

        var pliBody = new byte[8 + 4];
        BinaryPrimitives.WriteUInt32BigEndian(pliBody, 0x11111111);
        BinaryPrimitives.WriteUInt32BigEndian(pliBody.AsSpan(4), 0x22222222);
        pliBody[8] = 0xDE;
        var pli = Packet(RtcpPacketType.PayloadFeedback, RtcpPictureLossIndication.FeedbackMessageType, pliBody);

        var compound = new byte[rr.Length + pli.Length];
        rr.CopyTo(compound, 0);
        pli.CopyTo(compound, rr.Length);

        var decoded = Codec.Decode(compound);

        Assert.Single(decoded.OfType<RtcpReceiverReport>());
        Assert.Empty(decoded.OfType<RtcpPictureLossIndication>());
    }

    [Fact]
    public void A_pli_without_fci_still_decodes()
    {
        var body = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(body, 0x11111111);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4), 0x22222222);

        var pli = Assert.IsType<RtcpPictureLossIndication>(
            Codec.Decode(Packet(RtcpPacketType.PayloadFeedback, RtcpPictureLossIndication.FeedbackMessageType, body)).Single());

        Assert.Equal(0x22222222u, pli.MediaSsrc);
    }

    private static byte[] PadTo4(byte[] body)
    {
        var padded = (body.Length + 3) & ~3;
        if (padded == body.Length)
            return body;
        var result = new byte[padded];
        body.CopyTo(result, 0);
        return result;
    }
}
