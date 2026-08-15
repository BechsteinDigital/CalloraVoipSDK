using CalloraVoipSdk.Core.Application.Observability;
using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging;

using static CalloraVoipSdk.Core.Infrastructure.Sip.Signaling.SipCallSignalingHelpers;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Ends the dialog that an accepted <c>Replaces</c> transfer supersedes (RFC 3891 §3): once the replacing
/// dialog is established, the replaced one must be hung up with a <c>Reason: SIP;cause=200;text="Replaced"</c>
/// so the peer can tell an orderly transfer from a dropped call.
/// </summary>
/// <remarks>
/// Deliberately fire-and-forget at the call site and failure-tolerant here: the replacing call is already up,
/// and the caller is talking. Failing to tear down the old leg is worth a warning, never worth failing the
/// transfer that already succeeded.
/// </remarks>
internal sealed class SipReplacedDialogTerminator
{
    private readonly ISipTelemetrySink _telemetry;
    private readonly ILogger _logger;

    public SipReplacedDialogTerminator(ISipTelemetrySink telemetry, ILogger logger)
    {
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Hangs up <paramref name="replacedSession"/> on behalf of the replacing dialog.
    /// </summary>
    /// <param name="replacedSession">
    /// The superseded dialog, already resolved by the caller — keeping the session lookup outside means this
    /// collaborator needs no view of the service's session table.
    /// </param>
    /// <param name="replacingCallId">Call-ID of the dialog that took over.</param>
    /// <param name="replacedCallId">Call-ID of the dialog being ended.</param>
    /// <param name="traceId">Trace correlation of the replacing dialog.</param>
    public async Task TerminateAsync(
        SipCallSession replacedSession,
        string replacingCallId,
        string replacedCallId,
        string traceId)
    {
        try
        {
            // A dialog cannot replace itself, and one already gone needs no BYE.
            if (string.Equals(replacingCallId, replacedCallId, StringComparison.Ordinal))
                return;
            if (replacedSession.State == SipDialogState.Terminated)
                return;

            var reason = SipReasonHeader.CreateSipStatusReason(200, "Replaced");
            await replacedSession.HangupAsync(reason: reason).ConfigureAwait(false);
            _telemetry.PublishEvent(new SipEventRecord
            {
                EventType = "sip.dialog.replaces.completed",
                CallId = replacingCallId,
                CorrelationId = BuildCorrelationId(replacingCallId, "REPLACES", null),
                TraceId = traceId,
                Attributes = new Dictionary<string, string>
                {
                    ["replaced_call_id"] = replacedCallId
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed terminating replaced SIP dialog {ReplacedCallId} for replacing dialog {ReplacingCallId}.",
                replacedCallId,
                replacingCallId);
        }
    }
}
