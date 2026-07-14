using System.Buffers.Binary;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RTCP feedback wire codec (RFC 4585/5104): PLI and FIR (PSFB PT=206), Generic NACK
/// (RTPFB PT=205) — round trips, byte-level layout, NACK bitmask expansion, and
/// compound-packet coexistence with SR/RR.
/// </summary>
public sealed class RtcpFeedbackCodecTests
{
    private static readonly RtcpPacketCodec Codec = new();

    // ── PLI ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Pli_round_trips_and_matches_rfc4585_layout()
    {
        var pli = new RtcpPictureLossIndication { SenderSsrc = 0x11223344, MediaSsrc = 0xAABBCCDD };
        var wire = Codec.Encode([pli]);

        // 12 bytes: header(4) + sender SSRC(4) + media SSRC(4); FMT=1 in the RC field, PT=206.
        Assert.Equal(12, wire.Length);
        Assert.Equal(0x81, wire[0]); // V=2, FMT=1
        Assert.Equal(206, wire[1]);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(2)));
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(4)));
        Assert.Equal(0xAABBCCDDu, BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(8)));

        var decoded = Assert.IsType<RtcpPictureLossIndication>(Assert.Single(Codec.Decode(wire)));
        Assert.Equal(pli.SenderSsrc, decoded.SenderSsrc);
        Assert.Equal(pli.MediaSsrc, decoded.MediaSsrc);
    }

    // ── FIR ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Fir_round_trips_entries_and_carries_zero_media_ssrc_in_header()
    {
        var fir = new RtcpFullIntraRequest
        {
            SenderSsrc = 0x01020304,
            Entries =
            [
                new RtcpFirEntry { MediaSsrc = 0xDEADBEEF, SequenceNumber = 7 },
                new RtcpFirEntry { MediaSsrc = 0xFEEDFACE, SequenceNumber = 200 },
            ],
        };
        var wire = Codec.Encode([fir]);

        Assert.Equal(206, wire[1]);
        Assert.Equal(0x84, wire[0]); // FMT=4
        // RFC 5104 §4.3.1: the common-header media SSRC is 0 for FIR.
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(8)));

        var decoded = Assert.IsType<RtcpFullIntraRequest>(Assert.Single(Codec.Decode(wire)));
        Assert.Equal(fir.SenderSsrc, decoded.SenderSsrc);
        Assert.Equal(2, decoded.Entries.Count);
        Assert.Equal(0xDEADBEEFu, decoded.Entries[0].MediaSsrc);
        Assert.Equal(7, decoded.Entries[0].SequenceNumber);
        Assert.Equal(200, decoded.Entries[1].SequenceNumber);
    }

    // ── Generic NACK ─────────────────────────────────────────────────────────────

    [Fact]
    public void Nack_round_trips_and_expands_bitmask_to_lost_sequence_numbers()
    {
        var nack = new RtcpGenericNack
        {
            SenderSsrc = 0x1,
            MediaSsrc = 0x2,
            // PID 100 with bits 0 and 2 set → 100, 101, 103; PID 65535 wraps to 0.
            Entries =
            [
                new RtcpNackEntry { PacketId = 100, LostPacketBitmask = 0b0000_0000_0000_0101 },
                new RtcpNackEntry { PacketId = 65535, LostPacketBitmask = 0b0000_0000_0000_0001 },
            ],
        };
        var wire = Codec.Encode([nack]);

        Assert.Equal(205, wire[1]);
        Assert.Equal(0x81, wire[0]); // FMT=1

        var decoded = Assert.IsType<RtcpGenericNack>(Assert.Single(Codec.Decode(wire)));
        Assert.Equal((ushort[])[100, 101, 103, 65535, 0], decoded.LostSequenceNumbers().ToArray());
    }

    // ── Compound coexistence & fail-closed ──────────────────────────────────────

    [Fact]
    public void Feedback_after_receiver_report_in_a_compound_packet_is_decoded()
    {
        var rr = new RtcpReceiverReport { Ssrc = 0x5, ReportBlocks = [] };
        var pli = new RtcpPictureLossIndication { SenderSsrc = 0x5, MediaSsrc = 0x9 };

        var decoded = Codec.Decode(Codec.Encode([rr, pli]));

        Assert.Equal(2, decoded.Count);
        Assert.IsType<RtcpReceiverReport>(decoded[0]);
        Assert.IsType<RtcpPictureLossIndication>(decoded[1]);
    }

    [Fact]
    public void Unknown_feedback_format_is_skipped_without_discarding_the_compound()
    {
        // A PSFB with an unsupported FMT (e.g. 15 = AFB/REMB) followed by a valid RR:
        // the unknown feedback must be skipped, the RR still decoded.
        var afb = new byte[12];
        afb[0] = 0x8F; // V=2, FMT=15
        afb[1] = 206;
        BinaryPrimitives.WriteUInt16BigEndian(afb.AsSpan(2), 2);
        var rr = Codec.Encode([new RtcpReceiverReport { Ssrc = 0x7, ReportBlocks = [] }]);

        var compound = new byte[afb.Length + rr.Length];
        afb.CopyTo(compound, 0);
        rr.CopyTo(compound, afb.Length);

        var decoded = Codec.Decode(compound);
        Assert.Equal(0x7u, Assert.IsType<RtcpReceiverReport>(Assert.Single(decoded)).Ssrc);
    }

    [Fact]
    public void Truncated_feedback_ssrc_pair_throws()
    {
        var packet = new byte[8]; // header(4) + only one SSRC(4)
        packet[0] = 0x81;
        packet[1] = 206;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 1);

        Assert.Throws<ArgumentException>(() => Codec.Decode(packet));
    }

    [Fact]
    public void Fir_fci_not_divisible_by_entry_size_throws()
    {
        // header(4) + SSRC pair(8) + 4 FCI bytes (a FIR entry is 8 bytes).
        var packet = new byte[16];
        packet[0] = 0x84; // FMT=4
        packet[1] = 206;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 3);

        Assert.Throws<ArgumentException>(() => Codec.Decode(packet));
    }

    [Fact]
    public void Encoding_feedback_with_no_entries_throws()
    {
        var fir = new RtcpFullIntraRequest { SenderSsrc = 1, Entries = [] };
        var nack = new RtcpGenericNack { SenderSsrc = 1, MediaSsrc = 2, Entries = [] };

        Assert.Throws<ArgumentException>(() => Codec.Encode([fir]));
        Assert.Throws<ArgumentException>(() => Codec.Encode([nack]));
    }
}
