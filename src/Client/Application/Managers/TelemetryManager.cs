using CalloraVoipSdk.Core.Application.Observability;
using Microsoft.Extensions.Logging;

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

internal sealed class ClientTelemetrySink(ISipTelemetrySink inner, ILogger logger) : ISipTelemetrySink
{
    public event Action<SipEventRecord>? EventPublished;
    public event Action<SipMetricRecord>? MetricPublished;
    public event Action<SipCdrRecord>? CdrPublished;

    public void PublishEvent(SipEventRecord record) => Publish(record, inner.PublishEvent, EventPublished);

    public void PublishMetric(SipMetricRecord record) => Publish(record, inner.PublishMetric, MetricPublished);

    public void PublishCdr(SipCdrRecord record) => Publish(record, inner.PublishCdr, CdrPublished);

    /// <summary>
    /// #166 P1-3: telemetry must never steer the SIP service path. Core publishes these records from inside
    /// REGISTER/INVITE/cleanup steps, so a faulty monitoring sink or a throwing app subscriber must be isolated
    /// — its fault is logged and swallowed rather than propagated back into signalling — and one throwing
    /// subscriber must not block the others (each delegate in the invocation list is invoked independently).
    /// </summary>
    private void Publish<T>(T record, Action<T> innerSink, Action<T>? subscribers)
    {
        try
        {
            innerSink(record);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Telemetry sink threw for a {RecordType} record; ignored so signalling is unaffected.", typeof(T).Name);
        }

        if (subscribers is null)
            return;

        foreach (var handler in subscribers.GetInvocationList())
        {
            try
            {
                ((Action<T>)handler)(record);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Telemetry subscriber threw for a {RecordType} record; ignored so later subscribers still run.", typeof(T).Name);
            }
        }
    }
}
