using System.Net;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Security;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Video-m-line ICE across SDP negotiation and enrichment (RFC 8839 §5.3/§5.4). This SDK shares
/// one ICE credential set across m-lines (no BUNDLE): the video m-line inherits the session/audio
/// ufrag/pwd on parse, an offer/answer advertises them on the video m-line so a peer applies ICE to
/// the video 5-tuple, and the ICE enricher stamps the local credentials/role onto the video stream
/// — surviving the later SDES rebuild.
/// </summary>
public sealed class VideoIceNegotiationTests
{
    private static readonly IPEndPoint LocalAudio = new(IPAddress.Loopback, 41000);

    private static SdpVideoNegotiationOptions VideoOptions() => new() { Port = 41002 };

    // ── Parse: the video m-line inherits shared credentials ──────────────────────

    [Fact]
    public void Video_inherits_session_level_ice_credentials()
    {
        var offer =
            "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
            + "a=ice-ufrag:sessU\r\na=ice-pwd:sessPwd\r\n"
            + "m=audio 5002 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=sendrecv\r\n"
            + "m=video 5004 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\n";

        var parameters = SdpUtilities.TryParseMediaParameters(
            offer, LocalAudio, new SdpMediaNegotiationOptions { Video = VideoOptions() });

        Assert.NotNull(parameters?.Video);
        Assert.Equal("sessU", parameters!.Video!.RemoteIceUfrag);
        Assert.Equal("sessPwd", parameters.Video.RemoteIcePwd);
    }

    [Fact]
    public void Video_mline_own_ice_credentials_win_over_session()
    {
        // RFC 8839 §5.3: media-level ICE credentials override the session-level ones.
        var offer =
            "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
            + "a=ice-ufrag:sessU\r\na=ice-pwd:sessPwd\r\n"
            + "m=audio 5002 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=sendrecv\r\n"
            + "m=video 5004 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\n"
            + "a=ice-ufrag:vidU\r\na=ice-pwd:vidPwd\r\n";

        var parameters = SdpUtilities.TryParseMediaParameters(
            offer, LocalAudio, new SdpMediaNegotiationOptions { Video = VideoOptions() });

        Assert.NotNull(parameters?.Video);
        Assert.Equal("vidU", parameters!.Video!.RemoteIceUfrag);
        Assert.Equal("vidPwd", parameters.Video.RemoteIcePwd);
    }

    // ── Emission: the video m-line advertises the shared credentials ─────────────

    [Fact]
    public void Offer_with_video_and_ice_carries_ice_on_the_video_mline()
    {
        var offer = SdpUtilities.BuildDefaultSdp(LocalAudio, hold: false, new SdpMediaNegotiationOptions
        {
            Video = VideoOptions(),
            Ice = new SdpIceNegotiationOptions { Ufrag = "offU", Pwd = "offPwd" },
        });

        var videoSection = offer[offer.IndexOf("m=video", StringComparison.Ordinal)..];
        Assert.Contains("a=ice-ufrag:offU", videoSection, StringComparison.Ordinal);
        Assert.Contains("a=ice-pwd:offPwd", videoSection, StringComparison.Ordinal);
    }

    [Fact]
    public void Answer_to_video_and_ice_offer_carries_ice_on_the_video_mline()
    {
        var offer =
            "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
            + "a=ice-ufrag:peerU\r\na=ice-pwd:peerPwd\r\n"
            + "m=audio 5002 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=sendrecv\r\n"
            + "m=video 5004 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\n";

        var answer = SdpUtilities.TryBuildNegotiatedAnswer(offer, LocalAudio, hold: false,
            new SdpMediaNegotiationOptions
            {
                Video = VideoOptions(),
                Ice = new SdpIceNegotiationOptions { Ufrag = "ansU", Pwd = "ansPwd" },
            });

        Assert.NotNull(answer);
        var videoSection = answer![answer.IndexOf("m=video", StringComparison.Ordinal)..];
        Assert.Contains("a=ice-ufrag:ansU", videoSection, StringComparison.Ordinal);
        Assert.Contains("a=ice-pwd:ansPwd", videoSection, StringComparison.Ordinal);
    }

