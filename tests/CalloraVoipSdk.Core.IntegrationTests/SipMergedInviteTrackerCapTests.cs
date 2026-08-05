using System.Collections;
using System.Reflection;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// [SIP] #158 P1-7: the merged-INVITE tracker prunes expired identity tuples only every 64 registrations, so a
/// burst of distinct fresh identities could grow its map unbounded between prunes. This test pins the hard cap:
/// distinct fresh identities never grow the tracked set past its ceiling.
/// </summary>
public sealed class SipMergedInviteTrackerCapTests
{
    private static SipRequest Invite(int n)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = $"SIP/2.0/UDP 203.0.113.9:5060;branch=z9hG4bK-merge-{n}",
            ["From"] = $"<sip:alice@example.test>;tag=from-{n}",
            ["To"] = "<sip:bob@example.test>",
            ["Call-ID"] = $"merge-{n}@example.test",
            ["CSeq"] = "1 INVITE",
        };
        return new SipRequest("INVITE", "sip:bob@example.test", headers, body: string.Empty);
    }

    private static int SeenCount(SipMergedInviteTracker tracker)
    {
        var field = typeof(SipMergedInviteTracker)
            .GetField("_seen", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return ((IDictionary)field.GetValue(tracker)!).Count;
    }

    [Fact]
    public void Distinct_fresh_identities_never_grow_the_tracked_set_past_the_cap()
    {
        var tracker = new SipMergedInviteTracker(maxEntries: 4);

        for (var i = 0; i < 50; i++)
        {
            // Each request is a distinct out-of-dialog identity (distinct Call-ID/branch) — none is a merge.
            Assert.False(tracker.IsMergedInvite(Invite(i)));
        }

        Assert.True(SeenCount(tracker) <= 4, $"tracked set grew to {SeenCount(tracker)}, cap was 4");
    }
}
