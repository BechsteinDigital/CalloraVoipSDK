using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Transport selection from a Request-URI (RFC 3261 §26.2.2 and §19.1.5, RFC 5630) — the rule the compliance
/// matrix cites for "SIPS-URI erzwingt TLS" (#336).
/// </summary>
/// <remarks>
/// This is a security boundary, not a preference: <c>sips:</c> is the caller stating the request must not
/// travel in the clear. Getting it wrong downgrades silently, which is the failure mode nobody notices.
/// </remarks>
public sealed class SipTransportInferenceTests
{
    [Theory]
    [InlineData("sips:alice@example.com")]
    [InlineData("SIPS:alice@example.com")]              // scheme comparison is case-insensitive (§19.1.1)
    [InlineData("sips:alice@example.com;transport=tcp")] // the scheme outranks a plaintext parameter
    [InlineData("sips:alice@example.com;transport=udp")]
    public void A_sips_uri_never_resolves_to_a_plaintext_transport(string requestUri)
    {
        Assert.True(SipTransportRuntimeUtilities.TryInferTransportFromUri(requestUri, out var transport));

        Assert.Equal(SipTransportProtocol.Tls, transport);
    }

    [Fact]
    public void A_sips_uri_naming_wss_resolves_to_wss()
    {
        // Still secure, just the WebSocket carrier (RFC 7118).
        Assert.True(SipTransportRuntimeUtilities.TryInferTransportFromUri(
            "sips:alice@example.com;transport=wss", out var transport));

        Assert.Equal(SipTransportProtocol.Wss, transport);
    }

    [Theory]
    [InlineData("sip:bob@example.com;transport=udp", nameof(SipTransportProtocol.Udp))]
    [InlineData("sip:bob@example.com;transport=tcp", nameof(SipTransportProtocol.Tcp))]
    [InlineData("sip:bob@example.com;transport=tls", nameof(SipTransportProtocol.Tls))]
    [InlineData("sip:bob@example.com;transport=ws", nameof(SipTransportProtocol.Ws))]
    [InlineData("sip:bob@example.com;transport=wss", nameof(SipTransportProtocol.Wss))]
    [InlineData("sip:bob@example.com;TRANSPORT=TCP", nameof(SipTransportProtocol.Tcp))]
    public void An_explicit_transport_parameter_selects_directly(string requestUri, string expected)
    {
        // The enum is internal, so the expectation travels as its name — xUnit data must be public-typed.
        Assert.True(SipTransportRuntimeUtilities.TryInferTransportFromUri(requestUri, out var transport));

        Assert.Equal(expected, transport.ToString());
    }

    [Fact]
    public void Ws_and_wss_are_told_apart_despite_the_shared_prefix()
    {
        // ";transport=ws" is a prefix of ";transport=wss": a naive check in the wrong order downgrades a
        // secure WebSocket to a plaintext one.
        SipTransportRuntimeUtilities.TryInferTransportFromUri("sip:b@example.com;transport=wss", out var secure);
        SipTransportRuntimeUtilities.TryInferTransportFromUri("sip:b@example.com;transport=ws", out var plain);

        Assert.Equal(SipTransportProtocol.Wss, secure);
        Assert.Equal(SipTransportProtocol.Ws, plain);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sip:bob@example.com")]                  // no scheme demand, no parameter
    [InlineData("sip:bob@example.com;lr")]
    [InlineData("sip:bob@example.com;transport=sctp")]   // a transport this stack has no socket for
    public void A_uri_that_settles_nothing_defers_to_the_caller(string? requestUri)
    {
        // False means "not decided here" — the runtime then uses a learned endpoint hint or its default.
        // Answering Udp instead would look like a decision and quietly outrank both.
        Assert.False(SipTransportRuntimeUtilities.TryInferTransportFromUri(requestUri, out _));
    }
}
