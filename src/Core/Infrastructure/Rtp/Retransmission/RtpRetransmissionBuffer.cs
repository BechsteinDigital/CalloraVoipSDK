using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Retransmission;

/// <summary>
/// Bounded history of recently sent RTP packets, keyed by sequence number, so a NACK
/// (RFC 4585) can be answered by retransmitting the requested packets via RTX
/// (RFC 4588). Capacity-limited to bound memory: the oldest packet is evicted once the
/// window is full — a NACK for a packet older than the window simply misses. Thread-safe:
/// the send path stores while the RTCP receive path looks up, on different threads.
/// </summary>
internal sealed class RtpRetransmissionBuffer
{
    private readonly int _capacity;
    private readonly Dictionary<ushort, RtpPacket> _packets;
    private readonly Queue<ushort> _order;
    private readonly object _sync = new();

    /// <param name="capacity">
    /// Maximum packets retained. Sized to the retransmit window — roughly a round-trip
    /// time worth of packets so a NACK can still find its target (baresip keeps ~500 ms).
    /// Must stay well below 65536 so 16-bit sequence-number wrap cannot alias a live entry
    /// with an evicted one; capped at 32768 to guarantee that.
    /// </param>
    public RtpRetransmissionBuffer(int capacity = 512)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 32768);
        _capacity = capacity;
        _packets = new Dictionary<ushort, RtpPacket>(capacity);
        _order = new Queue<ushort>(capacity);
    }

    /// <summary>
    /// Records one sent packet. When the window is full the oldest is evicted first. A
    /// resent sequence number replaces the stored entry and is not re-queued (so eviction
    /// order stays correct and the window never grows past capacity).
    /// </summary>
    public void Store(RtpPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        lock (_sync)
        {
            if (!_packets.ContainsKey(packet.SequenceNumber))
            {
                if (_order.Count >= _capacity)
                    _packets.Remove(_order.Dequeue());

                _order.Enqueue(packet.SequenceNumber);
            }

            _packets[packet.SequenceNumber] = packet;
        }
    }

    /// <summary>
    /// Looks up a previously sent packet by its sequence number for retransmission.
    /// Returns <see langword="false"/> when it is no longer in the window.
    /// </summary>
    public bool TryGet(ushort sequenceNumber, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out RtpPacket packet)
    {
        lock (_sync)
        {
            return _packets.TryGetValue(sequenceNumber, out packet);
        }
    }

    /// <summary>Number of packets currently retained (test/telemetry seam).</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _packets.Count;
            }
        }
    }
}
