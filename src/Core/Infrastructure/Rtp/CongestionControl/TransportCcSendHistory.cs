namespace CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;

/// <summary>
/// Remembers, on the sending side, when each transport-wide sequence number was put on the wire, so
/// an incoming transport-cc feedback report can be correlated with local send times to compute the
/// one-way delay gradient (congestion control). A fixed-capacity direct-mapped ring keyed by
/// <c>sequence % capacity</c>: <see cref="Record"/> allocates nothing on the send path, and an entry
/// is transparently evicted once a later sequence reuses its slot (the feedback for a packet that
/// old is no longer useful). Thread-safe — the send loop records while the feedback loop looks up.
/// </summary>
internal sealed class TransportCcSendHistory
{
    private readonly object _sync = new();
    private readonly long[] _sendTimestamps;
    private readonly int[] _slotSequences; // stored sequence per slot; -1 when the slot is unused

    /// <summary>
    /// Creates a history holding up to <paramref name="capacity"/> in-flight sequence numbers — size
    /// it to comfortably cover one feedback round trip's worth of packets.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not positive.</exception>
    public TransportCcSendHistory(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _sendTimestamps = new long[capacity];
        _slotSequences = new int[capacity];
        Array.Fill(_slotSequences, -1);
    }

    /// <summary>
    /// Records that <paramref name="sequenceNumber"/> was sent at <paramref name="sendTimestamp"/>
    /// (monotonic ticks), overwriting whatever older sequence shared its slot.
    /// </summary>
    public void Record(ushort sequenceNumber, long sendTimestamp)
    {
        var slot = sequenceNumber % _sendTimestamps.Length;
        lock (_sync)
        {
            _slotSequences[slot] = sequenceNumber;
            _sendTimestamps[slot] = sendTimestamp;
        }
    }

    /// <summary>
    /// Returns the recorded send timestamp for <paramref name="sequenceNumber"/>, or
    /// <see langword="false"/> when it was never recorded or has since been evicted from its slot.
    /// </summary>
    public bool TryGetSendTimestamp(ushort sequenceNumber, out long sendTimestamp)
    {
        var slot = sequenceNumber % _sendTimestamps.Length;
        lock (_sync)
        {
            if (_slotSequences[slot] == sequenceNumber)
            {
                sendTimestamp = _sendTimestamps[slot];
                return true;
            }
        }

        sendTimestamp = 0;
        return false;
    }
}
