using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// L2 — #225 send side: the SDK writes a Dependency Descriptor for its own outbound video once the
/// application declares whether a frame is a key frame. Without that declaration nothing is written — the
/// SDK does not encode, and for an opaque frame (#223) it cannot look, so a guessed descriptor would be a
/// lie a receiver acts on.
/// </summary>
public sealed class DependencyDescriptorSendTests
{
    private const byte MidExtId = 3;
    private const byte DescriptorExtId = 20;
    private const byte VideoPayloadType = 96;
    private const uint VideoSsrc = 0x0B0B0B0B;
    private const uint RtpTimestamp = 90000;
    private const int ReorderDepth = 32;

    private static readonly byte[] MasterKey = Convert.FromHexString("E1F97A0D3E018BE0D64FA32C06DE4139");
    private static readonly byte[] MasterSalt = Convert.FromHexString("0EC675AD498AFEEBB6960B3AABE6");

    /// <summary>
    /// The round trip that matters: a declared key frame is stamped into the header, survives the wire, and
    /// the receiving track reports it as a key frame — even though the payload says otherwise.
    /// </summary>
    [Fact]
    public async Task A_declared_key_frame_travels_in_the_header_and_is_read_back()
    {
        var sent = new List<RtpPacket>();
        var outbound = OutboundWithDescriptor();
        outbound.PacketSent += sent.Add;
        outbound.InstallOutboundKey(new SrtpContext(Material()));

        using var sender = VideoTrack(outbound);
        // Payload P bit set → the clear-media depacketiser would call this a delta frame.
        await sender.SendFrameAsync(Vp8Frame(frameByte: 0x01), RtpTimestamp, isKeyFrame: true);

        var packet = Assert.Single(sent);
        Assert.True(TryReadDescriptor(packet, out var descriptor));
        Assert.True(descriptor.IsKeyFrame);
        Assert.True(descriptor.StartOfFrame);
        Assert.True(descriptor.EndOfFrame);

        using var receiver = VideoTrack(OutboundWithDescriptor());
        var claims = new List<bool>();
        receiver.FrameReceived += (_, _, isKeyFrame, _) => claims.Add(isKeyFrame);
        receiver.OnRtpPacket(packet);

        Assert.Equal([true], claims);
    }

    /// <summary>A declared delta frame carries the mandatory three bytes and no structure.</summary>
    [Fact]
    public async Task A_declared_delta_frame_carries_a_mandatory_only_descriptor()
    {
        var sent = new List<RtpPacket>();
        var outbound = OutboundWithDescriptor();
        outbound.PacketSent += sent.Add;
        outbound.InstallOutboundKey(new SrtpContext(Material()));

        using var sender = VideoTrack(outbound);
        await sender.SendFrameAsync(Vp8Frame(0x01), RtpTimestamp, isKeyFrame: false);

        Assert.Equal(3, DescriptorLength(sent[0]));
    }

    /// <summary>
    /// Frame numbers advance per frame, and the boundary flags follow the packetiser: a frame split across
    /// several packets starts on the first and ends on the last.
    /// </summary>
    [Fact]
    public async Task Frame_numbers_advance_and_the_boundary_flags_span_the_frame()
    {
        var sent = new List<RtpPacket>();
        var outbound = OutboundWithDescriptor();
        outbound.PacketSent += sent.Add;
        outbound.InstallOutboundKey(new SrtpContext(Material()));

        using var sender = VideoTrack(outbound);
        await sender.SendFrameAsync(Vp8Frame(0x00), RtpTimestamp, isKeyFrame: true);
        await sender.SendFrameAsync(Vp8Frame(0x01, length: 3_000), RtpTimestamp + 3000, isKeyFrame: false);

        var descriptors = sent.Select(p =>
        {
            Assert.True(TryReadDescriptor(p, out var d));
            return d;
        }).ToArray();

        Assert.Equal(0, descriptors[0].FrameNumber);
        Assert.All(descriptors[1..], d => Assert.Equal(1, d.FrameNumber));

        var multiPacket = descriptors[1..];
        Assert.True(multiPacket.Length > 1, "the second frame should have fragmented");
        Assert.True(multiPacket[0].StartOfFrame);
        Assert.False(multiPacket[0].EndOfFrame);
        Assert.True(multiPacket[^1].EndOfFrame);
    }

