using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Security;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;

/// <summary>
/// Emits the SRTP policy/decision telemetry for one <see cref="SipCoreCallChannel"/>.
/// Extracted from the channel to keep it focused and within the file-size budget; carries
/// the resolved policy and its source so call sites pass only the per-decision fields.
/// </summary>
internal sealed class SipCallChannelSrtpTelemetry
{
    private readonly ISipTelemetrySink _telemetry;
    private readonly SrtpPolicy _policy;
    private readonly string _policySource;

    public SipCallChannelSrtpTelemetry(ISipTelemetrySink telemetry, SrtpPolicy policy, string policySource)
    {
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _policy = policy;
        _policySource = policySource;
    }

    /// <summary>Emits one event indicating the resolved SRTP policy for this dialog.</summary>
    public void PublishPolicyApplied(ISipCallSession session)
    {
        _telemetry.PublishEvent(new SipEventRecord
        {
            EventType = "sip.media.srtp.policy.applied",
            CallId = session.CallId,
            CorrelationId = $"{session.CallId}:SRTP:POLICY",
            Attributes = new Dictionary<string, string>
            {
                ["policy"] = _policy.ToString(),
                ["policy_source"] = _policySource
            }
        });
    }

    /// <summary>Emits one telemetry decision record for an SRTP negotiation outcome.</summary>
    public void PublishDecision(
        ISipCallSession session,
        bool isSrtpNegotiated,
        string profile,
        string reasonCode,
        bool violatesPolicy)
    {
        _telemetry.PublishEvent(new SipEventRecord
        {
            EventType = "sip.media.srtp.decision",
            CallId = session.CallId,
            CorrelationId = $"{session.CallId}:SRTP:DECISION",
            Attributes = new Dictionary<string, string>
            {
                ["policy"] = _policy.ToString(),
                ["policy_source"] = _policySource,
                ["negotiated"] = isSrtpNegotiated ? "true" : "false",
                ["violates_policy"] = violatesPolicy ? "true" : "false",
                ["reason_code"] = reasonCode,
                ["media_profile"] = profile
            }
        });
    }
}
