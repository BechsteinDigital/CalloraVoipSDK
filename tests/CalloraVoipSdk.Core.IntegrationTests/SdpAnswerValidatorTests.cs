using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #160 SDP P1-b: an offerer must validate the remote answer against its own offer (RFC 3264 §6 /
/// RFC 8829) before building any transport. A formally-parsed but mismatched answer — reordered
/// m-lines, renamed MID, switched profile, un-offered payload type, or an un-offered BUNDLE mid —
/// must be rejected with a typed reason.
/// </summary>
public sealed class SdpAnswerValidatorTests
{
    private const string Offer =
        "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\n" +
        "a=group:BUNDLE 0 1\r\n" +
        "m=audio 40000 RTP/AVP 0 8\r\na=rtpmap:0 PCMU/8000\r\na=rtpmap:8 PCMA/8000\r\na=mid:0\r\n" +
        "m=video 40002 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\na=mid:1\r\n";

    private static SdpSessionDescription Parse(string sdp)
    {
        Assert.True(new SdpSessionParser().TryParse(sdp, out var parsed));
        return parsed!;
    }

    // The validator now returns a typed error (#160 P1-2b); these tests assert on the message, so the
    // helper keeps handing back the text.
    private static string? Validate(string answerSdp) =>
        SdpAnswerValidator.Validate(Parse(Offer), Parse(answerSdp))?.Message;

    [Fact]
    public void A_conforming_answer_is_accepted()
    {
        var answer =
            "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\n" +
            "a=group:BUNDLE 0 1\r\n" +
            "m=audio 40010 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=mid:0\r\n" +
            "m=video 40012 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\na=mid:1\r\n";

        Assert.Null(Validate(answer));
    }

    [Fact]
    public void A_declined_m_line_with_port_zero_is_accepted()
    {
        var answer =
            "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\n" +
            "a=group:BUNDLE 0\r\n" +
            "m=audio 40010 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=mid:0\r\n" +
            "m=video 0 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\na=mid:1\r\n"; // video declined

        Assert.Null(Validate(answer));
    }

    [Fact]
    public void An_m_line_count_mismatch_is_rejected()
    {
        var answer =
            "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\n" +
            "m=audio 40010 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=mid:0\r\n"; // video dropped

        Assert.Contains("m-line count", Validate(answer));
    }

    [Fact]
    public void A_reordered_media_type_is_rejected()
    {
        var answer =
            "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\n" +
            "m=video 40012 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\na=mid:1\r\n" +
            "m=audio 40010 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=mid:0\r\n"; // swapped

        Assert.Contains("media type", Validate(answer));
    }

    [Fact]
    public void A_changed_mid_is_rejected()
    {
        var answer =
            "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\n" +
            "m=audio 40010 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=mid:audio\r\n" + // was 0
            "m=video 40012 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\na=mid:1\r\n";

        Assert.Contains("mid", Validate(answer));
    }

    [Fact]
    public void A_switched_transport_profile_is_rejected()
    {
        var answer =
            "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\n" +
            "m=audio 40010 RTP/SAVP 0\r\na=rtpmap:0 PCMU/8000\r\na=mid:0\r\n" + // SAVP, offer was AVP
            "m=video 40012 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\na=mid:1\r\n";

        Assert.Contains("profile", Validate(answer));
    }

    [Fact]
    public void An_un_offered_payload_type_is_rejected()
    {
        var answer =
            "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\n" +
            "m=audio 40010 RTP/AVP 9\r\na=rtpmap:9 G722/8000\r\na=mid:0\r\n" + // 9 not offered
            "m=video 40012 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\na=mid:1\r\n";

        Assert.Contains("payload type", Validate(answer));
    }

    [Fact]
    public void An_answer_that_drops_the_offered_bundle_group_is_rejected()
    {
        // RFC 9143 §7.3.3: the offer asked for BUNDLE, so an answer with no a=group must not silently pass.
        var answer =
            "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\n" +
            "m=audio 40010 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=mid:0\r\n" +
            "m=video 40012 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\na=mid:1\r\n";

        Assert.Contains("BUNDLE", Validate(answer));
    }

    [Fact]
    public void An_un_offered_bundle_mid_is_rejected()
    {
        var answer =
            "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\n" +
            "a=group:BUNDLE 0 1 2\r\n" + // 2 was never offered for bundling
            "m=audio 40010 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=mid:0\r\n" +
            "m=video 40012 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\na=mid:1\r\n";

        Assert.Contains("BUNDLE", Validate(answer));
    }
}
