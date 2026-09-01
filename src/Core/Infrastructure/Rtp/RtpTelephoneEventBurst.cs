namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Sends one DTMF tone as the series of packets RFC 4733 describes, rather than as a single burst.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this replaces, and why it was wrong.</b> Both send paths used to emit three packets — one
/// start and two ends — back to back, within microseconds, while the payload claimed a duration of
/// 160 ms. Two things follow from that, and both are the kind of failure that shows up at one customer
/// and not at the next:
/// </para>
/// <list type="number">
/// <item>A gateway that reconstructs the tone from the <em>arrival timing</em> of the packets — which
/// is what happens when interworking to analogue or ISDN, and what several IVRs do — sees a tone that
/// lasted no time at all, and reports no digit.</item>
/// <item>There is nothing between the start and the end. One lost packet is a lost digit, where the
/// RFC's design recovers from it: every intermediate packet repeats the event with a longer duration,
/// so any one of them carries the whole tone.</item>
/// </list>
/// <para>
/// <b>The redundancy is at both ends.</b> RFC 4733 §2.5.1.4 asks for the final packet three times, and
/// the same argument applies to the first: it is the one carrying the marker bit, and a receiver that
/// misses it may treat the following packets as a tone it never saw begin.
/// </para>
/// <para>
/// <b>Every packet of one event carries the same RTP timestamp.</b> That is the event's start time
/// (RFC 4733 §2.5.1.2); only the duration field grows. A rising timestamp would describe several
/// consecutive tones instead of one.
/// </para>
/// </remarks>
internal static class RtpTelephoneEventBurst
{
    /// <summary>How often an in-progress event repeats itself. Matches ordinary audio packetisation.</summary>
    internal const int PacketPeriodMs = 20;

    /// <summary>Copies of the first and last packet — RFC 4733 §2.5.1.4.</summary>
    internal const int RedundantCopies = 3;

    /// <summary>
    /// Sends one tone.
    /// </summary>
    /// <param name="send">Sends one packet: the payload and whether it carries the marker bit.</param>
    /// <param name="delay">
    /// Waits between packets. Injected so a test can run the whole burst without waiting for it — and
    /// so a caller that needs a different clock is not forced onto <c>Task.Delay</c>.
    /// </param>
    /// <param name="toneCode">DTMF tone (0–15).</param>
    /// <param name="durationMs">How long the tone lasts.</param>
    /// <param name="clockRate">The audio clock the duration is expressed in.</param>
    /// <param name="cancellationToken">Stops the burst; the tone then ends where it stopped.</param>
    internal static async ValueTask SendAsync(
        Func<byte[], bool, CancellationToken, ValueTask> send,
        Func<int, CancellationToken, Task> delay,
        byte toneCode,
        int durationMs,
        int clockRate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(delay);

        var totalRtpUnits = RtpTelephoneEventCodec.DurationMsToRtpUnits(durationMs, clockRate);
        var stepRtpUnits = RtpTelephoneEventCodec.DurationMsToRtpUnits(PacketPeriodMs, clockRate);

        // The first packets already claim one period, not zero: a duration of zero describes an event
        // that has not started, and some receivers discard it.
        var elapsed = Math.Min(stepRtpUnits, totalRtpUnits);

        for (var copy = 0; copy < RedundantCopies; copy++)
        {
            // Marker on the very first packet only (RFC 4733 §2.5.1.3): it marks the start of the tone,
            // and repeating it on the duplicates would announce three tones.
            await send(
                    RtpTelephoneEventCodec.BuildPayload(toneCode, endOfEvent: false, durationRtpUnits: elapsed),
                    copy == 0,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        while (elapsed + stepRtpUnits < totalRtpUnits && !cancellationToken.IsCancellationRequested)
        {
            await delay(PacketPeriodMs, cancellationToken).ConfigureAwait(false);
            elapsed = (ushort)Math.Min(elapsed + stepRtpUnits, totalRtpUnits);

            await send(
                    RtpTelephoneEventCodec.BuildPayload(toneCode, endOfEvent: false, durationRtpUnits: elapsed),
                    false,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            await delay(PacketPeriodMs, cancellationToken).ConfigureAwait(false);
        }

        // The end packet reports the full duration even if the burst was cut short, because that is what
        // was reserved on the timestamp cursor: reporting less would leave a gap the next event falls
        // into, and a receiver would fold the two tones together.
        var endPayload = RtpTelephoneEventCodec.BuildPayload(
            toneCode, endOfEvent: true, durationRtpUnits: totalRtpUnits);

        for (var copy = 0; copy < RedundantCopies; copy++)
        {
            await send(endPayload, false, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
