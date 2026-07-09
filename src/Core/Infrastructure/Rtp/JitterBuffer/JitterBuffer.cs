using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp.JitterBuffer;

/// <summary>
/// Adaptive jitter buffer with RFC 3550 §6.4.1 jitter estimation.
///
/// Design:
///   - Pull model: caller drives playout via TryGetNext(now).
///   - Extended sequence numbers: signed 16-bit delta prevents wrap-around issues.
///   - Reference-point scheduling: the first packet anchors (RTP-ts, wall-clock);
///     subsequent playout times derive from that anchor plus the RTP timestamp delta
///     converted to wall-clock milliseconds.
///   - Adaptive delay: increases up to +4 ms per late/jittery packet, decays
///     -0.5 ms per on-time packet. Delay floor is coupled to jitter + RTT hints
///     and clamped to [MinDelayMs, MaxDelayMs].
///   - When delay increases the reference playout time shifts forward to avoid
///     immediate discard of buffered packets.
/// </summary>
internal sealed class JitterBuffer : IJitterBuffer
{
    // -------------------------------------------------------------------------
    // Fields
    // -------------------------------------------------------------------------

    private readonly JitterBufferOptions _options;
    private readonly object _sync = new();

    /// <summary>Packets keyed by extended sequence number, sorted ascending.</summary>
    private readonly SortedDictionary<long, RtpPacket> _buffer = new();

    /// <summary>Highest extended seq number delivered to the consumer (-1 = none).</summary>
    private long _lastDelivered = -1;

    /// <summary>Highest extended seq number ever seen (used to detect "old" packets).</summary>
    private long _highestSeen = long.MinValue;

    // Reference-point playout scheduling
    private uint   _refRtpTs;
    private DateTimeOffset _refPlayoutTime;
    private bool   _referenceSet;

    // RFC 3550 §6.4.1 inter-arrival jitter (in RTP clock units, fixed-point)
    private double _jitter;
    private uint   _lastTransitTs; // RTP timestamp of previous packet (adjusted for transit)
    private bool   _transitSet;

    // Extended seq tracking for signed-wrap expansion.
    private ushort _lastSeq;

    private double _currentDelayMs;
    private double _estimatedRoundTripTimeMs;
    // The seed above is a startup hint; the first real RTCP RTT sample must replace it
    // outright (fast lock), and only later samples are EWMA-smoothed.
    private bool   _hasRealRoundTripSample;

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    public JitterBuffer(JitterBufferOptions? options = null)
    {
        _options                  = options ?? new JitterBufferOptions();
        _currentDelayMs           = ClampDelay(_options.InitialDelayMs);
        _estimatedRoundTripTimeMs = ClampRoundTrip(_options.InitialRoundTripTimeMs);
    }

    // -------------------------------------------------------------------------
    // IJitterBuffer
    // -------------------------------------------------------------------------

    public int BufferedCount
    {
        get
        {
            lock (_sync)
                return _buffer.Count;
        }
    }

    public double CurrentDelayMs
    {
        get
        {
            lock (_sync)
                return _currentDelayMs;
        }
    }

    public double EstimatedJitterMs
    {
        get
        {
            lock (_sync)
                return ConvertJitterUnitsToMs(_jitter);
        }
    }

    public double EstimatedRoundTripTimeMs
    {
        get
        {
            lock (_sync)
                return _estimatedRoundTripTimeMs;
        }
    }

    public JitterBufferAddResult Add(RtpPacket packet, DateTimeOffset arrivalTime)
    {
        ArgumentNullException.ThrowIfNull(packet);

        lock (_sync)
        {
            var extSeq = ExtendSequenceNumber(packet.SequenceNumber);

            // Duplicate check
            if (extSeq <= _lastDelivered || _buffer.ContainsKey(extSeq))
                return JitterBufferAddResult.Duplicate;

            // Update jitter estimate (RFC 3550 §6.4.1)
            UpdateJitter(packet.Timestamp, arrivalTime);

            // Establish or update reference point
            var playoutTime = ComputePlayoutTime(packet.Timestamp, arrivalTime);

            // Late check: playout time already passed
            if (playoutTime < arrivalTime)
            {
                // Adapt delay upward — we were too aggressive
                AdaptDelayUp();
                return JitterBufferAddResult.Late;
            }

            // Overflow check
            if (_buffer.Count >= _options.Capacity)
                return JitterBufferAddResult.Overflow;

            _buffer[extSeq] = packet;

            // Adapt delay downward — packet arrived on time
            AdaptDelayDown();

            return JitterBufferAddResult.Queued;
        }
    }

    public void UpdateRoundTripTime(double roundTripTimeMs)
    {
        if (double.IsNaN(roundTripTimeMs) || double.IsInfinity(roundTripTimeMs))
            return;

        lock (_sync)
        {
            var clampedRtt   = ClampRoundTrip(roundTripTimeMs);
            var smoothing    = Clamp01(_options.RoundTripTimeSmoothingFactor);

            // First real sample locks the estimate (replacing the startup seed); later samples
            // are EWMA-smoothed. Using the seed sign as the "no sample yet" proxy would break
            // now that the seed is non-zero, so track it explicitly.
            if (!_hasRealRoundTripSample)
            {
                _estimatedRoundTripTimeMs = clampedRtt;
                _hasRealRoundTripSample = true;
            }
            else
            {
                _estimatedRoundTripTimeMs += (clampedRtt - _estimatedRoundTripTimeMs) * smoothing;
            }

            var floor = ComputeAdaptiveDelayFloorMs();
            if (_currentDelayMs < floor)
            {
                var increase = floor - _currentDelayMs;
                _currentDelayMs = floor;

                if (_referenceSet && increase > 0)
                    _refPlayoutTime = _refPlayoutTime.AddMilliseconds(increase);
            }
        }
    }

