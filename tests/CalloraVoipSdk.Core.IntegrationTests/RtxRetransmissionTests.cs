using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Retransmission;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RTX retransmission mechanics (WebRTC phase 3, RFC 4588): the OSN encapsulation round
/// trip and the bounded sent-packet history that answers NACKs. Pure building blocks —
/// no session wiring yet.
/// </summary>
public sealed class RtxRetransmissionTests
{
    // ── RtxPacketFactory (RFC 4588 §4) ───────────────────────────────────────────

    [Fact]
    public void Encapsulate_prefixes_osn_and_rewrites_stream_identity()
    {
        var original = OriginalPacket(seq: 0x1234, payload: [0xAA, 0xBB, 0xCC]);

        var rtx = RtxPacketFactory.Encapsulate(original, rtxPayloadType: 99, rtxSsrc: 0xDEAD, rtxSequenceNumber: 5);

        Assert.Equal(99, rtx.PayloadType);
        Assert.Equal(0xDEADu, rtx.Ssrc);
        Assert.Equal(5, rtx.SequenceNumber);
        Assert.Equal(original.Timestamp, rtx.Timestamp);
        Assert.Equal(original.Marker, rtx.Marker);
        // Payload = OSN(2, big-endian) + original payload.
        Assert.Equal(new byte[] { 0x12, 0x34, 0xAA, 0xBB, 0xCC }, rtx.Payload.ToArray());
    }

    [Fact]
    public void Encapsulate_then_decapsulate_reproduces_the_original()
    {
        var original = OriginalPacket(seq: 40000, payload: [1, 2, 3, 4, 5]);

        var rtx = RtxPacketFactory.Encapsulate(original, rtxPayloadType: 99, rtxSsrc: 0xBEEF, rtxSequenceNumber: 7);
        Assert.True(RtxPacketFactory.TryDecapsulate(
            rtx, originalPayloadType: original.PayloadType, originalSsrc: original.Ssrc, out var recovered));

        Assert.Equal(original.SequenceNumber, recovered!.SequenceNumber);
        Assert.Equal(original.PayloadType, recovered.PayloadType);
        Assert.Equal(original.Ssrc, recovered.Ssrc);
        Assert.Equal(original.Timestamp, recovered.Timestamp);
        Assert.Equal(original.Marker, recovered.Marker);
        Assert.Equal(original.Payload.ToArray(), recovered.Payload.ToArray());
    }

    [Fact]
    public void Encapsulation_intentionally_drops_header_extensions_and_csrc()
    {
        // RFC 4588 §4 carries only OSN + original payload; extensions/CSRC are not part
        // of the RTX payload. Nail that down so the wiring slice knows the recovered
        // packet has none (rather than silently relying on RtpPacket defaults).
        var original = new RtpPacket
        {
            PayloadType = 96,
            SequenceNumber = 7,
            Timestamp = 21000,
            Ssrc = 0xAAAA,
            Csrc = [0x1, 0x2],
            HeaderExtension = new RtpExtension { Profile = 0xBEDE, Data = new byte[] { 1, 2, 3, 4 } },
            Payload = new byte[] { 9, 9 },
        };

        var rtx = RtxPacketFactory.Encapsulate(original, 99, 0xBBBB, 1);
        Assert.Null(rtx.HeaderExtension);
        Assert.Empty(rtx.Csrc);

        Assert.True(RtxPacketFactory.TryDecapsulate(rtx, 96, original.Ssrc, out var recovered));
        Assert.Null(recovered!.HeaderExtension);
        Assert.Empty(recovered.Csrc);
        Assert.Equal(7, recovered.SequenceNumber);
    }

    [Fact]
    public void Decapsulate_rejects_payload_shorter_than_the_osn()
    {
        var tooShort = new RtpPacket { PayloadType = 99, Ssrc = 1, SequenceNumber = 1, Payload = new byte[] { 0x00 } };

        Assert.False(RtxPacketFactory.TryDecapsulate(tooShort, 96, 0x2, out var recovered));
        Assert.Null(recovered);
    }

    [Fact]
    public void Decapsulate_of_a_zero_length_original_payload_is_supported()
    {
        // A retransmitted packet whose original payload was empty: OSN only, no data.
        var original = OriginalPacket(seq: 100, payload: []);
        var rtx = RtxPacketFactory.Encapsulate(original, 99, 0x3, 1);

        Assert.True(RtxPacketFactory.TryDecapsulate(rtx, 96, original.Ssrc, out var recovered));
        Assert.Equal(100, recovered!.SequenceNumber);
        Assert.Empty(recovered.Payload.ToArray());
    }

    // ── RtpRetransmissionBuffer ──────────────────────────────────────────────────

    [Fact]
    public void Buffer_returns_a_stored_packet_by_sequence_number()
    {
        var buffer = new RtpRetransmissionBuffer(capacity: 8);
        var packet = OriginalPacket(seq: 500, payload: [9, 9]);

        buffer.Store(packet);

        Assert.True(buffer.TryGet(500, out var found));
        Assert.Same(packet, found);
        Assert.False(buffer.TryGet(501, out _));
    }

    [Fact]
    public void Buffer_evicts_the_oldest_packet_beyond_capacity()
    {
        var buffer = new RtpRetransmissionBuffer(capacity: 3);
        for (ushort seq = 1; seq <= 5; seq++)
            buffer.Store(OriginalPacket(seq, payload: [(byte)seq]));

        Assert.Equal(3, buffer.Count);
        Assert.False(buffer.TryGet(1, out _)); // evicted
        Assert.False(buffer.TryGet(2, out _)); // evicted
        Assert.True(buffer.TryGet(3, out _));
        Assert.True(buffer.TryGet(5, out _));
    }

    [Fact]
    public void Buffer_resend_of_same_sequence_replaces_without_growing()
    {
        var buffer = new RtpRetransmissionBuffer(capacity: 2);
        buffer.Store(OriginalPacket(seq: 10, payload: [1]));
        buffer.Store(OriginalPacket(seq: 10, payload: [2])); // same seq again

        Assert.Equal(1, buffer.Count);
        Assert.True(buffer.TryGet(10, out var found));
        Assert.Equal(new byte[] { 2 }, found!.Payload.ToArray());
    }

    [Fact]
    public void Buffer_is_safe_under_concurrent_store_and_lookup()
    {
        var buffer = new RtpRetransmissionBuffer(capacity: 1024);
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++)
                buffer.Store(OriginalPacket((ushort)i, payload: [(byte)i]));
        });
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++)
                buffer.TryGet((ushort)i, out _);
        });

        // No exception (torn dictionary state) is the assertion.
        Assert.True(Task.WhenAll(writer, reader).Wait(TimeSpan.FromSeconds(10)));
    }

    private static RtpPacket OriginalPacket(ushort seq, byte[] payload) => new()
    {
        PayloadType = 96,
        Marker = true,
        SequenceNumber = seq,
        Timestamp = (uint)(seq * 3000),
        Ssrc = 0xCAFEBABE,
        Payload = payload,
    };
}
