using System.Linq;
using System.Net;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #162 P2-6, single (SIP) path: leaving without an RTCP BYE is legal but leaves the peer to time this
/// participant out (RFC 3550 §6.3.7) instead of learning it left. The bundle path has announced its
/// departure for a while; the SIP path simply went silent. The farewell is also self-consistent — the SSRC
/// that departs is the one the compound's SDES identifies (§6.1/§6.5/§6.6).
/// </summary>
public sealed class RtcpSinglePathGoodbyeTests
{
    private const uint LocalSsrc = 0x0A0B0C0D;

    private static CallMediaParameters MuxedParameters() => new()
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 41200),
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 41202),
        PayloadType = 0,
        ClockRate = 8000,
        SamplesPerPacket = 160,
        RtcpMux = true,
        PayloadTypeCodecMap = new Dictionary<int, string> { [0] = "PCMU" },
    };

    private static async Task<List<byte[]>> RunAndDisposeAsync(bool startMonitor)
    {
        var session = new CapturingMediaSession(LocalSsrc);
        var monitor = new CallRtcpQualityMonitor(
            session, MuxedParameters(), NullLoggerFactory.Instance, new RtcpPacketCodec());

        if (startMonitor)
        {
            await monitor.StartAsync();
            // The send loop emits its first report immediately; wait for it so "we have reported" holds.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (session.Sent.Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(10);
        }

        await monitor.DisposeAsync();
        return session.Sent;
    }

    [Fact]
    public async Task Disposing_after_reporting_announces_the_departure()
    {
        var sent = await RunAndDisposeAsync(startMonitor: true);

        Assert.NotEmpty(sent);
        var teardown = new RtcpPacketCodec().Decode(sent[^1]);
        var bye = Assert.Single(teardown.OfType<RtcpByePacket>());

        Assert.Equal(LocalSsrc, bye.Sources.Single());
    }

    [Fact]
    public async Task The_farewell_compound_identifies_the_ssrc_it_departs()
    {
        // RFC 3550 §6.1: a compound leads with a report and carries a CNAME. Departing an SSRC the compound
        // never identified is what the bundle path used to do (#162 P2-6); the SIP path must not repeat it.
        var sent = await RunAndDisposeAsync(startMonitor: true);

        var teardown = new RtcpPacketCodec().Decode(sent[^1]);
        var bye = Assert.Single(teardown.OfType<RtcpByePacket>());
        var sdes = Assert.Single(teardown.OfType<RtcpSdesPacket>());

        Assert.NotEmpty(teardown.OfType<RtcpReceiverReport>());   // leads with a report
        Assert.Equal(bye.Sources.Single(), sdes.Chunks.Single().Ssrc);
    }

    [Fact]
    public async Task Disposing_without_ever_reporting_sends_no_bye()
    {
        // A participant the peer never heard from has nothing to depart from (RFC 3550 §6.6) — the same rule
        // the bundle reporter applies.
        var sent = await RunAndDisposeAsync(startMonitor: false);

        Assert.Empty(sent);
    }

    private sealed class CapturingMediaSession(uint localSsrc) : ICallMediaSession
    {
        public List<byte[]> Sent { get; } = [];

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

        public CallMediaRtpSnapshot GetRtpSnapshot() => new(
            CapturedAtUtc: DateTimeOffset.UtcNow,
            LocalSsrc: localSsrc,
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

        public Task SendRtcpMuxDatagramAsync(ReadOnlyMemory<byte> datagram, CancellationToken ct = default)
        {
            lock (Sent) Sent.Add(datagram.ToArray());
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
