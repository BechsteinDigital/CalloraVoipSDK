using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Matches an inbound request against a dialog's full identity (RFC 3261 §12.2.2, CF-013). A dialog is
/// identified by Call-ID plus the local and remote tags; the Call-ID is already matched when a request is
/// dispatched to a session, so this checks the tags: a mid-dialog request (one that carries a To-tag) must
/// carry <em>our</em> local tag in its To header and the peer's remote tag in its From header. A request whose
/// tags do not match belongs to a different dialog that merely shares the Call-ID (a forked branch, or a stale
/// or foreign request) and must not be applied to this dialog.
/// </summary>
internal static class SipDialogIdentity
{
    /// <summary>
    /// Returns whether an inbound request belongs to a dialog with the given local and remote tags. A request
    /// without a To-tag is normally dialog-creating (or a pre-dialog retransmission), not a mid-dialog request,
    /// so it is not tag-matched and returns <see langword="true"/> — <em>unless</em> <paramref name="toTagRequired"/>
    /// is set, which a strictly in-dialog method (see <see cref="RequiresDialogToTag"/>) demands: for those a
    /// missing To-tag is a stale or foreign request that must not mutate this dialog, so it returns
    /// <see langword="false"/>. Once a To-tag is present, each tag is only enforced when this side actually holds
    /// it, so a dialog still learning the remote tag does not reject a legitimate first in-dialog request.
    /// </summary>
    /// <param name="toHeader">The request's raw To header value (its To-tag is our local tag).</param>
    /// <param name="fromHeader">The request's raw From header value (its From-tag is the remote tag).</param>
    /// <param name="localTag">This dialog's local tag, or <see langword="null"/> if not yet assigned.</param>
    /// <param name="remoteTag">This dialog's remote tag, or <see langword="null"/> if not yet learned.</param>
    /// <param name="toTagRequired">
    /// Whether a missing To-tag is a mismatch (RFC 3261 §12.2.2). Set for strictly in-dialog methods so a
    /// To-tag-less BYE/INFO/REFER/NOTIFY/UPDATE/PRACK/in-dialog SUBSCRIBE cannot terminate or mutate the dialog.
    /// </param>
    public static bool Matches(
        string? toHeader, string? fromHeader, string? localTag, string? remoteTag, bool toTagRequired)
    {
        var toTag = SipProtocol.ExtractTag(toHeader);
        if (toTag is null)
            // No To-tag: a dialog-creating / pre-dialog request is not matched on dialog identity (→ true), but a
            // strictly in-dialog method without a To-tag is stale/foreign and must be rejected (→ false).
            return !toTagRequired;

        if (localTag is { } expectedLocalTag && !string.Equals(toTag, expectedLocalTag, StringComparison.Ordinal))
            return false;

        var fromTag = SipProtocol.ExtractTag(fromHeader);
        if (remoteTag is { } expectedRemoteTag && !string.Equals(fromTag, expectedRemoteTag, StringComparison.Ordinal))
            return false;

        return true;
    }

    /// <summary>
    /// Whether an inbound request of this method must carry a To-tag to be applied to an established dialog
    /// (RFC 3261 §12.2). The strictly in-dialog methods — BYE, INFO, REFER, NOTIFY, UPDATE, PRACK and an
    /// in-dialog SUBSCRIBE refresh — are only ever sent inside a confirmed dialog, so a missing To-tag makes
    /// them a stale or foreign request that must not mutate this dialog. A dialog-creating INVITE (an initial
    /// INVITE has no To-tag; a re-INVITE inherently carries one and is value-matched), an out-of-dialog OPTIONS,
    /// and ACK/CANCEL (matched by transaction, not dialog identity) legitimately arrive without a To-tag.
    /// </summary>
    /// <param name="method">The inbound request method, compared case-sensitively per RFC 3261 §7.1.</param>
    public static bool RequiresDialogToTag(string method) => method switch
    {
        "BYE" or "INFO" or "REFER" or "NOTIFY" or "UPDATE" or "PRACK" or "SUBSCRIBE" => true,
        _ => false,
    };
}
