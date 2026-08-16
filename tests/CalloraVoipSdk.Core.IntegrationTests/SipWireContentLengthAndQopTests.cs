using System.Text;
using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #158 P2-11 and P2-14 — two places where a malformed or unsupported input was quietly turned into
/// something the code could proceed with, instead of being refused.
/// </summary>
public sealed class SipWireContentLengthAndQopTests
{
    // ── P2-11: contradictory Content-Length ──────────────────────────────────

    private static byte[] Register(params string[] contentLengthRows)
    {
        var message = new StringBuilder()
            .Append("REGISTER sip:example.com SIP/2.0\r\n")
            .Append("Via: SIP/2.0/UDP 192.0.2.1:5060;branch=z9hG4bK1\r\n")
            .Append("From: <sip:alice@example.com>;tag=a\r\n")
            .Append("To: <sip:alice@example.com>\r\n")
            .Append("Call-ID: c1\r\n")
            .Append("CSeq: 1 REGISTER\r\n");

        foreach (var row in contentLengthRows)
            message.Append("Content-Length: ").Append(row).Append("\r\n");

        return Encoding.UTF8.GetBytes(message.Append("\r\n").ToString());
    }

    [Fact]
    public void Two_contradictory_content_length_rows_are_refused()
    {
        // The parser had a check for exactly this — it compares the rows and rejects a mismatch — but
        // CommitHeader overwrote the first row before it ever ran, so it never saw two values. With the
        // smaller value winning, the body would be cut short: two peers reading the same bytes disagree
        // about where the message ends.
        Assert.False(new SipWireProtocol().TryParseRequest(Register("120", "0"), out _));
    }

    [Fact]
    public void The_order_of_the_contradiction_does_not_matter()
    {
        // Growing the value is just as wrong as shrinking it, and last-wins made only one of the two
        // observable.
        Assert.False(new SipWireProtocol().TryParseRequest(Register("0", "120"), out _));
    }

    [Fact]
    public void Repeating_the_same_value_is_tolerated()
    {
        // Malformed per RFC 3261 §20 (Content-Length is not a comma-list header), but harmless: both
        // readers agree on the length, so refusing would buy nothing and break a sloppy peer.
        Assert.True(new SipWireProtocol().TryParseRequest(Register("0", "0"), out var request));
        Assert.NotNull(request);
    }

    [Fact]
    public void A_single_row_still_parses()
    {
        Assert.True(new SipWireProtocol().TryParseRequest(Register("0"), out var request));
        Assert.NotNull(request);
    }

    // ── P2-14: qop offered but unsupported ───────────────────────────────────

    private static bool TryAuthorize(string challenge) =>
        new SipDigestAuthentication().TryCreateAuthorizationHeader(
            challenge, "alice", "secret", "REGISTER", "sip:example.com", 1, out _);

    [Fact]
    public void An_unsupported_qop_is_refused_rather_than_answered_qop_less()
    {
        // The old code mapped "no supported qop" onto the same null as "no qop at all" and produced the
        // RFC 2069 legacy response: no nc, no cnonce, no qop in the hash. That is weaker than what the
        // server asked for, and a strict server rejects it anyway — the failure just surfaced later and
        // less clearly.
        Assert.False(TryAuthorize("Digest realm=\"r\", nonce=\"n\", qop=\"auth-conf\""));
    }

    [Fact]
    public void A_supported_qop_still_authenticates()
    {
        Assert.True(TryAuthorize("Digest realm=\"r\", nonce=\"n\", qop=\"auth\""));
    }

    [Fact]
    public void Auth_is_chosen_when_both_are_offered()
    {
        Assert.True(TryAuthorize("Digest realm=\"r\", nonce=\"n\", qop=\"auth,auth-int\""));
    }

    [Fact]
    public void Auth_int_alone_is_still_accepted()
    {
        Assert.True(TryAuthorize("Digest realm=\"r\", nonce=\"n\", qop=\"auth-int\""));
    }

    [Fact]
    public void A_challenge_without_qop_keeps_working()
    {
        // RFC 2069 servers exist; absence of qop is not the same as an unsupported one, and this is the
        // distinction the fix introduces.
        Assert.True(TryAuthorize("Digest realm=\"r\", nonce=\"n\""));
    }

    [Fact]
    public void An_empty_qop_is_treated_as_absent()
    {
        // qop="" is malformed rather than a demand for something we cannot do; refusing it would break
        // peers that emit it without gaining any security.
        Assert.True(TryAuthorize("Digest realm=\"r\", nonce=\"n\", qop=\"\""));
    }
}
