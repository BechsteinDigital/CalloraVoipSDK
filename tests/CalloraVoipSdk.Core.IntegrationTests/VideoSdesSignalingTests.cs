using System.Net;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Domain.Security;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// SDES video signalling to media parameters (RFC 4568): a peer's SDES-keyed video offer is
/// answered with our own video crypto, and the per-m-line key material is recovered onto the
/// video parameters by the SRTP enricher — the peer's key for inbound, our answer key for
/// outbound — the same recovery the audio path uses. This closes the SDP half of SDES video.
/// </summary>
public sealed class VideoSdesSignalingTests
{
    private static readonly IPEndPoint LocalAudio = new(IPAddress.Loopback, 41000);

    private static SdpVideoNegotiationOptions VideoOptions() => new() { Port = 41002 };

    [Fact]
    public void Sdes_video_offer_answered_and_keyed_onto_video_parameters()
    {
        var offeredKey = "inline:" + Convert.ToBase64String(new byte[30]);
        var offer =
            "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
            + "m=audio 5002 RTP/SAVP 0\r\na=rtpmap:0 PCMU/8000\r\n"
            + $"a=crypto:1 AES_CM_128_HMAC_SHA1_80 {offeredKey}\r\n"
            + "m=video 5004 RTP/SAVP 96\r\na=rtpmap:96 VP8/90000\r\n"
            + $"a=crypto:1 AES_CM_128_HMAC_SHA1_80 {offeredKey}\r\n";

        var options = new SdpMediaNegotiationOptions { Video = VideoOptions() };

        // Our answer carries a freshly generated video crypto (our outbound key).
        var answer = SdpUtilities.TryBuildNegotiatedAnswer(offer, LocalAudio, hold: false, options);
        Assert.NotNull(answer);

        // Parameters resolve the video m-line; the enricher then recovers the SDES keys from the
        // offer (remote/inbound) and our answer (local/outbound).
        var parameters = SdpUtilities.TryParseMediaParameters(offer, LocalAudio, options);
        Assert.NotNull(parameters);
        Assert.NotNull(parameters!.Video);

        var enriched = CallMediaParametersSrtpEnricher.Enrich(
            parameters, reasonCode: "test", remoteSdp: offer, localSdp: answer, appliedPolicy: SrtpPolicy.Required);

        Assert.NotNull(enriched.Video);
        Assert.Equal("AES_CM_128_HMAC_SHA1_80", enriched.Video!.SrtpSuite);
        // Remote (inbound decrypt) key is the peer's offered key.
        Assert.Equal(offeredKey, enriched.Video.SrtpRemoteKeyParams);
        // Local (outbound encrypt) key is our own answer key — present, inline, and distinct.
        Assert.NotNull(enriched.Video.SrtpLocalKeyParams);
        Assert.StartsWith("inline:", enriched.Video.SrtpLocalKeyParams!);
        Assert.NotEqual(offeredKey, enriched.Video.SrtpLocalKeyParams);
    }

    [Fact]
    public void Outbound_sdes_video_offer_keys_parameters_from_our_offer_and_peer_answer()
    {
        // Our locally originated offer carries its own video a=crypto (our outbound key).
        var ourOffer = SdpUtilities.BuildDefaultSdp(LocalAudio, hold: false,
            new SdpMediaNegotiationOptions { Video = VideoOptions(), OfferSrtpCrypto = true });
        var ourVideoKey = SdpUtilities.TryExtractVideoCrypto(ourOffer)?.KeyParams;
        Assert.NotNull(ourVideoKey);

        // The peer answers with its own video crypto (our inbound key).
        var peerVideoKey = "inline:" + Convert.ToBase64String(new byte[30]);
        var peerAnswer =
            "v=0\r\no=- 2 2 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
            + "m=audio 6002 RTP/SAVP 0\r\na=rtpmap:0 PCMU/8000\r\n"
            + $"a=crypto:1 AES_CM_128_HMAC_SHA1_80 inline:{Convert.ToBase64String(new byte[30])}\r\n"
            + "m=video 6004 RTP/SAVP 96\r\na=rtpmap:96 VP8/90000\r\n"
            + $"a=crypto:1 AES_CM_128_HMAC_SHA1_80 {peerVideoKey}\r\n";

        var parameters = SdpUtilities.TryParseMediaParameters(
            peerAnswer, LocalAudio, new SdpMediaNegotiationOptions { Video = VideoOptions() });
        Assert.NotNull(parameters!.Video);

        var enriched = CallMediaParametersSrtpEnricher.Enrich(
            parameters, reasonCode: "test", remoteSdp: peerAnswer, localSdp: ourOffer, appliedPolicy: SrtpPolicy.Required);

        Assert.NotNull(enriched.Video);
        Assert.Equal("AES_CM_128_HMAC_SHA1_80", enriched.Video!.SrtpSuite);
        Assert.Equal(ourVideoKey, enriched.Video.SrtpLocalKeyParams);   // our offer key = outbound
        Assert.Equal(peerVideoKey, enriched.Video.SrtpRemoteKeyParams); // peer answer key = inbound
    }

    [Fact]
    public void Plain_video_offer_leaves_video_parameters_unkeyed()
    {
        var offer =
            "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
            + "m=audio 5002 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=sendrecv\r\n"
            + "m=video 5004 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\n";

        var options = new SdpMediaNegotiationOptions { Video = VideoOptions() };
        var answer = SdpUtilities.TryBuildNegotiatedAnswer(offer, LocalAudio, hold: false, options);
        var parameters = SdpUtilities.TryParseMediaParameters(offer, LocalAudio, options);
        Assert.NotNull(parameters!.Video);

        var enriched = CallMediaParametersSrtpEnricher.Enrich(
            parameters, reasonCode: "test", remoteSdp: offer, localSdp: answer, appliedPolicy: SrtpPolicy.Optional);

        Assert.NotNull(enriched.Video);
        Assert.Null(enriched.Video!.SrtpSuite);
        Assert.Null(enriched.Video.SrtpLocalKeyParams);
        Assert.Null(enriched.Video.SrtpRemoteKeyParams);
    }
}
