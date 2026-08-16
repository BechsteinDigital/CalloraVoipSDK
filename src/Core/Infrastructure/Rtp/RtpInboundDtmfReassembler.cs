using CalloraVoipSdk.Core.Infrastructure.Common.Timing;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Reassembles an inbound RFC 4733 telephone-event (DTMF) stream. A tone is carried by a burst of packets
/// sharing one RTP timestamp with a growing duration, the last marked end-of-event (E-bit); the complete tone
/// is surfaced once, on the first end-of-event packet, with the reassembled duration. Shared by the bundled
/// and the single-stream media session so both behave alike.
/// <para>
/// The end-of-event packet can be lost. RFC 4733 §2.5.1.2 has the sender retransmit it three times, but all
/// three ride the same path and a burst of loss takes them together — and then the tone was simply never
/// reported, however complete the rest of the event was (#161 P3-16). Two bounded fallbacks close that:
/// a pending event is completed when it has been quiet for <see cref="EndOfEventTimeout"/> (checked on
/// inbound traffic, so it costs no timer), and when a different event displaces it. Both report the duration
/// accumulated so far, which is the last duration the sender actually announced.
/// </para>
/// </summary>
/// <remarks>
/// Threading: driven solely by its session's single receive loop, so the reassembly state needs no
/// synchronization. Keep it that way — any new caller from another thread must add explicit synchronization.
/// </remarks>
internal sealed class RtpInboundDtmfReassembler
{
    /// <summary>
    /// How long a pending event may stay quiet before it is completed without its end-of-event packet. A
    /// sender emits event updates at the packet cadence (20 ms is typical, 50 ms generous) and repeats the
    /// final packet three times, so a gap this long means the event is over — while a tone still being held
    /// keeps refreshing well inside it, however long the key stays down.
    /// </summary>
    public static readonly TimeSpan EndOfEventTimeout = TimeSpan.FromMilliseconds(200);

    private readonly int _telephoneEventClockRate;
    private readonly Action<byte, int> _onToneCompleted;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger _logger;

    private bool _hasPendingEvent;
    private uint _pendingSsrc;
    private uint _pendingTimestamp;
    private byte _pendingToneCode;
    private ushort _pendingDurationRtpUnits;
    private bool _pendingCompleted;
    private DateTimeOffset _pendingLastSeen;

    /// <summary>
    /// Creates a reassembler that reports a completed tone (code 0-15 and its duration in ms) via
    /// <paramref name="onToneCompleted"/>, converting durations with <paramref name="telephoneEventClockRate"/>.
    /// </summary>
    /// <param name="clock">Monotonic clock; injectable so the timeout is testable without waiting.</param>
    public RtpInboundDtmfReassembler(
        int telephoneEventClockRate,
        Action<byte, int> onToneCompleted,
        ILogger logger,
        Func<DateTimeOffset>? clock = null)
    {
        _telephoneEventClockRate = telephoneEventClockRate;
        _onToneCompleted = onToneCompleted ?? throw new ArgumentNullException(nameof(onToneCompleted));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? (() => MonotonicClock.Now);
    }

    /// <summary>Feeds one inbound telephone-event RTP packet; a completed tone is reported via the callback.</summary>
    public void Handle(RtpPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (!RtpTelephoneEventCodec.TryParse(
                packet.Payload.Span, out var toneCode, out var endOfEvent, out var durationRtpUnits))
        {
            _logger.LogDebug(
                "Ignoring malformed telephone-event RTP payload from SSRC={Ssrc:X8} (payloadLength={PayloadLength}).",
                packet.Ssrc, packet.Payload.Length);
            return;
        }

        if (toneCode > 15)
        {
            _logger.LogDebug("Ignoring unsupported telephone-event code {ToneCode}; supported range is 0-15.", toneCode);
            return;
        }

        var now = _clock();
        var isSameEvent =
            _hasPendingEvent &&
            _pendingSsrc == packet.Ssrc &&
            _pendingTimestamp == packet.Timestamp &&
            _pendingToneCode == toneCode;

        if (!isSameEvent)
        {
            // A different event displaces the pending one. If that one never got its end-of-event packet, it
            // is over regardless — report it now rather than drop the keypress on the floor.
            CompletePending("a new event displaced it");

            _hasPendingEvent = true;
            _pendingSsrc = packet.Ssrc;
            _pendingTimestamp = packet.Timestamp;
            _pendingToneCode = toneCode;
            _pendingDurationRtpUnits = durationRtpUnits;
            _pendingCompleted = false;
        }
        else if (durationRtpUnits > _pendingDurationRtpUnits)
        {
            _pendingDurationRtpUnits = durationRtpUnits;
        }

        _pendingLastSeen = now;

        if (!endOfEvent || _pendingCompleted)
            return;

        _pendingCompleted = true;
        Report(toneCode, _pendingDurationRtpUnits);
    }

    /// <summary>
    /// Completes a pending event whose end-of-event packet never arrived, once it has been quiet for
    /// <see cref="EndOfEventTimeout"/>. Call it from the session's inbound path for packets that are not
    /// telephone-events — ordinary audio keeps arriving while the lost end-of-event does not, so this needs no
    /// timer of its own. A no-op when nothing is pending or the event is still fresh.
    /// </summary>
    public void PollTimeout()
    {
        if (!_hasPendingEvent || _pendingCompleted)
            return;
        if (_clock() - _pendingLastSeen < EndOfEventTimeout)
            return;

        CompletePending("its end-of-event packet never arrived");
    }

    // Reports a pending, not-yet-completed event. Caller states why, for the diagnostic log.
    //
    // The event stays pending and is only marked completed — exactly what happens when a real end-of-event
    // packet arrives. That is what suppresses a duplicate: an end-of-event packet that was merely late rather
    // than lost still matches this event and is ignored, instead of being read as a fresh keypress.
    private void CompletePending(string reason)
    {
        if (!_hasPendingEvent || _pendingCompleted)
            return;

        _logger.LogDebug(
            "Completing telephone-event {ToneCode} from SSRC={Ssrc:X8} because {Reason} (RFC 4733 §2.5.1.2).",
            _pendingToneCode, _pendingSsrc, reason);

        _pendingCompleted = true;
        Report(_pendingToneCode, _pendingDurationRtpUnits);
    }

    private void Report(byte toneCode, ushort durationRtpUnits)
    {
        var durationMs = RtpTelephoneEventCodec.DurationRtpUnitsToMs(durationRtpUnits, _telephoneEventClockRate);

        try
        {
            _onToneCompleted(toneCode, durationMs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in inbound DtmfReceived handler.");
        }
    }
}
