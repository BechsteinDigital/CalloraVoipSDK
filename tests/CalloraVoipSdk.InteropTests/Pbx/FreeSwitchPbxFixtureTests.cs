using CalloraVoipSdk;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.InteropTests.Pbx;
using Xunit;

using DomainSipTransport = CalloraVoipSdk.Core.Domain.Lines.SipTransport;

namespace CalloraVoipSdk.InteropTests.Pbx;

[Trait("Category", "InteropFreeSwitch")]
public sealed class FreeSwitchPbxFixtureTests
{
    [DockerRequiredFact]
    public async Task PlainBridgePair_CallerRegisters_ThroughAdapter()
    {
        await using IPbxFixture pbx = new FreeSwitchPbxFixture();
        await pbx.StartAsync();
        var pair = pbx.BridgePair(PbxMediaMode.Plain, 0);

        using var client = new VoipClient(new VoipConfiguration { UserAgent = "CalloraInteropTest/1.0", SrtpPolicy = SrtpPolicy.Disabled });
        var reg = await client.ConnectAsync(
            new SipAccount
            {
                SipServer = pbx.SipHost, Port = pbx.SipUdpPort,
                Username = pair.Caller.Username, Password = pair.Caller.Password,
                Transport = DomainSipTransport.Udp,
            },
            new ConnectOptions { Timeout = TimeSpan.FromSeconds(20) });

        Assert.True(reg.IsSuccess, $"FreeSWITCH-Registrierung über den Adapter fehlgeschlagen: {reg.Status}");
    }
}
