using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #160 P1-2b: validating the answer's <em>structure</em> while letting its <em>attribute values</em>
/// through left an answerer free to change what was agreed. It could enable <c>rtcp-mux</c>, flip the
/// media direction, take a DTLS setup role the offer did not allow, or add feedback, header extensions
/// and format parameters that were never offered — and the offerer would build transport and tracks as
/// though it had agreed to all of it.
/// </summary>
public sealed class SdpAnswerAttributeValidationTests
{
    private const string SessionHeader =
        "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\n";

    private static SdpSessionDescription Parse(string sdp)
    {
        Assert.True(new SdpSessionParser().TryParse(sdp, out var parsed));
        return parsed!;
    }

    private static SdpAnswerValidationError? Validate(string offer, string answer) =>
        SdpAnswerValidator.Validate(Parse(SessionHeader + offer), Parse(SessionHeader + answer));

    private static string Audio(string direction, string extra = "") =>
        $"m=audio 40000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=mid:0\r\na={direction}\r\n{extra}";

    // ── direction (RFC 3264 §6.1) ────────────────────────────────────────────

    [Theory]
    [InlineData("sendrecv", "sendrecv")]
    [InlineData("sendrecv", "recvonly")]
    [InlineData("sendrecv", "inactive")]
    [InlineData("sendonly", "recvonly")]
    [InlineData("sendonly", "inactive")]
    [InlineData("recvonly", "sendonly")]
    [InlineData("inactive", "inactive")]
    public void A_legal_direction_response_is_accepted(string offered, string answered)
    {
        Assert.Null(Validate(Audio(offered), Audio(answered)));
    }

    [Theory]
    [InlineData("recvonly", "sendrecv")]   // widens: the peer would start sending media we never agreed to receive
    [InlineData("sendonly", "sendrecv")]
    [InlineData("sendonly", "sendonly")]   // both sides sending into a stream neither agreed to receive
    [InlineData("inactive", "sendrecv")]
    [InlineData("inactive", "recvonly")]
    public void A_direction_that_widens_the_offer_is_rejected(string offered, string answered)
    {
        var error = Validate(Audio(offered), Audio(answered));

        Assert.Equal(SdpAnswerViolation.Direction, error?.Violation);
    }

    // ── rtcp-mux (RFC 5761 §5.1.1) ───────────────────────────────────────────

    [Fact]
    public void An_answer_cannot_introduce_rtcp_mux()
    {
        // The offerer has a separate RTCP port open and is listening there. An answer that turns mux on
        // unilaterally sends RTCP to a port nothing reads.
        var error = Validate(Audio("sendrecv"), Audio("sendrecv", "a=rtcp-mux\r\n"));

        Assert.Equal(SdpAnswerViolation.RtcpMuxNotOffered, error?.Violation);
    }

    [Fact]
    public void An_answer_may_accept_or_decline_offered_rtcp_mux()
    {
        var offer = Audio("sendrecv", "a=rtcp-mux\r\n");

        Assert.Null(Validate(offer, Audio("sendrecv", "a=rtcp-mux\r\n")));   // accepted
        Assert.Null(Validate(offer, Audio("sendrecv")));                     // declined
    }

    // ── DTLS setup role (RFC 5763 §5) ────────────────────────────────────────

    [Theory]
    [InlineData("actpass", "active")]
    [InlineData("actpass", "passive")]
    [InlineData("active", "passive")]
    [InlineData("passive", "active")]
    public void A_legal_setup_role_response_is_accepted(string offered, string answered)
    {
        Assert.Null(Validate(
            Audio("sendrecv", $"a=setup:{offered}\r\n"),
            Audio("sendrecv", $"a=setup:{answered}\r\n")));
    }

    [Theory]
    [InlineData("actpass", "actpass")]   // nobody starts the handshake
    [InlineData("active", "active")]     // both clients
    [InlineData("passive", "passive")]   // both servers
    public void A_setup_role_that_cannot_complete_a_handshake_is_rejected(string offered, string answered)
    {
        var error = Validate(
            Audio("sendrecv", $"a=setup:{offered}\r\n"),
            Audio("sendrecv", $"a=setup:{answered}\r\n"));

        Assert.Equal(SdpAnswerViolation.DtlsSetupRole, error?.Violation);
    }

    // ── rtcp-fb (RFC 4585 §4) ────────────────────────────────────────────────

    [Fact]
    public void An_answer_cannot_introduce_feedback_that_was_not_offered()
    {
        var error = Validate(
            Audio("sendrecv"),
            Audio("sendrecv", "a=rtcp-fb:0 nack\r\n"));

        Assert.Equal(SdpAnswerViolation.UnofferedRtcpFeedback, error?.Violation);
    }

