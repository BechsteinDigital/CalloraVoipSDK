namespace CalloraVoipSdk.Core.Application.Observability;

/// <summary>
/// Simple metric data point for SIP counters/timers.
/// </summary>
public sealed class SipMetricRecord
{
    /// <summary>
    /// Metric name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Numeric metric value.
    /// </summary>
    public required double Value { get; init; }

    /// <summary>
    /// Metric timestamp.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Distributed trace identifier that this metric point belongs to.
    /// </summary>
    public string? TraceId { get; init; }

    /// <summary>
    /// Metric labels.
    /// </summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
}
