using System.Linq;
using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #160 P2-6 and P2-8: two ways one m-line was allowed to decide another's fate. The video direction was
/// resolved against the direction the *audio* answer came out at, and a leading declined audio section
/// ended the whole negotiation. Both produce a well-formed answer that says the wrong thing.
/// </summary>
public sealed class SdpAnswerDirectionAndDeclineTests
{
    private static readonly IPEndPoint Local = new(IPAddress.Parse("192.0.2.1"), 40000);

    private static readonly SdpCodecDefinition[] AudioCapabilities =
    [
        new() { PayloadType = 0, Name = "PCMU", ClockRate = 8000 },
    ];

    private static SdpSessionDescription ParseOffer(string body)
    {
        Assert.True(new SdpSessionParser().TryParse(
            "v=0\r\no=- 1 1 IN IP4 198.51.100.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 198.51.100.1\r\n" + body,
            out var offer));
        return offer!;
    }

    private static SdpMediaOptions VideoEnabledOptions() => new()
    {
        Video = new SdpVideoMediaOptions
        {
            Port = 40002,
            Codecs = [new SdpCodecDefinition { PayloadType = 96, Name = "VP8", ClockRate = 90000 }],
        },
    };

    private static SdpOfferAnswerResult Answer(
        SdpSessionDescription offer,
        SdpMediaDirection localDirection,
        SdpMediaOptions? options = null) =>
        new SdpOfferAnswerNegotiator().NegotiateAnswer(offer, Local, AudioCapabilities, localDirection, options);

    // ── P2-6: the video direction is independent of the audio answer ─────────

    [Fact]
    public void Video_recvonly_beside_audio_sendonly_is_answered_sendonly()
    {
        // The review's probe. Both offered directions are legal on their own (RFC 3264 §6.1): the peer
        // wants to send us audio and receive our video. Resolving video against the audio *answer*
        // (recvonly) instead of our own readiness cancelled it to inactive — the peer asked for video
        // and got a section that carries none, with nothing reporting why.
        var offer = ParseOffer(
            "m=audio 5000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=sendonly\r\n" +
            "m=video 5002 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\na=recvonly\r\n");

        var result = Answer(offer, SdpMediaDirection.SendRecv, VideoEnabledOptions());

        Assert.True(result.Success);
        var media = result.Answer!.Media;
        Assert.Equal(SdpMediaDirection.RecvOnly, media[0].Direction);
        Assert.Equal(SdpMediaDirection.SendOnly, media[1].Direction);
    }

    [Fact]
    public void Video_sendonly_beside_audio_recvonly_is_answered_recvonly()
    {
        // The mirror image, to show the fix is not a swapped constant.
        var offer = ParseOffer(
            "m=audio 5000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=recvonly\r\n" +
            "m=video 5002 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\na=sendonly\r\n");

        var result = Answer(offer, SdpMediaDirection.SendRecv, VideoEnabledOptions());

        Assert.Equal(SdpMediaDirection.SendOnly, result.Answer!.Media[0].Direction);
        Assert.Equal(SdpMediaDirection.RecvOnly, result.Answer.Media[1].Direction);
    }

    [Fact]
    public void Our_own_local_direction_still_constrains_the_video_answer()
    {
        // Independence from the audio answer must not become independence from our own readiness: on
        // hold (sendonly) we do not agree to receive video, whatever the peer offers.
        var offer = ParseOffer(
            "m=audio 5000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=sendrecv\r\n" +
            "m=video 5002 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\na=sendonly\r\n");

        var result = Answer(offer, SdpMediaDirection.SendOnly, VideoEnabledOptions());

        // Offered sendonly against our sendonly: neither side agrees to receive (RFC 3264 §6.1).
        Assert.Equal(SdpMediaDirection.Inactive, result.Answer!.Media[1].Direction);
    }

