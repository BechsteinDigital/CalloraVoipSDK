using System.Net;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// SDP RTX negotiation for video (RFC 4588 §8.1): the offer advertises one rtx repair
/// codec per video codec with an apt binding, the answer echoes the rtx payload types for
/// accepted codecs, and the negotiated rtx payload type surfaces on CallVideoParameters.
/// </summary>
public sealed class VideoRtxSdpTests
{
    private static readonly IPEndPoint LocalAudio = new(IPAddress.Loopback, 44000);

    private static SdpVideoNegotiationOptions VideoOptions() => new() { Port = 44002 };

    [Fact]
    public void Offer_advertises_one_rtx_codec_per_video_codec_with_apt()
    {
        var offer = SdpUtilities.BuildDefaultSdp(LocalAudio, hold: false,
            new SdpMediaNegotiationOptions { Video = VideoOptions() });

        var videoSection = offer[offer.IndexOf("m=video", StringComparison.Ordinal)..];
        // VP8=96, H264=97 → rtx payload types assigned above them (98, 99).
        Assert.Contains("a=rtpmap:98 rtx/90000", videoSection, StringComparison.Ordinal);
        Assert.Contains("a=rtpmap:99 rtx/90000", videoSection, StringComparison.Ordinal);
        Assert.Contains("a=fmtp:98 apt=96", videoSection, StringComparison.Ordinal);
        Assert.Contains("a=fmtp:99 apt=97", videoSection, StringComparison.Ordinal);
        // The rtx payload types are part of the m= line format list.
        var mLine = videoSection[..videoSection.IndexOf("\r\n", StringComparison.Ordinal)];
        Assert.Contains(" 98 99", mLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Answer_echoes_rtx_for_the_accepted_codec()
    {
        var offer =
            "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
            + "m=audio 5002 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n"
            + "m=video 5004 RTP/AVP 96 98\r\n"
            + "a=rtpmap:96 VP8/90000\r\na=rtpmap:98 rtx/90000\r\na=fmtp:98 apt=96\r\n";

        var answer = SdpUtilities.TryBuildNegotiatedAnswer(
            offer, LocalAudio, hold: false,
            new SdpMediaNegotiationOptions { Video = VideoOptions() });

        Assert.NotNull(answer);
        var videoSection = answer![answer.IndexOf("m=video", StringComparison.Ordinal)..];
        Assert.Contains("a=rtpmap:98 rtx/90000", videoSection, StringComparison.Ordinal);
        Assert.Contains("a=fmtp:98 apt=96", videoSection, StringComparison.Ordinal);
    }

    [Fact]
    public void Answer_omits_rtx_when_apt_points_to_an_unaccepted_codec()
    {
        // Offer's rtx binds to VP9 (PT 101), which we do not accept → no rtx in the answer.
        var offer =
            "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
            + "m=audio 5002 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n"
            + "m=video 5004 RTP/AVP 96 101 98\r\n"
            + "a=rtpmap:96 VP8/90000\r\na=rtpmap:101 VP9/90000\r\n"
            + "a=rtpmap:98 rtx/90000\r\na=fmtp:98 apt=101\r\n";

        var answer = SdpUtilities.TryBuildNegotiatedAnswer(
            offer, LocalAudio, hold: false,
            new SdpMediaNegotiationOptions { Video = VideoOptions() });

        Assert.NotNull(answer);
        var videoSection = answer![answer.IndexOf("m=video", StringComparison.Ordinal)..];
        Assert.Contains("a=rtpmap:96 VP8/90000", videoSection, StringComparison.Ordinal);
        Assert.DoesNotContain("rtx/90000", videoSection, StringComparison.Ordinal);
    }

    [Fact]
    public void Answer_without_offered_rtx_carries_none()
    {
        var offer =
            "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
            + "m=audio 5002 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n"
            + "m=video 5004 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\n";

        var answer = SdpUtilities.TryBuildNegotiatedAnswer(
            offer, LocalAudio, hold: false,
            new SdpMediaNegotiationOptions { Video = VideoOptions() });

        Assert.NotNull(answer);
        Assert.DoesNotContain("rtx/90000", answer!, StringComparison.Ordinal);
    }

    [Fact]
    public void Media_parameters_surface_the_negotiated_rtx_payload_type()
    {
        var offer =
            "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
            + "m=audio 5002 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n"
            + "m=video 5004 RTP/AVP 96 98\r\n"
            + "a=rtpmap:96 VP8/90000\r\na=rtpmap:98 rtx/90000\r\na=fmtp:98 apt=96\r\n";

        var parameters = SdpUtilities.TryParseMediaParameters(
            offer, LocalAudio, new SdpMediaNegotiationOptions { Video = VideoOptions() });

        Assert.NotNull(parameters?.Video);
        Assert.Equal(96, parameters!.Video!.PayloadType);
        Assert.Equal(98, parameters.Video.RtxPayloadType);
    }

    [Fact]
    public void Media_parameters_have_no_rtx_when_none_negotiated()
    {
        var offer =
            "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
            + "m=audio 5002 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n"
            + "m=video 5004 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\n";

        var parameters = SdpUtilities.TryParseMediaParameters(
            offer, LocalAudio, new SdpMediaNegotiationOptions { Video = VideoOptions() });

        Assert.NotNull(parameters?.Video);
        Assert.Null(parameters!.Video!.RtxPayloadType);
    }

    [Fact]
    public void Rtx_with_rtx_time_parameter_still_surfaces_the_payload_type()
    {
        // apt is one of several fmtp tokens (RFC 4588 §8.1 allows rtx-time) — parsing must
        // find it regardless of position/companions.
        var offer =
            "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
            + "m=audio 5002 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n"
            + "m=video 5004 RTP/AVP 96 98\r\n"
            + "a=rtpmap:96 VP8/90000\r\na=rtpmap:98 rtx/90000\r\na=fmtp:98 apt=96;rtx-time=200\r\n";

        var parameters = SdpUtilities.TryParseMediaParameters(
            offer, LocalAudio, new SdpMediaNegotiationOptions { Video = VideoOptions() });

        Assert.Equal(98, parameters!.Video!.RtxPayloadType);
    }

    [Fact]
    public void Rtx_with_non_numeric_apt_is_ignored()
    {
        var offer =
            "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
            + "m=audio 5002 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n"
            + "m=video 5004 RTP/AVP 96 98\r\n"
            + "a=rtpmap:96 VP8/90000\r\na=rtpmap:98 rtx/90000\r\na=fmtp:98 apt=abc\r\n";

        var parameters = SdpUtilities.TryParseMediaParameters(
            offer, LocalAudio, new SdpMediaNegotiationOptions { Video = VideoOptions() });

        Assert.NotNull(parameters?.Video);
        Assert.Null(parameters!.Video!.RtxPayloadType);
    }

    [Fact]
    public void Rtx_codecs_round_trip_through_parser_and_serializer()
    {
        var offer = SdpUtilities.BuildDefaultSdp(LocalAudio, hold: false,
            new SdpMediaNegotiationOptions { Video = VideoOptions() });

        var parsed = new SdpSessionParser().Parse(offer);
        var video = parsed.Media.Single(m => m.MediaType == "video");
        Assert.Equal(2, video.Codecs.Count(c => c.Name == "rtx"));

        var reparsed = new SdpSessionParser().Parse(new SdpSessionSerializer().Serialize(parsed))
            .Media.Single(m => m.MediaType == "video");
        Assert.Equal(2, reparsed.Codecs.Count(c => c.Name == "rtx"));
        Assert.Equal(2, reparsed.Fmtp.Count(f => f.Parameters.StartsWith("apt=", StringComparison.Ordinal)));
    }
}
