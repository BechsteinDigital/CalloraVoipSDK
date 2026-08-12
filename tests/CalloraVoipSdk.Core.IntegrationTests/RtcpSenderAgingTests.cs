using System.Linq;
using CalloraVoipSdk.Core.Application.Media.Rtcp;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #162 P2-7 sender aging (RFC 3550 §6.3.4/§6.4). A Sender Report describes a participant that sent RTP
/// during this interval; everyone else sends a Receiver Report. Both reporters answered a different
/// question — "has this track ever sent?" — so a stream that fell silent kept emitting SRs forever,
/// describing traffic that stopped and inflating the sender count the peer splits its RTCP bandwidth by.
/// </summary>
public sealed class RtcpSenderAgingTests
{
    private const uint AudioSsrc = 0x0A0A0A0A;
    private const uint VideoSsrc = 0x0B0B0B0B;
    private const string Cname = "aging-test";

    private static BundledSenderReportInfo Sender(uint ssrc, long packetCount) =>
        new(Ssrc: ssrc, PacketCount: packetCount, OctetCount: packetCount * 160, LastRtpTimestamp: 5000);

    private static async Task<List<IReadOnlyList<RtcpPacket>>> RunReportsAsync(
        Func<IReadOnlyList<BundledSenderReportInfo>> snapshot, int reportCount)
    {
        var sent = new List<byte[]>();
        var ticks = new SteppedDelay(reportCount);

        await using var reporter = new BundledRtcpReporter(
            snapshot,
            Array.Empty<BundledReceptionReportBlock>,
            localSsrc: AudioSsrc,
            (rtcp, _) => { sent.Add(rtcp.ToArray()); return ValueTask.FromResult(RtcpSendOutcome.Sent); },
            new RtcpPacketCodec(),
            Cname,
            NullLoggerFactory.Instance,
            interval: TimeSpan.FromSeconds(5),
            delay: ticks.WaitAsync,
            utcNow: () => new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero));

        reporter.Start();
        await ticks.WaitForAllConsumed();

        var codec = new RtcpPacketCodec();
        return [.. sent.Select(datagram => codec.Decode(datagram))];
    }

    [Fact]
    public async Task A_track_that_keeps_sending_keeps_its_sender_report()
    {
        var packetCount = 10L;
        var reports = await RunReportsAsync(() => [Sender(AudioSsrc, Interlocked.Add(ref packetCount, 10))], reportCount: 3);

        Assert.All(reports, r => Assert.NotEmpty(r.OfType<RtcpSenderReport>()));
    }

    [Fact]
    public async Task A_track_that_falls_silent_stops_sending_sender_reports()
    {
        // The counter stops moving after the first report: the peer must stop being told we are sending.
        var reports = await RunReportsAsync(() => [Sender(AudioSsrc, 10)], reportCount: 3);

        // Exactly one report goes out. The first has no previous count to compare against, so it
        // legitimately reports as a sender; afterwards the track is aged out and — with no inbound source to
        // report on either — there is nothing left to say, so the reporter emits nothing at all. Previously
        // this was three compounds, each carrying a Sender Report for a stream that had stopped.
        var senderReports = reports.SelectMany(r => r.OfType<RtcpSenderReport>()).ToList();
        Assert.Single(senderReports);
        Assert.Single(reports);
    }

    [Fact]
    public async Task A_silent_track_does_not_suppress_a_still_active_one()
    {
        // Aging is per SSRC: a bundle where video stops and audio continues must keep audio's SR.
        var audioCount = 10L;
        var reports = await RunReportsAsync(
            () => [Sender(AudioSsrc, Interlocked.Add(ref audioCount, 10)), Sender(VideoSsrc, 7)],
            reportCount: 3);

        var lastSenderReports = reports[^1].OfType<RtcpSenderReport>().ToList();
        Assert.Equal(AudioSsrc, Assert.Single(lastSenderReports).Ssrc);
    }

    [Fact]
    public async Task A_track_that_resumes_sending_reports_as_a_sender_again()
    {
        // Aging is not a one-way door: silence for an interval must not disqualify a stream permanently.
        var reportIndex = 0;
        var reports = await RunReportsAsync(
            () =>
            {
                var i = Interlocked.Increment(ref reportIndex);
                // 10, 10 (silent), then moving again.
                var count = i <= 2 ? 10L : 10L + i;
                return [Sender(AudioSsrc, count)];
            },
            reportCount: 3);

        // Two compounds reach the wire: the first (no baseline yet) and the one after the counter moves
        // again. The silent interval in between produces nothing, because there is nothing to report.
        var senderReports = reports.SelectMany(r => r.OfType<RtcpSenderReport>()).ToList();
        Assert.Equal(2, senderReports.Count);
        Assert.All(senderReports, sr => Assert.Equal(AudioSsrc, sr.Ssrc));
    }

    // Releases exactly `reportCount` ticks, then parks so the loop stops after a known number of reports.
    private sealed class SteppedDelay(int reportCount)
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _ticks;

        public Task WaitAsync(TimeSpan interval, CancellationToken ct)
        {
            var tick = Interlocked.Increment(ref _ticks);
            if (tick <= reportCount)
                return Task.CompletedTask;

            _done.TrySetResult();
            return Task.Delay(Timeout.Infinite, ct);
        }

        public Task WaitForAllConsumed() => _done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
