using System;
using System.Buffers.Binary;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// RFC 4733 telephone-event (DTMF) RTP payload wire codec: pure encode/decode of the 4-byte event
/// payload plus duration conversions between milliseconds and the RTP clock. Extracted from
/// <see cref="RtpCallMediaSession"/> so the wire format stays a small, focused, independently
/// testable unit with no session state.
/// </summary>
internal static class RtpTelephoneEventCodec
{
    /// <summary>Minimum sane DTMF event duration in milliseconds — the tone floor for send and receive.</summary>
    internal const int MinDurationMs = 40;

    private const int PayloadLength = 4;
    private const int DefaultVolume = 10;

    /// <summary>
    /// Parses a telephone-event payload (RFC 4733 §2.3): tone code, end-of-event bit, and duration in
    /// RTP units. Returns <see langword="false"/> for a payload shorter than the 4-byte event header.
    /// </summary>
    internal static bool TryParse(
        ReadOnlySpan<byte> payload,
        out byte toneCode,
        out bool endOfEvent,
        out ushort durationRtpUnits)
    {
        toneCode = 0;
        endOfEvent = false;
        durationRtpUnits = 0;

        if (payload.Length < PayloadLength)
            return false;

        toneCode = payload[0];
        endOfEvent = (payload[1] & 0x80) != 0;
        durationRtpUnits = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(2, 2));
        return true;
    }

    /// <summary>Builds a 4-byte telephone-event payload (RFC 4733 §2.3) at the default volume.</summary>
    internal static byte[] BuildPayload(byte toneCode, bool endOfEvent, ushort durationRtpUnits)
    {
        var payload = new byte[PayloadLength];
        payload[0] = toneCode;
        payload[1] = (byte)((endOfEvent ? 0x80 : 0x00) | (DefaultVolume & 0x3F));
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2, 2), durationRtpUnits);
        return payload;
    }

    /// <summary>Converts a duration in milliseconds to RTP clock units (clamped to [1, ushort.MaxValue]).</summary>
    internal static ushort DurationMsToRtpUnits(int durationMs, int clockRate)
    {
        var units = durationMs * clockRate / 1000.0;
        var rounded = (int)Math.Round(units, MidpointRounding.AwayFromZero);
        return (ushort)Math.Clamp(rounded, 1, ushort.MaxValue);
    }

    /// <summary>Converts a duration in RTP clock units to milliseconds (floored at <see cref="MinDurationMs"/>).</summary>
    internal static int DurationRtpUnitsToMs(ushort durationRtpUnits, int clockRate)
    {
        var milliseconds = durationRtpUnits * 1000.0 / Math.Max(clockRate, 1);
        var rounded = (int)Math.Round(milliseconds, MidpointRounding.AwayFromZero);
        return Math.Max(rounded, MinDurationMs);
    }
}
