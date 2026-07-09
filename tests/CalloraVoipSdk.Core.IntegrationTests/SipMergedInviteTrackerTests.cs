using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// A merged INVITE (RFC 3261 §8.2.2.2) shares Call-ID, From tag and CSeq with a recent
/// out-of-dialog INVITE but arrives over a <em>different</em> topmost Via branch. A repeat
/// with the same branch is a retransmission and must not be flagged as merged (which would
/// wrongly answer 482 Loop Detected) — the defect observed against sipgate double-INVITEs.
/// </summary>
public sealed class SipMergedInviteTrackerTests
{
    private static SipRequest Invite(
        string branch,
        string callId = "call-1",
        string fromTag = "ft-1",
        int cseq = 1,
        string? toTag = null)
    {
        var to = toTag is null
            ? "<sip:bob@example.org>"
            : $"<sip:bob@example.org>;tag={toTag}";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = $"SIP/2.0/UDP 203.0.113.5:5060;branch={branch}",
            ["From"] = $"<sip:alice@example.org>;tag={fromTag}",
            ["To"] = to,
            ["Call-ID"] = callId,
            ["CSeq"] = $"{cseq} INVITE",
        };

        return new SipRequest("INVITE", "sip:bob@example.org", headers, string.Empty);
    }

    [Fact]
    public void First_invite_is_not_merged()
    {
        var tracker = new SipMergedInviteTracker();
        Assert.False(tracker.IsMergedInvite(Invite("z9hG4bK-aaa")));
    }

    [Fact]
    public void Retransmitted_invite_same_branch_is_not_merged()
    {
        var tracker = new SipMergedInviteTracker();

        Assert.False(tracker.IsMergedInvite(Invite("z9hG4bK-aaa")));
        // Same identity tuple, same branch → retransmission, NOT a merge → no 482.
        Assert.False(tracker.IsMergedInvite(Invite("z9hG4bK-aaa")));
        Assert.False(tracker.IsMergedInvite(Invite("z9hG4bK-aaa")));
    }

    [Fact]
    public void Same_tuple_with_different_branch_is_merged()
    {
        var tracker = new SipMergedInviteTracker();

        Assert.False(tracker.IsMergedInvite(Invite("z9hG4bK-aaa")));
        // Same Call-ID/From-tag/CSeq but a different branch → forked+merged → 482.
        Assert.True(tracker.IsMergedInvite(Invite("z9hG4bK-bbb")));
    }

    [Fact]
    public void In_dialog_invite_with_to_tag_is_never_merged()
    {
        var tracker = new SipMergedInviteTracker();

        Assert.False(tracker.IsMergedInvite(Invite("z9hG4bK-aaa")));
        Assert.False(tracker.IsMergedInvite(Invite("z9hG4bK-bbb", toTag: "answered")));
    }

    [Fact]
    public void Non_invite_request_is_never_merged()
    {
        var tracker = new SipMergedInviteTracker();

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = "SIP/2.0/UDP 203.0.113.5:5060;branch=z9hG4bK-aaa",
            ["From"] = "<sip:alice@example.org>;tag=ft-1",
            ["To"] = "<sip:bob@example.org>",
            ["Call-ID"] = "call-1",
            ["CSeq"] = "2 OPTIONS",
        };
        var options = new SipRequest("OPTIONS", "sip:bob@example.org", headers, string.Empty);

        Assert.False(tracker.IsMergedInvite(options));
    }

    [Fact]
    public void Different_call_id_is_independent()
    {
        var tracker = new SipMergedInviteTracker();

        Assert.False(tracker.IsMergedInvite(Invite("z9hG4bK-aaa", callId: "call-1")));
        // Different dialog entirely — different branch here must not be read as a merge.
        Assert.False(tracker.IsMergedInvite(Invite("z9hG4bK-bbb", callId: "call-2")));
    }
}
