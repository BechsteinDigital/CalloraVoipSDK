using CalloraVoipSdk;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.InteropTests.Asterisk;
using Xunit;

// SipAccount.Transport erwartet den Core-Domain-Typ, nicht den Facade-Typ.
using DomainSipTransport = CalloraVoipSdk.Core.Domain.Lines.SipTransport;

namespace CalloraVoipSdk.InteropTests.Registration;

/// <summary>
/// Interop-Test: SDK-REGISTER-Flow gegen echten Asterisk (PJSIP, Dockerized).
/// Prüft, ob der vollständige SIP-Registrierungsablauf (401 Digest-Challenge → 200 OK)
/// gegen einen realen Asterisk-Server funktioniert.
/// </summary>
public sealed class AsteriskRegisterInteropTests
{
    [DockerRequiredFact]
    public async Task Sdk_RegistersSuccessfully_AgainstRealAsterisk()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();

        // Verbinde direkt über die Container-Bridge-IP (kein NAT / Port-Mapping).
        // Das vermeidet UDP-Forwarding-Probleme im Docker-NAT-Pfad.
        var host = asterisk.ContainerIpAddress;
        const int port = 5060;

        using var client = new VoipClient(new VoipConfiguration { UserAgent = "CalloraInteropTest/1.0" });
        var account = new SipAccount
        {
            SipServer = host,
            Port = port,
            Username = asterisk.Username,
            Password = asterisk.Password,
            Transport = DomainSipTransport.Udp,
        };

        var result = await client.ConnectAsync(
            account, new ConnectOptions { Timeout = TimeSpan.FromSeconds(20) });

        Assert.True(result.IsSuccess,
            $"Registrierung fehlgeschlagen: Status={result.Status}, LineState={result.FinalLineState}, Error={result.Error}");
    }
}
