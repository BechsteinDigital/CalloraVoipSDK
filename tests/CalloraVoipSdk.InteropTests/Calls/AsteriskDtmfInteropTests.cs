using System.Collections.Concurrent;
using CalloraVoipSdk;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.InteropTests.Asterisk;
using Xunit;

using DomainSipTransport = CalloraVoipSdk.Core.Domain.Lines.SipTransport;

namespace CalloraVoipSdk.InteropTests.Calls;

/// <summary>
/// DTMF-Interop (RFC 4733 telephone-event) gegen echten Asterisk: die dtmf-Extension beantwortet den
/// Call und sendet nach <c>Answer()</c> die Ziffern <c>1234</c> per <c>SendDTMF</c>. Geprüft SDK-seitig
/// über die Verhandlung (<see cref="CalloraVoipSdk.Core.Domain.Calls.CallMediaParameters.TelephoneEventPayloadType"/>)
/// und den Empfang (<c>ICall.DtmfReceived</c>). Plain RTP (<see cref="SrtpPolicy.Disabled"/>, siehe F007).
/// </summary>
[Trait("Category", "Interop")]
public sealed class AsteriskDtmfInteropTests
{
    [DockerRequiredFact]
    public async Task ReceivesRfc4733Dtmf_FromAsterisk()
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
                Transport = DomainSipTransport.Udp,
            },
            new ConnectOptions { Timeout = TimeSpan.FromSeconds(20) });
        Assert.True(reg.IsSuccess, $"Registrierung fehlgeschlagen: Status={reg.Status}");

        var received = new ConcurrentQueue<char>();
        var result = await client.DialAndWaitUntilConnectedAsync(
            reg.Line!, asterisk.CallTargetUri("dtmf"), new DialWaitOptions { ConnectTimeout = TimeSpan.FromSeconds(10) });
        Assert.True(result.IsSuccess, $"DialStatus: {result.Status}");
        var call = result.Call!;
        call.DtmfReceived += (_, e) => received.Enqueue(e.Tone.Symbol);

        Assert.NotNull(call.MediaParameters);
        Assert.NotNull(call.MediaParameters!.TelephoneEventPayloadType); // RFC 4733 verhandelt

        // Auf die vier von Asterisk (nach Wait(2)) gesendeten Ziffern warten.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (received.Count < 4 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(200);

        await call.HangupAsync();

        var digits = new string(received.ToArray());
        Assert.Equal("1234", digits);
    }
}
