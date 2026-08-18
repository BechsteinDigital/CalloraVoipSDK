using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The SIP-URI escaping and tel→SIP mapping the compliance matrix records as done for RFC 3261 §19.1.2 and
/// §19.1.6. Neither had a test, and §19.1.4 — recorded the same way — turned out to be wrong in half the cases
/// the RFC itself lists, so these claims are pinned rather than trusted.
/// </summary>
public sealed class SipUriEncodingTests
{
    // RFC 3261 §25.1: user = 1*( unreserved / escaped / user-unreserved ), where
    //   unreserved      = alphanum / mark,  mark = "-" "_" "." "!" "~" "*" "'" "(" ")"
    //   user-unreserved = "&" "=" "+" "$" "," ";" "?" "/"
    [Theory]
    [InlineData("alice", "alice")]
    [InlineData("Alice42", "Alice42")]
    [InlineData("a-b_c.d!e~f*g'h(i)j", "a-b_c.d!e~f*g'h(i)j")]   // mark: never escaped
    [InlineData("a&b=c+d$e,f;g?h/i", "a&b=c+d$e,f;g?h/i")]       // user-unreserved: never escaped
    public void Characters_allowed_in_a_user_part_are_left_alone(string user, string expected)
        => Assert.Equal(expected, SipProtocol.SipUriEncodeUser(user));

    [Theory]
    [InlineData("a@b", "a%40b")]      // "@" would end the user part
    [InlineData("a b", "a%20b")]      // space is not allowed unescaped
    [InlineData("a:b", "a%3Ab")]      // ":" separates user from password
    [InlineData("a%b", "a%25b")]      // the escape character itself must be escaped
    [InlineData("a#b", "a%23b")]
    public void Characters_that_would_change_the_uri_structure_are_escaped(string user, string expected)
        => Assert.Equal(expected, SipProtocol.SipUriEncodeUser(user));

    [Fact]
    public void Non_ascii_is_escaped_as_utf8_octets()
    {
        // "ä" is U+00E4 → UTF-8 C3 A4. Escaping per octet is what §19.1.2 requires; escaping per char would
        // produce something no peer could decode back.
        Assert.Equal("%C3%A4", SipProtocol.SipUriEncodeUser("ä"));
    }

    [Fact]
    public void Encoding_is_reversible_by_the_decoder()
    {
        foreach (var user in new[] { "alice", "a@b", "a b", "a%b", "ä", "a&b=c" })
            Assert.Equal(user, SipProtocol.SipUriDecodeUser(SipProtocol.SipUriEncodeUser(user)));
    }

    [Fact]
    public void An_empty_or_absent_user_encodes_to_nothing()
    {
        Assert.Equal(string.Empty, SipProtocol.SipUriEncodeUser(null));
        Assert.Equal(string.Empty, SipProtocol.SipUriEncodeUser(""));
        Assert.Equal(string.Empty, SipProtocol.SipUriDecodeUser(null));
    }

    // RFC 3261 §19.1.6 / RFC 3966: a tel URI maps to a SIP URI whose user part is the number, carrying
    // user=phone so the far end knows to compare it as a telephone number rather than as a string.
    [Theory]
    [InlineData("tel:+4930123456", "sip:+4930123456@example.com;user=phone")]
    // RFC 3966 §3 visual-separator is exactly "-" "." "(" ")" — a space is not one, and would not be legal
    // in a URI unescaped anyway.
    [InlineData("tel:+49-30-123456", "sip:+4930123456@example.com;user=phone")]
    [InlineData("tel:+49(30)123456", "sip:+4930123456@example.com;user=phone")]
    [InlineData("tel:+49.30.123456", "sip:+4930123456@example.com;user=phone")]
    [InlineData("TEL:+4930123456", "sip:+4930123456@example.com;user=phone")]        // scheme is case-insensitive
    [InlineData("tel:+4930123456;phone-context=+49", "sip:+4930123456@example.com;user=phone")]
    public void A_tel_uri_maps_to_a_sip_uri(string telUri, string expected)
    {
        Assert.True(SipProtocol.TryTelUriToSipUri(telUri, "example.com", out var sipUri));
        Assert.Equal(expected, sipUri);
    }

    [Theory]
    [InlineData("sip:alice@example.com")]   // not a tel URI
    [InlineData("tel:")]                    // no number
    [InlineData("tel:;phone-context=+49")]  // parameters only
    [InlineData("")]
    [InlineData(null)]
    public void A_non_tel_or_empty_uri_does_not_map(string? telUri)
        => Assert.False(SipProtocol.TryTelUriToSipUri(telUri, "example.com", out _));

    [Fact]
    public void A_missing_domain_does_not_map()
        => Assert.False(SipProtocol.TryTelUriToSipUri("tel:+4930123456", "", out _));
}
