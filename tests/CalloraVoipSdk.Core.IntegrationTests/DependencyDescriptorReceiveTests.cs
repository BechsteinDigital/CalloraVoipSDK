using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// L2 — #225: the Dependency Descriptor on the inbound track. The codec is pinned in
/// <see cref="DependencyDescriptorCodecTests"/>; what these tests cover is the wiring that decides which
/// key-frame flag a frame is delivered with — the descriptor's, written by the sender before any
/// encryption, or the payload-derived one the depacketiser guesses.
/// </summary>
/// <remarks>
/// This matters for every browser peer, not just an encrypted one: the SDK now offers the extension and
/// Chromium accepts it, so this path is live on ordinary calls. The VP8 payloads below carry a P bit that
/// deliberately contradicts the descriptor, which is what makes "the descriptor wins" observable at all.
/// </remarks>
public sealed class DependencyDescriptorReceiveTests
{
    private const byte MidExtId = 3;
    private const byte DescriptorExtId = 20;   // a two-byte-form id (#224), as a browser may well assign
    private const byte VideoPayloadType = 96;
    private const uint VideoSsrc = 0x0B0B0B0B;
    private const uint RtpTimestamp = 90000;
    private const int ReorderDepth = 32;

    /// <summary>
    /// The point of the whole feature: the sender says "key frame" in the header while the payload says the
    /// opposite. Without the descriptor the SDK would report the payload's answer — worthless once the
    /// payload is ciphertext (#223).
    /// </summary>
    [Fact]
    public void The_descriptor_key_frame_flag_wins_over_the_payload()
    {
        using var track = VideoTrack(DescriptorExtId);
        var claims = new List<bool>();
        track.FrameReceived += (_, _, isKeyFrame, _) => claims.Add(isKeyFrame);

        // Descriptor: key frame. Payload: P bit set → the clear-media VP8 depacketiser calls it a delta.
        track.OnRtpPacket(Packet(seq: 100, frameByte: 0x01, descriptor: KeyFrameDescriptor(frameNumber: 0)));

        Assert.Equal([true], claims);
        Assert.Equal(1, track.KeyFrames);
    }

    /// <summary>...and the same in the other direction, so this is not a constant dressed up as a decision.</summary>
    [Fact]
    public void A_delta_descriptor_overrides_a_payload_that_claims_a_key_frame()
    {
        using var track = VideoTrack(DescriptorExtId);
        var claims = new List<bool>();
        track.FrameReceived += (_, _, isKeyFrame, _) => claims.Add(isKeyFrame);

        // The key frame first, so the reader retains the template structure the delta below refers to.
        track.OnRtpPacket(Packet(seq: 100, frameByte: 0x01, descriptor: KeyFrameDescriptor(frameNumber: 0)));

        // Descriptor: delta (template 1). Payload: P bit clear → the depacketiser would call it a key frame.
        track.OnRtpPacket(Packet(seq: 101, frameByte: 0x00, descriptor: DeltaDescriptor(frameNumber: 1)));

        Assert.Equal([true, false], claims);
        Assert.Equal(1, track.KeyFrames);   // the second frame did not count as one
    }

    /// <summary>
    /// Without a negotiated extension nothing changes: the payload-derived flag is delivered exactly as
    /// before, so a peer that does not offer the descriptor sees the pre-#225 behaviour.
    /// </summary>
    [Fact]
    public void Without_a_negotiated_extension_the_payload_still_decides()
    {
        using var track = VideoTrack(dependencyDescriptorExtensionId: null);
        var claims = new List<bool>();
        track.FrameReceived += (_, _, isKeyFrame, _) => claims.Add(isKeyFrame);

        // A descriptor is on the wire, but nothing negotiated it — it must be ignored, not guessed at.
        track.OnRtpPacket(Packet(seq: 100, frameByte: 0x00, descriptor: DeltaDescriptor(frameNumber: 7)));

        Assert.Equal([true], claims);       // P bit clear → the payload says key frame, and it is believed
    }

