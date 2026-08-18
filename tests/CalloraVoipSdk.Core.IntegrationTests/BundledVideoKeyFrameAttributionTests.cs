using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Key-frame requests carry which outbound stream the peer asked about (#227). Routing a PLI to the right
/// <em>track</em> was already in place; what was missing is that the track then told nobody <em>which</em> of
/// its streams was named, so a forwarding layer had no way to ask one upstream source rather than all of them.
/// </summary>
/// <remarks>
/// The distinction matters most where it costs most: in a room of three or more, an unattributed request means
/// a key frame from every sender, and it arrives exactly when the link is already struggling — that is what
/// made the receiver ask in the first place.
/// </remarks>
public sealed class BundledVideoKeyFrameAttributionTests
{
    private const byte MidExtId = 3;
    private const byte RidExtId = 4;
    private const byte VideoPayloadType = 96;
    private const uint PrimarySsrc = 0x0A0A0A0A;
    private const uint HighLayerSsrc = 0x0C0C0C01;
    private const uint LowLayerSsrc = 0x0C0C0C02;
    private const uint OtherTrackSsrc = 0x0B0B0B0B;
    private const uint RemoteSsrc = 0x0D0D0D0D;

    [Fact]
    public void A_pli_names_the_stream_it_asked_about()
    {
        using var track = Track(Pipeline(), "video1", PrimarySsrc);
        var attributed = new List<VideoKeyFrameRequest>();
        track.KeyFrameRequestedForStream += attributed.Add;

        track.OnRtcpPackets([Pli(PrimarySsrc)]);

        var request = Assert.Single(attributed);
        Assert.Equal(PrimarySsrc, request.MediaSsrc);
        Assert.Null(request.Rid);   // this m-line is not simulcasting
    }

    [Fact]
    public void A_pli_for_a_simulcast_layer_names_that_layer()
    {
        // The reason the rid matters: asking a simulcast sender for "a key frame" without saying which layer
        // means all of them, which is the same waste one level down from the all-senders case.
        var pipeline = Pipeline();
        pipeline.RegisterTrack("video1", "hi", Encoding(HighLayerSsrc, "video1", "hi"));
        pipeline.RegisterTrack("video1", "lo", Encoding(LowLayerSsrc, "video1", "lo"));
        using var track = Track(pipeline, "video1", PrimarySsrc);

        var attributed = new List<VideoKeyFrameRequest>();
        track.KeyFrameRequestedForStream += attributed.Add;

        track.OnRtcpPackets([Pli(LowLayerSsrc)]);

        var request = Assert.Single(attributed);
        Assert.Equal(LowLayerSsrc, request.MediaSsrc);
        Assert.Equal("lo", request.Rid);
    }

    [Fact]
    public void A_fir_names_the_stream_its_entry_targets()
    {
        // FIR carries its targets in the FCI entries rather than the header (RFC 5104 §4.3.1), so the
        // attribution has to come from there — reading the header would report 0 for every FIR.
        using var track = Track(Pipeline(), "video1", PrimarySsrc);
        var attributed = new List<VideoKeyFrameRequest>();
        track.KeyFrameRequestedForStream += attributed.Add;

        track.OnRtcpPackets([new RtcpFullIntraRequest
        {
            SenderSsrc = RemoteSsrc,
            Entries = [new RtcpFirEntry { MediaSsrc = PrimarySsrc, SequenceNumber = 1 }],
        }]);

        Assert.Equal(PrimarySsrc, Assert.Single(attributed).MediaSsrc);
    }

    [Fact]
    public void A_request_for_another_track_attributes_nothing_here()
    {
        // The acceptance criterion, now observable rather than merely true: a PLI for source B produces no
        // request — attributed or bare — on source A.
        var pipeline = Pipeline();
        pipeline.RegisterTrack("video2", Encoding(OtherTrackSsrc, "video2", rid: null));
        using var track = Track(pipeline, "video1", PrimarySsrc);

        var attributed = new List<VideoKeyFrameRequest>();
        var bare = 0;
        track.KeyFrameRequestedForStream += attributed.Add;
        track.KeyFrameRequested += () => bare++;

        track.OnRtcpPackets([Pli(OtherTrackSsrc)]);

        Assert.Empty(attributed);
        Assert.Equal(0, bare);
    }

    [Fact]
    public void The_bare_event_still_fires_for_a_single_track_consumer()
    {
        // The mid-less event is unchanged: a one-track consumer that never cared which stream was named keeps
        // working without touching its code.
        using var track = Track(Pipeline(), "video1", PrimarySsrc);
        var bare = 0;
        track.KeyFrameRequested += () => bare++;

        track.OnRtcpPackets([Pli(PrimarySsrc)]);

        Assert.Equal(1, bare);
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private static RtcpPictureLossIndication Pli(uint mediaSsrc) =>
        new() { SenderSsrc = RemoteSsrc, MediaSsrc = mediaSsrc };

    private static BundledOutboundTrack Encoding(uint ssrc, string mid, string? rid) =>
        new(ssrc, VideoPayloadType, samplesPerPacket: 0,
            new RtpOutboundHeaderExtensionStamper(transportWideCcExtensionId: null, MidExtId, mid, RidExtId, rid),
            initialSequenceNumber: 1000, initialTimestamp: 90000);

    private static BundledOutboundPipeline Pipeline()
    {
        var pipeline = new BundledOutboundPipeline(
            new RtpPacketCodec(), new NoopSender(), NullLogger<BundledOutboundPipeline>.Instance);
        pipeline.RegisterTrack("video1", Encoding(PrimarySsrc, "video1", rid: null));
        pipeline.InstallOutboundKey(new IdentitySrtp());
        pipeline.InstallOutboundRtcpKey(new IdentitySrtcp());
        return pipeline;
    }

    private static BundledVideoTrack Track(BundledOutboundPipeline pipeline, string mid, uint ssrc) =>
        new(mid, "VP8", VideoPayloadType, ssrc, remoteSupportsNack: true, remoteSupportsPli: true,
            pipeline, reorderWindowDepth: 32, NullLoggerFactory.Instance);

    private sealed class NoopSender : IBundledDatagramSender
    {
        public ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private sealed class IdentitySrtp : ISrtpContext
    {
        public byte[] Protect(ReadOnlySpan<byte> rtpPacket) => rtpPacket.ToArray();
        public byte[] Unprotect(ReadOnlySpan<byte> srtpPacket) => srtpPacket.ToArray();
        public void Dispose() { }
    }

    private sealed class IdentitySrtcp : ISrtcpContext
    {
        public byte[] ProtectRtcp(ReadOnlySpan<byte> rtcpPacket) => rtcpPacket.ToArray();
        public byte[] UnprotectRtcp(ReadOnlySpan<byte> srtcpPacket) => srtcpPacket.ToArray();
        public void Dispose() { }
    }
}
