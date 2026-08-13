using System.Linq;
using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #160 P2-14 and P2-9. The ICE credentials and candidate fields are inputs to the ICE agent, not
/// description: a ufrag the peer cannot reproduce as a STUN username makes every connectivity check
/// fail, and the call simply never connects with nothing pointing at the SDP. And
/// <c>a=rtcp-mux-only</c> (RFC 8858) was not parsed at all, so a peer that opened no separate RTCP
/// port could be answered non-muxed.
/// </summary>
public sealed class SdpIceGrammarAndMuxOnlyTests
{
    private const string Header = "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\n";
    private const string Audio = "m=audio 40000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n";
    private const string ValidPwd = "0123456789abcdefghijkl";   // 22 chars, the RFC 8839 floor

    private static SdpSessionDescription? Parse(string body) =>
        new SdpSessionParser().TryParse(Header + body, out var parsed) ? parsed : null;

    // ── P2-14: credentials ───────────────────────────────────────────────────

    [Theory]
    [InlineData("abc")]                    // 3 chars — below the 4-char floor
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("bad!char")]
    public void An_unusable_ice_ufrag_fails_the_parse(string ufrag)
    {
        Assert.Null(Parse($"{Audio}a=ice-ufrag:{ufrag}\r\na=ice-pwd:{ValidPwd}\r\n"));
    }

    [Theory]
    [InlineData("tooshort")]               // 8 chars — below the 22-char floor
    [InlineData("0123456789abcdefghijk")]  // 21 chars — one short
    [InlineData("")]
    public void An_unusable_ice_pwd_fails_the_parse(string pwd)
    {
        Assert.Null(Parse($"{Audio}a=ice-ufrag:abcd\r\na=ice-pwd:{pwd}\r\n"));
    }

    [Fact]
    public void Credentials_at_the_lower_bound_are_accepted()
    {
        var parsed = Parse($"{Audio}a=ice-ufrag:abcd\r\na=ice-pwd:{ValidPwd}\r\n");

        Assert.Equal("abcd", parsed?.Media[0].IceUfrag);
    }

    [Theory]
    [InlineData("with-dash")]
    [InlineData("with_underscore")]
    [InlineData("with=equals")]
    [InlineData("with#hash")]
    public void The_characters_libwebrtc_still_accepts_are_accepted_here_too(string ufrag)
    {
        // RFC 8839 §5.4 restricts ice-char to ALPHA / DIGIT / "+" / "/", but libwebrtc only warns on
        // these four and lets them through. Enforcing the RFC strictly would reject peers Chrome
        // accepts — stricter than the reference, and not better: the value works as a STUN username
        // either way. SIPSorcery validates none of this at all.
        var parsed = Parse($"{Audio}a=ice-ufrag:{ufrag}\r\na=ice-pwd:{ValidPwd}\r\n");

        Assert.Equal(ufrag, parsed?.Media[0].IceUfrag);
    }

    [Fact]
    public void A_description_with_no_ice_attributes_still_parses()
    {
        // Non-ICE SDP is not broken SDP; the validation applies to a credential that is present.
        Assert.NotNull(Parse(Audio));
    }

    // ── P2-14: candidate fields ──────────────────────────────────────────────

    [Theory]
    [InlineData("1 0 UDP 2130706431 192.0.2.1 40000 typ host")]        // component 0
    [InlineData("1 257 UDP 2130706431 192.0.2.1 40000 typ host")]      // component above 256
    [InlineData("1 1 UDP 0 192.0.2.1 40000 typ host")]                 // priority 0 sorts below everything
    [InlineData("1 1 UDP 2130706431 192.0.2.1 70000 typ host")]        // port outside 16 bits
    [InlineData("1 1 UDP 2130706431 192.0.2.1 40000 typ nonsense")]    // type nobody can prioritise
    [InlineData(" 1 UDP 2130706431 192.0.2.1 40000 typ host")]         // no foundation
    public void A_candidate_field_outside_the_grammar_is_dropped(string candidate)
    {
        var parsed = Parse($"{Audio}a=ice-ufrag:abcd\r\na=ice-pwd:{ValidPwd}\r\na=candidate:{candidate}\r\n");

        Assert.NotNull(parsed);
        Assert.Empty(parsed!.Media[0].Candidates);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("srflx")]
    [InlineData("prflx")]
    [InlineData("relay")]
    public void Every_defined_candidate_type_is_kept(string type)
    {
        var parsed = Parse(
            $"{Audio}a=ice-ufrag:abcd\r\na=ice-pwd:{ValidPwd}\r\n" +
            $"a=candidate:1 1 UDP 2130706431 192.0.2.1 40000 typ {type}\r\n");

        Assert.Single(parsed!.Media[0].Candidates);
        Assert.Equal(type, parsed.Media[0].Candidates[0].Type);
    }

    [Fact]
    public void An_unusable_candidate_does_not_discard_the_good_ones_beside_it()
    {
        var parsed = Parse(
            $"{Audio}a=ice-ufrag:abcd\r\na=ice-pwd:{ValidPwd}\r\n" +
            "a=candidate:1 1 UDP 2130706431 192.0.2.1 40000 typ host\r\n" +
            "a=candidate:2 0 UDP 2130706431 192.0.2.2 40002 typ host\r\n" +
            "a=candidate:3 1 UDP 1694498815 192.0.2.3 40004 typ srflx\r\n");

        Assert.Equal(2, parsed!.Media[0].Candidates.Count);
        Assert.Equal(new[] { "1", "3" }, parsed.Media[0].Candidates.Select(c => c.Foundation));
    }

    [Fact]
    public void An_unusual_transport_token_is_still_kept()
    {
        // Deliberately not whitelisted: deployed endpoints emit ssltcp and similar, and discarding the
        // line would throw away a candidate the reference stacks keep. Pairing filters what it cannot use.
        var parsed = Parse(
            $"{Audio}a=ice-ufrag:abcd\r\na=ice-pwd:{ValidPwd}\r\n" +
            "a=candidate:1 1 ssltcp 2130706431 192.0.2.1 40000 typ host\r\n");

        Assert.Single(parsed!.Media[0].Candidates);
    }

    // ── P2-9: rtcp-mux-only (RFC 8858) ───────────────────────────────────────

    [Fact]
    public void Rtcp_mux_only_is_parsed()
    {
        var parsed = Parse($"{Audio}a=rtcp-mux\r\na=rtcp-mux-only\r\n");

        Assert.True(parsed!.Media[0].RtcpMuxOnly);
        Assert.True(parsed.Media[0].RtcpMux);
    }

    [Fact]
    public void Rtcp_mux_only_on_its_own_is_read_as_a_mux_request()
    {
        // RFC 8858 §4 requires it to accompany a=rtcp-mux. An offer carrying only this one is still
        // unambiguous about what it wants, and answering it non-muxed is the case that actually breaks:
        // the peer opened no second port, so our RTCP would go where nothing listens.
        var parsed = Parse($"{Audio}a=rtcp-mux-only\r\n");

        Assert.True(parsed!.Media[0].RtcpMuxOnly);
        Assert.True(parsed.Media[0].RtcpMux);
    }

    [Fact]
    public void An_offer_without_the_attribute_does_not_claim_it()
    {
        var parsed = Parse($"{Audio}a=rtcp-mux\r\n");

        Assert.False(parsed!.Media[0].RtcpMuxOnly);
        Assert.True(parsed.Media[0].RtcpMux);
    }

    [Fact]
    public void A_mux_only_offer_is_answered_with_rtcp_mux()
    {
        var offer = Parse($"{Audio}a=rtcp-mux\r\na=rtcp-mux-only\r\n");

        var result = new SdpOfferAnswerNegotiator().NegotiateAnswer(
            offer!,
            new IPEndPoint(IPAddress.Parse("192.0.2.1"), 40000),
            [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }],
            SdpMediaDirection.SendRecv);

        Assert.True(result.Success);
        Assert.True(result.Answer!.Media[0].RtcpMux);
        Assert.True(result.RtcpMuxNegotiated);
    }
}
