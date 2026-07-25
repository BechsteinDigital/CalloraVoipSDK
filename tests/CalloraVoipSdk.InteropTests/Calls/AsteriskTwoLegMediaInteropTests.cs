using CalloraVoipSdk;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.InteropTests.Asterisk;
using CalloraVoipSdk.InteropTests.Media;
using Xunit;

using DomainSipTransport = CalloraVoipSdk.Core.Domain.Lines.SipTransport;

namespace CalloraVoipSdk.InteropTests.Calls;

/// <summary>
/// Zwei-Bein-Media-Interop gegen echten Asterisk: zwei VoipClient-Legs (A=6001, B=6003) werden über den
/// PBX gebrückt und der Medienpfad bidirektional gemessen (Paketzähler, RTCP-Qualität, Inhalt). Diese
/// Suite wächst über mehrere Slices; die Fixture-Smoke-Tests bleiben als Aufbau-Regression bestehen.
/// </summary>
[Trait("Category", "Interop")]
public sealed class AsteriskTwoLegMediaInteropTests
{
    private static VoipClient NewClient() =>
        new(new VoipConfiguration { UserAgent = "CalloraInteropTest/1.0", SrtpPolicy = SrtpPolicy.Disabled });

    // Fixture-Smoke-Test: belegt, dass der zweite Plain-RTP-Endpoint 6003 sich registriert.
    // Die Bridge-/Media-Tests folgen in den nächsten Slices dieser Datei.
    [DockerRequiredFact]
    public async Task SecondPlainRtpEndpoint_6003_Registers()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        using var client = NewClient();

        var reg = await client.ConnectAsync(
            new SipAccount
            {
                SipServer = asterisk.ContainerIpAddress,
                Port = 5060,
                Username = asterisk.BridgeUsername,
                Password = asterisk.BridgePassword,
                Transport = DomainSipTransport.Udp,
            },
            new ConnectOptions { Timeout = TimeSpan.FromSeconds(20) });

        Assert.True(reg.IsSuccess, $"Registrierung 6003 fehlgeschlagen: Status={reg.Status}");
    }

    [DockerRequiredFact]
    public async Task BridgedCall_ConnectsBothLegs()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();

        await using var bridged = await TwoLegBridgedCall.StartAsync(asterisk);

        Assert.Equal(CalloraVoipSdk.Core.Domain.Calls.CallState.Connected, bridged.CallerCall.State);
        Assert.Equal(CalloraVoipSdk.Core.Domain.Calls.CallState.Connected, bridged.CalleeCall.State);
        Assert.Equal(0, bridged.CallerCall.MediaParameters!.PayloadType);  // PCMU beidseitig
        Assert.Equal(0, bridged.CalleeCall.MediaParameters!.PayloadType);
    }

    [DockerRequiredFact]
    public async Task BridgedCall_FlowsRtpInBothDirections()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        await using var bridged = await TwoLegBridgedCall.StartAsync(asterisk);

        await bridged.RunBidirectionalMediaAsync();

        AssertBidirectionalRtp(bridged.CallerCall, "Caller");
        AssertBidirectionalRtp(bridged.CalleeCall, "Callee");

        static void AssertBidirectionalRtp(CalloraVoipSdk.Core.Domain.Calls.ICall call, string label)
        {
            var rtp = call.RtpStatistics;
            Assert.True(rtp is { PacketsSent: > 0 }, $"{label}: keine gesendeten RTP-Pakete.");
            Assert.True(rtp is { PacketsReceived: > 0 }, $"{label}: keine empfangenen RTP-Pakete.");
        }
    }

    [DockerRequiredFact]
    public async Task BridgedCall_PopulatesLocalRtcpQuality()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        await using var bridged = await TwoLegBridgedCall.StartAsync(asterisk);

        await bridged.RunBidirectionalMediaAsync(TimeSpan.FromSeconds(10));

        AssertLocalQuality(bridged.CallerCall, "Caller");
        AssertLocalQuality(bridged.CalleeCall, "Callee");

        static void AssertLocalQuality(CalloraVoipSdk.Core.Domain.Calls.ICall call, string label)
        {
            var q = call.QualitySnapshot;
            Assert.True(q.RtcpActive, $"{label}: RTCP nicht aktiv.");
            Assert.True(double.IsFinite(q.LocalReceiveJitterMs) && q.LocalReceiveJitterMs >= 0,
                $"{label}: implausibler Jitter {q.LocalReceiveJitterMs}.");
            Assert.InRange(q.LocalReceivePacketLossPercent, 0.0, 100.0);
        }
    }

    [DockerRequiredFact]
    public async Task BridgedCall_PopulatesRemoteRtcpReport()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        await using var bridged = await TwoLegBridgedCall.StartAsync(asterisk);

        await bridged.RunBidirectionalMediaAsync(TimeSpan.FromSeconds(10));

        AssertRemoteReport(bridged.CallerCall, "Caller");
        AssertRemoteReport(bridged.CalleeCall, "Callee");

        // Der SDK parst Asterisks RTCP RR/SR: Peer-Sicht (Jitter/Loss) + RTT werden befüllt.
        // MOS bleibt null (Asterisk sendet kein RTCP-XR VoIP-Metrics) → hier bewusst nicht asserted.
        static void AssertRemoteReport(CalloraVoipSdk.Core.Domain.Calls.ICall call, string label)
        {
            var q = call.QualitySnapshot;
            Assert.True(q.RemoteReportJitterMs is >= 0,
                $"{label}: RemoteReportJitterMs nicht befüllt/implausibel ({q.RemoteReportJitterMs?.ToString() ?? "null"}).");
            Assert.True(q.RemoteReportPacketLossPercent is >= 0 and <= 100,
                $"{label}: RemoteReportPacketLossPercent nicht befüllt/implausibel ({q.RemoteReportPacketLossPercent?.ToString() ?? "null"}).");
            Assert.True(q.RoundTripTimeMs is >= 0,
                $"{label}: RoundTripTimeMs nicht befüllt ({q.RoundTripTimeMs?.ToString() ?? "null"}).");
        }
    }

    [DockerRequiredFact]
    public async Task BridgedCall_DeliversMarkedContentEndToEnd()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        await using var bridged = await TwoLegBridgedCall.StartAsync(asterisk);

        var result = await bridged.RunBidirectionalMediaAsync(TimeSpan.FromSeconds(8));

        var received = result.CalleeReceivedSequences;
        Assert.NotEmpty(received);
        // Größter kontiguierlicher Lauf empfangener Marker; Rand-/Playout-Verluste toleriert.
        var longestRun = LongestContiguousRun(received);
        Assert.True(longestRun >= 50,
            $"Nur {longestRun} zusammenhängende markierte Frames end-to-end (von {received.Count} empfangen).");

        static int LongestContiguousRun(IReadOnlyList<uint> seqs)
        {
            var set = new HashSet<uint>(seqs);
            var best = 0;
            foreach (var s in set)
            {
                if (set.Contains(s - 1)) continue; // nur Lauf-Anfänge
                var len = 1;
                while (set.Contains(s + (uint)len)) len++;
                best = Math.Max(best, len);
            }
            return best;
        }
    }
}
