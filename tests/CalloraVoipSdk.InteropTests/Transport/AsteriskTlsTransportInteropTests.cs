using CalloraVoipSdk;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.Core.Infrastructure.Security;
using CalloraVoipSdk.InteropTests.Asterisk;
using Xunit;

using DomainSipTransport = CalloraVoipSdk.Core.Domain.Lines.SipTransport;

namespace CalloraVoipSdk.InteropTests.Transport;

/// <summary>
/// SIP über TLS-Transport gegen echten Asterisk (Fixture hat [transport-tls] auf 5061 mit self-signed
/// Zertifikat; der SDK vertraut ihm über <see cref="TlsConfiguration.AcceptUntrustedCertificates"/>).
///
/// F010 (GEFIXT): Der NAT-korrektive Re-Register ist auf UDP beschränkt; über TLS übernimmt die
/// persistente Verbindung das Routing (RFC 5626), sodass die Registration nach dem Handshake stabil
/// bleibt (früher: Contact-Rewrite auf den SNAT-Port → 403). Siehe docs/audit/INTEROP_SOAK_AUDIT.md.
/// </summary>
[Trait("Category", "Interop")]
public sealed class AsteriskTlsTransportInteropTests
{
    [DockerRequiredFact]
    public async Task RegisterAndAnsweredCall_OverTlsSignaling()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        using var client = new VoipClient(new VoipConfiguration
        {
            UserAgent = "CalloraInteropTest/1.0",
            SrtpPolicy = SrtpPolicy.Disabled,
            Tls = new TlsConfiguration { AcceptUntrustedCertificates = true },
        });

        var reg = await client.ConnectAsync(
            new SipAccount
            {
                SipServer = asterisk.ContainerIpAddress,
                Port = asterisk.SipTlsPort,
                Username = asterisk.Username,
                Password = asterisk.Password,
                Transport = DomainSipTransport.Tls,
            },
            new ConnectOptions { Timeout = TimeSpan.FromSeconds(20) });
        Assert.True(reg.IsSuccess, $"TLS-Registrierung fehlgeschlagen: Status={reg.Status}");

        var result = await client.DialAndWaitUntilConnectedAsync(
            reg.Line!, asterisk.CallTargetUri("answer", asterisk.SipTlsPort), new DialWaitOptions { ConnectTimeout = TimeSpan.FromSeconds(10) });
        Assert.True(result.IsSuccess, $"TLS-Dial fehlgeschlagen: Status={result.Status}");

        var call = result.Call!;
        Assert.Equal(CallState.Connected, call.State);

        await call.HangupAsync();
        Assert.Equal(CallState.Terminated, call.State);
    }
}
