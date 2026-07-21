namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// The reception state of a single inbound SSRC on a BUNDLE transport (RFC 3550 §A.1 sequence tracking, §A.3
/// loss, §A.8 interarrival jitter, §6.4.1 LSR/DLSR). Held by <see cref="BundledInboundReceptionStats"/>, one
/// per source. All state is mutated under <see cref="_sync"/>: the RTP receive path, the RTCP receive path,
/// and the reporter's snapshot run on different threads.
/// <para>
/// The sequence and loss bookkeeping is the same algorithm as <see cref="InboundRtpStatistics"/> — the loss
/// and fraction-lost formulas are deliberately identical so both report paths agree — extended here with the
/// RFC 3550 §A.8 jitter estimate, which the SIP path takes from the jitter buffer instead.
/// </para>
/// </summary>
internal sealed class BundledSourceReceptionState
{
    private readonly object _sync = new();

    // The negotiated media clock rate for this source (Hz), 0 when unknown. When known it drives the §A.8
    // transit estimate directly; when 0 the rate is inferred from the first usable packet pair as a fallback.
    private readonly uint _negotiatedClockRate;

    /// <summary>
    /// Creates the reception state for one inbound source.
    /// </summary>
    /// <param name="negotiatedClockRate">
    /// The source's negotiated RTP clock rate (Hz) from the SDP codec, or 0 when it is not known. A known rate
    /// is used directly for the RFC 3550 §A.8 interarrival jitter (correct under network jitter, which the
    /// per-pair inference is not); 0 falls back to inferring the rate from the first usable packet pair.
    /// </param>
    public BundledSourceReceptionState(uint negotiatedClockRate = 0)
    {
        _negotiatedClockRate = negotiatedClockRate;
        _clockRate = negotiatedClockRate;
    }

    // Sequence / loss tracking (RFC 3550 §A.1 / §A.3), mirrored from InboundRtpStatistics.
    private bool _initialized;
    private ushort _baseSequence;
    private ushort _maxSequence;
    private uint _sequenceCycles;
    private uint _packetsReceived;
    private uint _priorExpectedForFraction;
    private uint _priorReceivedForFraction;

    // Interarrival jitter (RFC 3550 §A.8): smoothed |D(i-1,i)| in RTP timestamp units, held as J*16 so the
    // 1/16 decay is integer arithmetic exactly as the RFC pseudocode does it.
    private bool _hasTransit;
    private uint _lastRtpTimestamp;
    private DateTimeOffset _lastArrival;
    private uint _clockRate;
    private double _jitter;

    // Last Sender Report seen from this source (RFC 3550 §6.4.1): LSR = middle 32 NTP bits; the arrival is
    // kept to derive DLSR (delay since last SR) at report time.
    private bool _hasSenderReport;
    private uint _lastSrMiddle32;
    private DateTimeOffset _lastSrArrival;

    /// <summary>Records one inbound RTP packet: sequence tracking, receive count, and the §A.8 jitter step.</summary>
    public void RecordRtp(ushort sequenceNumber, uint rtpTimestamp, DateTimeOffset arrival)
    {
        lock (_sync)
        {
            if (!_initialized)
            {
                Reset(sequenceNumber);
                UpdateJitter(rtpTimestamp, arrival);
                return;
            }

            _packetsReceived++;

            if (IsSequenceNewer(sequenceNumber, _maxSequence))
            {
                if (sequenceNumber < _maxSequence)
                    _sequenceCycles += 1u << 16;
                _maxSequence = sequenceNumber;
            }

            UpdateJitter(rtpTimestamp, arrival);
        }
    }

    /// <summary>Records the last SR from this source (LSR + arrival for DLSR).</summary>
    public void RecordSenderReport(uint lastSrMiddle32, DateTimeOffset arrival)
    {
        lock (_sync)
        {
            _hasSenderReport = true;
            _lastSrMiddle32 = lastSrMiddle32;
            _lastSrArrival = arrival;
        }
    }

    /// <summary>
    /// Adopts the negotiated RTP clock rate once it becomes known by payload type (CF-004f). A source first seen
    /// via its SR — or via a not-yet-mapped payload type — carries a 0/inferred clock until an RTP packet with a
    /// mapped payload type resolves the exact negotiated rate. This is only ever called for such a source (one
    /// whose kind is still unknown), so it never clobbers an already-negotiated rate. When it replaces an
    /// <em>inferred</em> rate it re-establishes the RFC 3550 §A.8 transit baseline, so the jitter estimate is not
    /// left scaled by the discarded rate; seeding onto a 0 clock (the common early-SR case) resets nothing.
    /// </summary>
    /// <param name="clockRate">The negotiated RTP clock rate (Hz); ignored when 0 or already the current rate.</param>
    public void TrySeedNegotiatedClockRate(uint clockRate)
    {
        if (clockRate == 0)
            return;

        lock (_sync)
        {
            if (_clockRate == clockRate)
                return;

            var replacedInferredRate = _clockRate != 0;
            _clockRate = clockRate;
            if (replacedInferredRate)
            {
                // The accumulated jitter was measured in the discarded rate's units; re-establish the §A.8
                // transit baseline so the estimate rebuilds on the exact negotiated clock.
                _hasTransit = false;
                _jitter = 0;
            }
        }
    }

