using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The MID SDES header extension in a BUNDLE offer (ADR-011 B5-wire (a), RFC 9143 / RFC 8843 §9): a
/// bundled offer advertises <c>a=extmap … urn:ietf:params:rtp-hdrext:sdes:mid</c> on every m-line with
/// the SAME id, so the shared transport stamps and demultiplexes one consistent MID id. Outside BUNDLE
/// the extmaps are unchanged.
/// </summary>
public sealed class SdpBundleMidExtmapTests
{
    private static readonly IPEndPoint Local = new(IPAddress.Loopback, 5000);

    private static readonly IReadOnlyList<SdpCodecDefinition> AudioCodecs =
        [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }];

    private static SdpVideoMediaOptions Video() => new()
    {
        Port = 5002,
        Codecs = [new SdpCodecDefinition { PayloadType = 96, Name = "H264", ClockRate = 90000 }],
        HeaderExtensionUris = [RtpHeaderExtensionUris.TransportWideCc],
    };

    [Fact]
    public void A_bundle_offer_carries_the_mid_extension_on_both_m_lines_with_the_same_id()
    {
        var offer = new SdpOfferAnswerNegotiator().CreateOffer(
            Local, AudioCodecs, SdpMediaDirection.SendRecv,
            new SdpMediaOptions { Bundle = true, RtcpMux = true, Video = Video() });

        var audio = offer.Media.Single(m => m.MediaType == "audio");
        var video = offer.Media.Single(m => m.MediaType == "video");

        var audioMid = audio.Extensions.Single(e => e.Uri == RtpHeaderExtensionUris.Mid);
        var videoMid = video.Extensions.Single(e => e.Uri == RtpHeaderExtensionUris.Mid);

        Assert.Equal(1, audioMid.Id);                 // MID offered first → id 1
        Assert.Equal(audioMid.Id, videoMid.Id);        // same id across the bundle (RFC 8843 §9)

        // transport-cc is still offered on the video m-line, on a different id.
        var videoTcc = video.Extensions.Single(e => e.Uri == RtpHeaderExtensionUris.TransportWideCc);
        Assert.NotEqual(videoMid.Id, videoTcc.Id);

        Assert.Equal("BUNDLE audio video", offer.Group);
        Assert.Equal("audio", audio.Mid);
        Assert.Equal("video", video.Mid);
    }

    [Fact]
    public void A_non_bundle_offer_advertises_no_mid_extension()
    {
        var offer = new SdpOfferAnswerNegotiator().CreateOffer(
            Local, AudioCodecs, SdpMediaDirection.SendRecv,
            new SdpMediaOptions { RtcpMux = true, Video = Video() });

        var audio = offer.Media.Single(m => m.MediaType == "audio");
        var video = offer.Media.Single(m => m.MediaType == "video");

        Assert.Empty(audio.Extensions); // audio carries no extmaps without BUNDLE
        Assert.DoesNotContain(video.Extensions, e => e.Uri == RtpHeaderExtensionUris.Mid);
        Assert.Contains(video.Extensions, e => e.Uri == RtpHeaderExtensionUris.TransportWideCc); // still offered
        Assert.Null(offer.Group);
    }

    [Fact]
    public void A_bundle_answer_echoes_the_mid_extension_id_on_both_m_lines()
    {
        var negotiator = new SdpOfferAnswerNegotiator();
        var offer = negotiator.CreateOffer(
            Local, AudioCodecs, SdpMediaDirection.SendRecv,
            new SdpMediaOptions { Bundle = true, RtcpMux = true, Video = Video() });
        var offeredMidId = offer.Media.Single(m => m.MediaType == "audio")
            .Extensions.Single(e => e.Uri == RtpHeaderExtensionUris.Mid).Id;

        var result = negotiator.NegotiateAnswer(
            offer, new IPEndPoint(IPAddress.Loopback, 6000), AudioCodecs, SdpMediaDirection.SendRecv,
            new SdpMediaOptions { RtcpMux = true, Video = Video() });

        Assert.True(result.Success);
        var audio = result.Answer!.Media.Single(m => m.MediaType == "audio");
        var video = result.Answer.Media.Single(m => m.MediaType == "video");

        var audioMid = audio.Extensions.Single(e => e.Uri == RtpHeaderExtensionUris.Mid);
        var videoMid = video.Extensions.Single(e => e.Uri == RtpHeaderExtensionUris.Mid);
        Assert.Equal(offeredMidId, audioMid.Id); // the answer mirrors the offered id (RFC 8285 §5)
        Assert.Equal(offeredMidId, videoMid.Id);
    }

    [Fact]
    public void A_non_bundle_answer_advertises_no_mid_extension()
    {
        var negotiator = new SdpOfferAnswerNegotiator();
        var offer = negotiator.CreateOffer(
            Local, AudioCodecs, SdpMediaDirection.SendRecv,
            new SdpMediaOptions { RtcpMux = true, Video = Video() });

        var result = negotiator.NegotiateAnswer(
            offer, new IPEndPoint(IPAddress.Loopback, 6000), AudioCodecs, SdpMediaDirection.SendRecv,
            new SdpMediaOptions { RtcpMux = true, Video = Video() });

        Assert.True(result.Success);
        var audio = result.Answer!.Media.Single(m => m.MediaType == "audio");
        Assert.Empty(audio.Extensions); // no MID echoed → audio answer stays extmap-free (backward compatible)
        Assert.DoesNotContain(
            result.Answer.Media.Single(m => m.MediaType == "video").Extensions,
            e => e.Uri == RtpHeaderExtensionUris.Mid);
    }

    [Fact]
    public void TryExtractBundleMid_recovers_the_mid_id_and_tokens_from_a_bundle_sdp()
    {
        var offer = new SdpOfferAnswerNegotiator().CreateOffer(
            Local, AudioCodecs, SdpMediaDirection.SendRecv,
            new SdpMediaOptions { Bundle = true, RtcpMux = true, Video = Video() });
        var sdp = new SdpSessionSerializer().Serialize(offer);

        var info = SdpUtilities.TryExtractBundleMid(sdp);

        Assert.NotNull(info);
        Assert.Equal(1, info!.MidExtensionId);   // the shared sdes:mid id
        Assert.Equal("audio", info.AudioMid);
        Assert.Equal("video", info.VideoMid);
    }

    [Fact]
    public void TryExtractBundleMid_returns_null_without_the_mid_extension()
    {
        var offer = new SdpOfferAnswerNegotiator().CreateOffer(
            Local, AudioCodecs, SdpMediaDirection.SendRecv,
            new SdpMediaOptions { RtcpMux = true, Video = Video() });

        Assert.Null(SdpUtilities.TryExtractBundleMid(new SdpSessionSerializer().Serialize(offer)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryExtractBundleMid_is_null_for_empty_sdp(string? sdp)
    {
        Assert.Null(SdpUtilities.TryExtractBundleMid(sdp));
    }
}
