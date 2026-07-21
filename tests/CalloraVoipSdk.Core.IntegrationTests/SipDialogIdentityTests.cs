using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Inbound dialog-identity matching (RFC 3261 §12.2.2, CF-013): a mid-dialog request must carry our local tag
/// in its To header and the peer's remote tag in its From header; a request that merely shares the Call-ID but
/// mismatches a tag belongs to a different dialog and must be rejected. The To-tag requirement is
/// method-dependent: a strictly in-dialog method (BYE/INFO/REFER/NOTIFY/UPDATE/PRACK/in-dialog SUBSCRIBE) must
/// carry a To-tag, while a dialog-creating INVITE and out-of-dialog OPTIONS/ACK legitimately arrive without one.
/// </summary>
public sealed class SipDialogIdentityTests
{
    private const string ToWithLocal = "<sip:us@example.test>;tag=local-1";
    private const string ToWithoutTag = "<sip:us@example.test>";
    private const string FromWithRemote = "<sip:them@example.test>;tag=remote-1";

    [Fact]
    public void A_dialog_creating_request_without_a_to_tag_is_not_dialog_matched()
        => Assert.True(SipDialogIdentity.Matches(
            ToWithoutTag, FromWithRemote, "local-1", "remote-1", toTagRequired: false));

    [Fact]
    public void An_in_dialog_method_without_a_to_tag_is_a_mismatch()
        // CF-013 finding: a To-tag-less BYE (or any strictly in-dialog method) must not be applied to the dialog.
        => Assert.False(SipDialogIdentity.Matches(
            ToWithoutTag, FromWithRemote, "local-1", "remote-1", toTagRequired: true));

    [Fact]
    public void Matching_local_and_remote_tags_belong_to_the_dialog()
        => Assert.True(SipDialogIdentity.Matches(
            ToWithLocal, FromWithRemote, "local-1", "remote-1", toTagRequired: true));

    [Fact]
    public void A_wrong_to_tag_does_not_match_the_dialog()
        => Assert.False(SipDialogIdentity.Matches(
            "<sip:us@example.test>;tag=someone-else", FromWithRemote, "local-1", "remote-1", toTagRequired: true));

    [Fact]
    public void A_wrong_from_tag_does_not_match_the_dialog()
        => Assert.False(SipDialogIdentity.Matches(
            ToWithLocal, "<sip:them@example.test>;tag=someone-else", "local-1", "remote-1", toTagRequired: true));

    [Fact]
    public void A_dialog_still_learning_the_remote_tag_accepts_the_first_in_dialog_request()
        => Assert.True(SipDialogIdentity.Matches(
            ToWithLocal, FromWithRemote, "local-1", remoteTag: null, toTagRequired: true));

    [Fact]
    public void A_to_tag_without_an_assigned_local_tag_is_not_rejected()
        => Assert.True(SipDialogIdentity.Matches(
            ToWithLocal, FromWithRemote, localTag: null, remoteTag: null, toTagRequired: true));

    [Fact]
    public void An_empty_to_tag_on_a_dialog_creating_method_is_not_a_mismatch()
        // CF-046: ExtractTag yields null for an empty ";tag=", so a dialog-creating request is not tag-matched.
        => Assert.True(SipDialogIdentity.Matches(
            "<sip:us@example.test>;tag=", FromWithRemote, "local-1", "remote-1", toTagRequired: false));

    [Fact]
    public void An_empty_to_tag_on_an_in_dialog_method_is_a_mismatch()
        // An empty ";tag=" is no tag at all, so a strictly in-dialog method is still rejected.
        => Assert.False(SipDialogIdentity.Matches(
            "<sip:us@example.test>;tag=", FromWithRemote, "local-1", "remote-1", toTagRequired: true));

    [Theory]
    [InlineData("BYE")]
    [InlineData("INFO")]
    [InlineData("REFER")]
    [InlineData("NOTIFY")]
    [InlineData("UPDATE")]
    [InlineData("PRACK")]
    [InlineData("SUBSCRIBE")]
    public void Strictly_in_dialog_methods_require_a_to_tag(string method)
        => Assert.True(SipDialogIdentity.RequiresDialogToTag(method));

    [Theory]
    [InlineData("INVITE")]
    [InlineData("OPTIONS")]
    [InlineData("ACK")]
    [InlineData("CANCEL")]
    public void Dialog_creating_or_transaction_matched_methods_do_not_require_a_to_tag(string method)
        => Assert.False(SipDialogIdentity.RequiresDialogToTag(method));
}
