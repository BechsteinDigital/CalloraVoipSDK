using System.Net;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp.JitterBuffer;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// QoS metrics correctness: the RFC 3550 jitter estimator and the RTCP-derived RTT feed.
/// Regressions from a real Fritz!Box call: jitterMs froze at exactly the frame interval
/// (20.00 ms) on a clean LAN, and rttMs reported the never-updated 60 ms default.
/// </summary>
public sealed class QosMetricsTests
{
    // --- Jitter estimator (RFC 3550 §6.4.1) ---

    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(1_751_968_000_000);

    private static JitterBuffer FeedPackets(Func<int, double> arrivalOffsetMs, int count = 50)
    {
        var buffer = new JitterBuffer();
        for (var i = 0; i < count; i++)
        {
            buffer.Add(
                new RtpPacket
                {
                    PayloadType = 0,
                    SequenceNumber = (ushort)i,
                    Timestamp = (uint)(i * 160),
                    Ssrc = 0xCAFE,
                    Payload = new byte[160],
                },
                T0.AddMilliseconds(arrivalOffsetMs(i)));
        }

        return buffer;
    }

    [Fact]
    public void Perfectly_paced_arrival_yields_near_zero_jitter()
    {
        // Regression: the arrival-time conversion saturated on uint overflow, so the
        // transit fell by one frame per packet and jitter converged to exactly 20 ms.
        var buffer = FeedPackets(i => i * 20.0);

        Assert.True(
            buffer.EstimatedJitterMs < 2.0,
            $"Expected near-zero jitter on a perfectly paced stream, got {buffer.EstimatedJitterMs:F2} ms.");
    }

    [Fact]
    public void Alternating_arrival_variance_is_measured()
    {
        // Packets alternate ±5 ms around the nominal 20 ms spacing → |D| = 10 ms.
        var buffer = FeedPackets(i => i * 20.0 + (i % 2 == 0 ? 0 : 5));

        Assert.InRange(buffer.EstimatedJitterMs, 2.0, 15.0);
    }

    // --- RTCP round-trip feed (RFC 3550 §6.4.1 LSR/DLSR) ---

