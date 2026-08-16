using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// [SIP] #158 P2-10: a forking proxy can deliver a 2xx from another branch after the first one established the
/// dialog. RFC 3261 §13.2.2.4 requires an ACK for each such response and a BYE for every dialog the UAC does
/// not keep — otherwise the losing UAS retransmits its 200 OK until it gives up, and its call leg stays up.
/// The cancel target is cleared on the first 2xx (HARD-C2), so matching those late responses relies on the
/// completed INVITE identity outliving it.
/// </summary>
public sealed class SipLateForkedInviteTests
{
    private const int InviteCSeq = 7;

    [Fact]
    public async Task A_late_fork_2xx_is_acked_and_byed_after_the_cancel_target_is_gone()
    {
        var transport = new CapturingSipTransportRuntime();
        var context = new AckTestSipCallSessionContext(transport)
        {
            ActiveInviteCSeq = InviteCSeq,
            ActiveInviteBranch = "z9hG4bK-initial",
        };
        // Exactly what the transaction flow does on the first 2xx: the dialog is established with the selected
        // branch's tag, and the INVITE stops being cancellable.
        context.RemoteTag = "selected-tag";
        context.CompleteActiveInvite(InviteCSeq);

        var service = new SipCallSessionTransactionService(
            context,
            new SipCallSessionHeaderService(context));

        service.HandleInboundResponse(context.RemoteEndPoint, ForkSuccess(context.CallId, "late-fork-tag"));

        var ack = await transport.WaitForRequestAsync("ACK", TimeSpan.FromSeconds(2));
        Assert.Equal($"{InviteCSeq} ACK", ack.Headers["CSeq"]);
        // The ACK belongs to the losing branch's dialog, not to the one we kept.
        Assert.Contains("tag=late-fork-tag", ack.Headers["To"], StringComparison.Ordinal);

        var bye = await transport.WaitForRequestAsync("BYE", TimeSpan.FromSeconds(2));
        Assert.Contains("tag=late-fork-tag", bye.Headers["To"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_retransmitted_2xx_for_the_selected_dialog_is_acked_but_not_byed()
    {
        var transport = new CapturingSipTransportRuntime();
        var context = new AckTestSipCallSessionContext(transport) { RemoteTag = "selected-tag" };
        context.CompleteActiveInvite(InviteCSeq);

        var service = new SipCallSessionTransactionService(
            context,
            new SipCallSessionHeaderService(context));

        service.HandleInboundResponse(context.RemoteEndPoint, ForkSuccess(context.CallId, "selected-tag"));

        var ack = await transport.WaitForRequestAsync("ACK", TimeSpan.FromSeconds(2));
        Assert.Equal($"{InviteCSeq} ACK", ack.Headers["CSeq"]);

        // Tearing down the dialog we just established would be worse than the missing ACK ever was.
        await Task.Delay(200);
        Assert.DoesNotContain(transport.SnapshotRequests(), r => r.Method == "BYE");
    }

    [Fact]
    public void Completing_an_invite_keeps_its_identity_and_drops_the_cancel_target()
    {
        var context = new AckTestSipCallSessionContext(new CapturingSipTransportRuntime())
        {
            ActiveInviteCSeq = InviteCSeq,
            ActiveInviteBranch = "z9hG4bK-initial",
        };

        context.CompleteActiveInvite(InviteCSeq);

        // HARD-C2: a 2xx makes the INVITE non-cancellable, so the CANCEL flow must find nothing…
        Assert.Equal((0, null), ((ISipCallSessionContext)context).ActiveInvite);
        // …while the identity the fork match needs survives (#158 P2-10).
        Assert.Equal(InviteCSeq, context.CompletedInviteCSeq);
    }

    private static SipResponse ForkSuccess(string callId, string remoteTag)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = "SIP/2.0/UDP 192.0.2.10:5060;branch=z9hG4bK-fork",
            ["From"] = "<sip:alice@example.test>;tag=local-tag",
            ["To"] = $"<sip:bob@example.test>;tag={remoteTag}",
            ["Call-ID"] = callId,
            ["CSeq"] = $"{InviteCSeq} INVITE",
            ["Contact"] = "<sip:bob@192.0.2.10:5060>",
        };
        return new SipResponse(200, "OK", headers, string.Empty);
    }
}
