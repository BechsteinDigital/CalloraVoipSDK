using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// RFC 4733 §2.5.1.4: an out-of-band telephone-event (DTMF) burst must reserve timestamp space on the audio
/// clock so a following event or media frame carries a distinct, advancing timestamp — otherwise consecutive
/// DTMF tones reuse the same timestamp and a receiver folds them into one, dropping the repeat. This locks the
/// SIP RTP session's reserve-and-advance cursor primitive that <c>RtpCallMediaSession.SendDtmfAsync</c> uses.
/// </summary>
public sealed class RtpSessionTimestampReserveTests
{
    private static RtpSessionOptions Options() => new()
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 5004),
        PayloadType = 0,
        ClockRate = 8000,
        SamplesPerPacket = 160,
    };

    [Fact]
    public async Task Reserving_timestamp_space_returns_the_current_cursor_and_advances_past_the_event()
    {
        await using var session = new RtpSession(Options(), new RtpPacketCodec(), NullLogger<RtpSession>.Instance);

        const uint eventDuration = 640; // e.g. an 80 ms DTMF event on an 8 kHz clock
        var baseline = session.GetCurrentTimestamp();

        var firstEvent = session.ReserveTimestamp(eventDuration);
        var secondEvent = session.ReserveTimestamp(eventDuration);

        Assert.Equal(baseline, firstEvent);                                 // stamps the first event at the cursor
        Assert.Equal(unchecked(baseline + eventDuration), secondEvent);     // the next event is advanced, distinct
        Assert.NotEqual(firstEvent, secondEvent);
        Assert.Equal(unchecked(baseline + (2 * eventDuration)), session.GetCurrentTimestamp());
    }
}
