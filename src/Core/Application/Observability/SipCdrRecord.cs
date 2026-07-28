namespace CalloraVoipSdk.Core.Application.Observability;

/// <summary>
/// Call detail record emitted for SIP dialog lifecycles.
/// </summary>
public sealed class SipCdrRecord
{
    /// <summary>
    /// SIP Call-ID.
    /// </summary>
    public required string CallId { get; init; }

    /// <summary>
    /// Local URI.
    /// </summary>
    public required string LocalUri { get; init; }

    /// <summary>
    /// Remote URI.
    /// </summary>
    public required string RemoteUri { get; init; }

    /// <summary>
    /// Dialog start timestamp.
    /// </summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// Dialog end timestamp.
    /// </summary>
    public required DateTimeOffset EndedAt { get; init; }

    /// <summary>
    /// Final SIP/dialog outcome label.
    /// </summary>
    public required string Outcome { get; init; }

    /// <summary>
    /// Distributed trace identifier for correlation with events and metrics.
    /// </summary>
    public string? TraceId { get; init; }

    /// <summary>
    /// Total call duration.
    /// </summary>
    public TimeSpan Duration => EndedAt - StartedAt;
}
