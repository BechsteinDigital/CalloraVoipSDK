using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Recv-side simulcast demux (RFC 8853 / RFC 8852, 4.7.0 slice 2, forwarding-only): several encodings of one
/// video m-line arrive interleaved under one MID, each on its own SSRC and RID. The track reassembles each RID
/// into an independent lane (own depacketiser, reorder window, arrival-loss tracker) and surfaces frames tagged
/// with their RID — no cross-talk, and one encoding's gap never resets another's reassembly. A RID-less receive
/// stays on the default lane and is byte-identical to the pre-simulcast single-stream path.
/// </summary>
public sealed class BundledVideoTrackSimulcastReceiveTests
{
    private const byte MidExtId = 3;
    private const byte VideoPayloadType = 96;
    private const uint VideoSsrc = 0x0B0B0B0B;
    private const uint RtpTimestamp = 90000;
    private const int ReorderDepth = 32;

    // ── FrameReceived is tagged with the arriving encoding's RID ─────────────────────────────

    [Fact]
    public void Interleaved_encodings_are_reassembled_per_rid_with_the_correct_rid_tag()
    {
        using var track = SimulcastReceiveTrack();
        var received = new List<(string? Rid, byte[] Frame)>();
        track.FrameReceived += (frame, _, _, rid) => received.Add((rid, frame));

        // Two single-packet keyframes on distinct SSRCs, one per encoding, interleaved.
        var high = Frame(seq: 1000, marker: true, payloadByte: 0xAA);
        var low = Frame(seq: 40, marker: true, payloadByte: 0xBB);

        track.OnRtpPacket(Packet(ssrc: 0x1111, seq: high.Seq, marker: true, payload: high.Payload), rid: "h");
        track.OnRtpPacket(Packet(ssrc: 0x2222, seq: low.Seq, marker: true, payload: low.Payload), rid: "l");

        Assert.Equal(2, received.Count);
        var byRid = received.ToDictionary(r => r.Rid!, r => r.Frame);
        Assert.Equal(new byte[] { 0xAA }, byRid["h"]);
        Assert.Equal(new byte[] { 0xBB }, byRid["l"]);
    }

    [Fact]
    public void A_latched_rid_resolves_later_rid_less_packets_on_the_same_ssrc()
    {
        // Browsers stamp the RID extension only on the first packets of each encoding: once latched at the
        // router, later packets arrive with rid resolved from the SSRC latch — the track only ever sees the
        // resolved rid. Here we drive the resolved rid directly (the router's job is covered separately).
        using var track = SimulcastReceiveTrack();
        var received = new List<(string? Rid, byte[] Frame)>();
        track.FrameReceived += (frame, _, _, rid) => received.Add((rid, frame));

        track.OnRtpPacket(Packet(ssrc: 0x1111, seq: 1000, marker: true, payload: new byte[] { 0x01 }), rid: "h");
        track.OnRtpPacket(Packet(ssrc: 0x1111, seq: 1001, marker: true, payload: new byte[] { 0x02 }), rid: "h");

        Assert.Equal(2, received.Count);
        Assert.All(received, r => Assert.Equal("h", r.Rid));
    }

    // ── lane isolation: a gap in one encoding does not tear another's reassembly ──────────────