    /// <summary>
    /// Captures this source's reception report block and advances the fraction-lost interval baseline
    /// (RFC 3550 §A.3). Returns <see langword="null"/> before any RTP has been counted. Stateful: call once
    /// per emitted report.
    /// </summary>
    public BundledReceptionReportBlock? CaptureReportBlock(uint ssrc, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (!_initialized)
                return null;

            var packetsExpected = CalculatePacketsExpected();
            var packetsReceived = _packetsReceived;
            var cumulativeLost = ClampSigned24((long)packetsExpected - packetsReceived);

            var expectedInterval = packetsExpected - _priorExpectedForFraction;
            var receivedInterval = packetsReceived - _priorReceivedForFraction;
            var lostInterval = (long)expectedInterval - receivedInterval;
            var fractionLost = ComputeFractionLost(expectedInterval, lostInterval);

            _priorExpectedForFraction = packetsExpected;
            _priorReceivedForFraction = packetsReceived;

            var extendedHighest = _sequenceCycles + _maxSequence;
            // RFC 3550 §6.4.1: the RR block carries the integral jitter (J held as the smoothed estimate).
            var jitter = (uint)_jitter;

            uint lastSr = 0;
            uint dlsr = 0;
            if (_hasSenderReport)
            {
                lastSr = _lastSrMiddle32;
                dlsr = ToDlsr(now - _lastSrArrival);
            }

            return new BundledReceptionReportBlock(
                ssrc, fractionLost, cumulativeLost, extendedHighest, jitter, lastSr, dlsr);
        }
    }

    /// <summary>
    /// The current smoothed interarrival jitter (RFC 3550 §A.8) of this source expressed in milliseconds, or
    /// <see langword="null"/> before a clock rate is established (no RTP counted, or an inferred rate not yet
    /// settled). The on-the-wire jitter is in RTP timestamp units; this converts it as
    /// <c>jitterRtpUnits × 1000 / clockRate</c>, the same conversion the SIP path applies to a peer's reported
    /// jitter. This is our local receive-side jitter (the browser <c>getStats</c> inbound-rtp jitter), not the
    /// outbound/RTT metrics.
    /// </summary>
    public double? SnapshotJitterMs()
    {
        lock (_sync)
        {
            if (_clockRate == 0)
                return null;
            return _jitter * 1000.0 / _clockRate;
        }
    }

    private void Reset(ushort firstSequenceNumber)
    {
        _initialized = true;
        _baseSequence = firstSequenceNumber;
        _maxSequence = firstSequenceNumber;
        _sequenceCycles = 0;
        _packetsReceived = 1;
        _priorExpectedForFraction = 0;
        _priorReceivedForFraction = 0;
    }

    // RFC 3550 §A.8 interarrival jitter. D(i-1,i) is the difference of the RTP-timestamp span and the
    // wall-clock span between two packets, both in RTP units; J += (|D| - J) / 16. The clock rate is the
    // negotiated one (from the SDP codec) when known — correct even under network jitter, which the previous
    // per-pair inference is not (a jittered first pair would bake a wrong rate into every later estimate). Only
    // when no rate was negotiated does it fall back to inferring the rate from the first usable packet pair.
    private void UpdateJitter(uint rtpTimestamp, DateTimeOffset arrival)
    {
        if (!_hasTransit)
        {
            _hasTransit = true;
            _lastRtpTimestamp = rtpTimestamp;
            _lastArrival = arrival;
            return;
        }

        var arrivalDeltaSeconds = (arrival - _lastArrival).TotalSeconds;
        var rtpDelta = unchecked((int)(rtpTimestamp - _lastRtpTimestamp));

        if (_clockRate == 0)
        {
            // Infer the clock rate from the first inter-packet pair; guard against a zero/negative interval
            // (reorder or identical arrival) by deferring until a usable pair is seen.
            if (arrivalDeltaSeconds > 0 && rtpDelta > 0)
                _clockRate = (uint)Math.Round(rtpDelta / arrivalDeltaSeconds);
            _lastRtpTimestamp = rtpTimestamp;
            _lastArrival = arrival;
            return;
        }

        var arrivalDeltaRtpUnits = arrivalDeltaSeconds * _clockRate;
        var transitDifference = Math.Abs(arrivalDeltaRtpUnits - rtpDelta);
        _jitter += (transitDifference - _jitter) / 16.0;

        _lastRtpTimestamp = rtpTimestamp;
        _lastArrival = arrival;
    }

    private uint CalculatePacketsExpected()
    {
        if (!_initialized)
            return 0;
        return _sequenceCycles + _maxSequence - _baseSequence + 1;
    }

    private static byte ComputeFractionLost(uint expectedInterval, long lostInterval)
    {
        if (expectedInterval == 0 || lostInterval <= 0)
            return 0;

        var scaled = (lostInterval << 8) / expectedInterval;
        return (byte)Math.Clamp(scaled, 0, 255);
    }

    private static int ClampSigned24(long value)
    {
        const int min = -8_388_608;
        const int max = 8_388_607;
        return (int)Math.Clamp(value, min, max);
    }

    private static bool IsSequenceNewer(ushort sequenceNumber, ushort reference)
        => unchecked((short)(sequenceNumber - reference)) > 0;

    // RFC 3550 §6.4.1: DLSR is the delay since the last SR, expressed in units of 1/65536 seconds.
    private static uint ToDlsr(TimeSpan elapsedSinceLastSr)
    {
        if (elapsedSinceLastSr <= TimeSpan.Zero)
            return 0;

        var value = elapsedSinceLastSr.TotalSeconds * 65536.0;
        if (value >= uint.MaxValue)
            return uint.MaxValue;
        return (uint)Math.Round(value, MidpointRounding.AwayFromZero);
    }
}
