namespace CalloraVoipSdk.Core.Application.Observability;

/// <summary>
/// Structured SIP event record for diagnostics and event-stream consumers.
/// </summary>
public sealed class SipEventRecord
{
    /// <summary>
    /// Event type identifier.
    /// </summary>
    public required string EventType { get; init; }

    /// <summary>
    /// Correlated SIP call identifier when available.
    /// </summary>
    public string? CallId { get; init; }

    /// <summary>
    /// Correlation token for tracing distributed signaling paths.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Distributed trace identifier used for cross-component correlation.
    /// </summary>
    public string? TraceId { get; init; }

    /// <summary>
    /// Event timestamp.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Event attributes.
    /// </summary>
    public IReadOnlyDictionary<string, string> Attributes { get; init; } = new Dictionary<string, string>();
}
