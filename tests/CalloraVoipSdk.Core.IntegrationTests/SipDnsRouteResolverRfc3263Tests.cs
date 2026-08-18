using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Routing;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using DnsClient;
using DnsClient.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The RFC 3263 resolution chain — NAPTR → SRV → A/AAAA — driven from canned DNS answers (#336).
/// </summary>
/// <remarks>
/// Against live DNS the interesting part is untestable: the ordering rules are whatever the zone happens to
/// say that day, so an assertion either restates the zone or is dropped. Canned answers make the rules
/// themselves the subject — NAPTR order/preference, the service field deciding the transport, SRV priority,
/// and the port a SIPS target must land on.
/// </remarks>
public sealed class SipDnsRouteResolverRfc3263Tests
{
    // RFC 2606 reserves .invalid so it can never resolve. That matters here: the resolver falls back to a
    // real Dns.GetHostAddressesAsync when its chain yields nothing, and against a live name a broken test
    // would quietly come back green with whatever that zone answers — which is how the reference stack's
    // DNS test ended up asserting nothing but NotNull.
    private const string Domain = "example.invalid";

    [Fact]
    public async Task Naptr_service_fields_decide_the_transport_and_order_decides_the_winner()
    {
        // RFC 3263 §4.1: lowest order wins, preference breaks a tie. Here TCP is offered ahead of UDP, so a
        // caller with no transport preference must come out on TCP — the point of publishing NAPTR at all.
        // BOTH branches resolve all the way down, so the outcome can only come from the order field. With
        // only one branch reachable the test would pass whatever the ordering does — which it did, until a
        // mutation reversing the sort stayed green.
        var dns = new StubDns()
            .Naptr(Domain, order: 10, preference: 50, service: "SIP+D2T", replacement: $"_sip._tcp.{Domain}")
            .Naptr(Domain, order: 20, preference: 50, service: "SIP+D2U", replacement: $"_sip._udp.{Domain}")
            .Srv($"_sip._tcp.{Domain}", priority: 10, weight: 0, port: 5060, target: $"tcp-proxy.{Domain}")
            .Srv($"_sip._udp.{Domain}", priority: 10, weight: 0, port: 5060, target: $"udp-proxy.{Domain}")
            .A($"tcp-proxy.{Domain}", "192.0.2.10")
            .A($"udp-proxy.{Domain}", "192.0.2.20");

        var result = await Resolve(dns, Domain);

        Assert.Equal(
            [SipTransportProtocol.Tcp, SipTransportProtocol.Udp],
            result.Candidates.Select(c => c.Transport).ToArray());
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.0.2.10"), 5060), result.Candidates[0].EndPoint);
    }

    [Fact]
    public async Task A_sips_naptr_service_resolves_to_tls()
    {
        // RFC 3263 §4.1 / RFC 5630: SIPS+D2T is the only mapping that may carry a secure request.
        var dns = new StubDns()
            .Naptr(Domain, order: 10, preference: 50, service: "SIPS+D2T", replacement: $"_sips._tcp.{Domain}")
            .Srv($"_sips._tcp.{Domain}", priority: 10, weight: 0, port: 5061, target: $"proxy.{Domain}")
            .A($"proxy.{Domain}", "192.0.2.10");

        var candidate = Assert.Single((await Resolve(dns, Domain, SipTransportProtocol.Tls)).Candidates);

        Assert.Equal(SipTransportProtocol.Tls, candidate.Transport);
        Assert.Equal(5061, candidate.EndPoint.Port);
    }

    [Fact]
    public async Task Srv_priority_orders_the_candidates_lowest_first()
    {
        // RFC 2782: priority is absolute — a backup proxy must never be tried before the primary.
        var dns = new StubDns()
            .Naptr(Domain, order: 10, preference: 50, service: "SIP+D2U", replacement: $"_sip._udp.{Domain}")
            .Srv($"_sip._udp.{Domain}", priority: 20, weight: 0, port: 5060, target: $"backup.{Domain}")
            .Srv($"_sip._udp.{Domain}", priority: 10, weight: 0, port: 5060, target: $"primary.{Domain}")
            .A($"primary.{Domain}", "192.0.2.1")
            .A($"backup.{Domain}", "192.0.2.2");

        var result = await Resolve(dns, Domain);

        Assert.Equal(
            [IPAddress.Parse("192.0.2.1"), IPAddress.Parse("192.0.2.2")],
            result.Candidates.Select(c => c.EndPoint.Address).ToArray());
    }

    [Fact]
    public async Task Without_naptr_the_chain_falls_through_to_srv()
    {
        // RFC 3263 §4.1: no NAPTR is the common case — most zones publish only SRV.
        var dns = new StubDns()
            .Srv($"_sip._udp.{Domain}", priority: 10, weight: 0, port: 5080, target: $"proxy.{Domain}")
            .A($"proxy.{Domain}", "192.0.2.10");

        var candidate = Assert.Single((await Resolve(dns, Domain, SipTransportProtocol.Udp)).Candidates);

        Assert.Equal(5080, candidate.EndPoint.Port);
    }

    [Fact]
    public async Task Without_naptr_or_srv_the_chain_falls_through_to_a_records()
    {
        // RFC 3263 §4.2: the last resort is the host address at the default port for the transport.
        var dns = new StubDns().A(Domain, "192.0.2.10");

        var candidate = Assert.Single((await Resolve(dns, Domain, SipTransportProtocol.Udp)).Candidates);

        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.0.2.10"), 5060), candidate.EndPoint);
    }

    [Fact]
    public async Task A_naptr_service_the_sdk_does_not_speak_is_skipped_rather_than_followed()
    {
        // A zone may advertise transports this stack has no socket for (SIP+D2S/SCTP). Following one would
        // produce a candidate that can never connect and would mask the usable UDP row behind it.
        // The SCTP branch resolves all the way down too, so following it would produce a visible candidate.
        // Without that, the assertion passes for the wrong reason — the branch was simply a dead end.
        var dns = new StubDns()
            .Naptr(Domain, order: 10, preference: 50, service: "SIP+D2S", replacement: $"_sip._sctp.{Domain}")
            .Naptr(Domain, order: 20, preference: 50, service: "SIP+D2U", replacement: $"_sip._udp.{Domain}")
            .Srv($"_sip._sctp.{Domain}", priority: 10, weight: 0, port: 5060, target: $"sctp-proxy.{Domain}")
            .Srv($"_sip._udp.{Domain}", priority: 10, weight: 0, port: 5060, target: $"udp-proxy.{Domain}")
            .A($"sctp-proxy.{Domain}", "192.0.2.99")
            .A($"udp-proxy.{Domain}", "192.0.2.10");

        var candidate = Assert.Single((await Resolve(dns, Domain)).Candidates);

        Assert.Equal(SipTransportProtocol.Udp, candidate.Transport);
        Assert.Equal(IPAddress.Parse("192.0.2.10"), candidate.EndPoint.Address);
    }

    [Fact]
    public async Task A_naptr_offering_only_tls_is_not_used_for_a_plaintext_request()
    {
        // A caller that asked for UDP must not be routed onto a TLS candidate: the transports are not
        // interchangeable, and silently upgrading would hand the peer a socket it never agreed to.
        var dns = new StubDns()
            .Naptr(Domain, order: 10, preference: 50, service: "SIPS+D2T", replacement: $"_sips._tcp.{Domain}")
            .Srv($"_sips._tcp.{Domain}", priority: 10, weight: 0, port: 5061, target: $"proxy.{Domain}")
            .A($"proxy.{Domain}", "192.0.2.10");

        // No usable route rather than a silently upgraded one — and it says so instead of returning empty,
        // so the caller cannot mistake "nothing matched" for "nothing to do".
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Resolve(dns, Domain, SipTransportProtocol.Udp));

        Assert.Contains(Domain, ex.Message, StringComparison.Ordinal);
    }

    private static Task<SipRouteResolutionResult> Resolve(
        StubDns dns, string host, SipTransportProtocol? preferred = null)
    {
        // Weight draw pinned to 0 so a weighted group resolves deterministically (RFC 2782 §weight).
        var resolver = new SipDnsRouteResolver(dns, NullLoggerFactory.Instance, nextInt: _ => 0);
        return resolver.ResolveAsync(new SipRouteResolutionRequest
        {
            Host = host,
            PreferredTransport = preferred ?? SipTransportProtocol.Udp,
        });
    }
}
