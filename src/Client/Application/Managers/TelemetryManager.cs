using CalloraVoipSdk.Core.Application.Observability;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk;

/// <summary>
/// Telemetry facade for SIP events, metrics, and call-detail records.
/// </summary>
public sealed class TelemetryManager : ITelemetryManager
{
    internal TelemetryManager(ClientTelemetrySink sink, ILogger logger)
    {
        // #166 P3-14: the sink isolates each of ITS subscribers, and this manager is exactly one of them — so
        // without the shared dispatch here, one throwing app handler still blocked every later handler on the
        // same telemetry event. Same contract as everywhere else now: per subscriber, logged, never propagated.
        sink.EventPublished += record =>
            SdkEventDispatch.Raise(EventPublished, this, record, logger, nameof(EventPublished));
        sink.MetricPublished += record =>
            SdkEventDispatch.Raise(MetricPublished, this, record, logger, nameof(MetricPublished));
        sink.CdrPublished += record =>
            SdkEventDispatch.Raise(CdrPublished, this, record, logger, nameof(CdrPublished));
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
    /// subscriber must not block the others. The subscriber fan-out is the SDK-wide event contract
    /// (<see cref="SdkEventDispatch"/>, #166 P3-14); only the inner sink is guarded separately, because it is
    /// the host's monitoring pipeline rather than an event subscriber.
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

        SdkEventDispatch.Raise(subscribers, record, logger, $"telemetry {typeof(T).Name}");
    }
}
