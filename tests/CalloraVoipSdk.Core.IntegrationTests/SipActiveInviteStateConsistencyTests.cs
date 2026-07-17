using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Consistency gate for the active-INVITE CANCEL state on <c>SipCallSession</c> (HARD-C2). The CSeq
/// and branch that identify the cancellable INVITE transaction are set and cleared as a pair; they
/// must never leak past an abnormal INVITE exit (a later CANCEL would target a dead transaction) and
/// must never be observed half-updated (a CANCEL built with a live CSeq and a null/stale branch).
/// </summary>
public sealed class SipActiveInviteStateConsistencyTests
{
    [Fact]
    public async Task Failing_ack_after_2xx_does_not_leak_cancellable_invite_state()
    {
        var transport = new CapturingSipTransportRuntime();
        var context = new AckTestSipCallSessionContext(transport);
        var service = new SipCallSessionTransactionService(
            context,
            new SipCallSessionHeaderService(context));

        // INVITE succeeds, but the ACK send fails. The INVITE is already non-cancellable at that
        // point, so the active-INVITE state must have been cleared before the ACK was attempted.
        transport.ThrowOnSendMethod = "ACK";
        transport.ResponseFactory = request =>
            request.Method.Equals("INVITE", StringComparison.Ordinal)
                ? CreateResponse(request, 200, "OK")
                : null;

        await Assert.ThrowsAnyAsync<Exception>(() => service.SendInviteTransactionAsync(
            body: "v=0\r\n",
            allowRingingTransition: true,
            successState: SipDialogState.Established,
            CancellationToken.None));

        // Without the pre-ACK clear the branch would still be set here (stale CANCEL target).
        Assert.False(context.HasPendingLocalInviteTransaction);

        // A CANCEL issued after the abandoned INVITE must therefore be a no-op.
        await service.SendCancelAsync(CancellationToken.None);
        Assert.DoesNotContain(
            transport.SnapshotRequests(),
            r => r.Method.Equals("CANCEL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Active_invite_snapshot_is_observed_as_a_consistent_pair_under_concurrency()
    {
        var session = NewOutboundSession();
        var context = new SipCallSessionContextAdapter(session);

        const int cseq = 42;
        const string branch = "z9hG4bK-active";

        using var stop = new CancellationTokenSource();
        var inconsistent = 0;

        var writer = Task.Run(() =>
        {
            var set = false;
            while (!stop.IsCancellationRequested)
            {
                if (set)
                    context.SetActiveInvite(cseq, branch);
                else
                    context.ClearActiveInvite();
                set = !set;
            }
        });

        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 200_000; i++)
            {
                var (observedCSeq, observedBranch) = context.ActiveInvite;

                // Only two states are ever published: fully set or fully cleared. A live CSeq with a
                // missing branch (or the reverse) proves a torn read across the two fields.
                var consistent =
                    (observedCSeq == cseq && observedBranch == branch) ||
                    (observedCSeq == 0 && string.IsNullOrEmpty(observedBranch));

                if (!consistent)
                    Interlocked.Increment(ref inconsistent);
            }
        })).ToArray();

        await Task.WhenAll(readers);
        stop.Cancel();
        await writer;

        Assert.Equal(0, Volatile.Read(ref inconsistent));
    }

    private static SipCallSession NewOutboundSession()
    {
        var configuration = new SipCallSessionConfiguration
        {
            CallId = "call-c2",
            LocalUri = "sip:alice@example.com",
            RemoteUri = "sip:bob@example.com",
            AuthUsername = "alice",
            UserAgent = "callora-tests",
            Timeout = TimeSpan.FromSeconds(5),
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 5060),
        };

        var dependencies = new SipCallSessionDependencies
        {
            Transport = new CapturingSipTransportRuntime(),
            DigestAuthenticator = new NoopSipDigestAuthenticator(),
            Logger = NullLogger.Instance,
            ServerTransactions = new NoopSipServerTransactionEngine(),
            IdentityTrustPolicy = new DenyAllSipIdentityTrustPolicy(),
            SdpProvider = new SipSessionSdpProvider
            {
                BuildOffer = (_, _) => string.Empty,
                TryNegotiateAnswer = (_, _, _) => null,
                TryParseMediaParameters = (_, _) => null,
                IsRemoteHold = _ => false,
            },
        };

        return SipCallSession.CreateOutbound(configuration, dependencies);
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
