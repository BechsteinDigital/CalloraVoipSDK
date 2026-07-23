using CalloraVoipSdk;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.InteropTests.Asterisk;
using Xunit;

using DomainSipTransport = CalloraVoipSdk.Core.Domain.Lines.SipTransport;

namespace CalloraVoipSdk.InteropTests.Calls;

/// <summary>
/// Call-Transfer (REFER, RFC 3515) gegen echten Asterisk. Blind Transfer: der SDK weist den Peer per
/// REFER an, den Call zu einem anderen Ziel umzuleiten, und gibt den eigenen Call frei. Attended
/// Transfer verbindet einen aktiven mit einem Beratungs-Call. Plain RTP (<see cref="SrtpPolicy.Disabled"/>).
/// </summary>
[Trait("Category", "Interop")]
public sealed class AsteriskTransferInteropTests
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

    private static async Task<ICall> DialAnswerAsync(AsteriskContainer asterisk, VoipClient client, IPhoneLine line, string extension)
    {
        var result = await client.DialAndWaitUntilConnectedAsync(
            line, asterisk.CallTargetUri(extension), new DialWaitOptions { ConnectTimeout = TimeSpan.FromSeconds(10) });
        Assert.True(result.IsSuccess, $"Dial({extension}) fehlgeschlagen: Status={result.Status}");
        return result.Call!;
    }

    [DockerRequiredFact]
    public async Task BlindTransfer_IsAcceptedAndReleasesCall()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        using var client = NewClient();
        var line = await RegisterAsync(asterisk, client);

        var call = await DialAnswerAsync(asterisk, client, line, "answer");
        Assert.Equal(CallState.Connected, call.State);

        // REFER an den Peer: den Call blind zur answer-Extension umleiten. Erfolg = 202 + NOTIFY(200),
        // der lokale Call wird freigegeben.
        await call.BlindTransferAsync(asterisk.CallTargetUri("answer"));

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8);
        while (call.State != CallState.Terminated && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(100);
        Assert.Equal(CallState.Terminated, call.State);
    }

    [DockerRequiredFact]
    public async Task AttendedTransfer_BridgesConsultationCall()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        using var client = NewClient();
        var line = await RegisterAsync(asterisk, client);

        var primary = await DialAnswerAsync(asterisk, client, line, "answer");
        var consultation = await DialAnswerAsync(asterisk, client, line, "dtmf");
        Assert.Equal(CallState.Connected, primary.State);
        Assert.Equal(CallState.Connected, consultation.State);

        // Verbindet den Peer des primären Calls mit dem Peer des Beratungs-Calls (REFER mit Replaces).
        var ok = await primary.AttendedTransferAsync(consultation);
        Assert.True(ok, "Attended-Transfer wurde nicht bestätigt.");

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8);
        while (primary.State != CallState.Terminated && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(100);
        Assert.Equal(CallState.Terminated, primary.State);
    }
}
