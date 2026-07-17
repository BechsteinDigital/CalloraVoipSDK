using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;

namespace CalloraVoipSdk;

/// <summary>
/// Telemetry facade for SIP events, metrics, and call-detail records.
/// </summary>
public sealed class TelemetryManager : ITelemetryManager
{
    internal TelemetryManager(ClientTelemetrySink sink)
    {
        sink.EventPublished += record => EventPublished?.Invoke(this, record);
        sink.MetricPublished += record => MetricPublished?.Invoke(this, record);
        sink.CdrPublished += record => CdrPublished?.Invoke(this, record);
    }

    /// <summary>
    /// Raised when one SIP event record is published.
    /// </summary>
    public event EventHandler<SipEventRecord>? EventPublished;

    /// <summary>
    /// Raised when one SIP metric record is published.
    /// </summary>
    public event EventHandler<SipMetricRecord>? MetricPublished;

    /// <summary>
    /// Raised when one SIP call-detail record is published.
    /// </summary>
    public event EventHandler<SipCdrRecord>? CdrPublished;
}

internal sealed class ClientTelemetrySink(ISipTelemetrySink inner) : ISipTelemetrySink
{
    public event Action<SipEventRecord>? EventPublished;
    public event Action<SipMetricRecord>? MetricPublished;
    public event Action<SipCdrRecord>? CdrPublished;

    public void PublishEvent(SipEventRecord record)
    {
        inner.PublishEvent(record);
        EventPublished?.Invoke(record);
    }

    public void PublishMetric(SipMetricRecord record)
    {
        inner.PublishMetric(record);
        MetricPublished?.Invoke(record);
    }

    public void PublishCdr(SipCdrRecord record)
    {
        inner.PublishCdr(record);
        CdrPublished?.Invoke(record);
    }
}
