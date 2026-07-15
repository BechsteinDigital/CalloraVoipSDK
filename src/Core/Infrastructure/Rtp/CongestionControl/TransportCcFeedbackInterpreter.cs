using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;

/// <summary>
/// Reconstructs per-packet outcomes from a received transport-cc feedback report (the inverse of
/// <see cref="TransportCcFeedbackBuilder"/>): the report's reference time plus the cumulative receive
/// deltas rebuild each received packet's arrival time on the reporting peer's clock, and gaps stay
/// marked not-received. This is the neutral first stage of the sender-side estimator — it extracts
/// the arrival/loss facts a congestion-control policy then interprets (delay gradient, loss ratio);
/// it makes no bandwidth decision itself.
/// </summary>
internal static class TransportCcFeedbackInterpreter
{
    // draft-holmer §3.1: reference time is in 64 ms units, receive deltas in 250 µs units. Kept in
    // step with TransportCcFeedbackBuilder — the Build→Interpret round-trip test guards the pairing.
    private const long ReferenceTimeUnitMicros = 64_000;
    private const long DeltaUnitMicros = 250;

    /// <summary>
    /// Reconstructs each reported packet's outcome in sequence order. Received packets carry an
    /// arrival time in microseconds accumulated from the report's reference time; not-received
    /// packets carry none.
    /// </summary>
    public static IReadOnlyList<TransportCcFeedbackResult> Interpret(RtcpTransportFeedback feedback)
    {
        ArgumentNullException.ThrowIfNull(feedback);

        var results = new TransportCcFeedbackResult[feedback.Statuses.Count];
        var arrivalMicros = feedback.ReferenceTimeTicks * ReferenceTimeUnitMicros;
        for (var i = 0; i < feedback.Statuses.Count; i++)
        {
            var status = feedback.Statuses[i];
            if (status.Received)
            {
                // Each delta is relative to the previous received packet (or the reference time for
                // the first), so accumulate to rebuild the absolute arrival time.
                arrivalMicros += status.DeltaTicks * DeltaUnitMicros;
                results[i] = new TransportCcFeedbackResult
                {
                    SequenceNumber = status.SequenceNumber,
                    Received = true,
                    ArrivalMicros = arrivalMicros,
                };
            }
            else
            {
                results[i] = new TransportCcFeedbackResult
                {
                    SequenceNumber = status.SequenceNumber,
                    Received = false,
                };
            }
        }

        return results;
    }
}