    public RtpPacket? TryGetNext(DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_buffer.Count == 0)
                return null;

            // Peek at the lowest seq
            using var enumerator = _buffer.GetEnumerator();
            if (!enumerator.MoveNext())
                return null;

            var (extSeq, packet) = enumerator.Current;

            // We deliver the head only if its playout time has arrived.
            var playoutTime = ComputePlayoutTime(packet.Timestamp, default);
            if (now < playoutTime)
                return null;

            _buffer.Remove(extSeq);
            _lastDelivered = extSeq;
            return packet;
        }
    }

    // -------------------------------------------------------------------------
    // Extended sequence number (RFC 3550 §A.1 style, signed-delta approach)
    // -------------------------------------------------------------------------

    private long ExtendSequenceNumber(ushort seq)
    {
        if (_highestSeen == long.MinValue)
        {
            // First packet
            _lastSeq    = seq;
            _highestSeen = seq;
            return seq;
        }

        var delta = (short)(seq - _lastSeq);   // signed 16-bit wraparound delta
        var extended = _highestSeen + delta;

        if (extended > _highestSeen)
        {
            _highestSeen = extended;
            _lastSeq     = seq;
        }

        return extended;
    }

    // -------------------------------------------------------------------------
    // RFC 3550 §6.4.1 Inter-arrival jitter
    // -------------------------------------------------------------------------

    private void UpdateJitter(uint rtpTs, DateTimeOffset arrivalTime)
    {
        // Convert arrival time to RTP clock units, truncated modulo 2^32 like every RTP
        // timestamp. The previous double→uint cast overflowed (unix-ms × clock rate is
        // ~1.4e13) and SATURATED to uint.MaxValue on .NET, making the transit fall by
        // exactly one frame duration per packet — jitter converged to the frame interval
        // (20.00 ms) on a clean link instead of ~0.
        var arrivalRtpUnits = unchecked((uint)(arrivalTime.ToUnixTimeMilliseconds()
                                    * ClockRate / 1000));

        // transit = arrival_time_in_rtp_units - send_timestamp
        var transit = arrivalRtpUnits - rtpTs;

        if (!_transitSet)
        {
            _lastTransitTs = transit;
            _transitSet    = true;
            return;
        }

        // d(i) = |transit(i) - transit(i-1)|
        var d = (int)(transit - _lastTransitTs);
        if (d < 0) d = -d;

        // J(i) = J(i-1) + (|d| - J(i-1)) / 16
        _jitter        += (d - _jitter) / 16.0;
        _lastTransitTs  = transit;
    }

    // -------------------------------------------------------------------------
    // Reference-point playout scheduling
    // -------------------------------------------------------------------------

    private DateTimeOffset ComputePlayoutTime(uint rtpTs, DateTimeOffset arrivalTime)
    {
        if (!_referenceSet)
        {
            // Anchor: first packet plays out after InitialDelayMs from arrival
            _refRtpTs       = rtpTs;
            _refPlayoutTime = arrivalTime.AddMilliseconds(_currentDelayMs);
            _referenceSet   = true;
            return _refPlayoutTime;
        }

        // Delta in RTP clock units (signed 32-bit wraparound)
        var rtpDelta = (int)(rtpTs - _refRtpTs);
        var msDelta  = rtpDelta * 1000.0 / ClockRate;

        return _refPlayoutTime.AddMilliseconds(msDelta);
    }

    // -------------------------------------------------------------------------
    // Adaptive delay
    // -------------------------------------------------------------------------

    private void AdaptDelayUp()
    {
        var current = _currentDelayMs;
        var jitterMs = ConvertJitterUnitsToMs(_jitter);
        var increase = Math.Min(Math.Max(jitterMs, 1.0), 4.0);
        var candidate = ClampDelay(current + increase);
        var floor = ComputeAdaptiveDelayFloorMs();
        var next = Math.Max(candidate, floor);
        var appliedIncrease = next - current;

        _currentDelayMs = next;

        // Shift reference playout time forward so buffered packets aren't immediately dropped
        if (_referenceSet && appliedIncrease > 0)
            _refPlayoutTime = _refPlayoutTime.AddMilliseconds(appliedIncrease);
    }

    private void AdaptDelayDown()
    {
        var floor = ComputeAdaptiveDelayFloorMs();
        _currentDelayMs = Math.Max(_currentDelayMs - 0.5, floor);
    }

    private double ComputeAdaptiveDelayFloorMs()
    {
        var jitterContribution = ConvertJitterUnitsToMs(_jitter) * Math.Max(0, _options.JitterDelayWeight);
        var roundTripContribution = _estimatedRoundTripTimeMs * Math.Max(0, _options.RoundTripTimeDelayWeight);
        return ClampDelay(MinDelayMs + jitterContribution + roundTripContribution);
    }

    private double ConvertJitterUnitsToMs(double jitterUnits)
        => jitterUnits * 1000.0 / ClockRate;

    private double ClampDelay(double delayMs)
        => Math.Clamp(delayMs, MinDelayMs, MaxDelayMs);

    private double ClampRoundTrip(double roundTripTimeMs)
        => Math.Clamp(roundTripTimeMs, 0, Math.Max(0, _options.MaxRoundTripTimeMs));

    private static double Clamp01(double value)
        => Math.Clamp(value, 0, 1);

    private int ClockRate => Math.Max(1, _options.ClockRate);
    private double MinDelayMs => Math.Min(_options.MinDelayMs, _options.MaxDelayMs);
    private double MaxDelayMs => Math.Max(_options.MinDelayMs, _options.MaxDelayMs);
}
