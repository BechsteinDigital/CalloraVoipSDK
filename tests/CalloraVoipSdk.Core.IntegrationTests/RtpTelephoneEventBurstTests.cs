using CalloraVoipSdk.Core.Infrastructure.Rtp;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Sending one DTMF tone as the series of packets RFC 4733 describes.
/// </summary>
/// <remarks>
/// The previous shape — one start packet and two ends, emitted back to back within microseconds while
/// the payload claimed 160 ms — failed in two ways that only show at some customers: a gateway that
/// reconstructs the tone from arrival timing saw no tone at all, and a single lost packet lost the whole
/// digit because there was nothing in between to recover from.
/// </remarks>
public sealed class RtpTelephoneEventBurstTests
{
    private const int ClockRate = 8000;

    [Fact]
    public async Task The_tone_is_spread_over_its_duration_instead_of_leaving_at_once()
    {
        // 160 ms at a 20 ms period is eight packets' worth of tone, and the packets have to be spread
        // across it — the receiver on the other side may be timing them.
        var sent = await Send(toneCode: 1, durationMs: 160);

        Assert.True(
            sent.Count > RtpTelephoneEventBurst.RedundantCopies * 2,
            $"expected intermediate packets between start and end, got {sent.Count}");
    }

    [Fact]
    public async Task The_first_and_last_packets_are_sent_three_times()
    {
        // RFC 4733 §2.5.1.4 for the end, and the same argument for the start: it carries the marker
        // bit, and a receiver that misses it may treat the rest as a tone it never saw begin.
        var sent = await Send(toneCode: 5, durationMs: 160);

        Assert.Equal(
            RtpTelephoneEventBurst.RedundantCopies,
            sent.TakeWhile(packet => !packet.EndOfEvent).Take(RtpTelephoneEventBurst.RedundantCopies).Count());
        Assert.Equal(RtpTelephoneEventBurst.RedundantCopies, sent.Count(packet => packet.EndOfEvent));
    }

    [Fact]
    public async Task Only_the_very_first_packet_carries_the_marker()
    {
        // Repeating it on the duplicates would announce three tones where there is one.
        var sent = await Send(toneCode: 2, durationMs: 160);

        Assert.True(sent[0].Marker);
        Assert.All(sent.Skip(1), packet => Assert.False(packet.Marker));
    }

    [Fact]
    public async Task The_reported_duration_grows_and_never_shrinks()
    {
        // Each packet repeats the event with a longer duration, which is what lets any one of them
        // carry the whole tone if the others are lost.
        var sent = await Send(toneCode: 3, durationMs: 200);

        var durations = sent.Select(packet => packet.Duration).ToArray();
        Assert.Equal(durations, durations.OrderBy(duration => duration));
        Assert.True(durations[0] > 0, "a duration of zero describes an event that has not started");
    }

    [Fact]
    public async Task The_last_packet_reports_the_full_duration()
    {
        // It has to match what was reserved on the timestamp cursor. Reporting less leaves a gap the
        // next event falls into, and the receiver folds two tones into one.
        var sent = await Send(toneCode: 7, durationMs: 160);

        Assert.Equal(
            RtpTelephoneEventCodec.DurationMsToRtpUnits(160, ClockRate),
            sent[^1].Duration);
    }

    [Fact]
    public async Task A_tone_at_the_minimum_duration_still_starts_and_ends()
    {
        // The short case must not collapse into nothing: a 40 ms tone is two periods, and both ends
        // still need their redundancy.
        var sent = await Send(toneCode: 0, durationMs: RtpTelephoneEventCodec.MinDurationMs);

        Assert.Contains(sent, packet => !packet.EndOfEvent);
        Assert.Equal(RtpTelephoneEventBurst.RedundantCopies, sent.Count(packet => packet.EndOfEvent));
    }

    [Fact]
    public async Task Cancelling_mid_tone_still_ends_the_event()
    {
        // An event left without its end packet is a stuck tone at the far end — the receiver keeps
        // waiting for a digit to finish.
        using var cts = new CancellationTokenSource();
        var sent = new List<Packet>();

        await RtpTelephoneEventBurst.SendAsync(
            (payload, marker, _) =>
            {
                sent.Add(Packet.From(payload, marker));
                if (sent.Count == RtpTelephoneEventBurst.RedundantCopies + 1)
                {
                    cts.Cancel();
                }
                return ValueTask.CompletedTask;
            },
            static (_, _) => Task.CompletedTask,
            toneCode: 9,
            durationMs: 400,
            ClockRate,
            cts.Token);

        Assert.Equal(RtpTelephoneEventBurst.RedundantCopies, sent.Count(packet => packet.EndOfEvent));
    }

    private static async Task<List<Packet>> Send(byte toneCode, int durationMs)
    {
        var sent = new List<Packet>();

        // No real waiting: the burst's timing is what is under test, not the clock it runs on.
        await RtpTelephoneEventBurst.SendAsync(
            (payload, marker, _) =>
            {
                sent.Add(Packet.From(payload, marker));
                return ValueTask.CompletedTask;
            },
            static (_, _) => Task.CompletedTask,
            toneCode,
            durationMs,
            ClockRate,
            CancellationToken.None);

        return sent;
    }

    private readonly record struct Packet(byte Tone, bool EndOfEvent, ushort Duration, bool Marker)
    {
        public static Packet From(byte[] payload, bool marker)
        {
            Assert.True(RtpTelephoneEventCodec.TryParse(payload, out var tone, out var end, out var duration));
            return new Packet(tone, end, duration, marker);
        }
    }
}
