using CalloraVoipSdk;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.InteropTests.Asterisk;
using Xunit;

using DomainSipTransport = CalloraVoipSdk.Core.Domain.Lines.SipTransport;

namespace CalloraVoipSdk.InteropTests.Calls;

/// <summary>
/// Hold/Unhold (re-INVITE) gegen echten Asterisk: ein beantworteter Call wird gehalten
/// (<c>a=sendonly</c>) und wieder aktiviert (<c>a=sendrecv</c>). Geprüft über den öffentlichen
/// <see cref="ICall.State"/> (<see cref="CallState.OnHold"/> ↔ <see cref="CallState.Connected"/>).
/// Plain RTP (<see cref="SrtpPolicy.Disabled"/>, siehe F007).
/// </summary>
[Trait("Category", "Interop")]
public sealed class AsteriskHoldInteropTests
{
    [DockerRequiredFact]
    public async Task HoldThenUnhold_TogglesCallState()
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

        var result = await client.DialAndWaitUntilConnectedAsync(
            reg.Line!, asterisk.CallTargetUri("answer"), new DialWaitOptions { ConnectTimeout = TimeSpan.FromSeconds(10) });
        Assert.True(result.IsSuccess, $"DialStatus: {result.Status}");
        var call = result.Call!;
        Assert.Equal(CallState.Connected, call.State);

        await call.HoldAsync();
        await WaitForStateAsync(call, CallState.OnHold);
        Assert.Equal(CallState.OnHold, call.State);

        await call.UnholdAsync();
        await WaitForStateAsync(call, CallState.Connected);
        Assert.Equal(CallState.Connected, call.State);

        await call.HangupAsync();
        Assert.Equal(CallState.Terminated, call.State);
    }

    private static async Task WaitForStateAsync(ICall call, CallState target)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8);
        while (call.State != target && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(100);
    }
}
