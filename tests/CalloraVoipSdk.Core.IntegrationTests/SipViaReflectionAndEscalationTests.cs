using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Three claims the compliance matrix carried without a test naming what backs them (#336): Via reflection
/// (RFC 3261 §18.2.1 / RFC 3581 §4), UDP→TCP escalation for oversized requests (RFC 3261 §18.1.1), and
/// Require option-tag validation (§8.1.1.9 / §8.2.2.3 / §19.2, plus RFC 4488's <c>norefersub</c>).
/// </summary>
/// <remarks>
/// These are the parts a UA behind NAT depends on. A UAS that does not reflect <c>received</c> sends its
/// responses to an address the peer never had, and the call fails in the one deployment that is the norm
/// rather than the exception.
/// </remarks>
public sealed class SipViaReflectionAndEscalationTests
{
    private const string ViaHost = "SIP/2.0/UDP 192.0.2.10:5060;branch=z9hG4bK-1";

    [Fact]
    public void A_source_address_that_differs_from_sent_by_is_reflected_as_received()
    {
        // RFC 3261 §18.2.1 MUST: the UAS saw the request arrive from somewhere other than the Via claims,
        // which is exactly what a NAT does.
        var reflected = SipProtocol.ReflectViaRport(ViaHost, new IPEndPoint(IPAddress.Parse("198.51.100.7"), 33000));

        Assert.Contains(";received=198.51.100.7", reflected, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bare_rport_is_filled_in_with_the_port_the_request_arrived_on()
    {
        // RFC 3581 §4 MUST: the client asked to be told its mapped port by sending ;rport with no value.
        var reflected = SipProtocol.ReflectViaRport(
            ViaHost + ";rport", new IPEndPoint(IPAddress.Parse("198.51.100.7"), 33000));

        Assert.Contains(";rport=33000", reflected, StringComparison.Ordinal);
    }

    [Fact]
    public void A_matching_source_and_no_rport_leaves_the_via_untouched()
    {
        var reflected = SipProtocol.ReflectViaRport(ViaHost, new IPEndPoint(IPAddress.Parse("192.0.2.10"), 5060));

        Assert.Equal(ViaHost, reflected);
    }

    [Fact]
    public void Reflecting_twice_does_not_duplicate_the_parameters()
    {
        // The reflection runs both in the response builders and centrally in the transaction engine, so it
        // has to be idempotent — otherwise the second pass produces a malformed Via.
        var remote = new IPEndPoint(IPAddress.Parse("198.51.100.7"), 33000);
        var once = SipProtocol.ReflectViaRport(ViaHost + ";rport", remote);
        var twice = SipProtocol.ReflectViaRport(once, remote);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void An_escalated_request_advertises_tcp_in_its_via()
    {
        // RFC 3261 §18.1.1: a request that outgrew the UDP path is resent over TCP, and the Via transport
        // token has to follow — a response routed by a stale UDP token would go out the wrong socket.
        var escalated = SipTransportRuntimeUtilities.EscalateViaTransportToTcp(
            new Dictionary<string, string> { ["Via"] = ViaHost });

        Assert.Equal("SIP/2.0/TCP 192.0.2.10:5060;branch=z9hG4bK-1", escalated["Via"]);
    }

    [Fact]
    public void Escalation_leaves_headers_without_a_udp_via_alone()
    {
        var headers = new Dictionary<string, string> { ["Via"] = "SIP/2.0/TCP 192.0.2.10:5060;branch=z9hG4bK-1" };

        Assert.Same(headers, SipTransportRuntimeUtilities.EscalateViaTransportToTcp(headers));
    }

    [Theory]
    [InlineData("100rel")]
    [InlineData("timer")]
    [InlineData("replaces")]
    [InlineData("norefersub")]   // RFC 4488
    [InlineData("100rel, timer")]
    public void A_require_header_of_supported_tags_is_accepted(string requireHeader)
    {
        Assert.True(SipRequireOptionPolicy.TryValidateRequireHeader(requireHeader, out var unsupported));
        Assert.Equal(string.Empty, unsupported);
    }

    [Fact]
    public void An_unknown_require_tag_is_reported_for_the_unsupported_header()
    {
        // RFC 3261 §8.2.2.3: the 420 has to name what was not understood, or the peer cannot retry usefully.
        Assert.False(SipRequireOptionPolicy.TryValidateRequireHeader("timer, gruu", out var unsupported));
        Assert.Equal("gruu", unsupported);
    }

    [Fact]
    public void Only_the_unknown_tags_are_named_and_each_only_once()
    {
        Assert.False(SipRequireOptionPolicy.TryValidateRequireHeader("gruu, timer, gruu, path", out var unsupported));

        Assert.Equal("gruu, path", unsupported);
    }

    [Fact]
    public void An_absent_require_header_is_not_a_violation()
    {
        Assert.True(SipRequireOptionPolicy.TryValidateRequireHeader(null, out _));
        Assert.True(SipRequireOptionPolicy.TryValidateRequireHeader("  ", out _));
    }
}
