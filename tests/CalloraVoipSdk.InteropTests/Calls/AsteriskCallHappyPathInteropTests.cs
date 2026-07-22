using CalloraVoipSdk;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.InteropTests.Asterisk;
using Xunit;

using DomainSipTransport = CalloraVoipSdk.Core.Domain.Lines.SipTransport;

namespace CalloraVoipSdk.InteropTests.Calls;

/// <summary>
/// Happy-Path ausgehender Calls gegen echten Asterisk: INVITE → 200 OK → ACK → RTP-Fluss → BYE.
/// Die answer-Extension beantwortet den Call und sendet einen endlosen Milliwatt-Testton; der SDK
/// empfängt RTP. Da <c>SilenceAudioDevice</c> (Default) selbst kein RTP sendet, ist die Media-Assertion
/// unidirektional: <see cref="ICall.RtpStatistics"/>.<c>PacketsReceived &gt; 0</c>.
///
/// Media: Plain RTP (<see cref="SrtpPolicy.Disabled"/>), da Endpoint 6001 kein media_encryption
/// konfiguriert; das Default-SRTP-Angebot würde mit 488 abgelehnt (siehe Audit-Fund F007).
/// </summary>
[Trait("Category", "Interop")]
public sealed class AsteriskCallHappyPathInteropTests
{
    private static VoipClient NewClient() =>
        new(new VoipConfiguration { UserAgent = "CalloraInteropTest/1.0", SrtpPolicy = SrtpPolicy.Disabled });

    private static async Task<IPhoneLine> RegisterAsync(AsteriskContainer asterisk, VoipClient client)
    {
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
        return reg.Line!;
    }

    [DockerRequiredFact]
    public async Task AnsweredCall_EstablishesDialogAndReceivesRtp()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        using var client = NewClient();
        var line = await RegisterAsync(asterisk, client);

        var result = await client.DialAndWaitUntilConnectedAsync(
            line, asterisk.CallTargetUri("answer"), new DialWaitOptions { ConnectTimeout = TimeSpan.FromSeconds(10) });

        Assert.True(result.IsSuccess, $"DialStatus: {result.Status}");
        var call = result.Call!;
        Assert.Equal(CallState.Connected, call.State);
        Assert.NotNull(call.MediaParameters); // Codec verhandelt (PCMU)

        // RtpStatistics wird erst mit dem ersten RTCP-Report befüllt → auf empfangene Pakete pollen.
        uint received = 0;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (call.RtpStatistics is { PacketsReceived: > 0 } rtp) { received = rtp.PacketsReceived; break; }
            await Task.Delay(250);
        }

        Assert.True(received > 0, "Kein RTP von Asterisk empfangen (erwartet Milliwatt-Ton).");

        await call.HangupAsync();
        Assert.Equal(CallState.Terminated, call.State);
    }
}
