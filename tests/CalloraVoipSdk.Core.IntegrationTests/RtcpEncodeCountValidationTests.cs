using System.Linq;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #162 P2-2: the RC/SC header field is 5 bits (RFC 3550 §6.1), so at most 31 items fit in one packet.
/// The encoders used to mask the count with <c>&amp; 0x1F</c> while still writing every item to the body,
/// so at 32 items the packet contradicted itself — the header said 0, the body carried 32 — and our own
/// encode no longer round-tripped through our own decode. These tests pin the 31/32 boundary in both
/// directions: 31 still encodes and decodes losslessly, 32 is refused instead of silently corrupted.
/// </summary>
public sealed class RtcpEncodeCountValidationTests
{
    private const int MaxCount = 31;
    private static readonly RtcpPacketCodec Codec = new();

    private static RtcpReportBlock Block(uint ssrc) => new()
    {
        Ssrc = ssrc,
        FractionLost = 0,
        CumulativePacketsLost = 0,
        ExtendedHighestSeq = 1,
        Jitter = 0,
        LastSr = 0,
        DelaySinceLastSr = 0,
    };

    private static IReadOnlyList<RtcpReportBlock> Blocks(int count) =>
        [.. Enumerable.Range(0, count).Select(i => Block((uint)(0x1000 + i)))];

    private static RtcpSdesChunk Chunk(uint ssrc) => new()
    {
        Ssrc = ssrc,
        Items = [new RtcpSdesItem { ItemType = RtcpSdesItemType.CName, Value = "c" + ssrc }],
    };

    // ── the boundary that still works ────────────────────────────────────────

    [Fact]
    public void An_rr_with_31_blocks_round_trips()
    {
        var wire = Codec.Encode([new RtcpReceiverReport { Ssrc = 1, ReportBlocks = Blocks(MaxCount) }]);

        var decoded = Assert.IsType<RtcpReceiverReport>(Codec.Decode(wire).Single());
        Assert.Equal(MaxCount, decoded.ReportBlocks.Count);
        Assert.Equal(MaxCount, wire[0] & 0x1F);   // the header agrees with the body
    }

    [Fact]
    public void An_sr_with_31_blocks_round_trips()
    {
        var wire = Codec.Encode([new RtcpSenderReport { Ssrc = 2, ReportBlocks = Blocks(MaxCount) }]);

        var decoded = Assert.IsType<RtcpSenderReport>(Codec.Decode(wire).Single());
        Assert.Equal(MaxCount, decoded.ReportBlocks.Count);
    }

    [Fact]
    public void An_sdes_with_31_chunks_round_trips()
    {
        var chunks = Enumerable.Range(0, MaxCount).Select(i => Chunk((uint)(0x2000 + i))).ToArray();

        var wire = Codec.Encode([new RtcpSdesPacket { Chunks = chunks }]);

        var decoded = Assert.IsType<RtcpSdesPacket>(Codec.Decode(wire).Single());
        Assert.Equal(MaxCount, decoded.Chunks.Count);
    }

    [Fact]
    public void A_bye_with_31_sources_round_trips()
    {
        var sources = Enumerable.Range(0, MaxCount).Select(i => (uint)(0x3000 + i)).ToArray();

        var wire = Codec.Encode([new RtcpByePacket { Sources = sources, Reason = "bye" }]);

        var decoded = Assert.IsType<RtcpByePacket>(Codec.Decode(wire).Single());
        Assert.Equal(MaxCount, decoded.Sources.Count);
    }

    // ── one past the boundary: refused, not corrupted ────────────────────────

    [Fact]
    public void An_rr_with_32_blocks_is_refused()
    {
        // Previously: header RC=0, 776 bytes encoded, 0 blocks decoded back.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Codec.Encode([new RtcpReceiverReport { Ssrc = 1, ReportBlocks = Blocks(MaxCount + 1) }]));
    }

    [Fact]
    public void An_sr_with_32_blocks_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Codec.Encode([new RtcpSenderReport { Ssrc = 2, ReportBlocks = Blocks(MaxCount + 1) }]));
    }

    [Fact]
    public void An_sdes_with_32_chunks_is_refused()
    {
        // Previously: header SC=0, 388 bytes encoded, 0 chunks decoded back.
        var chunks = Enumerable.Range(0, MaxCount + 1).Select(i => Chunk((uint)(0x2000 + i))).ToArray();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => Codec.Encode([new RtcpSdesPacket { Chunks = chunks }]));
    }

    [Fact]
    public void A_bye_with_32_sources_is_refused()
    {
        // Previously: header SC=0, 136 bytes encoded, 0 sources decoded back.
        var sources = Enumerable.Range(0, MaxCount + 1).Select(i => (uint)(0x3000 + i)).ToArray();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => Codec.Encode([new RtcpByePacket { Sources = sources }]));
    }
}
