using CalloraVoipSdk.Core.Domain.Subscriptions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Reading the two event-state documents a telephone system lives on: who is available (PIDF,
/// RFC 3863) and what a line is currently doing (dialog-info, RFC 4235).
/// </summary>
/// <remarks>
/// Parsed in the SDK rather than in every application, for the same reason Diversion and History-Info
/// are: the rules are in an RFC and two consumers reading the same XML disagree in different ways.
/// </remarks>
public sealed class SipEventDocumentTests
{
    [Fact]
    public void A_presence_document_says_who_it_is_about_and_whether_they_are_reachable()
    {
        var presence = SipPresence.TryParse(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <presence xmlns="urn:ietf:params:xml:ns:pidf" entity="sip:dana@example.org">
              <tuple id="desk">
                <status><basic>open</basic></status>
                <contact>sip:dana@example.org</contact>
                <note>Am Platz</note>
              </tuple>
            </presence>
            """);

        Assert.NotNull(presence);
        Assert.Equal("sip:dana@example.org", presence!.Entity);
        Assert.True(presence.IsOpen);
        var tuple = Assert.Single(presence.Tuples);
        Assert.Equal("desk", tuple.Id);
        Assert.Equal("Am Platz", tuple.Note);
    }

    [Fact]
    public void One_open_device_makes_the_person_reachable()
    {
        // A desk phone offline and a mobile online is reachable. Requiring every tuple to be open
        // would report the opposite of the truth.
        var presence = SipPresence.TryParse(
            """
            <presence xmlns="urn:ietf:params:xml:ns:pidf" entity="sip:dana@example.org">
              <tuple id="desk"><status><basic>closed</basic></status></tuple>
              <tuple id="mobile"><status><basic>open</basic></status></tuple>
            </presence>
            """);

        Assert.True(presence!.IsOpen);
    }

    [Fact]
    public void A_status_nobody_defined_is_not_read_as_available()
    {
        // RFC 3863 defines open and closed. Announcing somebody as available on the strength of a
        // typo is the wrong way to be wrong.
        var presence = SipPresence.TryParse(
            """
            <presence xmlns="urn:ietf:params:xml:ns:pidf" entity="sip:dana@example.org">
              <tuple id="desk"><status><basic>maybe</basic></status></tuple>
            </presence>
            """);

        Assert.False(presence!.IsOpen);
    }

    [Fact]
    public void A_document_without_the_namespace_is_still_read()
    {
        // The RFC fixes the namespace and deployments do not. Matching on the local name accepts what
        // registrars actually send; the cost is accepting a document that was not PIDF, which then
        // produces no tuples and reads as "nothing known" — the same as an empty one.
        var presence = SipPresence.TryParse(
            "<presence entity=\"sip:dana@example.org\"><tuple id=\"a\"><status><basic>open</basic></status></tuple></presence>");

        Assert.True(presence!.IsOpen);
    }

    [Fact]
    public void A_line_on_a_call_reports_itself_busy()
    {
        var info = SipDialogInfo.TryParse(
            """
            <?xml version="1.0"?>
            <dialog-info xmlns="urn:ietf:params:xml:ns:dialog-info" version="4" state="full"
                         entity="sip:42@example.org">
              <dialog id="abc" direction="recipient">
                <state>confirmed</state>
                <local><identity>sip:42@example.org</identity></local>
                <remote><identity>sip:+4930111@example.org</identity></remote>
              </dialog>
            </dialog-info>
            """);

        Assert.NotNull(info);
        Assert.True(info!.IsBusy);
        Assert.Equal(4, info.Version);
        Assert.True(info.IsFullState);
        var dialog = Assert.Single(info.Dialogs);
        Assert.Equal(SipDialogState.Confirmed, dialog.State);
        Assert.Equal("sip:+4930111@example.org", dialog.RemoteIdentity);
    }

    [Fact]
    public void A_ringing_line_counts_as_busy()
    {
        // A colleague whose phone is ringing cannot take a second call either. A lamp that only lights
        // on "confirmed" invites exactly that.
        var info = SipDialogInfo.TryParse(
            "<dialog-info version=\"1\" entity=\"sip:42@x\"><dialog id=\"a\"><state>early</state></dialog></dialog-info>");

        Assert.True(info!.IsBusy);
    }

    [Fact]
    public void A_terminated_dialog_is_not_busy()
    {
        var info = SipDialogInfo.TryParse(
            "<dialog-info version=\"2\" entity=\"sip:42@x\"><dialog id=\"a\"><state>terminated</state></dialog></dialog-info>");

        Assert.False(info!.IsBusy);
    }

    [Fact]
    public void A_partial_document_says_so_rather_than_looking_complete()
    {
        // The trap in RFC 4235: a partial document carries only what changed and says nothing about
        // the rest. Treated as complete it clears a lamp that should still be lit.
        var partial = SipDialogInfo.TryParse(
            "<dialog-info version=\"7\" state=\"partial\" entity=\"sip:42@x\"><dialog id=\"a\"><state>terminated</state></dialog></dialog-info>");

        Assert.False(partial!.IsFullState);
    }

    [Fact]
    public void A_document_without_a_state_attribute_is_full()
    {
        // RFC 4235 makes full the default. Guessing partial would leave lamps lit for calls that ended.
        var info = SipDialogInfo.TryParse(
            "<dialog-info version=\"1\" entity=\"sip:42@x\"></dialog-info>");

        Assert.True(info!.IsFullState);
        Assert.Empty(info.Dialogs);
    }

    [Fact]
    public void A_dialog_state_nobody_defined_is_unknown_rather_than_idle()
    {
        // A lamp that goes dark on a word we do not know is worse than one that stays as it was.
        var info = SipDialogInfo.TryParse(
            "<dialog-info version=\"1\" entity=\"sip:42@x\"><dialog id=\"a\"><state>sleeping</state></dialog></dialog-info>");

        Assert.Equal(SipDialogState.Unknown, Assert.Single(info!.Dialogs).State);
        Assert.False(info.IsBusy);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not xml at all")]
    [InlineData("<something-else/>")]
    public void Anything_that_is_not_the_document_yields_null(string? xml)
    {
        // It arrives on a NOTIFY from somebody else's server. Their malformed XML must not take down
        // a call path of ours.
        Assert.Null(SipPresence.TryParse(xml));
        Assert.Null(SipDialogInfo.TryParse(xml));
    }
}
