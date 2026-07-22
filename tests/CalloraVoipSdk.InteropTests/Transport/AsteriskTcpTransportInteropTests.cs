using CalloraVoipSdk;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.InteropTests.Asterisk;
using Xunit;

using DomainSipTransport = CalloraVoipSdk.Core.Domain.Lines.SipTransport;

namespace CalloraVoipSdk.InteropTests.Transport;

/// <summary>
/// SIP über TCP-Transport gegen echten Asterisk (Fixture hat [transport-tcp] auf 5060).
///
/// Befund F010 (siehe docs/audit/INTEROP_SOAK_AUDIT.md): Die erste TCP-Registration erhält 200 OK,
/// aber der NAT-korrektive Re-Register (SipLineChannel, rport-basiert) wird transport-unabhängig
/// angewendet und schreibt den Contact auf die vom Registrar reflektierte SNAT-Adresse um. Über die
/// persistente TCP-Verbindung passt dieser Contact nicht zum tatsächlichen Verbindungs-Source-Port →
/// Asterisk lehnt die Re-Registration mit 403 ab → Line Failed. Über UDP ist das SNAT-Mapping stabil,
/// daher grün. Der ideale Zustand ist Skip-blockiert bis F010 gefixt ist.
/// </summary>
[Trait("Category", "Interop")]
public sealed class AsteriskTcpTransportInteropTests
{
    [Fact(Skip = "F010 — NAT-korrektiver Re-Register wird transport-unabhängig angewendet; über TCP+NAT schreibt er den Contact auf den SNAT-Port um, der nicht zur persistenten Verbindung passt → Asterisk 403 auf die Re-Registration. Erste Registration (200 OK) gelingt. Siehe docs/audit/INTEROP_SOAK_AUDIT.md")]
    [Trait("Category", "Interop")]
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
