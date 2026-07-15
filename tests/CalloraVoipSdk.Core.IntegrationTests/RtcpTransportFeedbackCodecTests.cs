using System.Buffers.Binary;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Transport-wide congestion-control feedback wire codec (RTPFB PT=205, FMT=15 —
/// draft-holmer-rmcat-transport-wide-cc-extensions-01 §3.1): round trips over mixed arrival
/// statuses, byte-level header layout, 32-bit padding, multi-chunk bodies, decoding of the
/// run-length and one/two-bit status-vector chunk forms, signed reference time, and the
/// truncation / range guards.
/// </summary>
public sealed class RtcpTransportFeedbackCodecTests
{
    // Splits an encoded packet into the SSRC pair + body the way the feedback dispatch does
    // (RtcpFeedbackCodec strips the 4-byte RTCP header, then the 8-byte SSRC pair).
    private static RtcpTransportFeedback DecodeWire(byte[] wire)
    {
        var senderSsrc = BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(4));
        var mediaSsrc = BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(8));
        return RtcpTransportFeedbackCodec.Decode(senderSsrc, mediaSsrc, wire.AsSpan(12));
    }

    [Fact]
    public void Round_trips_mixed_arrival_statuses_and_matches_header_layout()
    {
        var feedback = new RtcpTransportFeedback
        {
            SenderSsrc = 0x11223344,
            MediaSsrc = 0xAABBCCDD,
            ReferenceTimeTicks = 1000,
            FeedbackPacketCount = 7,
            Statuses =
            [
                new RtcpTransportFeedbackStatus { SequenceNumber = 100, Received = true, DeltaTicks = 20 },
                new RtcpTransportFeedbackStatus { SequenceNumber = 101, Received = false },
                new RtcpTransportFeedbackStatus { SequenceNumber = 102, Received = true, DeltaTicks = 300 },
                new RtcpTransportFeedbackStatus { SequenceNumber = 103, Received = true, DeltaTicks = -5 },
                new RtcpTransportFeedbackStatus { SequenceNumber = 104, Received = true, DeltaTicks = 0 },
            ],
        };

        var wire = RtcpTransportFeedbackCodec.Encode(feedback);

        // Header: V=2/FMT=15 (0x8F), PT=205, length = words-1, then base seq + status count.
        Assert.Equal(0x8F, wire[0]);
        Assert.Equal(205, wire[1]);
        Assert.Equal(0, wire.Length % 4);
        Assert.Equal(wire.Length / 4 - 1, BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(2)));
        Assert.Equal(100, BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(12)));
        Assert.Equal(5, BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(14)));
        Assert.Equal(7, wire[19]); // feedback packet count

        var decoded = DecodeWire(wire);
        Assert.Equal(0x11223344u, decoded.SenderSsrc);
        Assert.Equal(0xAABBCCDDu, decoded.MediaSsrc);
        Assert.Equal(1000, decoded.ReferenceTimeTicks);
        Assert.Equal(7, decoded.FeedbackPacketCount);
        Assert.Equal(feedback.Statuses.Count, decoded.Statuses.Count);
        for (var i = 0; i < feedback.Statuses.Count; i++)
        {
            Assert.Equal(feedback.Statuses[i].SequenceNumber, decoded.Statuses[i].SequenceNumber);
            Assert.Equal(feedback.Statuses[i].Received, decoded.Statuses[i].Received);
            if (feedback.Statuses[i].Received)
                Assert.Equal(feedback.Statuses[i].DeltaTicks, decoded.Statuses[i].DeltaTicks);
        }
    }

    [Fact]
    public void Round_trips_across_multiple_chunks_with_padding()
    {
        // 8 received packets → 2 status-vector chunks; body needs 32-bit padding.
        var statuses = new RtcpTransportFeedbackStatus[8];
        for (var i = 0; i < 8; i++)
            statuses[i] = new RtcpTransportFeedbackStatus
            {
                SequenceNumber = (ushort)(500 + i), Received = true, DeltaTicks = i,
            };

        var feedback = new RtcpTransportFeedback
        {
            SenderSsrc = 1, MediaSsrc = 2, ReferenceTimeTicks = 0, FeedbackPacketCount = 0, Statuses = statuses,
        };

        var wire = RtcpTransportFeedbackCodec.Encode(feedback);
        Assert.Equal(0, wire.Length % 4);

        var decoded = DecodeWire(wire);
        Assert.Equal(8, decoded.Statuses.Count);
        Assert.Equal(507, decoded.Statuses[7].SequenceNumber);
        Assert.Equal(7, decoded.Statuses[7].DeltaTicks);
    }

    [Fact]
    public void Round_trips_negative_reference_time()
    {
        var feedback = new RtcpTransportFeedback
        {
            SenderSsrc = 1, MediaSsrc = 2, ReferenceTimeTicks = -1000, FeedbackPacketCount = 3,
            Statuses = [new RtcpTransportFeedbackStatus { SequenceNumber = 0, Received = true, DeltaTicks = 1 }],
        };

        var decoded = DecodeWire(RtcpTransportFeedbackCodec.Encode(feedback));
        Assert.Equal(-1000, decoded.ReferenceTimeTicks);
    }

    [Fact]
    public void Single_small_delta_body_is_padded_to_a_word_boundary()
    {
        var feedback = new RtcpTransportFeedback
        {
            SenderSsrc = 1, MediaSsrc = 2, ReferenceTimeTicks = 0, FeedbackPacketCount = 0,
            Statuses = [new RtcpTransportFeedbackStatus { SequenceNumber = 9, Received = true, DeltaTicks = 42 }],
        };

        // header(4) + ssrc pair(8) + fields(8) + chunk(2) + delta(1) = 23 → padded to 24.
        var wire = RtcpTransportFeedbackCodec.Encode(feedback);
        Assert.Equal(24, wire.Length);
        Assert.Equal(5, BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(2)));
    }

    [Fact]
    public void Decodes_a_run_length_chunk()
    {
        // base seq 10, status count 3, ref time 0, fb count 0, one run-length chunk of
        // three "received small delta" symbols (0x2003), then deltas 5,6,7.
        var fci = new byte[] { 0x00, 0x0A, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x20, 0x03, 5, 6, 7 };

        var decoded = RtcpTransportFeedbackCodec.Decode(0x1, 0x2, fci);

        Assert.Equal(3, decoded.Statuses.Count);
        Assert.Equal(10, decoded.Statuses[0].SequenceNumber);
        Assert.Equal(12, decoded.Statuses[2].SequenceNumber);
        Assert.All(decoded.Statuses, s => Assert.True(s.Received));
        Assert.Equal([5, 6, 7], decoded.Statuses.Select(s => s.DeltaTicks).ToArray());
    }

    [Fact]
    public void Decodes_a_one_bit_status_vector_chunk()
    {
        // One-bit status vector (T=1, S=0): received, not received, received → 0xA800;
        // status count 3; two small deltas (10, 11) for the received packets.
        var fci = new byte[] { 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0xA8, 0x00, 10, 11 };

        var decoded = RtcpTransportFeedbackCodec.Decode(0x1, 0x2, fci);

        Assert.True(decoded.Statuses[0].Received);
        Assert.False(decoded.Statuses[1].Received);
        Assert.True(decoded.Statuses[2].Received);
        Assert.Equal(10, decoded.Statuses[0].DeltaTicks);
        Assert.Equal(11, decoded.Statuses[2].DeltaTicks);
    }

    [Fact]
    public void Round_trips_a_sequence_number_wrap_past_16_bits()
    {
        var feedback = new RtcpTransportFeedback
        {
            SenderSsrc = 1, MediaSsrc = 2, ReferenceTimeTicks = 0, FeedbackPacketCount = 0,
            Statuses =
            [
                new RtcpTransportFeedbackStatus { SequenceNumber = 65534, Received = true, DeltaTicks = 1 },
                new RtcpTransportFeedbackStatus { SequenceNumber = 65535, Received = true, DeltaTicks = 1 },
                new RtcpTransportFeedbackStatus { SequenceNumber = 0, Received = true, DeltaTicks = 1 },
                new RtcpTransportFeedbackStatus { SequenceNumber = 1, Received = true, DeltaTicks = 1 },
            ],
        };

        var decoded = DecodeWire(RtcpTransportFeedbackCodec.Encode(feedback));
        Assert.Equal(
            [(ushort)65534, 65535, 0, 1],
            decoded.Statuses.Select(s => s.SequenceNumber).ToArray());
    }

    [Fact]
    public void Decoding_a_reserved_symbol_throws()
    {
        // Two-bit status-vector chunk with the reserved symbol 3 in slot 0: 0xF000; status count 1.
        var fci = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0xF0, 0x00 };

        Assert.Throws<ArgumentException>(() => RtcpTransportFeedbackCodec.Decode(0x1, 0x2, fci));
    }

    [Fact]
    public void Encoding_with_no_statuses_throws()
    {
        var feedback = new RtcpTransportFeedback
        {
            SenderSsrc = 1, MediaSsrc = 2, ReferenceTimeTicks = 0, FeedbackPacketCount = 0, Statuses = [],
        };

        Assert.Throws<ArgumentException>(() => RtcpTransportFeedbackCodec.Encode(feedback));
    }

    [Fact]
    public void Encoding_a_delta_out_of_two_byte_range_throws()
    {
        var feedback = new RtcpTransportFeedback
        {
            SenderSsrc = 1, MediaSsrc = 2, ReferenceTimeTicks = 0, FeedbackPacketCount = 0,
            Statuses = [new RtcpTransportFeedbackStatus { SequenceNumber = 0, Received = true, DeltaTicks = 40000 }],
        };

        Assert.Throws<ArgumentException>(() => RtcpTransportFeedbackCodec.Encode(feedback));
    }

    [Fact]
    public void Decoding_a_body_truncated_inside_the_deltas_throws()
    {
        // status count 2, one run-length chunk of two "received" symbols, but only one delta byte.
        var fci = new byte[] { 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x20, 0x02, 5 };

        Assert.Throws<ArgumentException>(() => RtcpTransportFeedbackCodec.Decode(0x1, 0x2, fci));
    }

    [Fact]
    public void Decoding_a_body_too_short_for_the_header_fields_throws()
    {
        Assert.Throws<ArgumentException>(() => RtcpTransportFeedbackCodec.Decode(0x1, 0x2, new byte[4]));
    }
}
