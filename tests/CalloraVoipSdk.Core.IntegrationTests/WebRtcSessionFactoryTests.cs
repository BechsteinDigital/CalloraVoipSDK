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
    public async Task It_builds_additional_inbound_audio_tracks_from_a_multi_audio_exchange()
    {
        // 4.7.0 N-audio (the SFU pattern): a WebRTC exchange with three send-recv audio m-lines under one BUNDLE.
        // The factory keeps the FIRST audio m-line as the primary transport anchor and builds a receive-only sink
        // for each further audio m-line — so a 3-audio exchange yields exactly two additional audio tracks, keyed
        // by their MIDs, on the one shared transport.
        var (offer, answer) = MultiAudioExchange("0", "1", "2");

        // TryCreate takes (remoteDescription, localDescription): from the answerer's view the offer is remote.
        var session = WebRtcSessionFactory.TryCreate(
            offer, answer, PeerOptions(), Handshaker(), DtlsCertificate.GenerateEcdsaP256(), NullLoggerFactory.Instance);

        Assert.NotNull(session);
        await using var lease = session;
        Assert.True(session!.HasAdditionalAudio);
        Assert.Equal(2, session.AdditionalAudioTrackCount);
        Assert.Equal(new[] { "1", "2" }, session.AdditionalAudioMids); // "0" is the primary anchor, excluded
    }

    [Fact]
    public async Task It_builds_an_additional_outbound_audio_track_for_a_sendonly_offer()
    {
        // SFU offerer shape: primary audio stays send-recv while the per-participant additional audio m-line
        // sends from the server only. The browser answers recv-only, so the offerer's bundle must still
        // materialise MID "1" as an outbound sender.
        var (offer, answer) = MultiAudioExchange(
            SdpMediaDirection.SendOnly,
            "0",
            "1");

        // TryCreate takes (remoteDescription, localDescription): from the offerer's view the answer is remote.
        var session = WebRtcSessionFactory.TryCreate(
            answer, offer, PeerOptions(), Handshaker(), DtlsCertificate.GenerateEcdsaP256(), NullLoggerFactory.Instance);

        Assert.NotNull(session);
        await using var lease = session;
        Assert.Equal(["1"], session!.AdditionalAudioMids);
    }

    [Fact]
    public async Task A_single_audio_exchange_builds_no_additional_audio_tracks()
    {
        // Byte-identity guard: a one-audio exchange (the pre-4.7.0 shape) yields the primary anchor only — no
        // additional inbound audio tracks and an empty additional-audio MID list.
        var (offer, answer) = Exchange(withVideo: false);

        var session = WebRtcSessionFactory.TryCreate(
            offer, answer, PeerOptions(), Handshaker(), DtlsCertificate.GenerateEcdsaP256(), NullLoggerFactory.Instance);

        Assert.NotNull(session);
        await using var lease = session;
        Assert.False(session!.HasAdditionalAudio);
        Assert.Equal(0, session.AdditionalAudioTrackCount);
        Assert.Empty(session.AdditionalAudioMids);
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

    // A multi-audio BUNDLE exchange (4.7.0): N send-recv audio m-lines with ascending numeric MIDs on offer and
    // answer, one shared transport. Both sides send-recv, so every additional audio m-line is negotiated for
    // receiving and the factory builds a sink for each beyond the primary.
    private static (SdpSessionDescription Offer, SdpSessionDescription Answer) MultiAudioExchange(params string[] streamIds) =>
        MultiAudioExchange(SdpMediaDirection.SendRecv, streamIds);

    private static (SdpSessionDescription Offer, SdpSessionDescription Answer) MultiAudioExchange(
        SdpMediaDirection additionalDirection,
        params string[] streamIds)
    {
        var negotiator = new SdpOfferAnswerNegotiator();
        var tracks = streamIds
            .Select((sid, index) => new SdpTrackOptions
            {
                Kind = "audio",
                Codecs = Pcmu,
                Direction = index == 0 ? SdpMediaDirection.SendRecv : additionalDirection,
                Msid = new SdpMsid { StreamId = sid, TrackId = sid + "-a" },
            })
            .ToArray();

        var offer = negotiator.CreateOffer(
            new IPEndPoint(IPAddress.Loopback, 5000), Pcmu, SdpMediaDirection.SendRecv,
            new SdpMediaOptions { Bundle = true, RtcpMux = true, Dtls = OfferDtls(), Ice = OfferIce(), Tracks = tracks });

        var answer = negotiator.NegotiateAnswer(
            offer, new IPEndPoint(IPAddress.Loopback, 6000), Pcmu, SdpMediaDirection.SendRecv,
            new SdpMediaOptions { Bundle = true, RtcpMux = true, Dtls = AnswerDtls(), Ice = AnswerIce() }).Answer!;

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
