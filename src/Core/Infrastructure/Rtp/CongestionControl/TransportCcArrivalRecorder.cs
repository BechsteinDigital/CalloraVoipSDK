namespace CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;

/// <summary>
/// Thread-safe bounded recorder of packet arrivals for transport-wide congestion control. The
/// receive path calls <see cref="Record"/> for each stamped incoming packet; a feedback timer
/// periodically calls <see cref="Drain"/> to build a transport-cc RTCP report
/// (draft-holmer-rmcat-transport-wide-cc-extensions-01) from the batch. Backed by a fixed-capacity
/// ring buffer so <see cref="Record"/> allocates nothing on the hot receive path; when the buffer
/// is full the oldest arrival is overwritten and counted in <see cref="DroppedCount"/> rather than
/// silently lost.
/// </summary>
internal sealed class TransportCcArrivalRecorder
{
    private readonly object _sync = new();
    private readonly TransportCcArrival[] _buffer;
    private int _start; // index of the oldest buffered arrival
    private int _count;
    private long _dropped;

    /// <summary>
    /// Creates a recorder holding up to <paramref name="capacity"/> arrivals between drains. Sized
    /// to comfortably cover one feedback interval's worth of packets; overflow drops the oldest.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not positive.</exception>
    public TransportCcArrivalRecorder(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _buffer = new TransportCcArrival[capacity];
    }

    /// <summary>
    /// Total arrivals overwritten because the buffer was full when they were recorded (cumulative
    /// across drains). A non-zero, growing value means the feedback interval or capacity is too small.
    /// </summary>
    public long DroppedCount
    {
        get { lock (_sync) return _dropped; }
    }

    /// <summary>
    /// Records one packet arrival. Safe to call concurrently with <see cref="Drain"/>. When the
    /// buffer is full the oldest arrival is overwritten and <see cref="DroppedCount"/> is incremented.
    /// </summary>
    /// <param name="sequenceNumber">The transport-wide sequence number from the header extension.</param>
    /// <param name="arrivalTimestamp">A monotonic arrival timestamp (Stopwatch ticks).</param>
    public void Record(ushort sequenceNumber, long arrivalTimestamp)
    {
        var arrival = new TransportCcArrival
        {
            SequenceNumber = sequenceNumber,
            ArrivalTimestamp = arrivalTimestamp,
        };

        lock (_sync)
        {
            if (_count == _buffer.Length)
            {
                _buffer[_start] = arrival; // overwrite oldest
                _start = (_start + 1) % _buffer.Length;
                _dropped++;
                return;
            }

            _buffer[(_start + _count) % _buffer.Length] = arrival;
            _count++;
        }
    }

    /// <summary>
    /// Removes and returns all buffered arrivals in the order they were recorded (arrival order,
    /// which may differ from sequence order under reordering — the feedback builder sorts and fills
    /// gaps). Returns an empty list when nothing was recorded since the last drain.
    /// </summary>
    public IReadOnlyList<TransportCcArrival> Drain()
    {
        lock (_sync)
        {
            if (_count == 0)
                return [];

            var drained = new TransportCcArrival[_count];
            for (var i = 0; i < _count; i++)
                drained[i] = _buffer[(_start + i) % _buffer.Length];

            _start = 0;
            _count = 0;
            return drained;
        }
    }
}