    /// <summary>
    /// A packet that carries no descriptor on a stream that negotiated one falls back to the payload rather
    /// than to a stale flag from an earlier frame.
    /// </summary>
    [Fact]
    public void A_packet_without_a_descriptor_falls_back_to_the_payload()
    {
        using var track = VideoTrack(DescriptorExtId);
        var claims = new List<bool>();
        track.FrameReceived += (_, _, isKeyFrame, _) => claims.Add(isKeyFrame);

        track.OnRtpPacket(Packet(seq: 100, frameByte: 0x01, descriptor: KeyFrameDescriptor(frameNumber: 0)));
        track.OnRtpPacket(Packet(seq: 101, frameByte: 0x00, descriptor: null));

        Assert.Equal([true, true], claims); // second frame: no descriptor → payload's P=0 → key frame
    }

    /// <summary>
    /// Each simulcast encoding is its own stream with its own template structure, so the descriptor reader is
    /// per lane. A shared reader would resolve one encoding's template ids against another's structure.
    /// </summary>
    [Fact]
    public void Each_simulcast_lane_resolves_against_its_own_structure()
    {
        using var track = SimulcastTrack(DescriptorExtId);
        var claims = new List<(string? Rid, bool IsKeyFrame)>();
        track.FrameReceived += (_, _, isKeyFrame, rid) => claims.Add((rid, isKeyFrame));

        // "h" starts its sequence; "l" has not been seen at all yet, so its delta cannot resolve a template
        // and must not inherit "h"'s structure.
        track.OnRtpPacket(Packet(seq: 100, frameByte: 0x01, descriptor: KeyFrameDescriptor(0), ssrc: 0x1111), rid: "h");
        track.OnRtpPacket(Packet(seq: 40, frameByte: 0x01, descriptor: DeltaDescriptor(0), ssrc: 0x2222), rid: "l");

        Assert.Equal([("h", true), ("l", false)], claims);
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────

    private static BundledVideoTrack VideoTrack(byte? dependencyDescriptorExtensionId) =>
        new("video", "VP8", VideoPayloadType, VideoSsrc, remoteSupportsNack: false, remoteSupportsPli: false,
            Outbound(), ReorderDepth, NullLoggerFactory.Instance,
            dependencyDescriptorExtensionId: dependencyDescriptorExtensionId);

    private static BundledVideoTrack SimulcastTrack(byte? dependencyDescriptorExtensionId) =>
        new("video", "VP8", VideoPayloadType, VideoSsrc, remoteSupportsNack: false, remoteSupportsPli: false,
            ["h", "l"], Outbound(), ReorderDepth, NullLoggerFactory.Instance,
            dependencyDescriptorExtensionId: dependencyDescriptorExtensionId);

    private static BundledOutboundPipeline Outbound() =>
        new(new RtpPacketCodec(), new DiscardSender(), NullLogger<BundledOutboundPipeline>.Instance);

    private static byte[] KeyFrameDescriptor(ushort frameNumber) =>
        new DependencyDescriptorWriter().Write(isKeyFrame: true, startOfFrame: true, endOfFrame: true, frameNumber);

    private static byte[] DeltaDescriptor(ushort frameNumber) =>
        new DependencyDescriptorWriter().Write(isKeyFrame: false, startOfFrame: true, endOfFrame: true, frameNumber);

    // A single-packet VP8 frame: the 1-byte payload descriptor (RFC 7741 §4.2, S=1) plus one frame byte
    // whose low bit is the P flag — 0x00 reads as a key frame in the clear-media format, 0x01 as a delta.
    private static RtpPacket Packet(ushort seq, byte frameByte, byte[]? descriptor, uint ssrc = VideoSsrc) => new()
    {
        Ssrc = ssrc,
        PayloadType = VideoPayloadType,
        SequenceNumber = seq,
        Timestamp = RtpTimestamp,
        Marker = true,
        Payload = new byte[] { 0x10, frameByte },
        HeaderExtension = descriptor is null
            ? null
            : RtpHeaderExtensions.Encode([new RtpHeaderExtensionElement(DescriptorExtId, descriptor)]),
    };

    private sealed class DiscardSender : IBundledDatagramSender
    {
        public ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
