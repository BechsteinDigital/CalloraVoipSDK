using System.Net;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Mapping the negotiated call parameters onto a bundled session (ADR-011 B5-wire): the audio leg's
/// endpoints, DTLS role/fingerprint, and ICE credentials key and steer the shared transport, the audio
/// and video legs contribute their payload types and codec, and the BUNDLE-specific facts (MID tokens,
/// the MID header-extension id, the SSRCs) are supplied by the signalling layer.
/// </summary>
public sealed class BundledMediaSessionBuilderTests
{
    [Fact]
    public void BuildOptions_maps_the_negotiated_audio_and_video_legs()
    {
        var audio = AudioParams(local: 5000, remote: 6000, isClient: true,
            fingerprintAlgorithm: "sha-256", fingerprintValue: "AA:BB:CC");
        var video = new CallVideoParameters
        {
            PayloadType = 96,
            CodecName = "H264",
            LocalEndPoint = EndPoint(5000),
            RemoteEndPoint = EndPoint(6000),
        };

        var options = BundledMediaSessionBuilder.BuildOptions(
            audio, video, midExtensionId: 4, audioMid: "audio", audioSsrc: 0x11111111,
            videoMid: "video", videoSsrc: 0x22222222);

        Assert.Equal(EndPoint(5000), options.LocalEndPoint);
        Assert.Equal(EndPoint(6000), options.RemoteEndPoint);
        Assert.Equal(4, options.MidExtensionId);

        Assert.Equal("audio", options.Audio.Mid);
        Assert.Equal(0x11111111u, options.Audio.Ssrc);
        Assert.Equal(0, (int)options.Audio.PayloadType);
        Assert.Equal(160, options.Audio.SamplesPerPacket);
        Assert.Null(options.Audio.VideoCodecName);

        Assert.NotNull(options.Video);
        Assert.Equal("video", options.Video!.Mid);
        Assert.Equal(0x22222222u, options.Video.Ssrc);
        Assert.Equal(96, (int)options.Video.PayloadType);
        Assert.Equal("H264", options.Video.VideoCodecName);

        Assert.True(options.DtlsIsClient);
        Assert.Equal("sha-256", options.RemoteFingerprint.Algorithm);
        Assert.Equal("AA:BB:CC", options.RemoteFingerprint.Value);

        Assert.True(options.Ice.IceEnabled);
        Assert.True(options.Ice.IceControlling);
        Assert.Equal("localU", options.Ice.LocalIceUfrag);
        Assert.Equal("remoteU", options.Ice.RemoteIceUfrag);
    }

    [Fact]
    public void BuildOptions_without_video_makes_an_audio_only_bundle()
    {
        var options = BundledMediaSessionBuilder.BuildOptions(
            AudioParams(5000, 6000, isClient: false, "sha-256", "DD:EE"), video: null,
            midExtensionId: 1, audioMid: "audio", audioSsrc: 1, videoMid: null, videoSsrc: null);

        Assert.Null(options.Video);
        Assert.False(options.DtlsIsClient);
    }

    [Fact]
    public void A_leg_that_did_not_negotiate_dtls_is_rejected()
    {
        var audio = AudioParams(5000, 6000, isClient: true, "sha-256", "AA", dtlsNegotiated: false);
        Assert.Throws<InvalidOperationException>(() => BundledMediaSessionBuilder.BuildOptions(
            audio, null, 1, "audio", 1, null, null));
    }

    [Fact]
    public void A_leg_without_a_peer_fingerprint_is_rejected()
    {
        var audio = AudioParams(5000, 6000, isClient: true, fingerprintAlgorithm: null, fingerprintValue: null);
        Assert.Throws<InvalidOperationException>(() => BundledMediaSessionBuilder.BuildOptions(
            audio, null, 1, "audio", 1, null, null));
    }

    [Fact]
    public void A_video_leg_without_a_mid_is_rejected()
    {
        var audio = AudioParams(5000, 6000, isClient: true, "sha-256", "AA");
        var video = new CallVideoParameters
        {
            PayloadType = 96, CodecName = "VP8", LocalEndPoint = EndPoint(5000), RemoteEndPoint = EndPoint(6000),
        };
        Assert.ThrowsAny<ArgumentException>(() => BundledMediaSessionBuilder.BuildOptions(
            audio, video, 1, "audio", 1, videoMid: null, videoSsrc: 5));
    }

    [Fact]
    public async Task Build_constructs_and_binds_a_running_session()
    {
        var audio = AudioParams(local: 0, remote: 6000, isClient: true, "sha-256", "AA:BB");
        var video = new CallVideoParameters
        {
            PayloadType = 96, CodecName = "H264", LocalEndPoint = EndPoint(0), RemoteEndPoint = EndPoint(6000),
        };

        await using var session = BundledMediaSessionBuilder.Build(
            audio, video, midExtensionId: 3, audioMid: "audio", audioSsrc: 1, videoMid: "video", videoSsrc: 2,
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance),
            DtlsCertificate.GenerateEcdsaP256(), NullLoggerFactory.Instance);

        Assert.True(session.HasVideo);
        Assert.NotEqual(0, session.LocalEndPoint.Port); // the shared socket bound
    }

    private static IPEndPoint EndPoint(int port) => new(IPAddress.Loopback, port);

    private static CallMediaParameters AudioParams(
        int local, int remote, bool isClient, string? fingerprintAlgorithm, string? fingerprintValue,
        bool dtlsNegotiated = true) => new()
    {
        LocalEndPoint = EndPoint(local),
        RemoteEndPoint = EndPoint(remote),
        PayloadType = 0, // PCMU
        ClockRate = 8000,
        SamplesPerPacket = 160,
        IsDtlsNegotiated = dtlsNegotiated,
        DtlsIsClient = isClient,
        DtlsRemoteFingerprintAlgorithm = fingerprintAlgorithm,
        DtlsRemoteFingerprintValue = fingerprintValue,
        IceEnabled = true,
        IceControlling = true,
        LocalIceUfrag = "localU",
        LocalIcePwd = "localpassword1234567890",
        RemoteIceUfrag = "remoteU",
        RemoteIcePwd = "remotepassword1234567890",
    };
}
