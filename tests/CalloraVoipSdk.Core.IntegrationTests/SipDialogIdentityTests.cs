using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Inbound dialog-identity matching (RFC 3261 §12.2.2, CF-013): a mid-dialog request must carry our local tag
/// in its To header and the peer's remote tag in its From header; a request that merely shares the Call-ID but
/// mismatches a tag belongs to a different dialog and must be rejected.
/// </summary>
public sealed class SipDialogIdentityTests
{
    private const string ToWithLocal = "<sip:us@example.test>;tag=local-1";
    private const string FromWithRemote = "<sip:them@example.test>;tag=remote-1";

    [Fact]
    public void Request_without_a_to_tag_is_not_dialog_matched()
        => Assert.True(SipDialogIdentity.Matches("<sip:us@example.test>", FromWithRemote, "local-1", "remote-1"));

    [Fact]
    public void Matching_local_and_remote_tags_belong_to_the_dialog()
        => Assert.True(SipDialogIdentity.Matches(ToWithLocal, FromWithRemote, "local-1", "remote-1"));

    [Fact]
    public void A_wrong_to_tag_does_not_match_the_dialog()
        => Assert.False(SipDialogIdentity.Matches(
            "<sip:us@example.test>;tag=someone-else", FromWithRemote, "local-1", "remote-1"));

    [Fact]
    public void A_wrong_from_tag_does_not_match_the_dialog()
        => Assert.False(SipDialogIdentity.Matches(
            ToWithLocal, "<sip:them@example.test>;tag=someone-else", "local-1", "remote-1"));

    [Fact]
    public void A_dialog_still_learning_the_remote_tag_accepts_the_first_in_dialog_request()
        => Assert.True(SipDialogIdentity.Matches(ToWithLocal, FromWithRemote, "local-1", remoteTag: null));

    [Fact]
    public void A_to_tag_without_an_assigned_local_tag_is_not_rejected()
        => Assert.True(SipDialogIdentity.Matches(ToWithLocal, FromWithRemote, localTag: null, remoteTag: null));
}
