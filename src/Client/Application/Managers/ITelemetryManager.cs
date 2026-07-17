using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;

namespace CalloraVoipSdk;

/// <summary>
/// Telemetry facade for SIP events, metrics, and call-detail records.
/// </summary>
public interface ITelemetryManager
{
    /// <summary>
    /// Raised when one SIP event record is published.
    /// </summary>
    event EventHandler<SipEventRecord>? EventPublished;

    /// <summary>
    /// Raised when one SIP metric record is published.
    /// </summary>
    event EventHandler<SipMetricRecord>? MetricPublished;

    /// <summary>
    /// Raised when one SIP call-detail record is published.
    /// </summary>
    event EventHandler<SipCdrRecord>? CdrPublished;
}
