using System.Collections.Concurrent;
using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Live track mutation is transactional and keeps the metric attribution in step (#161 P2-11). Building a
/// track used to register its outbound sender(s) first and construct the track afterwards, so a rejected
/// config left a sender on a MID that has no track — claiming the MID and its SSRCs, and failing every
/// corrected retry with "already registered". The attribution maps (inbound clock per payload type, outbound
/// SSRC → MID/kind) were construction-time snapshots that no live add ever extended.
/// </summary>
public sealed class BundledTrackMutationTransactionTests
{
    private const byte MidExtId = 3;
    private const byte VideoPayloadType = 96;

    private static BundledMediaSessionOptions Options() => new()
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 40000),
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 40001),
        MidExtensionId = MidExtId,
        Audio = new BundledTrackConfig { Mid = "audio", Ssrc = 0x0A0A0A0A, PayloadType = 0, ClockRate = 8000, SamplesPerPacket = 160 },
        VideoTracks = [],
        VideoReorderDepth = 32,
        RidExtensionId = 4,
        DtlsIsClient = true,
        RemoteFingerprint = DtlsCertificate.GenerateEcdsaP256().Fingerprint,
        Ice = new IceMediaParameters(
            new IPEndPoint(IPAddress.Loopback, 40001), IceEnabled: false, IceControlling: true,
            LocalIceUfrag: null, LocalIcePwd: null, RemoteIceUfrag: null, RemoteIcePwd: null),
    };

    private static BundledOutboundPipeline Pipeline() =>
        new(new RtpPacketCodec(), new DiscardingSender(), NullLogger<BundledOutboundPipeline>.Instance);

    [Fact]
    public void A_rejected_video_config_registers_nothing_so_a_corrected_retry_succeeds()
    {
        var pipeline = Pipeline();
        var rejected = new BundledTrackConfig
        {
            Mid = "vid2", Ssrc = 0x0B0B0B0B, PayloadType = VideoPayloadType, VideoCodecName = "AV1",
        };

        // The codec has no RTP payload format, so the track constructor rejects it.
        Assert.Throws<InvalidOperationException>(
            () => BundledMediaSessionComposition.BuildVideoTrack(Options(), rejected, pipeline, NullLoggerFactory.Instance));

        // Nothing was left behind: the same MID builds cleanly once the codec is corrected.
        using var track = BundledMediaSessionComposition.BuildVideoTrack(
            Options(), rejected with { VideoCodecName = "H264" }, pipeline, NullLoggerFactory.Instance);

        Assert.True(pipeline.UnregisterTrack("vid2", null)); // the corrected build did register its sender
    }

    [Fact]
    public void A_rejected_simulcast_config_leaves_no_half_registered_layer()
    {
        var pipeline = Pipeline();
        var rejected = new BundledTrackConfig
        {
            Mid = "vid2", Ssrc = 0x0B0B0B0B, PayloadType = VideoPayloadType, VideoCodecName = "H264",
            Encodings =
            [
                new BundledVideoEncoding { Rid = "h", Ssrc = 0x0B0B0B0C },
                new BundledVideoEncoding { Rid = "h", Ssrc = 0x0B0B0B0D }, // duplicate rid
            ],
        };

        Assert.Throws<ArgumentException>(
            () => BundledMediaSessionComposition.BuildVideoTrack(Options(), rejected, pipeline, NullLoggerFactory.Instance));

        // The first layer must not survive the failure — otherwise the retry below hits "already registered".
        Assert.False(pipeline.UnregisterTrack("vid2", "h"));

        using var track = BundledMediaSessionComposition.BuildVideoTrack(
            Options(),
            rejected with
            {
                Encodings =
                [
                    new BundledVideoEncoding { Rid = "h", Ssrc = 0x0B0B0B0C },
                    new BundledVideoEncoding { Rid = "l", Ssrc = 0x0B0B0B0D },
                ],
            },
            pipeline,
            NullLoggerFactory.Instance);

        Assert.True(pipeline.UnregisterTrack("vid2", "h"));
        Assert.True(pipeline.UnregisterTrack("vid2", "l"));
    }

    [Fact]
    public void Outbound_identity_follows_a_track_being_added_and_deactivated()
    {
        var map = BundledMediaSessionComposition.BuildOutboundStreamIdentity(Options());
        var attribution = new BundledStreamAttribution(map, new BundledInboundReceptionStats());
        Assert.Equal("audio", map[0x0A0A0A0A].Mid);

        var video = new BundledTrackConfig
        {
            Mid = "vid2", Ssrc = 0x0B0B0B0B, PayloadType = VideoPayloadType, VideoCodecName = "H264",
            Encodings =
            [
                new BundledVideoEncoding { Rid = "h", Ssrc = 0x0B0B0B0C },
                new BundledVideoEncoding { Rid = "l", Ssrc = 0x0B0B0B0D },
            ],
        };

        attribution.TrackAdded(video, BundledStreamKind.Video, clockRate: 90000);

        // Every simulcast encoding is attributed to the MID, not just the primary SSRC.
        Assert.Equal(new BundledOutboundStreamIdentity("vid2", BundledStreamKind.Video), map[0x0B0B0B0C]);
        Assert.Equal(new BundledOutboundStreamIdentity("vid2", BundledStreamKind.Video), map[0x0B0B0B0D]);

        attribution.TrackRemoved("vid2");

        Assert.False(map.ContainsKey(0x0B0B0B0C));
        Assert.False(map.ContainsKey(0x0B0B0B0D));
        Assert.True(map.ContainsKey(0x0A0A0A0A)); // the other track is untouched
    }

    [Fact]
    public void An_additional_audio_track_from_the_options_is_attributed_too()
    {
        var options = Options() with
        {
            AdditionalAudioTracks =
            [
                new BundledTrackConfig { Mid = "audio2", Ssrc = 0x0A0A0A0B, PayloadType = 8, ClockRate = 8000 },
            ],
        };

        var map = BundledMediaSessionComposition.BuildOutboundStreamIdentity(options);

        Assert.Equal(new BundledOutboundStreamIdentity("audio2", BundledStreamKind.Audio), map[0x0A0A0A0B]);
    }

    [Fact]
    public void A_payload_type_registered_mid_call_attributes_the_sources_that_follow()
    {
        var stats = new BundledInboundReceptionStats(
            clockByPayloadType: new Dictionary<byte, BundledInboundClockDescriptor>
            {
                [0] = new BundledInboundClockDescriptor(8000, BundledStreamKind.Audio, "audio"),
            });

        // Before the registration a source on the new payload type has no MID to attribute to.
        stats.RecordRtp(0x1111, 1, 0, payloadType: 97);
        stats.RecordRtp(0x1111, 2, 960, payloadType: 97);
        Assert.All(stats.SnapshotJitterMsPerSsrc(), j => Assert.Null(j.Mid));

        Assert.True(stats.TryRegisterInboundClock(
            97, new BundledInboundClockDescriptor(90000, BundledStreamKind.Video, "vid2")));

        stats.RecordRtp(0x2222, 1, 0, payloadType: 97);
        stats.RecordRtp(0x2222, 2, 3000, payloadType: 97);

        var added = Assert.Single(stats.SnapshotJitterMsPerSsrc().Where(j => j.Ssrc == 0x2222));
        Assert.Equal("vid2", added.Mid);
        Assert.Equal(BundledStreamKind.Video, added.Kind);

        // First registration wins: a later track sharing the payload type does not re-point it.
        Assert.False(stats.TryRegisterInboundClock(
            97, new BundledInboundClockDescriptor(90000, BundledStreamKind.Video, "vid3")));
    }

    private sealed class DiscardingSender : IBundledDatagramSender
    {
        public ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }
}
