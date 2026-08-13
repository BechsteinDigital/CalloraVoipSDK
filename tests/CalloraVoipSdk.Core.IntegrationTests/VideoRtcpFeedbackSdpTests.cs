using System.Net;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// SDP a=rtcp-fb signaling for video (RFC 4585 §4.2): the offer advertises the SDK's
/// feedback set on the video m-line, the answer negotiates the intersection with the
/// peer's offer, and a=rtcp-fb round-trips through the parser/serializer.
/// </summary>
public sealed class VideoRtcpFeedbackSdpTests
{
    private static readonly IPEndPoint LocalAudio = new(IPAddress.Loopback, 43000);

    private static SdpVideoNegotiationOptions VideoOptions() => new() { Port = 43002 };

    [Fact]
    public void Offer_video_mline_advertises_nack_pli_and_fir()
    {
        var offer = SdpUtilities.BuildDefaultSdp(LocalAudio, hold: false,
            new SdpMediaNegotiationOptions { Video = VideoOptions() });

        var videoSection = offer[offer.IndexOf("m=video", StringComparison.Ordinal)..];
        Assert.Contains("a=rtcp-fb:* nack", videoSection, StringComparison.Ordinal);
        Assert.Contains("a=rtcp-fb:* nack pli", videoSection, StringComparison.Ordinal);
        Assert.Contains("a=rtcp-fb:* ccm fir", videoSection, StringComparison.Ordinal);
    }

    [Fact]
    public void Audio_mline_carries_no_rtcp_fb()
    {
        var offer = SdpUtilities.BuildDefaultSdp(LocalAudio, hold: false,
            new SdpMediaNegotiationOptions { Video = VideoOptions() });

        var audioSection = offer[
            offer.IndexOf("m=audio", StringComparison.Ordinal)..offer.IndexOf("m=video", StringComparison.Ordinal)];
        Assert.DoesNotContain("a=rtcp-fb", audioSection, StringComparison.Ordinal);
    }

    [Fact]
    public void Answer_negotiates_the_intersection_of_offered_feedback()
    {
        // Peer offers only PLI and FIR (no plain NACK) — the answer must not advertise NACK.
        var offer =
            "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
            + "m=audio 5002 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n"
            + "m=video 5004 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\n"
            + "a=rtcp-fb:* nack pli\r\na=rtcp-fb:96 ccm fir\r\n";

        var answer = SdpUtilities.TryBuildNegotiatedAnswer(
            offer, LocalAudio, hold: false,
            new SdpMediaNegotiationOptions { Video = VideoOptions() });

        Assert.NotNull(answer);
        var videoSection = answer![answer.IndexOf("m=video", StringComparison.Ordinal)..];
        // #160 P2-7: each entry is answered for the payload type it was offered for — "*" stays "*",
        // and "96 ccm fir" is confirmed for 96 rather than widened to every format.
        Assert.Contains("a=rtcp-fb:* nack pli", videoSection, StringComparison.Ordinal);
        Assert.Contains("a=rtcp-fb:96 ccm fir", videoSection, StringComparison.Ordinal);
        Assert.DoesNotContain("a=rtcp-fb:* ccm fir", videoSection, StringComparison.Ordinal);
        // Plain NACK was not offered — must be absent from the answer.
        Assert.DoesNotContain("a=rtcp-fb:* nack\r\n", videoSection, StringComparison.Ordinal);
    }

