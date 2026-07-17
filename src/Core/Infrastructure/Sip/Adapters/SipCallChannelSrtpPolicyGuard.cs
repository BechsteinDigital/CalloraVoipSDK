using CalloraVoipSdk.Core.Application.Calls;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Domain.Security;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;

/// <summary>
/// Inbound-offer SRTP policy validation and rejection for one call channel. Extracted
/// from <see cref="SipCoreCallChannel"/> so the channel stays within size limits;
/// termination of established dialogs remains in the channel (it owns call state).
/// </summary>
internal sealed class SipCallChannelSrtpPolicyGuard
{
    private readonly SrtpPolicy _appliedSrtpPolicy;
    private readonly SipCallChannelSrtpTelemetry _srtpTelemetry;
    private readonly ILogger _logger;

    public SipCallChannelSrtpPolicyGuard(
        SrtpPolicy appliedSrtpPolicy,
        SipCallChannelSrtpTelemetry srtpTelemetry,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(srtpTelemetry);
        ArgumentNullException.ThrowIfNull(logger);
        _appliedSrtpPolicy = appliedSrtpPolicy;
        _srtpTelemetry = srtpTelemetry;
        _logger = logger;
    }

    /// <summary>
    /// Validates an inbound offer against the applied SRTP policy. Returns whether the
    /// offer is acceptable and the stable reason code for the decision.
    /// </summary>
    public bool ValidateInboundOffer(string? remoteSdp, out string reasonCode)
    {
        if (string.IsNullOrWhiteSpace(remoteSdp))
        {
            reasonCode = _appliedSrtpPolicy == SrtpPolicy.Required
                ? SrtpDecisionReasonCodes.RequiredRemoteNoSrtp
                : SrtpDecisionReasonCodes.NotEvaluated;
            return _appliedSrtpPolicy != SrtpPolicy.Required;
        }

        if (!SdpSecurityInspector.TryInspectAudioSecurity(remoteSdp, out var isSrtpSignaled, out _))
        {
            reasonCode = _appliedSrtpPolicy == SrtpPolicy.Required
                ? SrtpDecisionReasonCodes.RequiredNegotiationFailed
                : SrtpDecisionReasonCodes.NotEvaluated;
            return _appliedSrtpPolicy != SrtpPolicy.Required;
        }

        reasonCode = SrtpPolicyEvaluator.ResolveReasonCode(_appliedSrtpPolicy, isSrtpSignaled);
        return !SrtpPolicyEvaluator.IsPolicyViolation(_appliedSrtpPolicy, isSrtpSignaled);
    }

    /// <summary>
    /// Rejects an inbound INVITE (488) when SRTP policy validation failed and publishes
    /// the decision to telemetry.
    /// </summary>
    public async Task RejectInboundAsync(
        ISipCallSession session,
        string reasonCode,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);

        var inspected = SdpSecurityInspector.TryInspectAudioSecurity(
            session.RemoteSdp,
            out var isSrtpSignaled,
            out var profile);
        _srtpTelemetry.PublishDecision(
            session,
            inspected && isSrtpSignaled,
            inspected ? profile : string.Empty,
            reasonCode,
            violatesPolicy: true);

        try
        {
            await session.RejectAsync(488, "Not Acceptable Here", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed sending SRTP policy rejection for call {CallId}.",
                session.CallId);
        }
    }
}
