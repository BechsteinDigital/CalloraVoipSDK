using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Reassembles an inbound RFC 4733 telephone-event (DTMF) stream on a bundled media session (RFC 4733 §2.5.1.2):
/// a DTMF tone is carried by a burst of packets sharing one RTP timestamp with a growing duration, the last marked
/// end-of-event (E-bit). The complete tone is surfaced once, on the first end-of-event packet, with the reassembled
/// duration. Mirrors the SIP path (<c>RtpCallMediaSession.HandleInboundTelephoneEvent</c>) so both behave alike.
/// <para>
/// Threading: driven solely by the session's single shared receive loop (the inbound pipeline dispatches
/// sequentially per the transport's one receive task), so its reassembly state needs no synchronization. Keep it
/// that way — any new caller from another thread must add explicit synchronization.
/// </para>
/// </summary>
internal sealed class BundledInboundDtmfReassembler
{
    private readonly int _telephoneEventClockRate;
    private readonly Action<byte, int> _onToneCompleted;
    private readonly ILogger _logger;

    private bool _hasPendingEvent;
    private uint _pendingSsrc;
    private uint _pendingTimestamp;
    private byte _pendingToneCode;
    private ushort _pendingDurationRtpUnits;
    private bool _pendingCompleted;

    /// <summary>
    /// Creates a reassembler that reports a completed tone (code 0-15 and its duration in ms) via
    /// <paramref name="onToneCompleted"/>, converting durations with <paramref name="telephoneEventClockRate"/>.
    /// </summary>
    public BundledInboundDtmfReassembler(int telephoneEventClockRate, Action<byte, int> onToneCompleted, ILogger logger)
    {
        _telephoneEventClockRate = telephoneEventClockRate;
        _onToneCompleted = onToneCompleted ?? throw new ArgumentNullException(nameof(onToneCompleted));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Feeds one inbound telephone-event RTP packet; a completed tone is reported via the callback.</summary>
    public void Handle(RtpPacket packet)
    {
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

        var isSameEvent =
            _hasPendingEvent &&
            _pendingSsrc == packet.Ssrc &&
            _pendingTimestamp == packet.Timestamp &&
            _pendingToneCode == toneCode;

        if (!isSameEvent)
        {
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

        if (!endOfEvent || _pendingCompleted)
            return;

        _pendingCompleted = true;
        var durationMs = RtpTelephoneEventCodec.DurationRtpUnitsToMs(_pendingDurationRtpUnits, _telephoneEventClockRate);

        try
        {
            _onToneCompleted(toneCode, durationMs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in bundled DtmfReceived handler.");
        }
    }
}
