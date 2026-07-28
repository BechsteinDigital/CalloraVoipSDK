using CalloraVoipSdk.Core.Application.Observability;
using CalloraVoipSdk.Core.Infrastructure.Common.Collections;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Observability;

/// <summary>
/// In-memory telemetry sink for diagnostics, tests, and local observability.
/// </summary>
internal sealed class InMemorySipTelemetrySink : ISipTelemetrySink
{
    private const int DefaultMaxEntriesPerStream = 4096;
    private readonly BoundedRingBuffer<SipEventRecord> _events;
    private readonly BoundedRingBuffer<SipMetricRecord> _metrics;
    private readonly BoundedRingBuffer<SipCdrRecord> _cdr;

    /// <summary>
    /// Creates an in-memory sink with bounded retention per stream.
    /// Oldest entries are dropped once capacity is reached.
    /// </summary>
    /// <param name="maxEntriesPerStream">Maximum retained entries per events/metrics/CDR stream.</param>
    internal InMemorySipTelemetrySink(int maxEntriesPerStream = DefaultMaxEntriesPerStream)
    {
        if (maxEntriesPerStream <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(maxEntriesPerStream),
                maxEntriesPerStream,
                "Maximum entries per stream must be greater than zero.");

        _events = new BoundedRingBuffer<SipEventRecord>(maxEntriesPerStream);
        _metrics = new BoundedRingBuffer<SipMetricRecord>(maxEntriesPerStream);
        _cdr = new BoundedRingBuffer<SipCdrRecord>(maxEntriesPerStream);
    }

    /// <summary>
    /// Snapshot of collected SIP events.
    /// </summary>
    public IReadOnlyCollection<SipEventRecord> Events => _events.Snapshot();

    /// <summary>
    /// Snapshot of collected SIP metric samples.
    /// </summary>
    public IReadOnlyCollection<SipMetricRecord> Metrics => _metrics.Snapshot();

    /// <summary>
    /// Snapshot of collected SIP call detail records.
    /// </summary>
    public IReadOnlyCollection<SipCdrRecord> Cdr => _cdr.Snapshot();

    /// <inheritdoc />
    public void PublishEvent(SipEventRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _events.Add(record);
    }

    /// <inheritdoc />
    public void PublishMetric(SipMetricRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _metrics.Add(record);
    }

    /// <inheritdoc />
    public void PublishCdr(SipCdrRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _cdr.Add(record);
    }
}
