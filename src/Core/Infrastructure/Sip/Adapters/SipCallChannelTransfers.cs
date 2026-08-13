using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;

/// <summary>
/// The REFER-based call transfers a <see cref="SipCoreCallChannel"/> can perform (RFC 5589).
/// </summary>
/// <remarks>
/// Extracted from the channel, which had grown to the 1000-line limit, and split along the same seam
/// as its other collaborators (notifier, frame taps, media publisher): the channel owns the session
/// and its lifecycle, this owns what a transfer puts on the wire.
///
/// Deciding <em>when</em> a transfer is finished is a separate question and lives in
/// <see cref="SipReferCompletion"/> — it is the same answer for both transfer kinds.
/// </remarks>
internal static class SipCallChannelTransfers
{
    /// <summary>
    /// Blind transfer (RFC 5589 §6): REFER the peer straight to <paramref name="targetUri"/>, with no
    /// consultation dialog to replace.
    /// </summary>
    public static Task<bool> BlindAsync(
        ISipCallSession session,
        string targetUri,
        TimeSpan timeout,
        ILogger logger,
        CancellationToken ct) =>
        SipReferCompletion.SendAndAwaitAsync(session, targetUri, timeout, logger, ct);

    /// <summary>
    /// Attended transfer (RFC 5589 §7): REFER the transferee to the consultation target, carrying an
    /// RFC 3891 <c>Replaces</c> that identifies the established consultation dialog.
    /// </summary>
    /// <remarks>
    /// Falls back to a plain REFER to the target URI when the consultation dialog has no tags yet —
    /// a Replaces that cannot identify a dialog is worse than none, since the peer would reject it.
    /// </remarks>
    public static Task<bool> AttendedAsync(
        ISipCallSession session,
        ISipCallSession consultation,
        TimeSpan timeout,
        ILogger logger,
        CancellationToken ct)
    {
        var referTo = AttendedTransferReferTo.Build(
            consultation.CallId,
            consultation.LocalTag,
            consultation.RemoteTag,
            consultation.RemoteUri)
            ?? consultation.RemoteUri;

        return SipReferCompletion.SendAndAwaitAsync(session, referTo, timeout, logger, ct);
    }
}
