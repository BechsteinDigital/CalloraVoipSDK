using CalloraVoipSdk.Core.Infrastructure.Rtp;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Unit tests for the RFC 4733 telephone-event (DTMF) wire codec extracted from the media session.
/// Pins the 4-byte payload layout and the ms ↔ RTP-unit duration conversions.
/// </summary>
public sealed class RtpTelephoneEventCodecTests
{
    [Fact]
    public void Build_then_parse_round_trips_tone_end_and_duration()
    {
        var payload = RtpTelephoneEventCodec.BuildPayload(toneCode: 5, endOfEvent: true, durationRtpUnits: 1600);

        Assert.Equal(4, payload.Length);
        Assert.True(RtpTelephoneEventCodec.TryParse(payload, out var tone, out var end, out var duration));
        Assert.Equal(5, tone);
        Assert.True(end);
        Assert.Equal(1600, duration);
    }

    [Fact]
    public void Parse_rejects_a_payload_shorter_than_the_event_header()
    {
        Assert.False(RtpTelephoneEventCodec.TryParse(new byte[] { 1, 2, 3 }, out _, out _, out _));
    }

    [Fact]
    public void Duration_converts_ms_to_rtp_units_on_the_8khz_clock()
    {
        // 100 ms on an 8 kHz clock = 800 units.
        Assert.Equal(800, RtpTelephoneEventCodec.DurationMsToRtpUnits(100, 8000));
    }

    [Fact]
    public void Duration_ms_conversion_is_floored_at_the_minimum()
    {
        // 8 units on an 8 kHz clock ≈ 1 ms, floored to the 40 ms tone minimum.
        Assert.Equal(RtpTelephoneEventCodec.MinDurationMs, RtpTelephoneEventCodec.DurationRtpUnitsToMs(8, 8000));
    }
}
