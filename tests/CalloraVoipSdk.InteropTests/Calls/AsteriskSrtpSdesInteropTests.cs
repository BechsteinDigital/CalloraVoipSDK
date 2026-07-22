using CalloraVoipSdk;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.InteropTests.Asterisk;
using Xunit;

using DomainSipTransport = CalloraVoipSdk.Core.Domain.Lines.SipTransport;

namespace CalloraVoipSdk.InteropTests.Calls;

/// <summary>
/// SRTP-SDES-Media gegen echten Asterisk: Endpoint 6002 erzwingt <c>media_encryption=sdes</c>. Der SDK
/// verhandelt mit <see cref="SrtpPolicy.Required"/> RTP/SAVP + <c>a=crypto</c> (RFC 4568) und tauscht
/// verschlüsseltes Media aus. Geprüft über die öffentliche <see cref="ICall.MediaParameters"/>
/// (<c>IsSrtpNegotiated</c>/<c>MediaProfile</c>/<c>SrtpSuite</c>) plus RTP-Empfang (entschlüsselt gezählt).
///
/// Hinweis: SDES keyt den Master-Key als <c>a=crypto</c> im Klartext-SDP über UDP-Signalisierung (F007);
/// dieser Test belegt die Interop-Verhandlungsmechanik, nicht die Signalisierungs-Vertraulichkeit.
/// </summary>
[Trait("Category", "Interop")]
public sealed class AsteriskSrtpSdesInteropTests
{
    [DockerRequiredFact]
    public async Task RequiredSrtp_NegotiatesSdesAndFlowsEncryptedMedia()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        using var client = new VoipClient(new VoipConfiguration
        {
            UserAgent = "CalloraInteropTest/1.0",
            SrtpPolicy = SrtpPolicy.Required,
        });

        var reg = await client.ConnectAsync(
            new SipAccount
            {
                SipServer = asterisk.ContainerIpAddress,
                Port = 5060,
                Username = asterisk.SdesUsername,
                Password = asterisk.SdesPassword,
                Transport = DomainSipTransport.Udp,
            },
            new ConnectOptions { Timeout = TimeSpan.FromSeconds(20) });
        Assert.True(reg.IsSuccess, $"Registrierung fehlgeschlagen: Status={reg.Status}");

        var result = await client.DialAndWaitUntilConnectedAsync(
            reg.Line!, asterisk.CallTargetUri("answer"), new DialWaitOptions { ConnectTimeout = TimeSpan.FromSeconds(10) });
        Assert.True(result.IsSuccess, $"DialStatus: {result.Status}");
        var call = result.Call!;

        var mediaParameters = call.MediaParameters;
        Assert.NotNull(mediaParameters);
        Assert.True(mediaParameters!.IsSrtpNegotiated, "SRTP wurde nicht verhandelt.");
        Assert.Equal("RTP/SAVP", mediaParameters.MediaProfile);
        Assert.False(string.IsNullOrEmpty(mediaParameters.SrtpSuite), "Keine SDES-Crypto-Suite gesetzt.");

        // Verschlüsseltes Media fließt (SRTP inbound, entschlüsselt gezählt).
        uint received = 0;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (call.RtpStatistics is { PacketsReceived: > 0 } rtp) { received = rtp.PacketsReceived; break; }
            await Task.Delay(250);
        }
        Assert.True(received > 0, "Kein (SRTP-)RTP von Asterisk empfangen.");

        await call.HangupAsync();
        Assert.Equal(CallState.Terminated, call.State);
    }
}
