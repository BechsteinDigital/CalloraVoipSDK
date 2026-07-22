using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Issue #3 (RFC 3261 §13.2.2.4): once an outbound INVITE dialog is confirmed, a RETRANSMITTED 2xx (the UAS
/// resending the 200 OK because the first ACK was lost) must still be ACKed. The active-invite state is cleared
/// on the first 2xx, so the re-ACK is recognised by the confirmed dialog's remote tag, not the (now-zero) active
/// INVITE CSeq — otherwise the UAS keeps retransmitting the 200 OK until it gives up and drops the call.
/// </summary>
public sealed class SipRetransmitted2xxReAckTests
{
    private const string RemoteTag = "remote-tag";

    private static SipResponse InviteSuccess(string toTag) =>
        new(200, "OK", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = "SIP/2.0/UDP 10.0.0.1:5060;branch=z9hG4bK-invite",
            ["From"] = "<sip:alice@example.test>;tag=local-tag",
            ["To"] = $"<sip:bob@example.test>;tag={toTag}",
            ["Call-ID"] = "call-ack-test",
            ["CSeq"] = "1 INVITE",
            ["Contact"] = "<sip:bob@192.0.2.10:5060>",
        }, string.Empty);

    private static (SipForkedInviteHandler Handler, CapturingSipTransportRuntime Transport) BuildConfirmedDialog()
    {
        var transport = new CapturingSipTransportRuntime();
        var context = new AckTestSipCallSessionContext(transport)
        {
            RemoteTag = RemoteTag,          // dialog confirmed with this remote tag
            ActiveInviteBranch = null,      // INVITE transaction already completed (ACK owned by TU no more)
            ActiveInviteCSeq = 0,           // cleared on the first 2xx
        };
        return (new SipForkedInviteHandler(context), transport);
    }

    [Fact]
    public async Task A_retransmitted_2xx_of_the_confirmed_dialog_is_re_acked()
    {
        var (handler, transport) = BuildConfirmedDialog();

        handler.HandleSuccessResponse(InviteSuccess(RemoteTag), new IPEndPoint(IPAddress.Loopback, 5060));

        // The re-ACK is sent fire-and-forget; wait for it. Before the fix the retransmission was rejected
        // (CSeq 1 != cleared ActiveInviteCSeq 0) and no ACK was ever sent.
        var ack = await transport.WaitForRequestAsync("ACK", TimeSpan.FromSeconds(2));
        // RFC 3261 §13.2.2.4: the ACK carries the INVITE's CSeq number with method ACK, keyed to the same dialog.
        Assert.Equal("1 ACK", ack.Headers["CSeq"]);
        Assert.Equal("call-ack-test", ack.Headers["Call-ID"]);
        Assert.Contains("tag=remote-tag", ack.Headers["To"]); // ACKs the confirmed dialog
    }

    [Fact]
    public async Task A_2xx_with_a_foreign_tag_is_not_acked_by_the_confirmed_dialog()
    {
        var (handler, transport) = BuildConfirmedDialog();

        // A 2xx whose To-tag is not our confirmed remote tag (a non-selected fork / stale branch) and does not
        // match the active INVITE CSeq must NOT be ACKed by this confirmed dialog.
        handler.HandleSuccessResponse(InviteSuccess("some-other-tag"), new IPEndPoint(IPAddress.Loopback, 5060));

        await Task.Delay(200); // let any fire-and-forget send run
        Assert.DoesNotContain(transport.SnapshotRequests(), r => r.Method == "ACK");
    }
}