    private static CallMediaParameters Parameters() => new()
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 40000),
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 40002),
        PayloadType = 0,
        ClockRate = 8000,
        SamplesPerPacket = 160,
        PayloadTypeCodecMap = new Dictionary<int, string> { [0] = "PCMU" },
    };

    [Fact]
    public void Receiver_report_with_valid_lsr_feeds_rtt_into_the_media_session()
    {
        var session = new RecordingMediaSession(localSsrc: 0xAA55);
        using var cts = new CancellationTokenSource();
        var monitor = new CallRtcpQualityMonitor(
            session, Parameters(), NullLoggerFactory.Instance, new RtcpPacketCodec());

        var srSentAt = T0;
        monitor.RecordLocalSenderReportForTest(srSentAt, ntpMiddle32: 0x12345678);

        // Peer answers 800 ms later, having held our SR for 500 ms (DLSR) → RTT 300 ms.
        var rr = new RtcpReceiverReport
        {
            Ssrc = 0xBB66,
            ReportBlocks =
            [
                new RtcpReportBlock
                {
                    Ssrc = 0xAA55,
                    LastSr = 0x12345678,
                    DelaySinceLastSr = (uint)(0.5 * 65536),
                },
            ],
        };
        var datagram = new RtcpPacketCodec().Encode([rr]);

        monitor.ProcessInboundDatagramForTest(datagram, srSentAt.AddMilliseconds(800));

        var hint = Assert.Single(session.RoundTripHints);
        Assert.Equal(300.0, hint.TotalMilliseconds, precision: 0);
    }

    [Fact]
    public void Receiver_report_with_stale_lsr_does_not_feed_rtt()
    {
        var session = new RecordingMediaSession(localSsrc: 0xAA55);
        var monitor = new CallRtcpQualityMonitor(
            session, Parameters(), NullLoggerFactory.Instance, new RtcpPacketCodec());

        monitor.RecordLocalSenderReportForTest(T0, ntpMiddle32: 0x12345678);

        var rr = new RtcpReceiverReport
        {
            Ssrc = 0xBB66,
            ReportBlocks =
            [
                new RtcpReportBlock { Ssrc = 0xAA55, LastSr = 0xDEADBEEF, DelaySinceLastSr = 100 },
            ],
        };

        monitor.ProcessInboundDatagramForTest(new RtcpPacketCodec().Encode([rr]), T0.AddMilliseconds(800));

        Assert.Empty(session.RoundTripHints);
    }

    // Standalone XR (PT=207) with one VoIP Metrics block: MOS-LQ ×10 = 43, MOS-CQ ×10 = 41.
    private static byte[] BuildXrWithVoipMetrics(uint sourceSsrc)
    {
        var packet = new byte[44];
        packet[0] = 0x80;                                            // V=2
        packet[1] = 207;                                            // PT = XR
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 10);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), 0xAABBCCDDu);
        packet[8] = 7;                                               // BT = VoIP Metrics
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10), 8);
        var c = packet.AsSpan(12);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(c, sourceSsrc);
        c[22] = 43;                                                 // MOS-LQ ×10
        c[23] = 41;                                                 // MOS-CQ ×10
        return packet;
    }

    [Fact]
    public void Extended_report_voip_metrics_surface_remote_mos_in_the_snapshot()
    {
        const uint sourceSsrc = 0x11223344u;
        var session = new RecordingMediaSession(localSsrc: sourceSsrc);
        var monitor = new CallRtcpQualityMonitor(
            session, Parameters(), NullLoggerFactory.Instance, new RtcpPacketCodec());

        monitor.ProcessInboundDatagramForTest(BuildXrWithVoipMetrics(sourceSsrc), T0);

        var snapshot = monitor.GetLatestSnapshot();
        Assert.Equal(4.3, snapshot.RemoteMosListeningQuality!.Value, 3);
        Assert.Equal(4.1, snapshot.RemoteMosConversationalQuality!.Value, 3);
    }

    [Fact]
    public void Extended_report_for_a_different_ssrc_does_not_set_mos()
    {
        var session = new RecordingMediaSession(localSsrc: 0x99999999u);
        var monitor = new CallRtcpQualityMonitor(
            session, Parameters(), NullLoggerFactory.Instance, new RtcpPacketCodec());

        // The VoIP Metrics block reports on a different SSRC than ours → not our stream.
        monitor.ProcessInboundDatagramForTest(BuildXrWithVoipMetrics(0x11223344u), T0);

        var snapshot = monitor.GetLatestSnapshot();
        Assert.Null(snapshot.RemoteMosListeningQuality);
        Assert.Null(snapshot.RemoteMosConversationalQuality);
    }

    private sealed class RecordingMediaSession : ICallMediaSession
    {
        private readonly uint _localSsrc;
        private readonly List<TimeSpan> _hints = [];

        public RecordingMediaSession(uint localSsrc) => _localSsrc = localSsrc;

        public IReadOnlyList<TimeSpan> RoundTripHints
        {
            get { lock (_hints) return [.. _hints]; }
        }

        public event Action<CallAudioFrame>? FrameReceived { add { } remove { } }
        public event Action<byte, int>? DtmfReceived { add { } remove { } }
        public event Action<CallMediaRuntimeMetrics>? RuntimeMetricsUpdated { add { } remove { } }
        public event Action<byte[]>? RtcpMuxDatagramReceived { add { } remove { } }

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendFrameAsync(CallAudioFrame frame, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendDtmfAsync(byte toneCode, int durationMs = 160, CancellationToken ct = default) => Task.CompletedTask;

        public void UpdateRoundTripTimeHint(TimeSpan roundTripTime)
        {
            lock (_hints) _hints.Add(roundTripTime);
        }

        public CallMediaRuntimeMetrics GetRuntimeMetricsSnapshot() => default!;

        public CallMediaRtpSnapshot GetRtpSnapshot() => new(
            CapturedAtUtc: DateTimeOffset.UtcNow,
            LocalSsrc: _localSsrc,
            RemoteSsrc: null,
            SenderPacketCount: 0,
            SenderOctetCount: 0,
            LastSentRtpTimestamp: 0,
            HasSentRtpPackets: false,
            PacketsExpected: 0,
            PacketsReceived: 0,
            FractionLost: 0,
            CumulativePacketsLost: 0,
            ExtendedHighestSequenceNumber: 0,
            InterarrivalJitterRtpUnits: 0,
            LocalReceiveJitterMs: 0,
            LocalReceivePacketLossPercent: 0,
            LocalRoundTripTimeHintMs: 0);

        public Task SendRtcpMuxDatagramAsync(ReadOnlyMemory<byte> datagram, CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