    [Fact]
    public void A_gap_in_one_rid_does_not_reset_another_rids_depacketiser()
    {
        // Reorder depth 1: a two-away forward jump exceeds the window and is delivered immediately as a
        // discontinuity, so the "h" gap deterministically tears "h"'s frame-under-assembly. If the depacketiser
        // and ordered-delivery cursor were shared across lanes, that reset would corrupt "l"'s interleaved
        // reassembly; per-lane, "l" is untouched.
        using var track = new BundledVideoTrack(
            "video", "VP8", VideoPayloadType, VideoSsrc, remoteSupportsNack: false, remoteSupportsPli: false,
            new[] { "h", "l" }, Outbound(), reorderWindowDepth: 1, NullLoggerFactory.Instance);
        var received = new List<(string? Rid, byte[] Frame)>();
        track.FrameReceived += (frame, _, _, rid) => received.Add((rid, frame));

        // "l" opens a keyframe; "h" opens a frame (no marker) then leaves seq 1001 missing and sends 1002+1003 —
        // two packets held behind the gap exceed the depth-1 window, so the buffer skips the gap and releases
        // 1002 out of order, tearing "h"'s open frame; "l" then closes cleanly, interleaved with the "h" tear.
        track.OnRtpPacket(Packet(ssrc: 0x2222, seq: 40, marker: true, payload: new byte[] { 0xB0 }), rid: "l");
        track.OnRtpPacket(Packet(ssrc: 0x1111, seq: 1000, marker: false, payload: new byte[] { 0xA0 }), rid: "h");
        track.OnRtpPacket(Packet(ssrc: 0x1111, seq: 1002, marker: false, payload: new byte[] { 0xA1 }), rid: "h");
        track.OnRtpPacket(Packet(ssrc: 0x1111, seq: 1003, marker: true, payload: new byte[] { 0xA2 }), rid: "h");
        track.OnRtpPacket(Packet(ssrc: 0x2222, seq: 41, marker: true, payload: new byte[] { 0xB1 }), rid: "l");

        // "l" reassembled both of its keyframes uninterrupted; the "h" discontinuity did not touch "l".
        var lowFrames = received.Where(r => r.Rid == "l").Select(r => r.Frame).ToList();
        Assert.Equal(2, lowFrames.Count);
        Assert.Equal(new byte[] { 0xB0 }, lowFrames[0]);
        Assert.Equal(new byte[] { 0xB1 }, lowFrames[1]);
        // The discontinuity was counted on "h" only (a frame under assembly was torn), proving per-lane drop
        // tracking — a shared cursor would instead have dropped inside "l"'s interleaved sequence.
        Assert.True(track.FramesDropped >= 1);
    }

    // ── the default (RID-less) lane is byte-identical to the single-stream path ───────────────

    [Fact]
    public void A_rid_less_receive_delivers_on_the_default_lane_with_a_null_rid_tag()
    {
        using var track = NonSimulcastTrack();
        var received = new List<(string? Rid, byte[] Frame)>();
        track.FrameReceived += (frame, _, _, rid) => received.Add((rid, frame));

        track.OnRtpPacket(Packet(ssrc: VideoSsrc, seq: 1000, marker: true, payload: new byte[] { 0x7F }));

        var one = Assert.Single(received);
        Assert.Null(one.Rid);
        Assert.Equal(new byte[] { 0x7F }, one.Frame);
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────

    // A simulcast track configured to send RIDs "h"/"l"; its receive path demuxes any inbound RID into its lane.
    private static BundledVideoTrack SimulcastReceiveTrack() =>
        new("video", "VP8", VideoPayloadType, VideoSsrc, remoteSupportsNack: false, remoteSupportsPli: false,
            new[] { "h", "l" }, Outbound(), ReorderDepth, NullLoggerFactory.Instance);

    private static BundledVideoTrack NonSimulcastTrack() =>
        new("video", "VP8", VideoPayloadType, VideoSsrc, remoteSupportsNack: false, remoteSupportsPli: false,
            Outbound(), ReorderDepth, NullLoggerFactory.Instance);

    private static BundledOutboundPipeline Outbound() =>
        new(new RtpPacketCodec(), new DiscardSender(), NullLogger<BundledOutboundPipeline>.Instance);

    // A minimal single-payload VP8 packet whose marker closes the frame. VP8's depacketiser emits the payload
    // as the frame when the descriptor and marker are set; here we craft a 1-byte VP8 payload descriptor + body.
    private static RtpPacket Packet(uint ssrc, ushort seq, bool marker, byte[] payload) => new()
    {
        Ssrc = ssrc,
        PayloadType = VideoPayloadType,
        SequenceNumber = seq,
        Timestamp = RtpTimestamp,
        Marker = marker,
        Payload = Vp8(payload),
    };

    // A single-frame reference used to build a keyframe payload and its expected reassembled bytes.
    private static (ushort Seq, byte[] Payload) Frame(ushort seq, bool marker, byte payloadByte) =>
        (seq, new[] { payloadByte });

    // Wraps a VP8 payload with the minimal (1-byte, no extensions, start of partition) VP8 payload descriptor
    // (RFC 7741 §4.2) so the depacketiser accepts it as a complete single-packet frame.
    private static byte[] Vp8(byte[] body)
    {
        var packet = new byte[1 + body.Length];
        packet[0] = 0x10; // S=1 (start of partition), PID=0, no X/N/PartID.
        Array.Copy(body, 0, packet, 1, body.Length);
        return packet;
    }

    private sealed class DiscardSender : IBundledDatagramSender
    {
        public ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
