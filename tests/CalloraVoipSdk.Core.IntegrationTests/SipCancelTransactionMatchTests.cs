using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions.Server;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// [SIP] #158 P1-6: an inbound CANCEL must be matched to the original INVITE server transaction by the RFC 3261
/// §17.2.3 transaction identifier (top Via branch + sent-by), not merely by Call-ID. A foreign or forked CANCEL
/// that only shares the Call-ID must be answered 481 and must NOT terminate the ringing call, nor repoint the
/// dialog's remote target at the CANCEL source. A CANCEL that copies the INVITE's branch (as a well-behaved UAC
/// does, RFC 3261 §9.1) still terminates the call with 487.
/// </summary>
public sealed class SipCancelTransactionMatchTests
{
    private const string CallId = "call-ack-test"; // AckTestSipCallSessionContext.CallId
    private const string InviteVia = "SIP/2.0/UDP 203.0.113.5:5060;branch=z9hG4bK-invite-1;rport";
    private static readonly IPEndPoint InviteSource = new(IPAddress.Parse("203.0.113.5"), 5060);

    [Fact]
    public async Task A_foreign_cancel_sharing_only_the_call_id_does_not_terminate_the_ringing_call()
    {
        var transactions = new CapturingServerTransactionEngine();
        var context = RingingInboundContext(transactions);
        var service = new SipCallSessionInboundService(context, new SipCallSessionHeaderService(context));

        // A CANCEL whose top Via branch does NOT match the INVITE transaction, from a spoofed source.
        var attacker = new IPEndPoint(IPAddress.Parse("198.51.100.9"), 5060);
        var foreignCancel = Cancel("SIP/2.0/UDP 198.51.100.9:5060;branch=z9hG4bK-attacker;rport");

        await service.HandleInboundRequestAsync(attacker, foreignCancel, CancellationToken.None);

        Assert.Equal(SipDialogState.Ringing, context.State);   // still ringing — not terminated
        Assert.Equal(InviteSource, context.RemoteEndPoint);    // remote target not repointed at the attacker
        Assert.Equal([481], transactions.StatusCodes);         // answered 481, never 200/487
    }

    [Fact]
    public async Task A_cancel_matching_the_invite_transaction_terminates_the_ringing_call()
    {
        var transactions = new CapturingServerTransactionEngine();
        var context = RingingInboundContext(transactions);
        var service = new SipCallSessionInboundService(context, new SipCallSessionHeaderService(context));

        // A well-behaved UAC copies the INVITE's branch + sent-by into the CANCEL (RFC 3261 §9.1).
        var matchingCancel = Cancel(InviteVia);

        await service.HandleInboundRequestAsync(InviteSource, matchingCancel, CancellationToken.None);

        Assert.Equal(SipDialogState.Terminated, context.State);
        Assert.Contains(487, transactions.StatusCodes);  // 200 to the CANCEL, 487 to the INVITE
    }

    private static AckTestSipCallSessionContext RingingInboundContext(ISipServerTransactionEngine transactions) =>
        new(new CapturingSipTransportRuntime())
        {
            IsInbound = true,
            State = SipDialogState.Ringing,
            RemoteEndPoint = InviteSource,
            InitialInvite = Invite(),
            RemoteTag = "remote-1",
            ServerTransactions = transactions,
        };

    private static SipRequest Invite() =>
        new("INVITE", "sip:us@example.test", Headers(InviteVia, "1 INVITE"), body: string.Empty);

    private static SipRequest Cancel(string via) =>
        new("CANCEL", "sip:us@example.test", Headers(via, "1 CANCEL"), body: string.Empty);

    private static Dictionary<string, string> Headers(string via, string cseq) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = via,
            ["From"] = "<sip:them@example.test>;tag=remote-1",
            ["To"] = "<sip:us@example.test>",
            ["Call-ID"] = CallId,
            ["CSeq"] = cseq,
        };

    private sealed class CapturingServerTransactionEngine : ISipServerTransactionEngine
    {
        public List<int> StatusCodes { get; } = new();

        public void Dispose() { }

        public SipServerTransactionRegistration RegisterInboundRequest(
            SipInboundRequestContext context, SipRequest request) => new();

        public Task SendResponseAsync(
            SipRequest request, IPEndPoint remoteEndPoint, SipTransportProtocol transport,
            int statusCode, string reasonPhrase, IReadOnlyDictionary<string, string> headers,
            string? body, CancellationToken ct = default)
        {
            StatusCodes.Add(statusCode);
            return Task.CompletedTask;
        }

        public void RegisterTransportErrorHandler(Action<SipServerTransactionKey, Exception> handler) { }
    }
}
