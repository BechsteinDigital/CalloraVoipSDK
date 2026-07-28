using CalloraVoipSdk.Core.Application.Observability;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Observability;

/// <summary>
/// No-op telemetry sink used when observability integration is not configured.
/// </summary>
internal sealed class NullSipTelemetrySink : ISipTelemetrySink
{
    /// <summary>
    /// Shared singleton instance.
    /// </summary>
    public static NullSipTelemetrySink Instance { get; } = new();

    /// <inheritdoc />
    public void PublishEvent(SipEventRecord record)
    {
    }

    /// <inheritdoc />
    public void PublishMetric(SipMetricRecord record)
    {
    }

    /// <inheritdoc />
    public void PublishCdr(SipCdrRecord record)
    {
    }
}

