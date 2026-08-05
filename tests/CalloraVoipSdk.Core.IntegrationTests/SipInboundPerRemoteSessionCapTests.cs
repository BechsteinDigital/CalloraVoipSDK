using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// [SIP] #158 P1-5 (per-remote cap): the global inbound-session cap alone lets a single source IP consume every
/// slot. These tests pin the per-remote cap: INVITEs beyond the per-remote ceiling from one address are answered
/// 486 Busy Here, while other addresses keep their own independent budget.
/// </summary>
public sealed class SipInboundPerRemoteSessionCapTests
{
    private static IPEndPoint Remote(string address) => new(IPAddress.Parse(address), 5060);

    private static SipRequest Invite(string callId)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = $"SIP/2.0/UDP 203.0.113.9:5060;branch=z9hG4bK-{callId}",
            ["Max-Forwards"] = "70",
            ["From"] = $"<sip:alice@example.test>;tag=from-{callId}",
            ["To"] = "<sip:bob@example.test>",
            ["Call-ID"] = $"{callId}@example.test",
            ["CSeq"] = "1 INVITE",
            ["Content-Length"] = "0",
        };
        return new SipRequest("INVITE", "sip:bob@example.test", headers, body: string.Empty);
    }

    private static async Task<int> WaitForBusyCountAsync(CapturingSipTransportRuntime transport, int expected)
    {
        for (var i = 0; i < 50; i++)
        {
            var busy = transport.SnapshotResponses().Count(r => r.StatusCode == 486);
            if (busy >= expected)
                return busy;
            await Task.Delay(10);
        }

        return transport.SnapshotResponses().Count(r => r.StatusCode == 486);
    }

    [Fact]
    public async Task Invites_beyond_the_per_remote_cap_from_one_address_are_answered_486()
    {
        using var transport = new CapturingSipTransportRuntime();
        using var service = new SipCallSignalingService(
            transport,
            new NoopSipDigestAuthenticator(),
            NullLoggerFactory.Instance,
            maxInboundSessionsPerRemote: 2);

        var incoming = 0;
        service.IncomingInvite += (_, _) => Interlocked.Increment(ref incoming);

        var remote = Remote("203.0.113.9");
        transport.DeliverInboundRequest(remote, Invite("ip-1"));
        transport.DeliverInboundRequest(remote, Invite("ip-2"));
        transport.DeliverInboundRequest(remote, Invite("ip-3"));

        Assert.Equal(2, Volatile.Read(ref incoming));
        var busy = await WaitForBusyCountAsync(transport, expected: 1);
        Assert.Equal(1, busy);
    }

    [Fact]
    public async Task Distinct_addresses_keep_independent_per_remote_budgets()
    {
        using var transport = new CapturingSipTransportRuntime();
        using var service = new SipCallSignalingService(
            transport,
            new NoopSipDigestAuthenticator(),
            NullLoggerFactory.Instance,
            maxInboundSessionsPerRemote: 2);

        var incoming = 0;
        service.IncomingInvite += (_, _) => Interlocked.Increment(ref incoming);

        transport.DeliverInboundRequest(Remote("203.0.113.10"), Invite("a-1"));
        transport.DeliverInboundRequest(Remote("203.0.113.10"), Invite("a-2"));
        transport.DeliverInboundRequest(Remote("203.0.113.11"), Invite("b-1"));
        transport.DeliverInboundRequest(Remote("203.0.113.11"), Invite("b-2"));

        // Each address stays within its own per-remote budget → all four create sessions, none is rejected.
        Assert.Equal(4, Volatile.Read(ref incoming));
        await Task.Delay(80);
        Assert.DoesNotContain(486, transport.SnapshotResponses().Select(r => r.StatusCode).ToArray());
    }
}
