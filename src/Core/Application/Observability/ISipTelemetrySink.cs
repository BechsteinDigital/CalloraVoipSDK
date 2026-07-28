namespace CalloraVoipSdk.Core.Application.Observability;

/// <summary>
/// Sink for SIP observability artifacts (events, metrics, CDR).
/// </summary>
public interface ISipTelemetrySink
{
    /// <summary>
    /// Publishes one structured SIP event.
    /// </summary>
    void PublishEvent(SipEventRecord record);

    /// <summary>
    /// Publishes one metric sample.
    /// </summary>
    void PublishMetric(SipMetricRecord record);

    /// <summary>
    /// Publishes one call detail record.
    /// </summary>
    void PublishCdr(SipCdrRecord record);
}
