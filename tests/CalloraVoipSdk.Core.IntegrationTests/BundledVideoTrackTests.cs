using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Video as a bundled track (ADR-011 B4): a video m-line rides the shared transport instead of its own
/// RtpSession. The track packetises an encoded frame onto the video MID through the outbound pipeline and
/// reassembles it on the inbound side, reusing the same H.264/VP8 payload format and reorder buffer as
/// the single-stream path — proven by a round trip through the pipelines and over a real shared socket.
/// </summary>
public sealed class BundledVideoTrackTests
{
    private const byte MidExtId = 3;
    private const byte VideoPayloadType = 96;
    private const uint VideoSsrc = 0x0B0B0B0B;
    private const uint RtpTimestamp = 90000;
    private const int ReorderDepth = 32;

    private static readonly byte[] MasterKey = Convert.FromHexString("E1F97A0D3E018BE0D64FA32C06DE4139");
    private static readonly byte[] MasterSalt = Convert.FromHexString("0EC675AD498AFEEBB6960B3AABE6");

    [Fact]
    public async Task A_fragmented_h264_frame_round_trips_through_the_bundle_pipelines()
    {
        // A large IDR fragments into several FU-A packets — exercises multi-packet reassembly + reorder.
        var frame = AnnexB((Nal(0x67, 25), false), (Nal(0x68, 8), false), (Nal(0x65, 4000), false));

        var sent = new List<RtpPacket>();
        var outbound = OutboundOver(new DiscardSender());
        outbound.PacketSent += sent.Add;
        outbound.InstallOutboundKey(new SrtpContext(Material())); // so sends are not fail-closed

        using var sender = VideoTrack(outbound);
        await sender.SendFrameAsync(frame, RtpTimestamp);

        Assert.True(sent.Count >= 3); // the IDR fragmented

        byte[]? received = null;
        using var receiver = VideoTrack(outbound);
        receiver.FrameReceived += (f, _, _) => received = f;
        foreach (var packet in sent)
            receiver.OnRtpPacket(packet);

        Assert.NotNull(received);
        Assert.Equal(
            AnnexBParser.ParseNalUnits(frame).Select(n => n.ToArray()),
            AnnexBParser.ParseNalUnits(received!).Select(n => n.ToArray()));
    }

    [Fact]
    public async Task A_video_frame_flows_over_the_shared_socket_end_to_end()
    {
        var frame = AnnexB((Nal(0x67, 20), false), (Nal(0x68, 6), false), (Nal(0x65, 3000), false));
        var reassembled = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Receiver: bundle transport → inbound pipeline routing the video MID to a receiving video track.
        using var receiverVideo = VideoTrack(OutboundOver(new DiscardSender()));
        receiverVideo.FrameReceived += (f, _, _) => reassembled.TrySetResult(f);
        await using var receiverTransport = Transport(InboundRoutingVideoTo(receiverVideo.OnRtpPacket));
        await receiverTransport.StartAsync();

        // Sender: bundle transport pointed at the receiver, video track sending over its outbound pipeline.
        await using var senderTransport = Transport(InboundRoutingVideoTo(_ => { }));
        senderTransport.SetRemoteEndPoint(receiverTransport.LocalEndPoint);
        var senderOutbound = OutboundOver(senderTransport);
        senderOutbound.InstallOutboundKey(new SrtpContext(Material()));
        using var senderVideo = VideoTrack(senderOutbound);

        await senderVideo.SendFrameAsync(frame, RtpTimestamp);

        var received = await reassembled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(
            AnnexBParser.ParseNalUnits(frame).Select(n => n.ToArray()),
            AnnexBParser.ParseNalUnits(received).Select(n => n.ToArray()));
    }

    // ── harness ──────────────────────────────────────────────────────────────────

    private static BundledVideoTrack VideoTrack(BundledOutboundPipeline outbound) =>
        new("video", "H264", VideoPayloadType, outbound, ReorderDepth, NullLogger<BundledVideoTrack>.Instance);

    // Outbound pipeline over the given send seam, with the video BundledOutboundTrack registered.
    private static BundledOutboundPipeline OutboundOver(IBundledDatagramSender sender)
    {
        var pipeline = new BundledOutboundPipeline(new RtpPacketCodec(), sender, NullLogger<BundledOutboundPipeline>.Instance);
        pipeline.RegisterTrack("video", new BundledOutboundTrack(
            VideoSsrc, VideoPayloadType, samplesPerPacket: 0,
            new RtpOutboundHeaderExtensionStamper(transportWideCcExtensionId: null, MidExtId, "video"),
            initialSequenceNumber: 1000, initialTimestamp: RtpTimestamp));
        return pipeline;
    }

    private static BundledInboundPipeline InboundRoutingVideoTo(Action<RtpPacket> videoSink)
    {
        var demux = BundledRtpDemultiplexerFactory.Create(
            MidExtId, new Dictionary<string, IReadOnlyCollection<int>> { ["video"] = new[] { (int)VideoPayloadType } });
        var router = new BundledTrackRouter(demux);
        router.RegisterTrack("video", videoSink);
        var pipeline = new BundledInboundPipeline(router, new RtpPacketCodec(), NullLogger<BundledInboundPipeline>.Instance);
        pipeline.InstallInboundKeys(new SrtpContext(Material()), new SrtcpContext(Material()));
        return pipeline;
    }

    private static BundledMediaTransport Transport(BundledInboundPipeline inbound) =>
        new(new BundledMediaTransportOptions { LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0) },
            inbound, NullLogger<BundledMediaTransport>.Instance);

    private static SrtpKeyMaterial Material() =>
        new() { MasterKey = MasterKey, MasterSalt = MasterSalt, Suite = SrtpCryptoSuite.AesCm128HmacSha1_80 };

    private static byte[] Nal(byte header, int bodyLength)
    {
        var nal = new byte[1 + bodyLength];
        nal[0] = header;
        for (var i = 1; i < nal.Length; i++)
            nal[i] = (byte)(1 + (i % 250)); // never 0x00 — keeps Annex-B parsing unambiguous
        return nal;
    }

    private static byte[] AnnexB(params (byte[] Nal, bool LongStartCode)[] nals)
    {
        var stream = new MemoryStream();
        foreach (var (nal, longStartCode) in nals)
        {
            stream.Write(longStartCode ? new byte[] { 0, 0, 0, 1 } : new byte[] { 0, 0, 1 });
            stream.Write(nal);
        }

        return stream.ToArray();
    }

    private sealed class DiscardSender : IBundledDatagramSender
    {
        public ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
