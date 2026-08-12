using System.Linq;
using CalloraVoipSdk.Core.Application.Media.Rtcp;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #162 P2-6: the BYE lifecycle. A departing source must actually retire — a participant that keeps its
/// reception state after announcing departure (RFC 3550 §6.6) goes on producing report blocks for a stream
/// nobody is sending, its frozen extended-highest-sequence makes every later interval look like total loss,
/// and its slot stays taken against the tracked-source cap. And our own farewell must describe the same
/// participant the rest of its compound identifies.
/// </summary>
public sealed class RtcpByeLifecycleTests
{
    private const uint DepartingSsrc = 0x0A0B0C0D;
    private const uint StayingSsrc = 0x01020304;
    private const string Cname = "test-cname";

    private static BundledInboundReceptionStats NewStats() => new();

    private static void DeliverRtp(BundledInboundReceptionStats stats, uint ssrc, ushort seq) =>
        stats.RecordRtp(ssrc, seq, rtpTimestamp: (uint)(seq * 160));

    // ── receive side: a departed source stops being reported ─────────────────

    [Fact]
    public void A_source_that_sent_rtp_is_reported_until_it_departs()
    {
        var stats = NewStats();
        DeliverRtp(stats, DepartingSsrc, 1);
        DeliverRtp(stats, DepartingSsrc, 2);

        Assert.Contains(stats.SnapshotReportBlocks(), b => b.Ssrc == DepartingSsrc);

        Assert.True(stats.RemoveSource(DepartingSsrc));

        Assert.DoesNotContain(stats.SnapshotReportBlocks(), b => b.Ssrc == DepartingSsrc);
    }

    [Fact]
    public void Removing_one_source_leaves_the_others_reporting()
    {
        var stats = NewStats();
        DeliverRtp(stats, DepartingSsrc, 1);
        DeliverRtp(stats, DepartingSsrc, 2);
        DeliverRtp(stats, StayingSsrc, 1);
        DeliverRtp(stats, StayingSsrc, 2);

        stats.RemoveSource(DepartingSsrc);

        var blocks = stats.SnapshotReportBlocks();
        Assert.DoesNotContain(blocks, b => b.Ssrc == DepartingSsrc);
        Assert.Contains(blocks, b => b.Ssrc == StayingSsrc);
    }

    [Fact]
    public void Removing_an_unknown_source_is_a_no_op()
    {
        // A BYE is unauthenticated wire input: naming an SSRC we never tracked must not create state, and
        // must not throw either.
        var stats = NewStats();

        Assert.False(stats.RemoveSource(0xDEADBEEF));
        Assert.False(stats.RemoveSource(0xDEADBEEF));   // idempotent
    }

    [Fact]
    public void A_departed_source_that_resumes_is_tracked_again()
    {
        // Departure is not a ban. A peer that leaves and comes back under the same SSRC — a re-join, or a
        // BYE we should not have believed — is simply a new source from here on.
        var stats = NewStats();
        DeliverRtp(stats, DepartingSsrc, 1);
        DeliverRtp(stats, DepartingSsrc, 2);
        stats.RemoveSource(DepartingSsrc);

        DeliverRtp(stats, DepartingSsrc, 100);
        DeliverRtp(stats, DepartingSsrc, 101);

        Assert.Contains(stats.SnapshotReportBlocks(), b => b.Ssrc == DepartingSsrc);
    }

    // ── send side: the farewell describes the participant it belongs to ──────

    [Fact]
    public async Task The_teardown_compound_announces_every_ssrc_it_departs()
    {
        // The compound used to lead with an RR and an SDES CNAME for the local SSRC while the BYE departed the
        // *sending* SSRCs — so on a bundle whose tracks do not use the local SSRC, the peer got a farewell for
        // sources that compound never identified, and none for the one it did.
        var senders = new BundledSenderReportInfo[]
        {
            new(Ssrc: 0x0A0A0A0A, PacketCount: 10, OctetCount: 1600, LastRtpTimestamp: 5000),
            new(Ssrc: 0x0B0B0B0B, PacketCount: 7, OctetCount: 1400, LastRtpTimestamp: 90000),
        };
        var sent = new List<byte[]>();
        var oneTick = new ByeOneShotDelay();

        var reporter = new BundledRtcpReporter(
            () => senders,
            Array.Empty<BundledReceptionReportBlock>,
            localSsrc: 0x0C0C0C0C,          // deliberately none of the sending SSRCs
            (rtcp, _) => { sent.Add(rtcp.ToArray()); return ValueTask.FromResult(RtcpSendOutcome.Sent); },
            new RtcpPacketCodec(),
            Cname,
            NullLoggerFactory.Instance,
            interval: TimeSpan.FromSeconds(5),
            delay: oneTick.WaitAsync,
            utcNow: () => new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero));

        reporter.Start();
        await oneTick.WaitForFirstTickConsumed();
        await reporter.DisposeAsync();

        var teardown = new RtcpPacketCodec().Decode(sent[^1]);
        var bye = Assert.Single(teardown.OfType<RtcpByePacket>());
        var sdes = Assert.Single(teardown.OfType<RtcpSdesPacket>());

        // Every SSRC this participant used departs — the two senders and the local one.
        Assert.Equal(
            new uint[] { 0x0A0A0A0A, 0x0B0B0B0B, 0x0C0C0C0C }.OrderBy(s => s),
            bye.Sources.OrderBy(s => s));

        // And each of them is identified by a CNAME chunk in the same compound (RFC 3550 §6.1/§6.5).
        Assert.Equal(
            bye.Sources.OrderBy(s => s),
            sdes.Chunks.Select(c => c.Ssrc).OrderBy(s => s));
    }

    private sealed class ByeOneShotDelay
    {
        private readonly TaskCompletionSource _firstIterationDone =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _ticks;

        public Task WaitAsync(TimeSpan interval, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _ticks) == 1)
                return Task.CompletedTask;

            _firstIterationDone.TrySetResult();
            return Task.Delay(Timeout.Infinite, ct);
        }

        public Task WaitForFirstTickConsumed() => _firstIterationDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
