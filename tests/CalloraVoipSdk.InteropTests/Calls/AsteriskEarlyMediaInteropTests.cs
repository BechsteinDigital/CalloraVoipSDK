using CalloraVoipSdk;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.InteropTests.Asterisk;
using Xunit;

using DomainSipTransport = CalloraVoipSdk.Core.Domain.Lines.SipTransport;

namespace CalloraVoipSdk.InteropTests.Calls;

/// <summary>
/// Early Media (183 Session Progress) gegen echten Asterisk: die earlymedia-Extension ruft
/// <c>Progress()</c> und spielt einen Dial-Ton, bevor sie nach einem Fenster <c>Answer()</c> ruft.
///
/// Befund F011 (siehe docs/audit/INTEROP_SOAK_AUDIT.md): Der SDK empfängt zwar das 183 Session Progress,
/// setzt aber KEINE Media-Session aus dessen SDP auf — der Provisional-Response-Handler verarbeitet nur
/// Dialog-Tag/PRACK/Ringing-Transition; MediaParametersNegotiated feuert erst beim 200 OK. Es gibt keinen
/// Pre-Answer-RTP-Empfang. Zusätzlich kehrt <see cref="IPhoneLine.DialAsync"/> erst nach der
/// INVITE-Transaktion zurück, sodass während des Ringing kein Call-Handle für die Beobachtung existiert.
/// Der ideale Zustand ist Skip-blockiert bis F011 gefixt ist.
/// </summary>
[Trait("Category", "Interop")]
public sealed class AsteriskEarlyMediaInteropTests
{
    [Fact(Skip = "F011 — kein Early-Media-Support: der SDK setzt aus der 183-SDP keine Media-Session auf (MediaParametersNegotiated erst beim 200 OK), und DialAsync kehrt erst nach der INVITE-Transaktion zurück (kein Call-Handle während Ringing). Siehe docs/audit/INTEROP_SOAK_AUDIT.md")]
    [Trait("Category", "Interop")]
    public async Task ReceivesEarlyMedia_BeforeAnswer()
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

        var call = await reg.Line!.DialAsync(asterisk.CallTargetUri("earlymedia"));

        // Early-Media-Empfang VOR dem 200 OK: RTP zählt, während der Call noch nicht Connected ist.
        uint earlyReceived = 0;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(6);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (call.State != CallState.Connected && call.RtpStatistics is { PacketsReceived: > 0 } rtp)
            {
                earlyReceived = rtp.PacketsReceived;
                break;
            }
            if (call.State == CallState.Connected) break; // schon beantwortet → Early-Media verpasst
            await Task.Delay(150);
        }

        Assert.True(earlyReceived > 0, $"Kein Early-Media im Pre-Answer-Zustand empfangen (State={call.State}).");

        await call.HangupAsync();
    }
}
