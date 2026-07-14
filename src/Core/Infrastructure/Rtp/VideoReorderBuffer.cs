using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Contiguous-release reorder/playout window for a video RTP stream. Video packets arrive out
/// of order (network reordering) and are filled late by RTX retransmission (RFC 4588); playout
/// and depacketisation need them in ascending sequence order.
///
/// Design:
///   - Contiguous release: each packet is emitted as soon as it extends the delivered run, so
///     an in-order stream adds no buffering latency at all — packets pass straight through.
///   - Gap hold: when the next expected sequence is missing, subsequent packets are held (not
///     released) to give a reordered or RTX-retransmitted packet a window to arrive and slot
///     into the gap. Latency is therefore incurred only while a gap is open, not continuously.
///   - Bounded wait: the hold is capped at <c>depth</c> packets. Once more than <c>depth</c>
///     packets are buffered behind an unfilled gap, the window gives up — it skips forward to
///     the lowest buffered sequence and resumes releasing. The consumer sees the resulting jump
///     in emitted sequence numbers (its cue to conceal or request a keyframe).
///   - Extended sequence numbers (RFC 3550 §A.1, signed 16-bit delta) make the ordering
///     wrap-aware: a packet that wraps 65535→0 still sorts after its predecessor.
///   - Duplicates (still buffered or already released) and too-late packets (below the next
///     expected sequence) are dropped.
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

    /// <summary>Extended sequence number of the next packet to release in order.</summary>
    private long _nextExpected;

    /// <summary>False until the first packet defines the delivery baseline.</summary>
    private bool _started;

    /// <param name="depth">
    /// Maximum packets held behind an unfilled gap before the window skips past it. Sizes the
    /// reorder/RTX wait window: large enough to absorb realistic reordering and a round-trip of
    /// retransmission, small enough to bound the latency a gap adds. Must stay well below 32768
    /// so 16-bit sequence wrap cannot alias a live entry with a released one.
    /// </param>
    public VideoReorderBuffer(int depth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(depth, 16384);
        _depth = depth;
    }

    /// <summary>Number of packets currently held behind a gap (test/telemetry seam).</summary>
    public int BufferedCount
    {
        get
        {
            lock (_sync)
                return _buffer.Count;
        }
    }

    /// <summary>
    /// Inserts one received packet and returns any packets that become releasable, in ascending
    /// sequence order. An in-order packet is returned immediately; a packet that fills a gap
    /// releases itself and everything now contiguous behind it. While a gap is open and under
    /// the depth budget the returned list is empty (the packet is held). Duplicates and packets
    /// below the next expected sequence are dropped and return an empty list.
    /// </summary>
    public IReadOnlyList<RtpPacket> Insert(RtpPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        lock (_sync)
        {
            var extSeq = ExtendSequenceNumber(packet.SequenceNumber);

            // Too late: this sequence has already been released past.
            if (_started && extSeq < _nextExpected)
                return None;

            // Duplicate still held behind a gap.
            if (!_buffer.TryAdd(extSeq, packet))
                return None;

            // The first packet ever seen defines where in-order delivery starts.
            if (!_started)
            {
                _nextExpected = extSeq;
                _started = true;
            }

            return DrainReleasable();
        }
    }

    /// <summary>
    /// Drains every buffered packet in ascending sequence order, emptying the window. Used at
    /// end of stream to flush whatever is still held behind a gap.
    /// </summary>
    public IReadOnlyList<RtpPacket> Flush()
    {
        lock (_sync)
        {
            if (_buffer.Count == 0)
                return None;

            var drained = new List<RtpPacket>(_buffer.Count);
            var lastKey = _nextExpected;
            foreach (var (key, value) in _buffer) // SortedDictionary enumerates ascending
            {
                drained.Add(value);
                lastKey = key;
            }

            _buffer.Clear();
            _nextExpected = lastKey + 1; // stale re-arrivals after a flush are too late
            _started = true;
            return drained;
        }
    }

    /// <summary>
    /// Releases the contiguous run starting at <see cref="_nextExpected"/>. If more than
    /// <see cref="_depth"/> packets remain held behind an unfilled gap afterwards, skips forward
    /// to the lowest buffered sequence and releases again — bounding how long a gap is awaited.
    /// </summary>
    private IReadOnlyList<RtpPacket> DrainReleasable()
    {
        RtpPacket? first = null;
        List<RtpPacket>? rest = null;

        while (true)
        {
            while (_buffer.TryGetValue(_nextExpected, out var next))
            {
                _buffer.Remove(_nextExpected);
                _nextExpected++;

                if (first is null)
                    first = next;
                else
                    (rest ??= new List<RtpPacket> { first }).Add(next);
            }

            if (_buffer.Count <= _depth)
                break;

            _nextExpected = FirstBufferedKey(); // give up on the gap, skip to the lowest held
        }

        if (rest is not null)
            return rest;
        return first is null ? None : new[] { first };
    }

    /// <summary>Smallest extended sequence number currently buffered (buffer is non-empty).</summary>
    private long FirstBufferedKey()
    {
        using var keys = _buffer.Keys.GetEnumerator();
        keys.MoveNext();
        return keys.Current;
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
