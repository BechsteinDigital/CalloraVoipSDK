using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The periodic RTCP Sender Report sender for a BUNDLE session (CF-004a, RFC 3550 §6.4): each interval it
/// snapshots the sending tracks' SR counters and emits one compound packet — a Sender Report per SSRC that
/// has sent, followed by an SDES carrying the CNAME — over the fail-closed SRTCP send path. Only tracks that
/// have sent are reported; when none has, nothing goes out.
/// </summary>
public sealed class BundledRtcpReporterTests
{
    private const string Cname = "voipsdk-test-host";

    [Fact]
    public async Task A_tick_emits_one_sender_report_per_sending_ssrc_and_one_sdes_with_the_cname()
    {
        var snapshot = new List<BundledSenderReportInfo>
        {
            new(Ssrc: 0x0A0A0A0A, PacketCount: 42, OctetCount: 6720, LastRtpTimestamp: 5000),
            new(Ssrc: 0x0B0B0B0B, PacketCount: 7, OctetCount: 1400, LastRtpTimestamp: 90000),
        };
        // 2026-07-20T00:00:00Z as NTP: seconds since 1900-01-01.
        var now = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
        var expectedNtp = ToNtp(now);

        var sent = new List<byte[]>();
        var oneTick = new OneShotDelay();
        await using var reporter = new BundledRtcpReporter(
            () => snapshot,
            (rtcp, _) => { sent.Add(rtcp.ToArray()); return ValueTask.CompletedTask; },
            new RtcpPacketCodec(),
            Cname,
            NullLoggerFactory.Instance,
            interval: TimeSpan.FromSeconds(5),
            delay: oneTick.WaitAsync,
            utcNow: () => now);

        reporter.Start();
        await oneTick.WaitForFirstTickConsumed();

        var datagram = Assert.Single(sent);
        var packets = new RtcpPacketCodec().Decode(datagram);

        var senderReports = packets.OfType<RtcpSenderReport>().ToList();
        Assert.Equal(2, senderReports.Count);

        var audio = senderReports.Single(sr => sr.Ssrc == 0x0A0A0A0A);
        Assert.Equal(expectedNtp, audio.NtpTimestamp);
        Assert.Equal(5000u, audio.RtpTimestamp);
        Assert.Equal(42u, audio.SenderPacketCount);
        Assert.Equal(6720u, audio.SenderOctetCount);
        Assert.Empty(audio.ReportBlocks); // RR/RTT are a later slice

        var video = senderReports.Single(sr => sr.Ssrc == 0x0B0B0B0B);
        Assert.Equal(7u, video.SenderPacketCount);
        Assert.Equal(1400u, video.SenderOctetCount);
        Assert.Equal(90000u, video.RtpTimestamp);

        var sdes = Assert.Single(packets.OfType<RtcpSdesPacket>());
        foreach (var ssrc in new uint[] { 0x0A0A0A0A, 0x0B0B0B0B })
        {
            var chunk = sdes.Chunks.Single(c => c.Ssrc == ssrc);
            var cname = Assert.Single(chunk.Items, i => i.ItemType == RtcpSdesItemType.CName);
            Assert.Equal(Cname, cname.Value);
        }
    }

    [Fact]
    public async Task A_tick_with_no_sending_track_emits_nothing()
    {
        var sent = new List<byte[]>();
        var oneTick = new OneShotDelay();
        await using var reporter = new BundledRtcpReporter(
            () => Array.Empty<BundledSenderReportInfo>(),
            (rtcp, _) => { sent.Add(rtcp.ToArray()); return ValueTask.CompletedTask; },
            new RtcpPacketCodec(),
            Cname,
            NullLoggerFactory.Instance,
            delay: oneTick.WaitAsync,
            utcNow: () => DateTimeOffset.UtcNow);

        reporter.Start();
        await oneTick.WaitForFirstTickConsumed();

        Assert.Empty(sent);
    }

    [Fact]
    public async Task Dispose_stops_the_loop_cleanly()
    {
        var oneTick = new OneShotDelay();
        var reporter = new BundledRtcpReporter(
            () => Array.Empty<BundledSenderReportInfo>(),
            (_, _) => ValueTask.CompletedTask,
            new RtcpPacketCodec(),
            Cname,
            NullLoggerFactory.Instance,
            delay: oneTick.WaitAsync);

        reporter.Start();
        await oneTick.WaitForFirstTickConsumed();

        // Disposal cancels the delay the loop is now awaiting and awaits the loop to completion.
        await reporter.DisposeAsync();
        await reporter.DisposeAsync(); // idempotent
    }

    private static ulong ToNtp(DateTimeOffset timestamp)
    {
        var ntpEpoch = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var delta = timestamp.ToUniversalTime() - ntpEpoch;
        var totalSeconds = Math.Max(0, delta.TotalSeconds);
        var wholeSeconds = Math.Floor(totalSeconds);
        var seconds = (ulong)wholeSeconds;
        var fraction = (ulong)((totalSeconds - wholeSeconds) * 4_294_967_296.0);
        return (seconds << 32) | fraction;
    }

    // An injectable delay that lets exactly the first tick through, then blocks forever (until cancelled by
    // disposal), so the loop runs one deterministic iteration. The FIRST iteration's report is fully sent
    // before the loop reaches its SECOND delay call — so signalling there guarantees the send has completed.
    private sealed class OneShotDelay
    {
        private readonly TaskCompletionSource _firstIterationDone =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _ticks;

        public Task WaitAsync(TimeSpan interval, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _ticks) == 1)
                return Task.CompletedTask; // first tick fires immediately

            // Reaching the second delay means the first iteration (snapshot + encode + send) has finished.
            _firstIterationDone.TrySetResult();
            // Park here until disposal cancels the token.
            return Task.Delay(Timeout.Infinite, ct);
        }

        public Task WaitForFirstTickConsumed() => _firstIterationDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
