using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// [SIP] #158 P1-5 (ring deadline): a UAS creates dialog session state (and raises IncomingInvite) for every
/// served-user INVITE, and the session sits in <see cref="SipDialogState.Ringing"/> until the consumer answers
/// or rejects it. Without a deadline an INVITE the application never answers pins that state indefinitely. These
/// tests pin the ring deadline: an un-answered session is auto-rejected 480 Temporarily Unavailable and removed,
/// while a session the consumer handles in time is not touched by the deadline.
/// </summary>
public sealed class SipInboundRingDeadlineTests
{
    private static readonly IPEndPoint Remote = new(IPAddress.Loopback, 5060);
    private static readonly TimeSpan ShortDeadline = TimeSpan.FromMilliseconds(150);

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

    private static async Task<IReadOnlyList<int>> WaitForStatusAsync(
        CapturingSipTransportRuntime transport,
        int expectedStatus)
    {
        for (var i = 0; i < 100; i++)
        {
            var statuses = transport.SnapshotResponses().Select(r => r.StatusCode).ToArray();
            if (statuses.Contains(expectedStatus))
                return statuses;
            await Task.Delay(20);
        }

        return transport.SnapshotResponses().Select(r => r.StatusCode).ToArray();
    }

    [Fact]
    public async Task Unanswered_inbound_invite_is_rejected_480_after_the_ring_deadline()
    {
        using var transport = new CapturingSipTransportRuntime();
        using var service = new SipCallSignalingService(
            transport,
            new NoopSipDigestAuthenticator(),
            NullLoggerFactory.Instance,
            inboundRingDeadline: ShortDeadline);

        ISipCallSession? captured = null;
        service.IncomingInvite += (_, e) => captured = e.Session;

        transport.DeliverInboundRequest(Remote, Invite("ring-1"));
        Assert.NotNull(captured);
        Assert.Equal(SipDialogState.Ringing, captured!.State);

        var statuses = await WaitForStatusAsync(transport, expectedStatus: 480);

        // The deadline fired: the session was auto-rejected 480 and driven to Terminated (which removes it from
        // the signaling service's session map via the normal lifecycle cleanup).
        Assert.Single(statuses, s => s == 480);
        Assert.Equal(SipDialogState.Terminated, captured.State);
    }

    [Fact]
    public async Task Inbound_invite_the_consumer_rejects_in_time_is_not_touched_by_the_ring_deadline()
    {
        using var transport = new CapturingSipTransportRuntime();
        using var service = new SipCallSignalingService(
            transport,
            new NoopSipDigestAuthenticator(),
            NullLoggerFactory.Instance,
            inboundRingDeadline: ShortDeadline);

        ISipCallSession? captured = null;
        service.IncomingInvite += (_, e) => captured = e.Session;

        transport.DeliverInboundRequest(Remote, Invite("ring-2"));
        Assert.NotNull(captured);

        // The consumer decides before the deadline elapses. This must cancel the ring-deadline timer so no
        // spurious 480 is emitted on top of the consumer's own 603.
        await captured!.RejectAsync(603, "Decline");

        // Wait well past the deadline; the cancelled timer must not fire a 480.
        await Task.Delay(ShortDeadline + TimeSpan.FromMilliseconds(300));

        var statuses = transport.SnapshotResponses().Select(r => r.StatusCode).ToArray();
        Assert.Contains(603, statuses);
        Assert.DoesNotContain(480, statuses);
    }
}
