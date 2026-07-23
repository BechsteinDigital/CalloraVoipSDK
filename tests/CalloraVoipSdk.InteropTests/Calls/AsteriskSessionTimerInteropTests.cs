using CalloraVoipSdk;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.InteropTests.Asterisk;
using Xunit;

using DomainSipTransport = CalloraVoipSdk.Core.Domain.Lines.SipTransport;

namespace CalloraVoipSdk.InteropTests.Calls;

/// <summary>
/// Session-Timer (RFC 4028) gegen echten Asterisk: der SDK bietet im INVITE <c>Session-Expires</c> +
/// <c>Min-SE</c> und <c>Supported: timer</c> an; Asterisk bestätigt die Session-Timer im 200 OK.
/// Verifiziert über den Asterisk-Wire-Log (<c>pjsip set logger on</c>).
///
/// Der eigentliche Refresh-Zyklus (RFC-Min-SE ≥ 90 s, Refresh bei Session-Interval/2 → hier 900 s) ist
/// nicht ohne echte Wartezeit prüfbar — siehe Audit-Fund F003 (fehlende Zeit-Abstraktion im
/// Signaling-Layer). Dieser Test deckt die Verhandlung ab, nicht den Refresh selbst.
/// </summary>
[Trait("Category", "Interop")]
public sealed class AsteriskSessionTimerInteropTests
{
    [DockerRequiredFact]
    public async Task NegotiatesSessionTimer_WithAsterisk()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        await asterisk.ExecAsync("asterisk", "-rx", "pjsip set logger on");

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

        await Task.Delay(800); // die ausgetauschten Nachrichten in die Konsole schreiben lassen
        var logs = await asterisk.GetConsoleLogsAsync();
        await result.Call!.HangupAsync();

        Assert.Contains("Session-Expires:", logs, StringComparison.OrdinalIgnoreCase); // ausgehandelt
        Assert.Contains("refresher=uac", logs, StringComparison.OrdinalIgnoreCase);    // SDK ist Refresher
        Assert.Contains("Min-SE: 90", logs, StringComparison.OrdinalIgnoreCase);       // RFC-4028-Untergrenze angeboten
    }
}
