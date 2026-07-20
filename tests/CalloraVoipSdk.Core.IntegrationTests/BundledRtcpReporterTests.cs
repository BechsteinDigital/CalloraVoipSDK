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
            Array.Empty<BundledReceptionReportBlock>,
            localSsrc: 0x0A0A0A0A,
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
            Array.Empty<BundledReceptionReportBlock>,
            localSsrc: 0x0C0C0C0C,
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
            Array.Empty<BundledReceptionReportBlock>,
            localSsrc: 0x0C0C0C0C,
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

    [Fact]
    public async Task A_sending_endpoint_emits_a_sender_report_carrying_the_inbound_reception_blocks()
    {
        var senders = new List<BundledSenderReportInfo>
        {
            new(Ssrc: 0x0A0A0A0A, PacketCount: 42, OctetCount: 6720, LastRtpTimestamp: 5000),
        };
        var receptionBlocks = new List<BundledReceptionReportBlock>
        {
            new(Ssrc: 0xDEAD_BEEF, FractionLost: 12, CumulativePacketsLost: 3,
                ExtendedHighestSequenceNumber: 0x0001_2345, InterarrivalJitter: 77,
                LastSr: 0x3344_5566, DelaySinceLastSr: 131072),
        };

        var sent = new List<byte[]>();
        var oneTick = new OneShotDelay();
        await using var reporter = new BundledRtcpReporter(
            () => senders,
            () => receptionBlocks,
            localSsrc: 0x0A0A0A0A,
            (rtcp, _) => { sent.Add(rtcp.ToArray()); return ValueTask.CompletedTask; },
            new RtcpPacketCodec(),
            Cname,
            NullLoggerFactory.Instance,
            delay: oneTick.WaitAsync,
            utcNow: () => DateTimeOffset.UtcNow);

        reporter.Start();
        await oneTick.WaitForFirstTickConsumed();

        var packets = new RtcpPacketCodec().Decode(Assert.Single(sent));
        Assert.Empty(packets.OfType<RtcpReceiverReport>()); // a sender uses SR, not RR

        var sr = Assert.Single(packets.OfType<RtcpSenderReport>());
        Assert.Equal(0x0A0A0A0Au, sr.Ssrc);
        var block = Assert.Single(sr.ReportBlocks);
        Assert.Equal(0xDEAD_BEEFu, block.Ssrc);
        Assert.Equal(12, block.FractionLost);
        Assert.Equal(3, block.CumulativePacketsLost);
        Assert.Equal(0x0001_2345u, block.ExtendedHighestSeq);
        Assert.Equal(77u, block.Jitter);
        Assert.Equal(0x3344_5566u, block.LastSr);
        Assert.Equal(131072u, block.DelaySinceLastSr);
    }

    [Fact]
    public async Task A_receive_only_endpoint_emits_a_receiver_report_with_the_reception_blocks()
    {
        var receptionBlocks = new List<BundledReceptionReportBlock>
        {
            new(Ssrc: 0xDEAD_BEEF, FractionLost: 5, CumulativePacketsLost: 1,
                ExtendedHighestSequenceNumber: 200, InterarrivalJitter: 9,
                LastSr: 0, DelaySinceLastSr: 0),
        };

        var sent = new List<byte[]>();
        var oneTick = new OneShotDelay();
        await using var reporter = new BundledRtcpReporter(
            () => Array.Empty<BundledSenderReportInfo>(), // never sent → receive-only
            () => receptionBlocks,
            localSsrc: 0x0C0C0C0C,
            (rtcp, _) => { sent.Add(rtcp.ToArray()); return ValueTask.CompletedTask; },
            new RtcpPacketCodec(),
            Cname,
            NullLoggerFactory.Instance,
            delay: oneTick.WaitAsync,
            utcNow: () => DateTimeOffset.UtcNow);

        reporter.Start();
        await oneTick.WaitForFirstTickConsumed();

        var packets = new RtcpPacketCodec().Decode(Assert.Single(sent));
        Assert.Empty(packets.OfType<RtcpSenderReport>()); // receive-only uses RR, not SR

        var rr = Assert.Single(packets.OfType<RtcpReceiverReport>());
        Assert.Equal(0x0C0C0C0Cu, rr.Ssrc);
        var block = Assert.Single(rr.ReportBlocks);
        Assert.Equal(0xDEAD_BEEFu, block.Ssrc);
        Assert.Equal(5, block.FractionLost);
        Assert.Equal(200u, block.ExtendedHighestSeq);

        // The compound still carries the CNAME (RFC 3550 §6.5) keyed by the local SSRC.
        var sdes = Assert.Single(packets.OfType<RtcpSdesPacket>());
        var chunk = Assert.Single(sdes.Chunks, c => c.Ssrc == 0x0C0C0C0C);
        Assert.Equal(Cname, Assert.Single(chunk.Items, i => i.ItemType == RtcpSdesItemType.CName).Value);
    }

    [Fact]
    public async Task Nothing_is_emitted_when_there_is_no_sending_track_and_no_inbound_source()
    {
        var sent = new List<byte[]>();
        var oneTick = new OneShotDelay();
        await using var reporter = new BundledRtcpReporter(
            () => Array.Empty<BundledSenderReportInfo>(),
            () => Array.Empty<BundledReceptionReportBlock>(),
            localSsrc: 0x0C0C0C0C,
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
