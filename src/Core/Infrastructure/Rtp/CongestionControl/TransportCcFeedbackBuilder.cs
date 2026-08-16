using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;

/// <summary>
/// Turns a batch of recorded packet arrivals (<see cref="TransportCcArrivalRecorder.Drain"/>) into a
/// transport-wide congestion-control feedback model (draft-holmer-rmcat-transport-wide-cc-extensions-01
/// §3.1) ready for <c>RtcpTransportFeedbackCodec.Encode</c>. It orders the arrivals by sequence number
/// (unwrapping the 16-bit counter so a batch straddling the 65535→0 boundary stays contiguous), fills
/// missing sequence numbers as not-received, and quantises arrival times into the reference time
/// (64 ms units, measured from a shared epoch so successive feedbacks stay on one timeline) and the
/// per-packet receive deltas (250 µs units).
/// <para>
/// The reference time is a signed 24-bit wire field and therefore cyclic: it repeats every
/// 2^24 × 64 ms ≈ 12.4 days, with the positive half lasting ≈ 6.21 days. The field is wrapped into
/// that range rather than rejected (matching libwebrtc's <c>TransportFeedback::SetBase</c>, which
/// takes the base time modulo the wrap period), so a long-running session keeps reporting instead of
/// failing permanently once the epoch ages out. The receive deltas stay on the unwrapped timeline, so
/// every arrival in a report remains correctly spaced relative to the reported reference time — which
/// is all a consumer needs, since delay gradients are formed inside one report, never across two.
/// </para>
/// </summary>
internal static class TransportCcFeedbackBuilder
{
    private const int MaxReferenceTime = 0x7FFFFF;   // signed 24-bit
    private const int MinReferenceTime = -0x800000;

    /// <summary>Period of the cyclic reference-time field, in 64 ms units (2^24 ≈ 12.4 days).</summary>
    private const long ReferenceTimeCycle = 0x1000000;

    // The 16-bit signed sequence unwrap can represent at most ±32767 from the anchor, so a batch may
    // span fewer than 2^15 sequence numbers; a wider span is rejected as ambiguous (and unbounded).
    private const int MaxUnwrappedSequenceSpan = 32_768;

    /// <summary>
    /// Builds one feedback message covering every sequence number from the lowest to the highest in
    /// <paramref name="arrivals"/>. Duplicate sequence numbers keep their earliest arrival.
    /// </summary>
    /// <param name="arrivals">Recorded arrivals in record order (need not be sorted); must be non-empty.</param>
    /// <param name="senderSsrc">SSRC of this endpoint (the feedback sender / media receiver).</param>
    /// <param name="mediaSsrc">SSRC of the media source the feedback is about.</param>
    /// <param name="feedbackPacketCount">Monotonic per-source feedback counter (caller-owned, wraps at 8 bits).</param>
    /// <param name="epochTimestamp">
    /// Shared time origin (Stopwatch ticks) the reference time is measured from, so reference times of
    /// successive feedbacks share one timeline. Arrivals are expected to be at or after it; a distance
    /// beyond the cyclic 24-bit range simply wraps.
    /// </param>
    /// <param name="ticksPerSecond">Tick frequency of the arrival timestamps (e.g. <see cref="System.Diagnostics.Stopwatch.Frequency"/>).</param>
    /// <exception cref="ArgumentException"><paramref name="arrivals"/> is empty, or the batch spans more sequence numbers than the 16-bit unwrap can resolve.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ticksPerSecond"/> is not positive.</exception>
    public static RtcpTransportFeedback Build(
        IReadOnlyList<TransportCcArrival> arrivals,
        uint senderSsrc,
        uint mediaSsrc,
        byte feedbackPacketCount,
        long epochTimestamp,
        long ticksPerSecond)
    {
        ArgumentNullException.ThrowIfNull(arrivals);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerSecond);
        if (arrivals.Count == 0)
            throw new ArgumentException("Cannot build transport-cc feedback from an empty batch.", nameof(arrivals));

        // Unwrap the 16-bit sequence numbers relative to the first recorded one, and keep the earliest
        // arrival per (unwrapped) sequence number so duplicates and reordering collapse cleanly. The
        // signed-16-bit difference resolves the 65535→0 wrap for any batch spanning ≤ 32767 sequence
        // numbers — far beyond a real feedback window; a larger span is rejected below.
        int anchor = arrivals[0].SequenceNumber;
        var earliestBySequence = new Dictionary<int, long>();
        foreach (var arrival in arrivals)
        {
            var unwrapped = anchor + (short)(arrival.SequenceNumber - anchor);
            if (!earliestBySequence.TryGetValue(unwrapped, out var existing) || arrival.ArrivalTimestamp < existing)
                earliestBySequence[unwrapped] = arrival.ArrivalTimestamp;
        }

