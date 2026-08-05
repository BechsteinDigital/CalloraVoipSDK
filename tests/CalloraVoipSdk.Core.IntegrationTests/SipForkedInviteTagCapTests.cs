using System.Net;
using System.Reflection;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// [SIP] #158 P1-8: the forked-INVITE handler tracks one To-tag per non-selected 2xx branch to de-duplicate the
/// BYE it sends. A 2xx-fork flood with many distinct To-tags would otherwise grow that set without limit. This
/// test pins the tracking cap.
/// </summary>
public sealed class SipForkedInviteTagCapTests
{
    private static readonly IPEndPoint Remote = new(IPAddress.Loopback, 5060);

    private static SipResponse ForkSuccess(string toTag)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = "SIP/2.0/UDP 203.0.113.9:5060;branch=z9hG4bK-fork",
            ["From"] = "<sip:alice@example.test>;tag=local-tag",
            ["To"] = $"<sip:bob@example.test>;tag={toTag}",
            ["Call-ID"] = "call-ack-test",
            ["CSeq"] = "1 INVITE",
            ["Contact"] = "<sip:bob@203.0.113.9>",
        };
        return new SipResponse(200, "OK", headers, body: null);
    }

    private static int TrackedTagCount(SipForkedInviteHandler handler)
    {
        var field = typeof(SipForkedInviteHandler)
            .GetField("_terminatedForkedInviteTags", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return ((ICollection<string>)field.GetValue(handler)!).Count;
    }

    [Fact]
    public void A_flood_of_non_selected_forks_never_grows_the_tracked_tag_set_past_the_cap()
    {
        using var transport = new CapturingSipTransportRuntime();
        var context = new AckTestSipCallSessionContext(transport)
        {
            // Fork handling applies once the INVITE transaction has completed (no active branch), against the
            // confirmed selected dialog. Every inbound 2xx here carries a different, non-selected To-tag.
            RemoteTag = "selected",
            ActiveInviteBranch = null,
            ActiveInviteCSeq = 1,
        };
        var handler = new SipForkedInviteHandler(context);

        for (var i = 0; i < 200; i++)
            handler.HandleSuccessResponse(ForkSuccess($"fork-{i}"), Remote);

        Assert.True(TrackedTagCount(handler) <= 64, $"tracked tags grew to {TrackedTagCount(handler)}, cap was 64");
    }
}