    // ── Enrichment: local credentials/role stamped and preserved ─────────────────

    [Fact]
    public void Ice_enricher_stamps_local_credentials_and_role_onto_video()
    {
        var parameters = AudioVideoParameters(VideoWithRemoteIce());
        var localDescription = new CallIceLocalDescription { Ufrag = "myU", Pwd = "myPwd" };

        var enriched = CallMediaParametersIceEnricher.Enrich(parameters, localDescription, iceControlling: false);

        Assert.NotNull(enriched.Video);
        Assert.True(enriched.Video!.IceEnabled);
        Assert.False(enriched.Video.IceControlling);
        Assert.Equal("myU", enriched.Video.LocalIceUfrag);
        Assert.Equal("myPwd", enriched.Video.LocalIcePwd);
        // Remote credentials resolved at parse survive the enrichment.
        Assert.Equal("peerU", enriched.Video.RemoteIceUfrag);
        Assert.Equal("peerPwd", enriched.Video.RemoteIcePwd);
    }

    [Fact]
    public void Srtp_enricher_preserves_video_ice_through_the_sdes_rebuild()
    {
        // The SDES rebuild produces a fresh CallVideoParameters — the ICE fields the ICE enricher
        // stamped must not be dropped, or the video stream loses its consent credentials.
        var inlineKey = Convert.ToBase64String(new byte[30]);
        var videoCrypto = $"a=crypto:1 AES_CM_128_HMAC_SHA1_80 inline:{inlineKey}\r\n";
        var remoteSdp =
            "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
            + "m=audio 5002 RTP/SAVP 0\r\na=rtpmap:0 PCMU/8000\r\n" + videoCrypto
            + "m=video 5004 RTP/SAVP 96\r\na=rtpmap:96 VP8/90000\r\n" + videoCrypto;
        var localSdp =
            "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=me\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
            + "m=audio 41000 RTP/SAVP 0\r\na=rtpmap:0 PCMU/8000\r\n" + videoCrypto
            + "m=video 41002 RTP/SAVP 96\r\na=rtpmap:96 VP8/90000\r\n" + videoCrypto;

        // Post-ICE-enrichment input: video already carries local + remote ICE.
        var iceEnriched = CallMediaParametersIceEnricher.Enrich(
            AudioVideoParameters(VideoWithRemoteIce()),
            new CallIceLocalDescription { Ufrag = "myU", Pwd = "myPwd" },
            iceControlling: true);

        var enriched = CallMediaParametersSrtpEnricher.Enrich(
            iceEnriched, reasonCode: "test", remoteSdp, localSdp, SrtpPolicy.Optional);

        Assert.NotNull(enriched.Video);
        Assert.Equal("AES_CM_128_HMAC_SHA1_80", enriched.Video!.SrtpSuite); // SDES engaged (rebuild path)
        // ICE credentials survive the rebuild.
        Assert.True(enriched.Video.IceEnabled);
        Assert.Equal("myU", enriched.Video.LocalIceUfrag);
        Assert.Equal("myPwd", enriched.Video.LocalIcePwd);
        Assert.Equal("peerU", enriched.Video.RemoteIceUfrag);
        Assert.Equal("peerPwd", enriched.Video.RemoteIcePwd);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static CallVideoParameters VideoWithRemoteIce() => new()
    {
        PayloadType = 96,
        CodecName = "VP8",
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 41002),
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 5004),
        RemoteIceUfrag = "peerU",
        RemoteIcePwd = "peerPwd",
    };

    private static CallMediaParameters AudioVideoParameters(CallVideoParameters video) => new()
    {
        LocalEndPoint = LocalAudio,
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 5002),
        PayloadType = 0,
        ClockRate = 8000,
        SamplesPerPacket = 160,
        RemoteIceUfrag = "peerU",
        RemoteIcePwd = "peerPwd",
        Video = video,
    };
}
