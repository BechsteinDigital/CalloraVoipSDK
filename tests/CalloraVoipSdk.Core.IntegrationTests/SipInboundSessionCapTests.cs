using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// [SIP] #158 P1-5: a UAS creates dialog session state (and fires IncomingInvite) for every served-user
/// INVITE, before any line/trunk takes ownership. Without a cap a flood of INVITEs with distinct Call-IDs
/// pins unbounded session state. These tests pin the concurrent-inbound-session cap: INVITEs beyond it are
/// answered 486 Busy Here and create no session.
/// </summary>
public sealed class SipInboundSessionCapTests
{
    private static readonly IPEndPoint Remote = new(IPAddress.Loopback, 5060);

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

    private static async Task<IReadOnlyList<int>> WaitForStatusesAsync(CapturingSipTransportRuntime transport, int expectedFinal)
    {
        // Ingress responses are fire-and-forget; poll until the expected 486 count is observed.
        for (var i = 0; i < 50; i++)
        {
            var statuses = transport.SnapshotResponses().Select(r => r.StatusCode).ToArray();
            if (statuses.Count(s => s == 486) >= expectedFinal)
                return statuses;
            await Task.Delay(10);
        }

        return transport.SnapshotResponses().Select(r => r.StatusCode).ToArray();
    }

    [Fact]
    public async Task Invites_beyond_the_session_cap_are_answered_486_and_create_no_session()
    {
        using var transport = new CapturingSipTransportRuntime();
        using var service = new SipCallSignalingService(
            transport,
            new NoopSipDigestAuthenticator(),
            NullLoggerFactory.Instance,
            maxConcurrentInboundSessions: 2);

        var incoming = 0;
        service.IncomingInvite += (_, _) => Interlocked.Increment(ref incoming);

        transport.DeliverInboundRequest(Remote, Invite("cap-1"));
        transport.DeliverInboundRequest(Remote, Invite("cap-2"));
        transport.DeliverInboundRequest(Remote, Invite("cap-3"));

        // The first two INVITEs create sessions (IncomingInvite fires synchronously in the ingress handler);
        // the third hits the cap and never becomes a session.
        Assert.Equal(2, Volatile.Read(ref incoming));

        var statuses = await WaitForStatusesAsync(transport, expectedFinal: 1);
        // Exactly one 486 — for the third INVITE. The first two are left ringing for the consumer to answer.
        Assert.Single(statuses, s => s == 486);
    }

    [Fact]
    public async Task Invites_within_the_cap_all_create_sessions()
    {
        using var transport = new CapturingSipTransportRuntime();
        using var service = new SipCallSignalingService(
            transport,
            new NoopSipDigestAuthenticator(),
            NullLoggerFactory.Instance,
            maxConcurrentInboundSessions: 3);

        var incoming = 0;
        service.IncomingInvite += (_, _) => Interlocked.Increment(ref incoming);

        transport.DeliverInboundRequest(Remote, Invite("ok-1"));
        transport.DeliverInboundRequest(Remote, Invite("ok-2"));
        transport.DeliverInboundRequest(Remote, Invite("ok-3"));

        Assert.Equal(3, Volatile.Read(ref incoming));
        await Task.Delay(100);
        Assert.DoesNotContain(486, transport.SnapshotResponses().Select(r => r.StatusCode).ToArray());
    }
}