    [Fact]
    public void A_matching_sendrecv_offer_is_still_answered_sendrecv()
    {
        var offer = ParseOffer(
            "m=audio 5000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=sendrecv\r\n" +
            "m=video 5002 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\na=sendrecv\r\n");

        var result = Answer(offer, SdpMediaDirection.SendRecv, VideoEnabledOptions());

        Assert.Equal(SdpMediaDirection.SendRecv, result.Answer!.Media[0].Direction);
        Assert.Equal(SdpMediaDirection.SendRecv, result.Answer.Media[1].Direction);
    }

    // ── P2-8: a leading declined audio section is not the end of the offer ───

    [Fact]
    public void A_leading_zero_port_audio_section_does_not_end_the_negotiation()
    {
        // The review's probe: a peer re-offering after having declined its first audio section — ordinary
        // SDP (RFC 8866 §5.14). Taking "the first audio m-line" as the primary meant the whole offer was
        // answered as declined, dropping the live audio and video sections behind it.
        var offer = ParseOffer(
            "m=audio 0 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n" +
            "m=audio 5000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n" +
            "m=video 5002 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\n");

        var result = Answer(offer, SdpMediaDirection.SendRecv, VideoEnabledOptions());

        Assert.True(result.Success);
        var media = result.Answer!.Media;
        Assert.Equal(3, media.Count);
        Assert.Equal(0, media[0].Port);                   // the declined section stays declined
        Assert.True(media[1].Port > 0);                   // the live audio is answered
        Assert.True(media[2].Port > 0);                   // and so is the video behind it
        Assert.NotEmpty(result.NegotiatedCodecs);
    }

    [Fact]
    public void A_declined_section_keeps_its_mid_so_the_peer_can_map_the_answer()
    {
        var offer = ParseOffer(
            "m=audio 0 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=mid:0\r\n" +
            "m=audio 5000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=mid:1\r\n");

        var result = Answer(offer, SdpMediaDirection.SendRecv);

        Assert.Equal("0", result.Answer!.Media[0].Mid);
        Assert.Equal("1", result.Answer.Media[1].Mid);
    }

    [Fact]
    public void An_offer_whose_audio_sections_are_all_declined_is_mirrored_back_declined()
    {
        // The case the early return was actually for. It still behaves the same — and now keeps the mids
        // (RFC 8829 §5.3.1), so the peer can map the rejection onto the offer it sent.
        var offer = ParseOffer(
            "m=audio 0 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=mid:0\r\n" +
            "m=video 0 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\na=mid:1\r\n");

        var result = Answer(offer, SdpMediaDirection.SendRecv, VideoEnabledOptions());

        Assert.True(result.Success);
        Assert.All(result.Answer!.Media, m => Assert.Equal(0, m.Port));
        Assert.All(result.Answer.Media, m => Assert.Equal(SdpMediaDirection.Inactive, m.Direction));
        Assert.Equal(new[] { "0", "1" }, result.Answer.Media.Select(m => m.Mid));
        Assert.Empty(result.NegotiatedCodecs);
    }

    [Fact]
    public void An_offer_with_no_audio_section_at_all_is_still_rejected()
    {
        // "No audio offered" and "audio offered but declined" are different answers, and the split of the
        // early return must not merge them.
        var offer = ParseOffer("m=video 5002 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\n");

        Assert.False(Answer(offer, SdpMediaDirection.SendRecv, VideoEnabledOptions()).Success);
    }

    [Fact]
    public void The_bundle_group_of_the_answer_skips_the_declined_leading_section()
    {
        // RFC 9143 §7.3.3: the answer group is the ordered subset of offered mids we actually accepted.
        var offer = ParseOffer(
            "a=group:BUNDLE 0 1 2\r\n" +
            "m=audio 0 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=mid:0\r\n" +
            "m=audio 5000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=mid:1\r\n" +
            "m=video 5002 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\na=mid:2\r\n");

        var result = Answer(offer, SdpMediaDirection.SendRecv, VideoEnabledOptions());

        Assert.Equal("BUNDLE 1 2", result.Answer!.Group);
    }
}
