using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Inbound RFC 4733 reassembly, shared by the bundled and the single-stream path. A tone is normally surfaced
/// on its end-of-event packet — but that packet can be lost, and RFC 4733 §2.5.1.2's three retransmissions all
/// ride the same path, so one burst of loss takes them together. Without a fallback the keypress was simply
/// never reported (#161 P3-16). Two bounded ones close it: a quiet pending event times out, and a new event
/// displaces the pending one. Neither may turn a merely late end-of-event packet into a second keypress.
/// </summary>
public sealed class RtpInboundDtmfReassemblerTests
{
    private const int ClockRate = 8000;
    private const uint Ssrc = 0x1234;

    private static RtpPacket Event(byte toneCode, bool endOfEvent, ushort durationRtpUnits, uint timestamp = 1000) => new()
    {
        PayloadType = 101,
        SequenceNumber = 1,
        Timestamp = timestamp,
        Ssrc = Ssrc,
        Payload = RtpTelephoneEventCodec.BuildPayload(toneCode, endOfEvent, durationRtpUnits),
    };

    private static RtpInboundDtmfReassembler Reassembler(
        List<(byte Tone, int DurationMs)> tones, Func<DateTimeOffset> clock)
        => new(ClockRate, (tone, ms) => tones.Add((tone, ms)), NullLogger.Instance, clock);

    [Fact]
    public void A_complete_burst_still_reports_once_on_the_end_of_event_packet()
    {
        var now = DateTimeOffset.UnixEpoch;
        var tones = new List<(byte, int)>();
        var reassembler = Reassembler(tones, () => now);

        reassembler.Handle(Event(5, endOfEvent: false, durationRtpUnits: 160));
        now += TimeSpan.FromMilliseconds(20);
        reassembler.Handle(Event(5, endOfEvent: false, durationRtpUnits: 320));
        now += TimeSpan.FromMilliseconds(20);
        reassembler.Handle(Event(5, endOfEvent: true, durationRtpUnits: 480));
        // The sender repeats the final packet three times (RFC 4733 §2.5.1.2) — still one tone.
        reassembler.Handle(Event(5, endOfEvent: true, durationRtpUnits: 480));
        reassembler.Handle(Event(5, endOfEvent: true, durationRtpUnits: 480));

        Assert.Equal([((byte)5, 60)], tones); // 480 units at 8 kHz
    }

    [Fact]
    public void A_lost_end_of_event_packet_is_completed_by_the_timeout()
    {
        var now = DateTimeOffset.UnixEpoch;
        var tones = new List<(byte, int)>();
        var reassembler = Reassembler(tones, () => now);

        reassembler.Handle(Event(7, endOfEvent: false, durationRtpUnits: 160));
        now += TimeSpan.FromMilliseconds(20);
        reassembler.Handle(Event(7, endOfEvent: false, durationRtpUnits: 320));

        // Still inside the window: the event may yet be alive, so nothing is reported.
        now += TimeSpan.FromMilliseconds(100);
        reassembler.PollTimeout();
        Assert.Empty(tones);

        now += RtpInboundDtmfReassembler.EndOfEventTimeout;
        reassembler.PollTimeout();

        Assert.Equal([((byte)7, 40)], tones); // the last duration the sender announced (320 units)
    }

    [Fact]
    public void A_late_end_of_event_packet_after_a_timeout_does_not_report_a_second_keypress()
    {
        var now = DateTimeOffset.UnixEpoch;
        var tones = new List<(byte, int)>();
        var reassembler = Reassembler(tones, () => now);

        reassembler.Handle(Event(3, endOfEvent: false, durationRtpUnits: 160));
        now += RtpInboundDtmfReassembler.EndOfEventTimeout;
        reassembler.PollTimeout();
        Assert.Single(tones);

        // The packet was late, not lost: same event, so it is absorbed rather than read as a new press.
        reassembler.Handle(Event(3, endOfEvent: true, durationRtpUnits: 240));

        Assert.Single(tones);
    }

    [Fact]
    public void A_new_event_completes_the_one_it_displaces()
    {
        var now = DateTimeOffset.UnixEpoch;
        var tones = new List<(byte, int)>();
        var reassembler = Reassembler(tones, () => now);

        reassembler.Handle(Event(1, endOfEvent: false, durationRtpUnits: 800, timestamp: 1000));
        now += TimeSpan.FromMilliseconds(20);

        // The next keypress arrives before the first one's end-of-event packet did.
        reassembler.Handle(Event(2, endOfEvent: false, durationRtpUnits: 160, timestamp: 5000));
        now += TimeSpan.FromMilliseconds(20);
        reassembler.Handle(Event(2, endOfEvent: true, durationRtpUnits: 320, timestamp: 5000));

        // Both keypresses, in order: the displaced one with the last duration its sender announced.
        Assert.Equal([((byte)1, 100), ((byte)2, 40)], tones);
    }

    [Fact]
    public void Polling_with_nothing_pending_or_after_a_normal_completion_reports_nothing()
    {
        var now = DateTimeOffset.UnixEpoch;
        var tones = new List<(byte, int)>();
        var reassembler = Reassembler(tones, () => now);

        reassembler.PollTimeout();
        Assert.Empty(tones);

        reassembler.Handle(Event(9, endOfEvent: true, durationRtpUnits: 160));
        Assert.Single(tones);

        now += RtpInboundDtmfReassembler.EndOfEventTimeout * 10;
        reassembler.PollTimeout();

        Assert.Single(tones);
    }
}
