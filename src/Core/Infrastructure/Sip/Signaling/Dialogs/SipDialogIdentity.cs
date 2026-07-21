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
    /// without a To-tag is dialog-creating (or a pre-dialog retransmission), not a mid-dialog request, so it is
    /// not tag-matched here and returns <see langword="true"/>. Each tag is only enforced when this side actually
    /// holds it, so a dialog still learning the remote tag does not reject a legitimate first in-dialog request.
    /// </summary>
    /// <param name="toHeader">The request's raw To header value (its To-tag is our local tag).</param>
    /// <param name="fromHeader">The request's raw From header value (its From-tag is the remote tag).</param>
    /// <param name="localTag">This dialog's local tag, or <see langword="null"/> if not yet assigned.</param>
    /// <param name="remoteTag">This dialog's remote tag, or <see langword="null"/> if not yet learned.</param>
    public static bool Matches(string? toHeader, string? fromHeader, string? localTag, string? remoteTag)
    {
        var toTag = SipProtocol.ExtractTag(toHeader);
        if (toTag is null)
            return true; // no To-tag → dialog-creating / pre-dialog request, not matched on dialog identity.

        if (localTag is { } expectedLocalTag && !string.Equals(toTag, expectedLocalTag, StringComparison.Ordinal))
            return false;

        var fromTag = SipProtocol.ExtractTag(fromHeader);
        if (remoteTag is { } expectedRemoteTag && !string.Equals(fromTag, expectedRemoteTag, StringComparison.Ordinal))
            return false;

        return true;
    }
}
