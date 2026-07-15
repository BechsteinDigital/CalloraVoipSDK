namespace CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;

/// <summary>
/// Correlates the arrival times reconstructed from a transport-cc report (see
/// <see cref="TransportCcFeedbackInterpreter"/>) with the local send times kept in a
/// <see cref="TransportCcSendHistory"/> to produce one-way delay gradients — the delay-based
/// congestion signal. For each pair of consecutive packets that were received and whose send time is
/// still known, it emits <c>(arrival − prevArrival) − (send − prevSend)</c>. Not-received packets and
/// packets whose send time has been evicted are skipped (they only break the chain, contributing no
/// sample). This stage extracts the signal; deciding a target bitrate from a run of gradients is a
/// separate policy.
/// </summary>
internal static class TransportCcFeedbackCorrelator
{
    /// <summary>
    /// Produces the delay-gradient samples for one report, in sequence order. Empty when fewer than
    /// two packets could be correlated (a gradient needs a predecessor).
    /// </summary>
    /// <param name="results">Per-packet outcomes reconstructed from the report.</param>
    /// <param name="sendHistory">The send-timestamp history for the reported sequence numbers.</param>
    /// <param name="ticksPerSecond">Tick frequency of the recorded send timestamps (e.g. Stopwatch.Frequency).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ticksPerSecond"/> is not positive.</exception>
    public static IReadOnlyList<TransportCcDelaySample> Correlate(
        IReadOnlyList<TransportCcFeedbackResult> results,
        TransportCcSendHistory sendHistory,
        long ticksPerSecond)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(sendHistory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerSecond);

        var samples = new List<TransportCcDelaySample>();
        var hasPrevious = false;
        long previousArrivalMicros = 0;
        long previousSendMicros = 0;

        foreach (var result in results)
        {
            if (!result.Received || !sendHistory.TryGetSendTimestamp(result.SequenceNumber, out var sendTicks))
                continue; // a gap or an evicted send time breaks the chain — no sample here

            var sendMicros = TransportCcTime.ToMicros(sendTicks, ticksPerSecond);
            if (hasPrevious)
            {
                var gradient = (result.ArrivalMicros - previousArrivalMicros) - (sendMicros - previousSendMicros);
                samples.Add(new TransportCcDelaySample
                {
                    SequenceNumber = result.SequenceNumber,
                    DelayGradientMicros = gradient,
                });
            }

            previousArrivalMicros = result.ArrivalMicros;
            previousSendMicros = sendMicros;
            hasPrevious = true;
        }

        return samples;
    }
}