    [Fact]
    public void Answer_without_offered_feedback_advertises_none()
    {
        var offer =
            "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
            + "m=audio 5002 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n"
            + "m=video 5004 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\n";

        var answer = SdpUtilities.TryBuildNegotiatedAnswer(
            offer, LocalAudio, hold: false,
            new SdpMediaNegotiationOptions { Video = VideoOptions() });

        Assert.NotNull(answer);
        Assert.Contains("m=video 43002", answer!, StringComparison.Ordinal);
        Assert.DoesNotContain("a=rtcp-fb", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Rtcp_fb_round_trips_through_parser_and_serializer()
    {
        var offer = SdpUtilities.BuildDefaultSdp(LocalAudio, hold: false,
            new SdpMediaNegotiationOptions { Video = VideoOptions() });

        var parsed = new SdpSessionParser().Parse(offer);
        var video = parsed.Media.Single(m => m.MediaType == "video");

        Assert.Equal(3, video.RtcpFeedback.Count);
        Assert.Contains(video.RtcpFeedback, f => f is { FeedbackType: "nack", Parameter: null });
        Assert.Contains(video.RtcpFeedback, f => f is { FeedbackType: "nack", Parameter: "pli" });
        Assert.Contains(video.RtcpFeedback, f => f is { FeedbackType: "ccm", Parameter: "fir" });

        var reserialized = new SdpSessionSerializer().Serialize(parsed);
        var reparsed = new SdpSessionParser().Parse(reserialized).Media.Single(m => m.MediaType == "video");
        Assert.Equal(video.RtcpFeedback.Count, reparsed.RtcpFeedback.Count);
    }

    [Fact]
    public void Pt_specific_offered_feedback_is_answered_for_that_pt_only()
    {
        // #160 P2-7. This used to normalise "96 ccm fir" to "* ccm fir" — a deliberate decision at the
        // time, on the grounds that "*" is a superset and there was only ever one video codec. RTX put
        // a second payload type on the m-line, and the answer then claimed feedback for formats the
        // peer had never offered it for. It also contradicted SdpAnswerValidator, which rejects a
        // remote answer that widens feedback beyond the offer: an answer must not do what it refuses
        // to accept.
        var offer =
            "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
            + "m=audio 5002 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n"
            + "m=video 5004 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\na=rtcp-fb:96 ccm fir\r\n";

        var answer = SdpUtilities.TryBuildNegotiatedAnswer(
            offer, LocalAudio, hold: false,
            new SdpMediaNegotiationOptions { Video = VideoOptions() });

        Assert.NotNull(answer);
        Assert.Contains("a=rtcp-fb:96 ccm fir", answer!, StringComparison.Ordinal);
        Assert.DoesNotContain("a=rtcp-fb:* ccm fir", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Feedback_for_a_payload_type_we_did_not_accept_is_dropped()
    {
        // #160 P2-7: PT 97 is not in our capability set, so it is not in the answer — feedback for it
        // would describe a format the answer does not contain.
        var offer =
            "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
            + "m=audio 5002 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n"
            + "m=video 5004 RTP/AVP 96 97\r\na=rtpmap:96 VP8/90000\r\na=rtpmap:97 H263/90000\r\n"
            + "a=rtcp-fb:96 nack\r\na=rtcp-fb:97 nack\r\n";

        var answer = SdpUtilities.TryBuildNegotiatedAnswer(
            offer, LocalAudio, hold: false,
            new SdpMediaNegotiationOptions { Video = VideoOptions() });

        Assert.NotNull(answer);
        Assert.Contains("a=rtcp-fb:96 nack", answer!, StringComparison.Ordinal);
        Assert.DoesNotContain("a=rtcp-fb:97", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_offered_feedback_yields_no_duplicate_in_the_answer()
    {
        var offer =
            "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
            + "m=audio 5002 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n"
            + "m=video 5004 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\n"
            + "a=rtcp-fb:* nack pli\r\na=rtcp-fb:96 nack pli\r\n";

        var answer = SdpUtilities.TryBuildNegotiatedAnswer(
            offer, LocalAudio, hold: false,
            new SdpMediaNegotiationOptions { Video = VideoOptions() });

        Assert.NotNull(answer);
        var occurrences = answer!.Split("\r\n").Count(line => line == "a=rtcp-fb:* nack pli");
        Assert.Equal(1, occurrences);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("*", null)]                          // missing feedback type
    [InlineData("* nack", "nack")]
    [InlineData("96 ccm fir", "ccm")]
    [InlineData("* ccm fir tmmbr", "ccm")]           // multi-word parameter preserved
    public void Rtcp_fb_parse_handles_valid_and_malformed_values(string value, string? expectedType)
    {
        var parsed = SdpRtcpFeedback.TryParse(value);

        if (expectedType is null)
        {
            Assert.Null(parsed);
        }
        else
        {
            Assert.Equal(expectedType, parsed!.FeedbackType);
            if (value == "* ccm fir tmmbr")
                Assert.Equal("fir tmmbr", parsed.Parameter);
        }
    }
}
