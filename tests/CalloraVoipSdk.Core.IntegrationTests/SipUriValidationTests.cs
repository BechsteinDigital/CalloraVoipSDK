using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// [SIP] #158 P3-15: <see cref="SipProtocol.TryParseSipUri"/> is the ingress validity gate
/// (<c>SipIngressRequestPolicy</c>), and its port and host output feed route resolution, which ends in
/// <c>new IPEndPoint(address, port)</c>. A port outside 1–65535 or a host that is not a host therefore has to
/// fail the parse, not travel onward.
/// </summary>
public sealed class SipUriValidationTests
{
    [Theory]
    // A port of 0 addresses no service; RFC 3261 §19.1.1 gives no meaning to it in a URI.
    [InlineData("sip:bob@example.test:0")]
    [InlineData("sip:bob@example.test:65536")]
    [InlineData("sip:bob@example.test:99999")]
    [InlineData("sip:bob@example.test:4294967296")]
    // int.TryParse accepts a sign and surrounding whitespace — neither is a SIP port (port = 1*DIGIT).
    [InlineData("sip:bob@example.test:-1")]
    [InlineData("sip:bob@example.test:+5060")]
    [InlineData("sip:bob@example.test: 5060")]
    [InlineData("sip:bob@[::1]:99999")]
    [InlineData("sip:bob@[::1]:-1")]
    public void A_port_outside_the_valid_range_fails_the_parse(string uri)
    {
        Assert.False(SipProtocol.TryParseSipUri(uri, out _, out _, out _));
    }

    [Theory]
    // A colon in the host part means a port follows. When it is not a port, the URI is malformed — the old
    // fallback kept the whole thing as the host, so "example.test:abc" parsed as a host named that.
    [InlineData("sip:bob@example.test:abc")]
    [InlineData("sip:bob@example.test:")]
    [InlineData("sip:bob@::1")]
    // Whitespace and delimiters cannot appear in a host (RFC 3261 §25.1 host = hostname / IPv4 / IPv6ref).
    [InlineData("sip:bob@ex ample.test")]
    [InlineData("sip:bob@exa/mple.test")]
    [InlineData("sip:bob@exa\\mple.test")]
    [InlineData("sip:bob@example..test")]
    [InlineData("sip:bob@.example.test")]
    // Bracketed hosts are IPv6 references; anything else in brackets is not a host.
    [InlineData("sip:bob@[not-an-ip]")]
    [InlineData("sip:bob@[example.test]")]
    public void A_host_that_is_not_a_host_fails_the_parse(string uri)
    {
        Assert.False(SipProtocol.TryParseSipUri(uri, out _, out _, out _));
    }

    [Theory]
    [InlineData("sip:bob@example.test", "bob", "example.test", null)]
    [InlineData("sip:bob@example.test:5060", "bob", "example.test", 5060)]
    [InlineData("sip:bob@example.test:65535", "bob", "example.test", 65535)]
    [InlineData("sip:bob@example.test:1", "bob", "example.test", 1)]
    [InlineData("sips:bob@example.test:5061", "bob", "example.test", 5061)]
    [InlineData("sip:192.0.2.10:5060", "", "192.0.2.10", 5060)]
    [InlineData("sip:bob@[::1]:5061", "bob", "::1", 5061)]
    [InlineData("sip:bob@[2001:db8::1]", "bob", "2001:db8::1", null)]
    // A trailing dot is a legal FQDN root label, and underscores appear in real deployments.
    [InlineData("sip:bob@example.test.", "bob", "example.test.", null)]
    [InlineData("sip:bob@my_host.example.test", "bob", "my_host.example.test", null)]
    [InlineData("<sip:bob@example.test:5060;transport=tcp>", "bob", "example.test", 5060)]
    public void Valid_uris_keep_parsing(string uri, string expectedUser, string expectedHost, int? expectedPort)
    {
        Assert.True(SipProtocol.TryParseSipUri(uri, out var user, out var host, out var port));
        Assert.Equal(expectedUser, user);
        Assert.Equal(expectedHost, host);
        Assert.Equal(expectedPort, port);
    }

    [Theory]
    [InlineData("sip:bob@example.test:99999")]
    [InlineData("sip:bob@example.test:0")]
    [InlineData("sip:bob@ex ample.test")]
    public void The_ingress_rejects_a_request_uri_with_an_impossible_host_or_port(string requestUri)
    {
        var request = Invite(requestUri);

        Assert.False(SipIngressRequestPolicy.TryValidateIngressRequest(request, out var code, out var reason));
        // Not 416: the scheme is supported, the URI is simply malformed (RFC 3261 §8.2.1 vs §8.2.2).
        Assert.Equal(400, code);
        Assert.Equal("Bad Request", reason);
    }

    [Fact]
    public void The_ingress_still_accepts_a_well_formed_request_uri()
    {
        Assert.True(SipIngressRequestPolicy.TryValidateIngressRequest(
            Invite("sip:bob@example.test:5060"),
            out _,
            out _));
    }

    private static SipRequest Invite(string requestUri)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = "SIP/2.0/UDP 203.0.113.9:5060;branch=z9hG4bK-uri",
            ["Max-Forwards"] = "70",
            ["From"] = "<sip:alice@example.test>;tag=from-uri",
            ["To"] = "<sip:bob@example.test>",
            ["Call-ID"] = "uri@example.test",
            ["CSeq"] = "1 INVITE",
            ["Content-Length"] = "0",
        };
        return new SipRequest("INVITE", requestUri, headers, body: string.Empty);
    }
}
