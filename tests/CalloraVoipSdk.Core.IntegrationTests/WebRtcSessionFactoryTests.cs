using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Deriving the shared bundle transport for a WebRTC peer from the negotiated descriptions (Weg 1 media
/// wiring): the peer's own answer plus the remote offer yield the endpoints, DTLS role/fingerprint, ICE
/// credentials, payload types, and BUNDLE MID facts — no SIP CallMediaParameters/enrichers involved.
/// </summary>
public sealed class WebRtcSessionFactoryTests
{
    private static readonly IReadOnlyList<SdpCodecDefinition> Pcmu =
        [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }];

    [Fact]
    public async Task It_builds_a_bundle_session_with_video_from_a_webrtc_exchange()
    {
        var (offer, answer) = Exchange(withVideo: true);

        var session = WebRtcSessionFactory.TryCreate(
            offer, answer, PeerOptions(), Handshaker(), DtlsCertificate.GenerateEcdsaP256(), NullLoggerFactory.Instance);

        Assert.NotNull(session);
        await using var lease = session;
        Assert.True(session!.HasVideo);
        Assert.NotEqual(0, session.LocalEndPoint.Port); // the shared socket bound
    }

    [Fact]
    public async Task It_builds_an_audio_only_bundle_when_the_answer_has_no_video()
    {
        var (offer, answer) = Exchange(withVideo: false);

        var session = WebRtcSessionFactory.TryCreate(
            offer, answer, PeerOptions(), Handshaker(), DtlsCertificate.GenerateEcdsaP256(), NullLoggerFactory.Instance);

        Assert.NotNull(session);
        await using var lease = session;
        Assert.False(session!.HasVideo);
    }

    [Fact]
    public void A_non_bundle_exchange_yields_no_session()
    {
        var negotiator = new SdpOfferAnswerNegotiator();
        var offer = negotiator.CreateOffer(
            new IPEndPoint(IPAddress.Loopback, 5000), Pcmu, SdpMediaDirection.SendRecv,
            new SdpMediaOptions { RtcpMux = true, Dtls = OfferDtls(), Ice = OfferIce() }); // no Bundle
        var answer = negotiator.NegotiateAnswer(
            offer, new IPEndPoint(IPAddress.Loopback, 6000), Pcmu, SdpMediaDirection.SendRecv,
            new SdpMediaOptions { RtcpMux = true, Dtls = AnswerDtls(), Ice = AnswerIce() }).Answer!;

        Assert.Null(WebRtcSessionFactory.TryCreate(
            offer, answer, PeerOptions(), Handshaker(), DtlsCertificate.GenerateEcdsaP256(), NullLoggerFactory.Instance));
    }

    // ── harness ──────────────────────────────────────────────────────────────────

    private static (SdpSessionDescription Offer, SdpSessionDescription Answer) Exchange(bool withVideo)
    {
        var negotiator = new SdpOfferAnswerNegotiator();
        SdpVideoMediaOptions? video = withVideo
            ? new SdpVideoMediaOptions { Port = 5002, Codecs = [new SdpCodecDefinition { PayloadType = 96, Name = "H264", ClockRate = 90000 }] }
            : null;

        var offer = negotiator.CreateOffer(
            new IPEndPoint(IPAddress.Loopback, 5000), Pcmu, SdpMediaDirection.SendRecv,
            new SdpMediaOptions { Bundle = true, RtcpMux = true, Dtls = OfferDtls(), Ice = OfferIce(), Video = video });

        var answer = negotiator.NegotiateAnswer(
            offer, new IPEndPoint(IPAddress.Loopback, 6000), Pcmu, SdpMediaDirection.SendRecv,
            new SdpMediaOptions { Bundle = true, RtcpMux = true, Dtls = AnswerDtls(), Ice = AnswerIce(), Video = video }).Answer!;

        return (offer, answer);
    }

    private static WebRtcPeerOptions PeerOptions() => new()
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        AudioCodecs = Pcmu,
        Dtls = AnswerDtls(),
        Ice = AnswerIce(),
    };

    private static IDtlsSrtpHandshaker Handshaker() => new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance);

    private static SdpDtlsParameters OfferDtls() => new() { Algorithm = "sha-256", Fingerprint = "AA:BB:CC", Setup = "actpass" };
    private static SdpDtlsParameters AnswerDtls() => new() { Algorithm = "sha-256", Fingerprint = "11:22:33" };
    private static SdpIceParameters OfferIce() => new() { Ufrag = "remoteU", Pwd = "remotepassword1234567890" };
    private static SdpIceParameters AnswerIce() => new() { Ufrag = "localU", Pwd = "localpassword1234567890" };
}
