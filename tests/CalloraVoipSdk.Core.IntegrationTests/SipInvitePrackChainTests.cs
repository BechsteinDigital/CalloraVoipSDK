using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// CF-044 (RFC 3262 §4): the UAC's reliable-provisional PRACK handling across the INVITE retry loop.
/// <list type="bullet">
/// <item><description>The reliable-provisional receipt order is reset per INVITE attempt, so a retried INVITE
/// (401/422) accepts and PRACKs its own fresh RSeq=1 provisional instead of rejecting it as a duplicate of the
/// previous attempt's sequence.</description></item>
/// <item><description>The PRACK send-chain awaits its predecessor, so a failed earlier PRACK is not swallowed by
/// a fire-and-forget continuation but propagates and aborts the INVITE.</description></item>
/// </list>
/// </summary>
public sealed class SipInvitePrackChainTests
{
    private static string Challenge(string nonce) => $"Digest realm=\"pbx\", nonce=\"{nonce}\", qop=\"auth\"";

    private static SipResponse Echo(CapturedSipRequest req, int statusCode, string reason, params (string, string)[] extra)
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
        foreach (var (name, value) in extra)
            headers[name] = value;
        return new SipResponse(statusCode, reason, headers, string.Empty);
    }

    // A reliable provisional (RFC 3262): 1xx (≠100) + RSeq + Require: 100rel — drives the UAC's PRACK path.
    private static SipResponse ReliableProvisional(CapturedSipRequest inviteReq, int rseq) =>
        Echo(inviteReq, 183, "Session Progress", ("RSeq", rseq.ToString()), ("Require", "100rel"));

    [Fact]
    public async Task Each_invite_attempt_resets_the_receipt_order_and_pracks_its_own_reliable_provisional()
    {
        var transport = new CapturingSipTransportRuntime
        {
            // Every INVITE attempt receives a fresh RSeq=1 reliable provisional (a new transaction restarts RSeq).
            ProvisionalResponsesFactory = req => req.Method == "INVITE"
                ? [ReliableProvisional(req, rseq: 1)]
                : Array.Empty<SipResponse>(),
            ResponseFactory = req => req.Method switch
            {
                "PRACK" => Echo(req, 200, "OK"),
                "INVITE" => req.Headers.ContainsKey("Authorization")
                    ? Echo(req, 200, "OK")
                    : Echo(req, 401, "Unauthorized", ("WWW-Authenticate", Challenge("n1"))),
                _ => null,
            },
        };
        var context = new AckTestSipCallSessionContext(transport)
        {
            AuthPassword = "s3cret",
            DigestAuthenticator = new SipDigestAuthentication(),
        };
        var service = new SipCallSessionTransactionService(context, new SipCallSessionHeaderService(context));

        await service.SendInviteTransactionAsync(
            body: null, allowRingingTransition: true, SipDialogState.Established, CancellationToken.None);

        // Two INVITE attempts (unauth → 401, authed → 200), each PRACKs its own RSeq=1 provisional. Without a
        // per-attempt reset, the retry's RSeq=1 is rejected as a duplicate and only one PRACK is ever sent.
        var pracks = transport.SnapshotRequests().Count(r => r.Method == "PRACK");
        Assert.Equal(2, pracks);
    }

    [Fact]
    public async Task A_failed_earlier_prack_in_the_chain_aborts_the_invite_instead_of_being_swallowed()
    {
        var prackSends = 0;
        var transport = new CapturingSipTransportRuntime
        {
            // Two reliable provisionals in one INVITE, so the second PRACK chains after the first.
            ProvisionalResponsesFactory = req => req.Method == "INVITE"
                ? [ReliableProvisional(req, rseq: 1), ReliableProvisional(req, rseq: 2)]
                : Array.Empty<SipResponse>(),
            // Fail ONLY the first PRACK send; a fire-and-forget continuation would let the second PRACK succeed
            // and swallow this failure. Awaiting the predecessor must surface it and abort the INVITE.
            ThrowOnSendPredicate = req => req.Method == "PRACK" && Interlocked.Increment(ref prackSends) == 1,
            ResponseFactory = req => req.Method switch
            {
                "PRACK" => Echo(req, 200, "OK"),
                "INVITE" => Echo(req, 200, "OK"),
                _ => null,
            },
        };
        var context = new AckTestSipCallSessionContext(transport);
        var service = new SipCallSessionTransactionService(context, new SipCallSessionHeaderService(context));

        await Assert.ThrowsAnyAsync<Exception>(() => service.SendInviteTransactionAsync(
            body: null, allowRingingTransition: true, SipDialogState.Established, CancellationToken.None));
    }
}
