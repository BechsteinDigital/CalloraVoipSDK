using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// CF-047 (RFC 4028 §5/§6): when a peer or proxy rejects the outbound INVITE with 422 "Session Interval Too
/// Small" and a higher Min-SE, the UAC raises its offered Session-Expires (and Min-SE) to at least that value
/// and retries — bounded, so a peer that keeps rejecting cannot loop the transaction.
/// </summary>
public sealed class SipSessionTimerMinSeRetryTests
{
    private static SipResponse Echo(CapturedSipRequest req, int statusCode, string reason, (string, string)? extra = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = req.Headers["Via"],
            ["From"] = req.Headers["From"],
            ["To"] = req.Headers["To"],
            ["Call-ID"] = req.Headers["Call-ID"],
            ["CSeq"] = req.Headers["CSeq"],
            ["Contact"] = req.Headers.TryGetValue("Contact", out var c) ? c : "<sip:bob@192.0.2.10:5060>",
        };
        if (extra is { } e)
            headers[e.Item1] = e.Item2;
        return new SipResponse(statusCode, reason, headers, string.Empty);
    }

    [Fact]
    public async Task A_422_with_a_higher_min_se_retries_the_invite_with_the_raised_interval()
    {
        var transport = new CapturingSipTransportRuntime
        {
            // Reject the default 1800 offer with Min-SE 3600; accept the raised (3600) retry.
            ResponseFactory = req =>
            {
                if (req.Method != "INVITE")
                    return null;
                var se = req.Headers.TryGetValue("Session-Expires", out var s) ? s : string.Empty;
                return se.StartsWith("1800", StringComparison.Ordinal)
                    ? Echo(req, 422, "Session Interval Too Small", ("Min-SE", "3600"))
                    : Echo(req, 200, "OK");
            },
        };
        var context = new AckTestSipCallSessionContext(transport);
        var service = new SipCallSessionTransactionService(context, new SipCallSessionHeaderService(context));

        await service.SendInviteTransactionAsync(
            body: null, allowRingingTransition: true, SipDialogState.Established, CancellationToken.None);

        var invites = transport.SnapshotRequests().Where(r => r.Method == "INVITE").ToList();
        Assert.Equal(2, invites.Count);
        Assert.StartsWith("1800", invites[0].Headers["Session-Expires"]);
        Assert.StartsWith("3600", invites[1].Headers["Session-Expires"]); // raised to the peer's Min-SE
        Assert.Equal("3600", invites[1].Headers["Min-SE"]);               // our Min-SE raised too (§5)
        Assert.Equal(SipDialogState.Established, context.State);
    }

    [Fact]
    public async Task A_peer_that_keeps_rejecting_does_not_loop_and_fails_the_call()
    {
        var transport = new CapturingSipTransportRuntime
        {
            // Always 422 with the same Min-SE: once we have raised to it, a further 422 must not retry again.
            ResponseFactory = req => req.Method != "INVITE"
                ? null
                : Echo(req, 422, "Session Interval Too Small", ("Min-SE", "3600")),
        };
        var context = new AckTestSipCallSessionContext(transport);
        var service = new SipCallSessionTransactionService(context, new SipCallSessionHeaderService(context));

        var invite = service.SendInviteTransactionAsync(
            body: null, allowRingingTransition: true, SipDialogState.Established, CancellationToken.None);
        var completed = await Task.WhenAny(invite, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        Assert.True(completed == invite, "session-timer retry loop did not terminate within the timeout window");
        await Assert.ThrowsAsync<SipFinalResponseException>(() => invite).ConfigureAwait(false);

        var invites = transport.SnapshotRequests().Count(r => r.Method == "INVITE");
        Assert.True(invites <= 3, $"expected a bounded INVITE count, got {invites}");
    }
}