    /// <summary>
    /// No declaration, no descriptor: the SDK does not invent one. A receiver then falls back to the payload,
    /// which is exactly the pre-#225 behaviour.
    /// </summary>
    [Fact]
    public async Task Without_a_declaration_no_descriptor_is_written()
    {
        var sent = new List<RtpPacket>();
        var outbound = OutboundWithDescriptor();
        outbound.PacketSent += sent.Add;
        outbound.InstallOutboundKey(new SrtpContext(Material()));

        using var sender = VideoTrack(outbound);
        await sender.SendFrameAsync(Vp8Frame(0x01), RtpTimestamp);

        Assert.Equal(-1, DescriptorLength(sent[0]));
    }

    /// <summary>
    /// Nor when the peer never negotiated the extension — declaring a key frame then changes nothing on the
    /// wire, so the packets stay byte-identical to a build without this feature.
    /// </summary>
    [Fact]
    public async Task Without_a_negotiated_extension_no_descriptor_is_written()
    {
        var sent = new List<RtpPacket>();
        var outbound = OutboundWithoutDescriptor();
        outbound.PacketSent += sent.Add;
        outbound.InstallOutboundKey(new SrtpContext(Material()));

        using var sender = VideoTrack(outbound, dependencyDescriptorExtensionId: null);
        await sender.SendFrameAsync(Vp8Frame(0x01), RtpTimestamp, isKeyFrame: true);

        Assert.Equal(-1, DescriptorLength(sent[0]));
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────

    // The span the extension lookup yields cannot live in an async method on net8.0 (ref locals in async
    // bodies need C# 13), so the span-consuming steps stay in these synchronous helpers.
    private static bool TryReadDescriptor(RtpPacket packet, out DependencyDescriptor descriptor)
    {
        descriptor = default;
        return RtpHeaderExtensions.TryFindValue(packet.HeaderExtension, DescriptorExtId, out var value)
               && new DependencyDescriptorReader().TryParse(value, out descriptor);
    }

    /// <summary>The descriptor's byte length on this packet, or -1 when it carries none.</summary>
    private static int DescriptorLength(RtpPacket packet)
        => RtpHeaderExtensions.TryFindValue(packet.HeaderExtension, DescriptorExtId, out var value) ? value.Length : -1;

    private static BundledVideoTrack VideoTrack(
        BundledOutboundPipeline outbound, byte? dependencyDescriptorExtensionId = DescriptorExtId) =>
        new("video", "VP8", VideoPayloadType, VideoSsrc, remoteSupportsNack: false, remoteSupportsPli: false,
            outbound, ReorderDepth, NullLoggerFactory.Instance,
            dependencyDescriptorExtensionId: dependencyDescriptorExtensionId);

    private static BundledOutboundPipeline OutboundWithDescriptor() => Outbound(DescriptorExtId);

    private static BundledOutboundPipeline OutboundWithoutDescriptor() => Outbound(null);

    private static BundledOutboundPipeline Outbound(byte? descriptorId)
    {
        var pipeline = new BundledOutboundPipeline(
            new RtpPacketCodec(), new DiscardSender(), NullLogger<BundledOutboundPipeline>.Instance);
        pipeline.RegisterTrack("video", new BundledOutboundTrack(
            VideoSsrc, VideoPayloadType, samplesPerPacket: 0,
            new RtpOutboundHeaderExtensionStamper(
                transportWideCcExtensionId: null, MidExtId, "video",
                dependencyDescriptorExtensionId: descriptorId),
            initialSequenceNumber: 1000, initialTimestamp: RtpTimestamp));
        return pipeline;
    }

    private static SrtpKeyMaterial Material() => new(MasterKey, MasterSalt, SrtpCryptoSuite.AesCm128HmacSha1_80);

    // A VP8 frame: the 1-byte payload descriptor (RFC 7741 §4.2) plus body; the first body byte's low bit is
    // the P flag the clear-media depacketiser reads as "key frame or not".
    private static byte[] Vp8Frame(byte frameByte, int length = 1)
    {
        var frame = new byte[1 + length];
        frame[0] = 0x10;
        frame[1] = frameByte;
        for (var i = 2; i < frame.Length; i++)
            frame[i] = (byte)i;
        return frame;
    }

    private sealed class DiscardSender : IBundledDatagramSender
    {
        public ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
