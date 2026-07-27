using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Cancellation contract for an in-flight outbound INVITE at the signaling-service boundary.
///
/// When the caller cancels the dial while the INVITE is still ringing,
/// <see cref="SipCallSignalingService.InviteAsync"/> must NOT tear the session down — it has to stay
/// registered and reachable so the caller's cancellation path can put a wire-CANCEL on the wire
/// (RFC 3261 §9.1) and drive the dialog to Terminated(487). The earlier behavior swallowed the
/// <see cref="OperationCanceledException"/> in the generic failure catch and disposed the session
/// first, which reported Canceled/Terminated locally while stranding the UAS dialog with no CANCEL.
///
/// This exercises the real signaling stack (the PR #107 fake-channel test bypassed it), so it is the
/// canonical, Docker-free regression guard for the fix.
/// </summary>
public sealed class SipInviteCancellationTests
{
    private static SipInviteRequest NewInvite() => new()
    {
        LocalUsername = "alice",
        LocalDomain = "example.com",
        RemoteUri = "sip:bob@192.0.2.10",
        SessionDescription = "v=0\r\n",
        Timeout = TimeSpan.FromSeconds(30),
    };

    [Fact]
    public async Task InviteAsync_CancelledWhileRinging_KeepsSessionCancelableAndSendsWireCancel()
    {
        using var transport = new CapturingSipTransportRuntime();
        using var service = new SipCallSignalingService(
            transport,
            new NoopSipDigestAuthenticator(),
            NullLoggerFactory.Instance);

        // The far end rings but never answers, so the INVITE client transaction stays open and gives
        // the caller a window to cancel. Answer the later CANCEL with 200 so HangupAsync completes.
        transport.ResponseFactory = request =>
        {
            if (request.Method.Equals("INVITE", StringComparison.Ordinal))
                return CreateResponse(request, 180, "Ringing");
            if (request.Method.Equals("CANCEL", StringComparison.Ordinal))
                return CreateResponse(request, 200, "OK");
            return null;
        };

        ISipCallSession? session = null;
        using var cts = new CancellationTokenSource();

        var invite = service.InviteAsync(
            NewInvite(),
            onSessionCreated: s => session = s,
            cts.Token);

        // Wait until the INVITE is on the wire and the dialog is actually ringing before cancelling.
        await transport.WaitForRequestAsync("INVITE", TimeSpan.FromSeconds(2));
        await WaitForStateAsync(() => session, SipDialogState.Ringing, TimeSpan.FromSeconds(2));

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invite);

        // The fix: cancelling the in-flight INVITE must leave the session alive and ringing — not
        // disposed in the generic catch — so the caller still owns a cancelable dialog.
        Assert.NotNull(session);
        Assert.Equal(SipDialogState.Ringing, session!.State);

        // The canonical caller cancellation path: HangupAsync on the surviving ringing session puts a
        // CANCEL on the wire and drives the dialog to Terminated. Before the fix no CANCEL was sent.
        await session.HangupAsync();

        var cancel = await transport.WaitForRequestAsync("CANCEL", TimeSpan.FromSeconds(2));
        Assert.Equal("CANCEL", cancel.Method);
        Assert.Equal(SipDialogState.Terminated, session.State);
    }

    private static async Task WaitForStateAsync(
        Func<ISipCallSession?> session,
        SipDialogState expected,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (session() is { } current && current.State == expected)
                return;
            await Task.Delay(10);
        }

        throw new TimeoutException($"Session did not reach {expected} within {timeout}.");
    }

    private static SipResponse CreateResponse(CapturedSipRequest request, int statusCode, string reasonPhrase)
    {
        var toHeader = request.Headers["To"];
        if (SipProtocol.ExtractTag(toHeader) is null)
            toHeader = $"{toHeader};tag=remote-tag";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = request.Headers["Via"],
            ["From"] = request.Headers["From"],
            ["To"] = toHeader,
            ["Call-ID"] = request.Headers["Call-ID"],
            ["CSeq"] = request.Headers["CSeq"],
            ["Contact"] = "<sip:bob@192.0.2.10:5060>",
        };

        return new SipResponse(statusCode, reasonPhrase, headers, string.Empty);
    }
}