        var baseSequence = int.MaxValue;
        var maxSequence = int.MinValue;
        foreach (var sequence in earliestBySequence.Keys)
        {
            if (sequence < baseSequence) baseSequence = sequence;
            if (sequence > maxSequence) maxSequence = sequence;
        }

        // Reject a span the 16-bit unwrap cannot represent unambiguously — this also bounds the
        // status array (a malformed batch cannot force a huge allocation).
        if ((long)maxSequence - baseSequence >= MaxUnwrappedSequenceSpan)
            throw new ArgumentException(
                "Batch spans more than 32767 sequence numbers; the 16-bit unwrap window is ambiguous.",
                nameof(arrivals));

        var baseArrivalMicros = TransportCcTime.ToMicros(earliestBySequence[baseSequence] - epochTimestamp, ticksPerSecond);
        var referenceTime = FloorDiv(baseArrivalMicros, TransportCcTime.ReferenceTimeUnitMicros);

        // The wire field is cyclic, so fold it into the signed 24-bit range instead of failing. Rejecting
        // it used to end transport-cc for the rest of the session: the epoch is fixed at the first arrival
        // and never moves, so once the session passed ≈6.21 days every batch was dropped by the caller and
        // the next one was too. Wrapping keeps the reports flowing across the boundary.
        var wireReferenceTime = WrapReferenceTime(referenceTime);

        // reconstructedMicros tracks the receiver's view rebuilt from the quantised deltas, so delta
        // rounding does not drift across the report (each delta is relative to the previous rebuilt time).
        // It stays on the UNWRAPPED timeline: the deltas then describe the same spacing the peer sees when
        // it rebuilds them from the wrapped reference time.
        var reconstructedMicros = referenceTime * TransportCcTime.ReferenceTimeUnitMicros;
        var statuses = new RtcpTransportFeedbackStatus[maxSequence - baseSequence + 1];
        for (var sequence = baseSequence; sequence <= maxSequence; sequence++)
        {
            var index = sequence - baseSequence;
            if (!earliestBySequence.TryGetValue(sequence, out var arrivalTicks))
            {
                statuses[index] = new RtcpTransportFeedbackStatus
                {
                    SequenceNumber = unchecked((ushort)sequence),
                    Received = false,
                };
                continue;
            }

            var arrivalMicros = TransportCcTime.ToMicros(arrivalTicks - epochTimestamp, ticksPerSecond);
            var deltaTicks = RoundDiv(arrivalMicros - reconstructedMicros, TransportCcTime.DeltaUnitMicros);
            reconstructedMicros += deltaTicks * TransportCcTime.DeltaUnitMicros;
            statuses[index] = new RtcpTransportFeedbackStatus
            {
                SequenceNumber = unchecked((ushort)sequence),
                Received = true,
                DeltaTicks = (int)deltaTicks,
            };
        }

        return new RtcpTransportFeedback
        {
            SenderSsrc = senderSsrc,
            MediaSsrc = mediaSsrc,
            ReferenceTimeTicks = wireReferenceTime,
            FeedbackPacketCount = feedbackPacketCount,
            Statuses = statuses,
        };
    }

    // Folds an unbounded reference time (64 ms units) into the cyclic signed 24-bit wire range, so the
    // value crossing the positive edge continues into the negative half rather than being rejected.
    private static int WrapReferenceTime(long referenceTime)
    {
        var wrapped = referenceTime % ReferenceTimeCycle; // (-2^24, 2^24)
        if (wrapped > MaxReferenceTime)
            wrapped -= ReferenceTimeCycle;
        else if (wrapped < MinReferenceTime)
            wrapped += ReferenceTimeCycle;
        return (int)wrapped;
    }

    // Floor division toward negative infinity (C# integer division truncates toward zero).
    private static long FloorDiv(long value, long divisor)
    {
        var quotient = value / divisor;
        if (value % divisor != 0 && (value < 0) != (divisor < 0))
            quotient--;
        return quotient;
    }

    // Division rounded to the nearest integer, symmetric about zero (negative deltas occur under reordering).
    private static long RoundDiv(long value, long divisor)
        => value >= 0
            ? (value + divisor / 2) / divisor
            : -((-value + divisor / 2) / divisor);
}
