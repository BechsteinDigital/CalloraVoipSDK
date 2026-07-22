using CalloraVoipSdk;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Events;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.InteropTests.Asterisk;
using Xunit;

using DomainSipTransport = CalloraVoipSdk.Core.Domain.Lines.SipTransport;

namespace CalloraVoipSdk.InteropTests.Calls;

/// <summary>
/// Inbound Call (Asterisk → SDK) gegen echten Asterisk: nach der Registration lässt ein
/// <c>channel originate PJSIP/6001 application Milliwatt</c> Asterisk den SDK anrufen. Der SDK empfängt
/// den INVITE (<see cref="IVoipClient.IncomingCall"/>), akzeptiert ihn und empfängt Media. Geprüft über
/// <see cref="ICall.Direction"/>/<see cref="ICall.State"/> und RTP-Empfang. Plain RTP (F007).
/// </summary>
[Trait("Category", "Interop")]
public sealed class AsteriskInboundCallInteropTests
{
    [DockerRequiredFact]
    public async Task InboundCall_FromAsterisk_IsAcceptedWithMedia()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        using var client = new VoipClient(new VoipConfiguration
        {
            UserAgent = "CalloraInteropTest/1.0",
            SrtpPolicy = SrtpPolicy.Disabled,
        });

        var incoming = new TaskCompletionSource<ICall>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.IncomingCall += (_, e) => incoming.TrySetResult(e.Call);

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

        // Asterisk ruft den registrierten Endpoint 6001 an und spielt bei Answer einen Testton.
        await asterisk.ExecAsync("asterisk", "-rx", "channel originate PJSIP/6001 application Milliwatt");

        var call = await incoming.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(CallDirection.Inbound, call.Direction);

        await call.AcceptAsync();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8);
        while (call.State != CallState.Connected && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(100);
        Assert.Equal(CallState.Connected, call.State);

        uint received = 0;
        var mediaDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTimeOffset.UtcNow < mediaDeadline)
        {
            if (call.RtpStatistics is { PacketsReceived: > 0 } rtp) { received = rtp.PacketsReceived; break; }
            await Task.Delay(250);
        }
        Assert.True(received > 0, "Kein RTP vom eingehenden Asterisk-Call empfangen.");

        await call.HangupAsync();
        Assert.Equal(CallState.Terminated, call.State);
    }
}
