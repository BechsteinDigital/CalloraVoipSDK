using System.Net;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Building a bundle session for a negotiated call (ADR-011 B5-wire b): the factory generates fresh
/// local SSRCs and includes a video track only when both a video leg and its bundle MID are present,
/// then binds a running <see cref="BundledMediaSession"/>.
/// </summary>
public sealed class BundledMediaSessionFactoryTests
{
    [Fact]
    public async Task Create_with_a_video_leg_and_mid_builds_a_video_bundle()
    {
        await using var session = BundledMediaSessionFactory.Create(
            AudioParams(), VideoParams(), midExtensionId: 3, audioMid: "audio", videoMid: "video",
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance),
            DtlsCertificate.GenerateEcdsaP256(), NullLoggerFactory.Instance);

        Assert.True(session.HasVideo);
        Assert.NotEqual(0, session.LocalEndPoint.Port);
    }

    [Fact]
    public async Task Create_without_a_video_mid_builds_an_audio_only_bundle()
    {
        // A video leg exists but the m-line was not grouped into the bundle (no MID) — audio-only bundle.
        await using var session = BundledMediaSessionFactory.Create(
            AudioParams(), VideoParams(), midExtensionId: 3, audioMid: "audio", videoMid: null,
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance),
            DtlsCertificate.GenerateEcdsaP256(), NullLoggerFactory.Instance);

        Assert.False(session.HasVideo);
    }

    [Fact]
    public async Task Create_without_a_video_leg_builds_an_audio_only_bundle()
    {
        await using var session = BundledMediaSessionFactory.Create(
            AudioParams(), video: null, midExtensionId: 3, audioMid: "audio", videoMid: "video",
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance),
            DtlsCertificate.GenerateEcdsaP256(), NullLoggerFactory.Instance);

        Assert.False(session.HasVideo);
    }

    private static IPEndPoint EndPoint(int port) => new(IPAddress.Loopback, port);

    private static CallMediaParameters AudioParams() => new()
    {
        LocalEndPoint = EndPoint(0), // ephemeral bind
        RemoteEndPoint = EndPoint(6000),
        PayloadType = 0,
        ClockRate = 8000,
        SamplesPerPacket = 160,
        IsDtlsNegotiated = true,
        DtlsIsClient = true,
        DtlsRemoteFingerprintAlgorithm = "sha-256",
        DtlsRemoteFingerprintValue = "AA:BB:CC",
        IceEnabled = true,
        IceControlling = true,
        LocalIceUfrag = "localU",
        LocalIcePwd = "localpassword1234567890",
        RemoteIceUfrag = "remoteU",
        RemoteIcePwd = "remotepassword1234567890",
    };

    private static CallVideoParameters VideoParams() => new()
    {
        PayloadType = 96,
        CodecName = "H264",
        LocalEndPoint = EndPoint(0),
        RemoteEndPoint = EndPoint(6000),
    };
}
