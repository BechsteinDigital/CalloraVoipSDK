using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Depth-bounded reorder window for a video RTP stream. Video packets arrive out of order
/// (network reordering) and are filled late by RTX retransmission (RFC 4588); playout and
/// depacketisation need them in ascending sequence order. This buffer holds up to
/// <c>depth</c> packets so a reordered or retransmitted packet has a window to slot into
/// place, then emits packets strictly in ascending sequence order.
///
/// Design:
///   - Sliding depth window: once more than <c>depth</c> packets are buffered, the lowest
///     sequence number is released. A gap that the window outgrows is skipped — the release
///     jumps past the missing sequence, and the consumer sees the discontinuity in the
///     emitted sequence numbers (its cue to conceal or request a keyframe).
///   - Extended sequence numbers (RFC 3550 §A.1, signed 16-bit delta) make the ordering
///     wrap-aware: a packet that wraps 65535→0 still sorts after its predecessor.
///   - Duplicates (already buffered or already released) and too-late packets (at or below
///     the last released sequence) are dropped.
///
/// The buffer is a pure mechanic: it never inspects payloads and makes no timing decisions.
/// Thread-safe — the RTP receive path inserts while a playout path may flush, on different
/// threads.
/// </summary>
internal sealed class VideoReorderBuffer
{
    private static readonly IReadOnlyList<RtpPacket> None = Array.Empty<RtpPacket>();

    private readonly int _depth;
    private readonly object _sync = new();

    /// <summary>Buffered packets keyed by extended sequence number, sorted ascending.</summary>
    private readonly SortedDictionary<long, RtpPacket> _buffer = new();

    /// <summary>Highest extended sequence number ever seen (anchors wrap expansion).</summary>
    private long _highestSeen = long.MinValue;

    /// <summary>Raw 16-bit sequence number of the highest-seen packet.</summary>
    private ushort _lastSeq;

    /// <summary>Extended sequence number of the most recently released packet.</summary>
    private long _lastReleased;

    /// <summary>False until the first packet is released (so sequence 0 is not mistaken for "released").</summary>
    private bool _hasReleased;

    /// <param name="depth">
    /// Maximum packets held before the front is released. Sizes the reorder/RTX window: large
    /// enough to absorb realistic reordering and a round-trip of retransmission, small enough
    /// to bound added latency. Must stay well below 32768 so 16-bit sequence wrap cannot alias
    /// a live entry with a released one.
    /// </param>
    public VideoReorderBuffer(int depth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(depth, 16384);
        _depth = depth;
    }

    /// <summary>Number of packets currently held in the window (test/telemetry seam).</summary>
    public int BufferedCount
    {
        get
        {
            lock (_sync)
                return _buffer.Count;
        }
    }

    /// <summary>
    /// Inserts one received packet and returns any packets that become releasable, in
    /// ascending sequence order. A packet is released once the window would otherwise exceed
    /// <c>depth</c>; the returned list is empty while the window is still filling. Duplicates
    /// and packets at or below the last released sequence are dropped and return an empty list.
    /// </summary>
    public IReadOnlyList<RtpPacket> Insert(RtpPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        lock (_sync)
        {
            var extSeq = ExtendSequenceNumber(packet.SequenceNumber);

            // Too late: this sequence has already been released past.
            if (_hasReleased && extSeq <= _lastReleased)
                return None;

            // Duplicate still in the window.
            if (!_buffer.TryAdd(extSeq, packet))
                return None;

            if (_buffer.Count <= _depth)
                return None;

            // Over depth: release from the front until back within the window. A single insert
            // can only push the count one past depth, so the steady state releases exactly one
            // packet — return that without a list allocation. The loop is a defensive drain.
            var first = ReleaseFront();
            if (_buffer.Count <= _depth)
                return new[] { first };

            var released = new List<RtpPacket> { first };
            while (_buffer.Count > _depth)
                released.Add(ReleaseFront());

            return released;
        }
    }

    /// <summary>
    /// Drains every buffered packet in ascending sequence order, emptying the window. Used at
    /// end of stream to flush whatever reordering slack is still held.
    /// </summary>
    public IReadOnlyList<RtpPacket> Flush()
    {
        lock (_sync)
        {
            if (_buffer.Count == 0)
                return None;

            var drained = new List<RtpPacket>(_buffer.Count);
            while (_buffer.Count > 0)
                drained.Add(ReleaseFront());

            return drained;
        }
    }

    /// <summary>Removes and returns the lowest-sequence buffered packet, advancing the released mark.</summary>
    private RtpPacket ReleaseFront()
    {
        using var enumerator = _buffer.GetEnumerator();
        enumerator.MoveNext();
        var (extSeq, packet) = enumerator.Current;

        _buffer.Remove(extSeq);
        _lastReleased = extSeq;
        _hasReleased = true;
        return packet;
    }

    /// <summary>
    /// Expands a 16-bit sequence number to a monotonic extended value using a signed 16-bit
    /// delta against the highest seen (RFC 3550 §A.1 style), so wrap-around and moderate
    /// reordering both sort correctly. Older (reordered) sequences return a lower extended
    /// value without moving the anchor.
    /// </summary>
    private long ExtendSequenceNumber(ushort seq)
    {
        if (_highestSeen == long.MinValue)
        {
            _lastSeq = seq;
            _highestSeen = seq;
            return seq;
        }

        var delta = (short)(seq - _lastSeq);
        var extended = _highestSeen + delta;

        if (extended > _highestSeen)
        {
            _highestSeen = extended;
            _lastSeq = seq;
        }

        return extended;
    }
}
