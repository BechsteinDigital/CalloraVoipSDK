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
        track.FrameReceived += (frame, _) => claims.Add(frame.IsKeyFrame);

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
        track.FrameReceived += (frame, _) => claims.Add(frame.IsKeyFrame);

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
        track.FrameReceived += (frame, _) => claims.Add(frame.IsKeyFrame);

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
        track.FrameReceived += (frame, _) => claims.Add(frame.IsKeyFrame);

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
        track.FrameReceived += (frame, rid) => claims.Add((rid, frame.IsKeyFrame));

        // "h" starts its sequence; "l" has not been seen at all yet, so its delta cannot resolve a template
        // and must not inherit "h"'s structure.
        track.OnRtpPacket(Packet(seq: 100, frameByte: 0x01, descriptor: KeyFrameDescriptor(0), ssrc: 0x1111), rid: "h");
        track.OnRtpPacket(Packet(seq: 40, frameByte: 0x01, descriptor: DeltaDescriptor(0), ssrc: 0x2222), rid: "l");

        Assert.Equal([("h", true), ("l", false)], claims);
    }

    /// <summary>
    /// The other half of what the descriptor is for: the layer a frame sits on travels with it, so a forwarder
    /// can drop the top temporal layer without decoding — or, for an encrypted stream, without being able to.
    /// The structure below declares two temporal layers, which is the only way to tell a reported layer from a
    /// hard-coded zero.
    /// </summary>
    [Fact]
    public void The_frame_carries_the_layer_the_descriptor_puts_it_on()
    {
        using var track = VideoTrack(DescriptorExtId);
        var layers = new List<(int? Spatial, int? Temporal)>();
        track.FrameReceived += (frame, _) => layers.Add((frame.SpatialId, frame.TemporalId));

        // The key frame declares the L1T2 structure and rides on template 0 (spatial 0, temporal 0)…
        track.OnRtpPacket(Packet(seq: 100, frameByte: 0x01, descriptor: TwoTemporalLayerStructure(templateId: 0, frameNumber: 0)));
        // …the delta names template 1, which that structure puts on temporal layer 1.
        track.OnRtpPacket(Packet(seq: 101, frameByte: 0x01, descriptor: MandatoryOnly(templateId: 1, frameNumber: 1)));

        Assert.Equal([(0, 0), (0, 1)], layers);
    }

    /// <summary>
    /// Unknown is reported as unknown. A peer that never negotiated the extension yields no layer information
    /// rather than a plausible-looking zero — an SFU must be able to tell "base layer" from "no idea".
    /// </summary>
    [Fact]
    public void Without_a_descriptor_the_layer_is_unknown_rather_than_zero()
    {
        using var track = VideoTrack(dependencyDescriptorExtensionId: null);
        var layers = new List<(int? Spatial, int? Temporal)>();
        track.FrameReceived += (frame, _) => layers.Add((frame.SpatialId, frame.TemporalId));

        track.OnRtpPacket(Packet(seq: 100, frameByte: 0x00, descriptor: null));

        Assert.Equal([((int?)null, (int?)null)], layers);
    }

    /// <summary>
    /// Joining mid-sequence: the mandatory fields parse, but no structure has been seen, so the template
    /// resolves to nothing and the layer stays unknown instead of being guessed at.
    /// </summary>
    [Fact]
    public void A_frame_whose_structure_was_never_seen_reports_an_unknown_layer()
    {
        using var track = VideoTrack(DescriptorExtId);
        var layers = new List<(int? Spatial, int? Temporal)>();
        track.FrameReceived += (frame, _) => layers.Add((frame.SpatialId, frame.TemporalId));

        track.OnRtpPacket(Packet(seq: 100, frameByte: 0x00, descriptor: MandatoryOnly(templateId: 1, frameNumber: 9)));

        Assert.Equal([((int?)null, (int?)null)], layers);
    }

    /// <summary>
    /// #310: the flag says where it came from. With a descriptor the answer is the sender's own and holds
    /// whatever the payload contains; without one it was read out of the payload, which for an encrypting
    /// sender may be ciphertext (ADR-071). A consumer that must not guess needs to tell those apart.
    /// </summary>
    [Fact]
    public void A_descriptor_derived_flag_reports_the_header_as_its_source()
    {
        using var track = VideoTrack(DescriptorExtId);
        var sources = new List<VideoKeyFrameSource>();
        track.FrameReceived += (frame, _) => sources.Add(frame.KeyFrameSource);

        track.OnRtpPacket(Packet(seq: 100, frameByte: 0x01, descriptor: KeyFrameDescriptor(frameNumber: 0)));

        Assert.Equal([VideoKeyFrameSource.RtpHeaderExtension], sources);
    }

    [Fact]
    public void A_payload_derived_flag_reports_the_payload_as_its_source()
    {
        using var track = VideoTrack(dependencyDescriptorExtensionId: null);
        var sources = new List<VideoKeyFrameSource>();
        track.FrameReceived += (frame, _) => sources.Add(frame.KeyFrameSource);

        track.OnRtpPacket(Packet(seq: 100, frameByte: 0x00, descriptor: null));

        Assert.Equal([VideoKeyFrameSource.Payload], sources);
    }

    /// <summary>
    /// A negotiated extension is not the same as a descriptor on the frame: a packet that carries none falls
    /// back to the payload, and must say so rather than keep claiming the header.
    /// </summary>
    [Fact]
    public void A_frame_without_a_descriptor_reports_the_payload_even_when_the_extension_is_negotiated()
    {
        using var track = VideoTrack(DescriptorExtId);
        var sources = new List<VideoKeyFrameSource>();
        track.FrameReceived += (frame, _) => sources.Add(frame.KeyFrameSource);

        track.OnRtpPacket(Packet(seq: 100, frameByte: 0x01, descriptor: KeyFrameDescriptor(frameNumber: 0)));
        track.OnRtpPacket(Packet(seq: 101, frameByte: 0x00, descriptor: null));

        Assert.Equal([VideoKeyFrameSource.RtpHeaderExtension, VideoKeyFrameSource.Payload], sources);
    }

    /// <summary>
    /// The correction #225 made possible and #310 makes visible: an <em>opaque</em> session — payload
    /// ciphertext, nothing to parse — still gets a real key-frame flag when the peer negotiated the
    /// descriptor, because that one is written before the encryption. Until #225 the answer there was
    /// "always false, meaning unknown", and the documentation said so; that is no longer the whole truth.
    /// </summary>
    [Fact]
    public void An_opaque_stream_still_gets_a_key_frame_flag_from_the_descriptor()
    {
        using var track = new BundledVideoTrack(
            "video", "VP8", VideoPayloadType, VideoSsrc, remoteSupportsNack: false, remoteSupportsPli: false,
            Outbound(), ReorderDepth, NullLoggerFactory.Instance,
            opaqueFrames: true, dependencyDescriptorExtensionId: DescriptorExtId);
        var claims = new List<(bool IsKeyFrame, VideoKeyFrameSource Source)>();
        track.FrameReceived += (frame, _) => claims.Add((frame.IsKeyFrame, frame.KeyFrameSource));

        // The payload is ciphertext as far as the SDK is concerned; only the header speaks.
        track.OnRtpPacket(Packet(seq: 100, frameByte: 0x01, descriptor: KeyFrameDescriptor(frameNumber: 0)));

        Assert.Equal([(true, VideoKeyFrameSource.RtpHeaderExtension)], claims);
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

    // The SDK's own writer only ever declares L1T1 — it does not encode video, so it knows of no layer ladder
    // to describe. A scalable sender does, and that is the case worth testing, so the structure here is
    // hand-written: two templates on one spatial layer, the second on temporal layer 1
    // (AV1 RTP specification §A.8, template_dependency_structure()).
    private static byte[] TwoTemporalLayerStructure(int templateId, ushort frameNumber)
    {
        var writer = new RtpBitWriter(16);
        WriteMandatoryFields(ref writer, templateId, frameNumber);

        writer.WriteFlag(true);            // template_dependency_structure_present_flag
        writer.WriteFlag(false);           // active_decode_targets_present_flag
        writer.WriteFlag(false);           // custom_dtis_flag
        writer.WriteFlag(false);           // custom_fdiffs_flag
        writer.WriteFlag(false);           // custom_chains_flag

        writer.Write(0, 6);                // template_id_offset
        writer.Write(0, 5);                // dt_cnt_minus_one → one decode target

        // template_layers(): template 0 at T0, then next_layer_idc = 1 → the next template is T1; then stop.
        writer.Write(1, 2);
        writer.Write(3, 2);

        writer.Write(2, 2);                // template_dtis: Switch for both templates
        writer.Write(2, 2);

        writer.WriteFlag(false);           // template 0 depends on nothing
        writer.WriteFlag(true);            // template 1 depends on one earlier frame…
        writer.Write(0, 4);                // …at distance 1
        writer.WriteFlag(false);

        writer.WriteNonSymmetric(0, 2);    // chain_cnt = 0
        writer.WriteFlag(false);           // resolutions_present_flag

        return writer.ToArray();
    }

    // A delta frame's descriptor: the three mandatory bytes, naming a template of the retained structure.
    private static byte[] MandatoryOnly(int templateId, ushort frameNumber)
    {
        var writer = new RtpBitWriter(3);
        WriteMandatoryFields(ref writer, templateId, frameNumber);
        return writer.ToArray();
    }

    private static void WriteMandatoryFields(ref RtpBitWriter writer, int templateId, ushort frameNumber)
    {
        writer.WriteFlag(true);            // start_of_frame
        writer.WriteFlag(true);            // end_of_frame
        writer.Write((uint)templateId, 6);
        writer.Write(frameNumber, 16);
    }

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
