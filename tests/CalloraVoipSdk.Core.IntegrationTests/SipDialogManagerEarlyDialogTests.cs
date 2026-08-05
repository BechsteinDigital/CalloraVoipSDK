using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// [SIP] #158 P1-8: the UAC dialog manager tracks an early dialog per distinct To-tag seen on a provisional
/// response. A forking proxy or malicious peer can emit provisionals carrying unbounded distinct To-tags, and
/// after a dialog is confirmed the non-selected early dialogs were previously left in place. These tests pin the
/// early-dialog cap and the release of all early dialogs on success.
/// </summary>
public sealed class SipDialogManagerEarlyDialogTests
{
    private const string FallbackRemoteUri = "sip:bob@example.test";

    private static SipResponse Response(int statusCode, string toTag)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["To"] = $"<sip:bob@example.test>;tag={toTag}",
            ["From"] = "<sip:alice@example.test>;tag=local",
            ["Call-ID"] = "early-dialog-test@example.test",
            ["CSeq"] = "1 INVITE",
            ["Contact"] = "<sip:bob@203.0.113.9>",
        };
        return new SipResponse(statusCode, statusCode == 200 ? "OK" : "Ringing", headers, body: string.Empty);
    }

    [Fact]
    public void Provisionals_with_distinct_tags_never_grow_early_dialogs_past_the_cap()
    {
        var manager = new SipDialogManager();

        for (var i = 0; i < 100; i++)
            manager.ApplyInviteResponse(Response(180, $"fork-{i}"), FallbackRemoteUri);

        Assert.True(manager.EarlyDialogCount <= 32, $"early dialogs grew to {manager.EarlyDialogCount}, cap was 32");
    }

    [Fact]
    public void A_confirmed_dialog_releases_all_non_selected_early_dialogs()
    {
        var manager = new SipDialogManager();

        manager.ApplyInviteResponse(Response(180, "fork-a"), FallbackRemoteUri);
        manager.ApplyInviteResponse(Response(180, "fork-b"), FallbackRemoteUri);
        manager.ApplyInviteResponse(Response(180, "fork-c"), FallbackRemoteUri);
        Assert.Equal(3, manager.EarlyDialogCount);

        // One branch answers 2xx. The selected branch becomes a confirmed dialog and every early dialog —
        // including the two non-selected ones — is released.
        manager.ApplyInviteResponse(Response(200, "fork-b"), FallbackRemoteUri);

        Assert.Equal(0, manager.EarlyDialogCount);
        Assert.Equal(1, manager.ConfirmedDialogCount);
    }
}
