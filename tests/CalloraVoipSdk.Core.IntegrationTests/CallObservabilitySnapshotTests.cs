using System.Net;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;
using CalloraVoipSdk.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Pins the read-only observability snapshots exposed on <see cref="ICall"/>:
/// raw RTP statistics (CORE-024) and ICE connectivity state (CORE-018). Verifies the internal
/// runtime record → public domain value mapping and the round-trip onto the call aggregate.
/// </summary>
public sealed class CallObservabilitySnapshotTests : IDisposable
{
    private readonly SipCoreCallChannel _channel;
    private readonly Call _call;

    public CallObservabilitySnapshotTests()
    {
        _channel = new SipCoreCallChannel(
            NullLogger<SipCoreCallChannel>.Instance,
            new SdpNegotiator(),
            NullSipTelemetrySink.Instance,
            SrtpPolicy.Disabled,
            "test");

        _call = new Call(
            CallId.New(),
            CallDirection.Inbound,
            "sip:remote@test.invalid",
            _channel,
            new FakePhoneLine(),
            NullLogger<Call>.Instance);
    }

    public void Dispose() => _channel.Dispose();

    // ── CORE-024: raw RTP statistics ──────────────────────────────────────────────

    [Fact]
    public void RtpStatistics_is_null_before_media_metrics_are_available()
    {
        Assert.Null(_call.RtpStatistics);
    }

    [Fact]
    public void RtpStatisticsFactory_maps_ssrc_and_packet_counters()
    {
        var captured = new DateTimeOffset(2026, 7, 11, 8, 0, 0, TimeSpan.Zero);
        var snapshot = new CallMediaRtpSnapshot(
            CapturedAtUtc: captured,
            LocalSsrc: 0x11112222u,
            RemoteSsrc: 0x33334444u,
            SenderPacketCount: 100u,
            SenderOctetCount: 16000u,
            LastSentRtpTimestamp: 54321u,
            HasSentRtpPackets: true,
            PacketsExpected: 95u,
            PacketsReceived: 90u,
            FractionLost: 13,
            CumulativePacketsLost: 5,
            ExtendedHighestSequenceNumber: 200u,
            InterarrivalJitterRtpUnits: 42u,
            LocalReceiveJitterMs: 3.5,
            LocalReceivePacketLossPercent: 5.0,
            LocalRoundTripTimeHintMs: 20.0);

        var stats = CallRtpStatisticsFactory.From(snapshot);

        Assert.Equal(captured, stats.CapturedAtUtc);
        Assert.Equal(0x11112222u, stats.LocalSsrc);
        Assert.Equal(0x33334444u, stats.RemoteSsrc);
        Assert.Equal(100u, stats.PacketsSent);
        Assert.Equal(16000u, stats.OctetsSent);
        Assert.Equal(90u, stats.PacketsReceived);
        Assert.Equal(95u, stats.PacketsExpected);
        Assert.Equal(5, stats.CumulativePacketsLost);
        Assert.Equal((byte)13, stats.FractionLost);
        Assert.Equal(200u, stats.ExtendedHighestSequenceNumber);
        Assert.Equal(42u, stats.InterarrivalJitterRtpUnits);
    }

    [Fact]
    public void RtpStatistics_roundtrips_onto_the_call()
    {
        var stats = new CallRtpStatistics(
            CapturedAtUtc: DateTimeOffset.UnixEpoch,
            LocalSsrc: 0xAAAABBBBu,
            RemoteSsrc: 0xCCCCDDDDu,
            PacketsSent: 7u,
            OctetsSent: 1120u,
            PacketsReceived: 6u,
            PacketsExpected: 7u,
            CumulativePacketsLost: 1,
            FractionLost: 36,
            ExtendedHighestSequenceNumber: 6u,
            InterarrivalJitterRtpUnits: 3u);

        _call.SetRtpStatistics(stats);

        Assert.Equal(stats, _call.RtpStatistics);
    }

    // ── CORE-018: ICE connectivity snapshot ───────────────────────────────────────

    [Fact]
    public void IceSnapshot_is_null_for_non_ice_calls()
    {
        Assert.Null(_call.IceSnapshot);
    }

    [Fact]
    public void IceSnapshotFactory_maps_connected_selection_with_pair()
    {
        var local = Candidate("host", "192.168.1.10", 4000);
        var remote = Candidate("srflx", "203.0.113.9", 5000);
        var selection = new CallIceSelectionResult
        {
            State = CallIceNegotiationState.Connected,
            HasSelectedPair = true,
            Nominated = true,
            LocalEndPoint = new IPEndPoint(IPAddress.Parse("192.168.1.10"), 4000),
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 5000),
            LocalCandidate = local,
            RemoteCandidate = remote,
            ReasonCode = "ice.selected"
        };

        var snapshot = CallIceSnapshotFactory.From(selection);

        Assert.Equal(CallIceState.Connected, snapshot.State);
        Assert.True(snapshot.HasSelectedPair);
        Assert.True(snapshot.Nominated);
        Assert.Same(local, snapshot.LocalCandidate);
        Assert.Same(remote, snapshot.RemoteCandidate);
        Assert.Equal(4000, snapshot.SelectedLocalEndPoint!.Port);
        Assert.Equal(5000, snapshot.SelectedRemoteEndPoint!.Port);
    }

    [Fact]
    public void IceSnapshotFactory_maps_failed_selection_without_pair()
    {
        var selection = new CallIceSelectionResult
        {
            State = CallIceNegotiationState.Failed,
            HasSelectedPair = false,
            ReasonCode = "ice.failed"
        };

        var snapshot = CallIceSnapshotFactory.From(selection);

        Assert.Equal(CallIceState.Failed, snapshot.State);
        Assert.False(snapshot.HasSelectedPair);
        Assert.Null(snapshot.LocalCandidate);
        Assert.Null(snapshot.SelectedRemoteEndPoint);
    }

    [Fact]
    public void IceSnapshot_roundtrips_onto_the_call()
    {
        var snapshot = new CallIceSnapshot(
            CallIceState.Connected,
            HasSelectedPair: true,
            Nominated: true,
            LocalCandidate: Candidate("host", "10.0.0.1", 6000),
            RemoteCandidate: Candidate("relay", "10.0.0.2", 7000),
            SelectedLocalEndPoint: new IPEndPoint(IPAddress.Parse("10.0.0.1"), 6000),
            SelectedRemoteEndPoint: new IPEndPoint(IPAddress.Parse("10.0.0.2"), 7000));

        _call.SetIceSnapshot(snapshot);

        Assert.Same(snapshot, _call.IceSnapshot);
    }

    private static CallIceCandidate Candidate(string type, string address, int port) => new()
    {
        Foundation = "1",
        Component = 1,
        Transport = "UDP",
        Priority = 2130706431,
        Address = address,
        Port = port,
        Type = type
    };
}
