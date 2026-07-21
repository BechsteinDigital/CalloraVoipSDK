using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// CF-067: Digest <c>qop=auth-int</c> support (RFC 7616 §3.4.3 — the entity body is folded into the digest) with
/// <c>auth</c> preferred when both are offered; and Reason-Phrase control-character hardening (RFC 3261 §7.2 —
/// no control characters other than SP/HTAB may reach the status line).
/// </summary>
public sealed class SipDigestAuthIntAndReasonTests
{
    private const string Realm = "biloxi.com";
    private const string Nonce = "zanzibar";
    private const string Username = "bob";
    private const string Password = "secret";
    private const string Uri = "sip:biloxi.com";

    // ── qop=auth-int (RFC 7616) ──────────────────────────────────────────────────

    [Fact]
    public void An_auth_int_only_challenge_selects_auth_int()
    {
        var auth = new SipDigestAuthentication();
        var challenge = $"Digest realm=\"{Realm}\", nonce=\"{Nonce}\", qop=\"auth-int\"";

        var ok = auth.TryCreateAuthorizationHeader(
            challenge, Username, Password, "INVITE", Uri, nonceCount: 1, out var header, body: "v=0\r\no=- 1 1 IN IP4 x");

        Assert.True(ok);
        Assert.Contains("qop=auth-int,", header);
        Assert.Contains("nc=00000001", header);
        Assert.Contains("cnonce=", header);
        Assert.Contains("response=", header);
    }

    [Fact]
    public void Auth_is_preferred_when_both_auth_and_auth_int_are_offered()
    {
        var auth = new SipDigestAuthentication();
        var challenge = $"Digest realm=\"{Realm}\", nonce=\"{Nonce}\", qop=\"auth,auth-int\"";

        var ok = auth.TryCreateAuthorizationHeader(
            challenge, Username, Password, "INVITE", Uri, nonceCount: 1, out var header, body: "body");

        Assert.True(ok);
        Assert.Contains("qop=auth,", header);
        Assert.DoesNotContain("qop=auth-int", header);
    }

    // ── Reason-Phrase control-char hardening (RFC 3261 §7.2) ──────────────────────

    [Theory]
    [InlineData("OK")]
    [InlineData("Not Found")]
    public void A_reason_phrase_of_printable_text_serializes(string reason)
    {
        var codec = new SipWireProtocol();

        var bytes = codec.SerializeResponse(200, reason, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        Assert.NotEmpty(bytes);
    }

    [Fact]
    public void A_reason_phrase_with_a_horizontal_tab_is_permitted()
    {
        var codec = new SipWireProtocol();
        var reason = "Busy" + (char)9 + "Here"; // HTAB (0x09) is allowed by RFC 3261 §7.2

        Assert.NotEmpty(codec.SerializeResponse(200, reason, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData(0)]    // NUL
    [InlineData(7)]    // BEL
    [InlineData(27)]   // ESC
    [InlineData(127)]  // DEL
    [InlineData(13)]   // CR
    [InlineData(10)]   // LF
    public void A_reason_phrase_with_a_control_char_is_rejected(int controlCode)
    {
        var codec = new SipWireProtocol();
        var reason = "Busy" + (char)controlCode + "Here";

        Assert.Throws<ArgumentException>(() =>
            codec.SerializeResponse(200, reason, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
    }
}
