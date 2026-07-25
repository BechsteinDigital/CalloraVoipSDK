using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// F011 Slice 3a regression anchor: the outbound INVITE transaction still reaches its final dialog state
/// after passing through a (non-reliable) provisional response. Slice 3a binds the media adapter to the
/// session early (before the INVITE is sent) so it observes the early dialog live; this test guards the
/// underlying provisional → final transaction mechanics that early binding must not disturb.
/// </summary>
public sealed class SipEarlyMediaBindingTests
{
    private static SipResponse Echo(CapturedSipRequest req, int code, string reason) =>
        new(code, reason, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = req.Headers["Via"], ["From"] = req.Headers["From"], ["To"] = req.Headers["To"],
            ["Call-ID"] = req.Headers["Call-ID"], ["CSeq"] = req.Headers["CSeq"],
            ["Contact"] = req.Headers.TryGetValue("Contact", out var c) ? c : "<sip:bob@192.0.2.10:5060>",
        }, string.Empty);

    [Fact]
    public async Task Invite_transaction_reaches_established_through_a_provisional()
    {
        var transport = new CapturingSipTransportRuntime
        {
            ProvisionalResponsesFactory = req => req.Method == "INVITE" ? [Echo(req, 180, "Ringing")] : Array.Empty<SipResponse>(),
            ResponseFactory = req => req.Method == "INVITE" ? Echo(req, 200, "OK") : null,
        };
        var context = new AckTestSipCallSessionContext(transport);
        var service = new SipCallSessionTransactionService(context, new SipCallSessionHeaderService(context));

        await service.SendInviteTransactionAsync(
            body: null, allowRingingTransition: true, SipDialogState.Established, CancellationToken.None);

        // The INVITE went out and the transaction settled at the final state after the 180 provisional.
        Assert.Contains(transport.SnapshotRequests(), r => r.Method == "INVITE");
        Assert.Equal(SipDialogState.Established, context.State);
    }
}
