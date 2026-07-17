namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Inbound RTP statistics for one <see cref="RtpCallMediaSession"/>: the lock-free per-packet delivery
/// counters (received/queued/delivered/dropped/concealed/lost) plus the lock-guarded RTCP
/// receiver-report state (per-SSRC sequence tracking, cumulative/fraction loss, extended highest
/// sequence). Extracted so the counting and RTCP-report bookkeeping live behind one collaborator
/// instead of being scattered across the session; the session still decides WHEN to count.
/// <para>Thread-safe: counters use <see cref="System.Threading.Interlocked"/>; the RTCP state is held
/// under an internal lock, exactly as it was in the session.</para>
/// </summary>
internal sealed class InboundRtpStatistics
{
    // Delivery counters — updated lock-free from the RTP receive/playout/concealment paths.
    private long _packetsReceived;
    private long _packetsQueued;
    private long _packetsDelivered;
    private long _packetsDroppedLate;
    private long _packetsDroppedOverflow;
    private long _packetsDroppedDuplicate;
    private long _packetsConcealed;
    private long _packetsUnrecoverableLoss;

    // RTCP receiver-report state — all reads/writes under _rtcpSync.
    private readonly object _rtcpSync = new();
    private bool _hasInboundRtcpStats;
    private bool _hasRemoteSsrc;
    private uint _remoteSsrc;
    private ushort _baseSequence;
    private ushort _maxSequence;
    private uint _sequenceCycles;
    private uint _packetsReceivedForRtcp;
    private uint _priorExpectedForFraction;
    private uint _priorReceivedForFraction;

    /// <summary>Counts one packet handed to the session by the RTP receive loop.</summary>
    public void RecordReceived() => Interlocked.Increment(ref _packetsReceived);

    /// <summary>Counts one packet accepted into the jitter buffer.</summary>
    public void RecordQueued() => Interlocked.Increment(ref _packetsQueued);

    /// <summary>Counts one packet delivered to the consumer.</summary>
    public void RecordDelivered() => Interlocked.Increment(ref _packetsDelivered);

    /// <summary>Counts one packet dropped as too late for playout.</summary>
    public void RecordDroppedLate() => Interlocked.Increment(ref _packetsDroppedLate);

    /// <summary>Counts one packet dropped due to jitter-buffer overflow.</summary>
    public void RecordDroppedOverflow() => Interlocked.Increment(ref _packetsDroppedOverflow);

    /// <summary>Counts one packet dropped as a duplicate.</summary>
    public void RecordDroppedDuplicate() => Interlocked.Increment(ref _packetsDroppedDuplicate);

    /// <summary>Counts one concealment frame synthesised for a gap.</summary>
    public void RecordConcealed() => Interlocked.Increment(ref _packetsConcealed);

    /// <summary>Counts <paramref name="count"/> lost packets that could not be concealed.</summary>
    public void AddUnrecoverableLoss(long count) => Interlocked.Add(ref _packetsUnrecoverableLoss, count);

    /// <summary>Reads the current delivery counters (each read atomically; not a group snapshot).</summary>
    public InboundCounters SnapshotCounters() => new(
        PacketsReceived: Interlocked.Read(ref _packetsReceived),
        PacketsQueued: Interlocked.Read(ref _packetsQueued),
        PacketsDelivered: Interlocked.Read(ref _packetsDelivered),
        PacketsDroppedLate: Interlocked.Read(ref _packetsDroppedLate),
        PacketsDroppedOverflow: Interlocked.Read(ref _packetsDroppedOverflow),
        PacketsDroppedDuplicate: Interlocked.Read(ref _packetsDroppedDuplicate),
        PacketsConcealed: Interlocked.Read(ref _packetsConcealed),
        PacketsUnrecoverableLoss: Interlocked.Read(ref _packetsUnrecoverableLoss));

    /// <summary>
    /// Updates the per-SSRC sequence tracking that feeds the RTCP receiver report. A change of remote
    /// SSRC restarts tracking from <paramref name="sequenceNumber"/>.
    /// </summary>
    public void TrackSequence(uint ssrc, ushort sequenceNumber)
    {
        lock (_rtcpSync)
        {
            if (!_hasRemoteSsrc || _remoteSsrc != ssrc)
            {
                _remoteSsrc = ssrc;
                _hasRemoteSsrc = true;
                Reset(sequenceNumber);
                return;
            }

            _packetsReceivedForRtcp++;

            if (IsSequenceNewer(sequenceNumber, _maxSequence))
            {
                if (sequenceNumber < _maxSequence)
                    _sequenceCycles += 1u << 16;

                _maxSequence = sequenceNumber;
            }
        }
    }

    /// <summary>
    /// Captures the current RTCP receiver-report figures and advances the fraction-lost interval
    /// baseline. Stateful: call exactly once per emitted report.
    /// </summary>
    public InboundRtcpReport CaptureRtcpReport()
    {
        lock (_rtcpSync)
        {
            var packetsExpected = CalculatePacketsExpected();
            var packetsReceived = _packetsReceivedForRtcp;
            var cumulativeLost = ClampSigned24((long)packetsExpected - packetsReceived);

            var expectedInterval = packetsExpected - _priorExpectedForFraction;
            var receivedInterval = packetsReceived - _priorReceivedForFraction;
            var lostInterval = (long)expectedInterval - receivedInterval;
            var fractionLost = ComputeFractionLost(expectedInterval, lostInterval);

            _priorExpectedForFraction = packetsExpected;
            _priorReceivedForFraction = packetsReceived;

            var extendedHighest = !_hasInboundRtcpStats ? 0u : _sequenceCycles + _maxSequence;
            var remoteSsrc = _hasRemoteSsrc ? (uint?)_remoteSsrc : null;

            return new InboundRtcpReport(
                PacketsExpected: packetsExpected,
                PacketsReceived: packetsReceived,
                FractionLost: fractionLost,
                CumulativePacketsLost: cumulativeLost,
                ExtendedHighestSequenceNumber: extendedHighest,
                RemoteSsrc: remoteSsrc);
        }
    }

    private void Reset(ushort firstSequenceNumber)
    {
        _hasInboundRtcpStats = true;
        _baseSequence = firstSequenceNumber;
        _maxSequence = firstSequenceNumber;
        _sequenceCycles = 0;
        _packetsReceivedForRtcp = 1;
        _priorExpectedForFraction = 0;
        _priorReceivedForFraction = 0;
    }

    private uint CalculatePacketsExpected()
    {
        if (!_hasInboundRtcpStats)
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
}

/// <summary>Immutable snapshot of the inbound delivery counters.</summary>
internal readonly record struct InboundCounters(
    long PacketsReceived,
    long PacketsQueued,
    long PacketsDelivered,
    long PacketsDroppedLate,
    long PacketsDroppedOverflow,
    long PacketsDroppedDuplicate,
    long PacketsConcealed,
    long PacketsUnrecoverableLoss);

/// <summary>RTCP receiver-report figures captured from the inbound sequence tracking.</summary>
internal readonly record struct InboundRtcpReport(
    uint PacketsExpected,
    uint PacketsReceived,
    byte FractionLost,
    int CumulativePacketsLost,
    uint ExtendedHighestSequenceNumber,
    uint? RemoteSsrc);