    [Fact]
    public void An_answer_may_keep_offered_feedback()
    {
        var offer = Audio("sendrecv", "a=rtcp-fb:0 nack\r\n");

        Assert.Null(Validate(offer, Audio("sendrecv", "a=rtcp-fb:0 nack\r\n")));
        Assert.Null(Validate(offer, Audio("sendrecv")));   // or drop it
    }

    [Fact]
    public void A_wildcard_feedback_offer_covers_a_concrete_answer()
    {
        // "a=rtcp-fb:* nack" offers nack for every payload type, so answering it for PT 0 is in scope.
        Assert.Null(Validate(
            Audio("sendrecv", "a=rtcp-fb:* nack\r\n"),
            Audio("sendrecv", "a=rtcp-fb:0 nack\r\n")));
    }

    // ── extmap (RFC 8285 §5) ─────────────────────────────────────────────────

    [Fact]
    public void An_answer_cannot_introduce_a_header_extension_id()
    {
        var error = Validate(
            Audio("sendrecv"),
            Audio("sendrecv", "a=extmap:3 urn:ietf:params:rtp-hdrext:sdes:mid\r\n"));

        Assert.Equal(SdpAnswerViolation.UnofferedHeaderExtension, error?.Violation);
    }

    [Fact]
    public void An_answer_cannot_remap_an_offered_extension_id_to_another_uri()
    {
        // The damaging case: the offerer would read one extension's bytes as another's on every packet.
        var error = Validate(
            Audio("sendrecv", "a=extmap:3 urn:ietf:params:rtp-hdrext:sdes:mid\r\n"),
            Audio("sendrecv", "a=extmap:3 urn:ietf:params:rtp-hdrext:ssrc-audio-level\r\n"));

        Assert.Equal(SdpAnswerViolation.UnofferedHeaderExtension, error?.Violation);
    }

    [Fact]
    public void An_answer_may_keep_an_offered_extension_mapping()
    {
        var mid = "a=extmap:3 urn:ietf:params:rtp-hdrext:sdes:mid\r\n";

        Assert.Null(Validate(Audio("sendrecv", mid), Audio("sendrecv", mid)));
    }

    // ── fmtp and the RTX association (RFC 4588 §8.1) ─────────────────────────

    [Fact]
    public void An_answer_cannot_attach_fmtp_to_an_unoffered_payload_type()
    {
        var error = Validate(
            Audio("sendrecv"),
            Audio("sendrecv", "a=fmtp:96 minptime=10\r\n"));

        Assert.Equal(SdpAnswerViolation.UnofferedFormatParameters, error?.Violation);
    }

    [Fact]
    public void An_answer_cannot_point_rtx_at_an_unoffered_primary_payload_type()
    {
        // The retransmission stream would refer to a primary stream that does not exist.
        const string offer =
            "m=video 40002 RTP/AVP 96 97\r\na=rtpmap:96 VP8/90000\r\na=rtpmap:97 rtx/90000\r\n" +
            "a=fmtp:97 apt=96\r\na=mid:0\r\na=sendrecv\r\n";
        const string answer =
            "m=video 40002 RTP/AVP 96 97\r\na=rtpmap:96 VP8/90000\r\na=rtpmap:97 rtx/90000\r\n" +
            "a=fmtp:97 apt=98\r\na=mid:0\r\na=sendrecv\r\n";

        var error = Validate(offer, answer);

        Assert.Equal(SdpAnswerViolation.RtxAssociatedPayloadTypeNotOffered, error?.Violation);
    }

    [Fact]
    public void A_matching_rtx_association_is_accepted()
    {
        const string sdp =
            "m=video 40002 RTP/AVP 96 97\r\na=rtpmap:96 VP8/90000\r\na=rtpmap:97 rtx/90000\r\n" +
            "a=fmtp:97 apt=96\r\na=mid:0\r\na=sendrecv\r\n";

        Assert.Null(Validate(sdp, sdp));
    }

    // ── declined m-lines stay exempt ─────────────────────────────────────────

    [Fact]
    public void A_declined_m_line_is_not_attribute_checked()
    {
        // Port 0 means "no thanks" (RFC 3264 §6). Whatever attributes ride along are moot, and rejecting
        // the answer over them would turn a normal decline into a failed negotiation.
        var error = Validate(
            Audio("sendrecv"),
            "m=audio 0 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=mid:0\r\na=sendrecv\r\na=rtcp-mux\r\n");

        Assert.Null(error);
    }
}
