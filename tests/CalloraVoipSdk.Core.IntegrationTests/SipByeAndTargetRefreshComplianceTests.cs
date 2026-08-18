using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Evidence for two rows the compliance matrix carried without any (#336): RFC 3261 §15.1.2 (a UAS answers an
/// inbound BYE with 200 OK and terminates the dialog) and §12.2.1.2 with RFC 6141 (a successful target-refresh
/// response moves the remote target, so subsequent in-dialog requests go to the new Contact).
/// </summary>
/// <remarks>
/// Both rows named tests that no longer exist in the repository. The behaviours themselves were implemented and,
/// as these tests show, correct — but nothing failed when the evidence disappeared, which is what the new
/// <c>ComplianceMatrixEvidenceTests</c> gate now prevents.
/// </remarks>
public sealed class SipByeAndTargetRefreshComplianceTests
{
    private const string LocalTag = "local-tag";     // AckTestSipCallSessionContext defaults
    private const string RemoteTag = "remote-tag";
    private const string CallId = "call-ack-test";
    private const string FallbackRemoteUri = "sip:bob@example.test";

    // ── §15.1.2 — UAS behaviour on an inbound BYE ────────────────────────────

    [Fact]
    public async Task An_inbound_bye_is_answered_200_and_terminates_the_dialog()
    {
        var (service, engine, context) = BuildInboundService(SipDialogState.Established);

        await service.HandleInboundRequestAsync(Remote(), Bye(), CancellationToken.None);

        var response = Assert.Single(engine.Responses);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal(SipDialogState.Terminated, context.State);
    }

    [Fact]
    public async Task An_inbound_bye_terminates_a_dialog_that_is_on_hold()
    {
        // A held dialog is still a dialog: RFC 3261 §15.1.2 makes no exception for one whose media is
        // suspended, and a UAS that ignored the BYE would leak the session for as long as the peer stayed away.
        var (service, engine, context) = BuildInboundService(SipDialogState.OnHold);

        await service.HandleInboundRequestAsync(Remote(), Bye(), CancellationToken.None);

        Assert.Equal(200, Assert.Single(engine.Responses).StatusCode);
        Assert.Equal(SipDialogState.Terminated, context.State);
    }

    // ── §12.2.1.2 / RFC 6141 — target refresh ────────────────────────────────

    [Fact]
    public void A_successful_target_refresh_moves_the_remote_target_to_the_new_contact()
    {
        var manager = new SipDialogManager();
        manager.ApplyInviteResponse(Ok("<sip:bob@203.0.113.9>"), FallbackRemoteUri);
        Assert.Equal("sip:bob@203.0.113.9", manager.RemoteTargetUri);

        // The re-INVITE's 200 OK advertises a different Contact — the peer moved, and every later in-dialog
        // request (BYE included) has to follow it rather than the address the dialog was created with.
        manager.ApplyTargetRefreshResponse(Ok("<sip:bob@198.51.100.7>"), "INVITE", FallbackRemoteUri);

        Assert.Equal("sip:bob@198.51.100.7", manager.RemoteTargetUri);
    }

    [Fact]
    public void A_failed_response_and_a_non_refreshing_method_leave_the_remote_target_alone()
    {
        var manager = new SipDialogManager();
        manager.ApplyInviteResponse(Ok("<sip:bob@203.0.113.9>"), FallbackRemoteUri);

        // 4xx to a re-INVITE: the peer stays where it was — adopting a Contact from a failed exchange would
        // point the dialog at an address the peer never accepted.
        manager.ApplyTargetRefreshResponse(
            new SipResponse(486, "Busy Here", Headers("<sip:bob@198.51.100.7>"), body: string.Empty),
            "INVITE", FallbackRemoteUri);
        Assert.Equal("sip:bob@203.0.113.9", manager.RemoteTargetUri);

        // RFC 6141: only INVITE and UPDATE refresh the target. A 200 OK to anything else must not move it.
        manager.ApplyTargetRefreshResponse(Ok("<sip:bob@198.51.100.7>"), "OPTIONS", FallbackRemoteUri);
        Assert.Equal("sip:bob@203.0.113.9", manager.RemoteTargetUri);
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private static (SipCallSessionInboundService Service,
                    CapturingSipServerTransactionEngine Engine,
                    AckTestSipCallSessionContext Context) BuildInboundService(SipDialogState state)
    {
        var engine = new CapturingSipServerTransactionEngine();
        var context = new AckTestSipCallSessionContext(new CapturingSipTransportRuntime())
        {
            ServerTransactions = engine,
            RemoteTag = RemoteTag,
            State = state,
        };
        return (new SipCallSessionInboundService(context, new SipCallSessionHeaderService(context)), engine, context);
    }

    private static System.Net.IPEndPoint Remote() => new(System.Net.IPAddress.Parse("192.0.2.1"), 5060);

    private static SipRequest Bye() => new(
        "BYE", "sip:us@example.test",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = "SIP/2.0/UDP 192.0.2.1:5060;branch=z9hG4bK-bye",
            ["Max-Forwards"] = "70",
            ["From"] = $"<sip:them@example.test>;tag={RemoteTag}",
            ["To"] = $"<sip:us@example.test>;tag={LocalTag}",
            ["Call-ID"] = CallId,
            ["CSeq"] = "2 BYE",
        },
        string.Empty);

    private static SipResponse Ok(string contact) =>
        new(200, "OK", Headers(contact), body: string.Empty);

    private static Dictionary<string, string> Headers(string contact) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["To"] = $"<sip:bob@example.test>;tag={RemoteTag}",
        ["From"] = "<sip:alice@example.test>;tag=local",
        ["Call-ID"] = CallId,
        ["CSeq"] = "1 INVITE",
        ["Contact"] = contact,
    };
}
