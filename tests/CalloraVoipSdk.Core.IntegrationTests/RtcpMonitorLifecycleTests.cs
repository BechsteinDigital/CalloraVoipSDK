using System.Buffers.Binary;
using System.Net;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #162 P2-9: quality-monitor lifecycle and telemetry semantics. Four defects that only show up at the
/// edges — a start racing teardown, a transient bind failure, a compound that says nothing about quality,
/// and an out-of-range MOS — but each one either throws where a caller does not expect it, disables
/// reporting permanently, wakes every subscriber for nothing, or publishes a number no scale produces.
/// </summary>
public sealed class RtcpMonitorLifecycleTests
{
    private const uint LocalSsrc = 0x0A0B0C0D;
    private const uint RemoteSsrc = 0x01020304;

    private static CallMediaParameters MuxedParameters() => new()
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 41300),
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 41302),
        PayloadType = 0,
        ClockRate = 8000,
        SamplesPerPacket = 160,
        RtcpMux = true,
        PayloadTypeCodecMap = new Dictionary<int, string> { [0] = "PCMU" },
    };

    private static CallRtcpQualityMonitor NewMonitor(ICallMediaSession session) =>
        new(session, MuxedParameters(), NullLoggerFactory.Instance, new RtcpPacketCodec());

    // ── start / dispose ordering ─────────────────────────────────────────────

    [Fact]
    public async Task Starting_after_dispose_is_a_no_op_rather_than_throwing()
    {
        // The start path reads the cancellation source teardown already disposed. Callers do not expect a
        // start to throw ObjectDisposedException, and a monitor that has been torn down has nothing to start.
        var monitor = NewMonitor(new StubMediaSession(LocalSsrc));
        await monitor.DisposeAsync();

        await monitor.StartAsync();   // must not throw
    }

    [Fact]
    public async Task Disposing_twice_is_a_no_op()
    {
        var monitor = NewMonitor(new StubMediaSession(LocalSsrc));
        await monitor.StartAsync();

        await monitor.DisposeAsync();
        await monitor.DisposeAsync();
    }

    // ── telemetry semantics ──────────────────────────────────────────────────

    [Fact]
    public async Task A_compound_that_carries_no_quality_information_publishes_no_event()
    {
        // SDES says who a source is, not how it is doing. Publishing an identical snapshot for it woke every
        // subscriber for nothing — on a busy session, once per received compound.
        var session = new StubMediaSession(LocalSsrc);
        var monitor = NewMonitor(session);

        var events = 0;
        monitor.QualitySnapshotUpdated += _ => Interlocked.Increment(ref events);

        monitor.ProcessInboundDatagramForTest(
            new RtcpPacketCodec().Encode([SdesOnly()]), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        Assert.Equal(0, Volatile.Read(ref events));
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task A_compound_carrying_a_receiver_report_still_publishes()
    {
        // The counterpart: suppressing the noise must not suppress the signal.
        var session = new StubMediaSession(LocalSsrc);
        var monitor = NewMonitor(session);

        var events = 0;
        monitor.QualitySnapshotUpdated += _ => Interlocked.Increment(ref events);

        monitor.ProcessInboundDatagramForTest(
            new RtcpPacketCodec().Encode([ReceiverReport(), SdesOnly()]),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

        Assert.Equal(1, Volatile.Read(ref events));
        await monitor.DisposeAsync();
    }

    // ── XR MOS range (RFC 3611 §4.7) ─────────────────────────────────────────

    [Theory]
    [InlineData(255)]   // out of range — used to surface as a MOS of 25.5
    [InlineData(127)]   // "unavailable" per the RFC
    [InlineData(0)]     // unset
    [InlineData(9)]     // below 1.0
    [InlineData(51)]    // above 5.0
    public async Task An_out_of_range_xr_mos_is_not_published(byte wireValue)
    {
        var session = new StubMediaSession(LocalSsrc);
        var monitor = NewMonitor(session);

        monitor.ProcessInboundDatagramForTest(
            ExtendedReportDatagramWithMos(wireValue), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        Assert.Null(monitor.GetLatestSnapshot().RemoteMosListeningQuality);
        await monitor.DisposeAsync();
    }

    [Theory]
    [InlineData(10, 1.0)]   // lower bound
    [InlineData(43, 4.3)]
    [InlineData(50, 5.0)]   // upper bound
    public async Task An_in_range_xr_mos_is_published(byte wireValue, double expected)
    {
        var session = new StubMediaSession(LocalSsrc);
        var monitor = NewMonitor(session);

        monitor.ProcessInboundDatagramForTest(
            ExtendedReportDatagramWithMos(wireValue), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        Assert.Equal(expected, monitor.GetLatestSnapshot().RemoteMosListeningQuality);
        await monitor.DisposeAsync();
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static RtcpSdesPacket SdesOnly() => new()
    {
        Chunks =
        [
            new RtcpSdesChunk
            {
                Ssrc = RemoteSsrc,
                Items = [new RtcpSdesItem { ItemType = RtcpSdesItemType.CName, Value = "peer" }],
            },
        ],
    };

    private static RtcpReceiverReport ReceiverReport() => new()
    {
        Ssrc = RemoteSsrc,
        ReportBlocks =
        [
            new RtcpReportBlock
            {
                Ssrc = LocalSsrc,
                FractionLost = 0,
                CumulativePacketsLost = 0,
                ExtendedHighestSeq = 100,
                Jitter = 10,
                LastSr = 0,
                DelaySinceLastSr = 0,
            },
        ],
    };

    // The codec decodes XR but does not encode it (a known capability gap, #162 P3-11), so the wire format
    // is built here: header + SSRC + one VoIP Metrics block (BT=7, RFC 3611 §4.7). MOS-LQ and MOS-CQ sit at
    // content offsets 22 and 23.
    private static byte[] ExtendedReportDatagramWithMos(byte mos)
    {
        const int contentBytes = 32;
        var datagram = new byte[4 + 4 + 4 + contentBytes];

        datagram[0] = 0x80;                                   // V=2
        datagram[1] = 207;                                    // PT = XR
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(2), (ushort)(datagram.Length / 4 - 1));
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(4), RemoteSsrc);

        datagram[8] = 7;                                      // block type: VoIP metrics
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(10), contentBytes / 4);

        var content = datagram.AsSpan(12);
        BinaryPrimitives.WriteUInt32BigEndian(content, LocalSsrc);   // the block reports on OUR stream
        content[22] = mos;                                    // MOS-LQ
        content[23] = mos;                                    // MOS-CQ
        return datagram;
    }

    private sealed class StubMediaSession(uint localSsrc) : ICallMediaSession
    {
        public event Action<CallAudioFrame>? FrameReceived { add { } remove { } }
        public event Action<byte, int>? DtmfReceived { add { } remove { } }
        public event Action<CallMediaRuntimeMetrics>? RuntimeMetricsUpdated { add { } remove { } }
        public event Action<IReadOnlyList<RtcpPacket>>? RtcpCompoundReceived { add { } remove { } }
        public event Action? MediaConsentLost { add { } remove { } }
        public event Action? MediaConnectivityDegraded { add { } remove { } }
        public event Action? MediaConnectivityRecovered { add { } remove { } }

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendFrameAsync(CallAudioFrame frame, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendDtmfAsync(byte toneCode, int durationMs = 160, CancellationToken ct = default) => Task.CompletedTask;
        public void UpdateRoundTripTimeHint(TimeSpan roundTripTime) { }
        public CallMediaRuntimeMetrics GetRuntimeMetricsSnapshot() => default!;
        public Task SendRtcpMuxDatagramAsync(ReadOnlyMemory<byte> datagram, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public CallMediaRtpSnapshot GetRtpSnapshot() => new(
            CapturedAtUtc: DateTimeOffset.UnixEpoch,
            LocalSsrc: localSsrc,
            RemoteSsrc: RemoteSsrc,
            SenderPacketCount: 10,
            SenderOctetCount: 1600,
            LastSentRtpTimestamp: 8000,
            HasSentRtpPackets: true,
            PacketsExpected: 100,
            PacketsReceived: 100,
            FractionLost: 0,
            CumulativePacketsLost: 0,
            ExtendedHighestSequenceNumber: 100,
            InterarrivalJitterRtpUnits: 0,
            LocalReceiveJitterMs: 0,
            LocalReceivePacketLossPercent: 0,
            LocalRoundTripTimeHintMs: 0);
    }
}
