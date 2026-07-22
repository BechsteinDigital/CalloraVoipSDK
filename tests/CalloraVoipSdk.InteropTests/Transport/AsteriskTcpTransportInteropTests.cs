using CalloraVoipSdk;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.InteropTests.Asterisk;
using Xunit;

using DomainSipTransport = CalloraVoipSdk.Core.Domain.Lines.SipTransport;

namespace CalloraVoipSdk.InteropTests.Transport;

/// <summary>
/// SIP über TCP-Transport gegen echten Asterisk (Fixture hat [transport-tcp] auf 5060): Register und
/// ein beantworteter Call laufen komplett über TCP-Signalisierung. Media bleibt Plain RTP über UDP.
///
/// F010 (GEFIXT): Der NAT-korrektive Re-Register ist jetzt auf UDP beschränkt — über TCP/TLS übernimmt
/// die persistente Verbindung das Routing (RFC 5626), sodass die Registration stabil bleibt (früher:
/// Contact-Rewrite auf den SNAT-Port → 403). Siehe docs/audit/INTEROP_SOAK_AUDIT.md.
/// </summary>
[Trait("Category", "Interop")]
public sealed class AsteriskTcpTransportInteropTests
{
    [DockerRequiredFact]
    public async Task RegisterAndAnsweredCall_OverTcpSignaling()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        using var client = new VoipClient(new VoipConfiguration
        {
            UserAgent = "CalloraInteropTest/1.0",
            SrtpPolicy = SrtpPolicy.Disabled,
        });

        var reg = await client.ConnectAsync(
            new SipAccount
            {
                SipServer = asterisk.ContainerIpAddress,
                Port = 5060,
                Username = asterisk.Username,
                Password = asterisk.Password,
                Transport = DomainSipTransport.Tcp,
            },
            new ConnectOptions { Timeout = TimeSpan.FromSeconds(20) });
        Assert.True(reg.IsSuccess, $"TCP-Registrierung fehlgeschlagen: Status={reg.Status}");

        var result = await client.DialAndWaitUntilConnectedAsync(
            reg.Line!, asterisk.CallTargetUri("answer"), new DialWaitOptions { ConnectTimeout = TimeSpan.FromSeconds(10) });
        Assert.True(result.IsSuccess, $"TCP-Dial fehlgeschlagen: Status={result.Status}");

        var call = result.Call!;
        Assert.Equal(CallState.Connected, call.State);

        await call.HangupAsync();
        Assert.Equal(CallState.Terminated, call.State);
    }
}
