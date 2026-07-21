using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The Via-parameter reading in <see cref="SipProtocol"/> was duplicated across
/// <c>ExtractViaReceivedRport</c> and <c>ResolveUdpResponseDestination</c> (HARD-G3/R6). Both now
/// share one <c>ReadViaParameter</c>/<c>ParsePort</c> scan. These tests pin the shared behaviour and
/// the two edge fixes the consolidation brought: an out-of-range <c>rport</c> no longer produces an
/// invalid endpoint, and a parameter whose name merely ends with the searched token is not matched.
/// </summary>
public sealed class SipProtocolViaParameterTests
{
    // ── CF-040: ReflectViaParameters idempotency (safe to apply at builder + transaction layer) ──

    [Fact]
    public void ReflectViaParameters_fills_bare_rport_and_adds_received_for_a_natted_source()
    {
        var via = "SIP/2.0/UDP 10.0.0.1:5060;branch=z9hG4bK1;rport";
        var source = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 40000);

        var reflected = SipProtocol.ReflectViaParameters(via, source);

        Assert.Contains(";rport=40000", reflected);
        Assert.Contains(";received=203.0.113.7", reflected);
    }

    [Fact]
    public void ReflectViaParameters_is_idempotent()
    {
        var via = "SIP/2.0/UDP 10.0.0.1:5060;branch=z9hG4bK1;rport";
        var source = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 40000);

        var once = SipProtocol.ReflectViaParameters(via, source);
        var twice = SipProtocol.ReflectViaParameters(once, source);

        Assert.Equal(once, twice); // a second pass must not duplicate received=/rport=
        Assert.Equal(1, CountOccurrences(twice, ";received="));
        Assert.Equal(1, CountOccurrences(twice, ";rport="));
    }

    [Fact]
    public void ReflectViaParameters_leaves_an_already_reflected_via_unchanged()
    {
        var via = "SIP/2.0/UDP 10.0.0.1:5060;branch=z9hG4bK1;received=203.0.113.7;rport=40000";
        var source = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 40000);

        Assert.Equal(via, SipProtocol.ReflectViaParameters(via, source));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    [Fact]
    public void ExtractViaReceivedRport_reads_received_and_rport()
    {
        var via = "SIP/2.0/UDP 10.0.0.1:5060;branch=z9hG4bK1;received=203.0.113.7;rport=40000";

        var (host, port) = SipProtocol.ExtractViaReceivedRport(via);

        Assert.Equal("203.0.113.7", host);
        Assert.Equal(40000, port);
    }

    [Fact]
    public void ExtractViaReceivedRport_returns_nulls_when_absent()
    {
        var (host, port) = SipProtocol.ExtractViaReceivedRport("SIP/2.0/UDP 10.0.0.1:5060;branch=z9hG4bK1");

        Assert.Null(host);
        Assert.Null(port);
    }

    [Fact]
    public void ResolveUdpResponseDestination_routes_to_received_and_rport()
    {
        var via = "SIP/2.0/UDP 10.0.0.1:5060;branch=z9hG4bK1;received=203.0.113.7;rport=40000";
        var actual = new IPEndPoint(IPAddress.Parse("198.51.100.9"), 1234);

        var target = SipProtocol.ResolveUdpResponseDestination(via, actual);

        Assert.Equal(IPAddress.Parse("203.0.113.7"), target.Address);
        Assert.Equal(40000, target.Port);
    }

    [Fact]
    public void ResolveUdpResponseDestination_falls_back_to_sent_by_port_without_rport()
    {
        var via = "SIP/2.0/UDP 10.0.0.1:5070;branch=z9hG4bK1";
        var actual = new IPEndPoint(IPAddress.Parse("198.51.100.9"), 1234);

        var target = SipProtocol.ResolveUdpResponseDestination(via, actual);

        // No received → actual source IP; no rport → sent-by port.
        Assert.Equal(IPAddress.Parse("198.51.100.9"), target.Address);
        Assert.Equal(5070, target.Port);
    }

    [Fact]
    public void ResolveUdpResponseDestination_ignores_out_of_range_rport_instead_of_throwing()
    {
        var via = "SIP/2.0/UDP 10.0.0.1:5070;received=203.0.113.7;rport=99999";
        var actual = new IPEndPoint(IPAddress.Parse("198.51.100.9"), 1234);

        // Pre-consolidation this produced new IPEndPoint(ip, 99999) → ArgumentOutOfRangeException.
        var target = SipProtocol.ResolveUdpResponseDestination(via, actual);

        Assert.Equal(IPAddress.Parse("203.0.113.7"), target.Address);
        Assert.Equal(5070, target.Port); // out-of-range rport dropped → sent-by port
    }

    [Fact]
    public void ResolveUdpResponseDestination_does_not_match_a_parameter_ending_in_received()
    {
        var via = "SIP/2.0/UDP 10.0.0.1:5070;xreceived=1.1.1.1";
        var actual = new IPEndPoint(IPAddress.Parse("198.51.100.9"), 1234);

        var target = SipProtocol.ResolveUdpResponseDestination(via, actual);

        // "xreceived" must not satisfy the received= scan → route to the actual source.
        Assert.Equal(IPAddress.Parse("198.51.100.9"), target.Address);
    }
}
