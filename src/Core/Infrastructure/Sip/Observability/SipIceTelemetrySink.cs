using CalloraVoipSdk.Core.Application.Observability;
using CalloraVoipSdk.Core.Application.Ports.Connectivity;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Observability;

/// <summary>
/// Infrastructure adapter mapping application ICE telemetry events to SIP telemetry records.
/// </summary>
internal sealed class SipIceTelemetrySink : IIceTelemetrySink
{
    private readonly ISipTelemetrySink _telemetry;

    /// <summary>
    /// Creates an adapter around the shared SIP telemetry sink.
    /// </summary>
    internal SipIceTelemetrySink(ISipTelemetrySink telemetry)
    {
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
    }

    /// <inheritdoc />
    public void PublishEvent(IceTelemetryEvent record)
    {
        ArgumentNullException.ThrowIfNull(record);

        _telemetry.PublishEvent(new SipEventRecord
        {
            EventType = record.EventType,
            CallId = record.CallId,
            CorrelationId = record.CorrelationId,
            Timestamp = record.Timestamp,
            Attributes = record.Attributes
        });
    }
}
