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
}
