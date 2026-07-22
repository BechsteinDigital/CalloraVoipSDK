using CalloraVoipSdk;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.InteropTests.Asterisk;
using Xunit;

using DomainSipTransport = CalloraVoipSdk.Core.Domain.Lines.SipTransport;

namespace CalloraVoipSdk.InteropTests.Registration;

/// <summary>
/// Non-Happy-Path des SDK-REGISTER-Flows gegen echten Asterisk (Gruppe A):
/// falsches Passwort, unbekannter User, unerreichbarer Server. Prüft die Facade-Ergebnis-Taxonomie
/// (<see cref="ConnectStatus"/>) auf L4.
///
/// Befund F005 (siehe docs/audit/INTEROP_SOAK_AUDIT.md): Bei permanenter Auth-Ablehnung erreicht die
/// Line intern <see cref="LineState.Failed"/>, aber die Convenience-<c>ConnectAsync</c> short-circuittet
/// NICHT — sie wartet das volle Timeout ab und meldet dann <see cref="ConnectStatus.Timeout"/> mit
/// <c>Error == null</c>. Die grünen Tests halten das reale Verhalten fest; der ideale Zustand
/// (Status=Failed + Auth-Code) ist Skip-blockiert bis F005 gefixt ist.
/// </summary>
[Trait("Category", "Interop")]
public sealed class AsteriskRegisterFailureInteropTests
{
    private static SipAccount Account(string server, string user, string password) => new()
    {
        SipServer = server,
        Port = 5060,
        Username = user,
        Password = password,
        Transport = DomainSipTransport.Udp,
    };

    // ── Grün: reales Verhalten (die Line erkennt die permanente Ablehnung) ───────────────────────

    [DockerRequiredFact]
    public async Task WrongPassword_LineReachesFailedState()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();

        using var client = new VoipClient(new VoipConfiguration { UserAgent = "CalloraInteropTest/1.0" });
        var result = await client.ConnectAsync(
            Account(asterisk.ContainerIpAddress, asterisk.Username, "definitely-wrong"),
            new ConnectOptions { Timeout = TimeSpan.FromSeconds(6) });

        Assert.False(result.IsSuccess);
        Assert.Equal(LineState.Failed, result.FinalLineState);
    }

    [DockerRequiredFact]
    public async Task UnknownUser_LineReachesFailedState()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();

        using var client = new VoipClient(new VoipConfiguration { UserAgent = "CalloraInteropTest/1.0" });
        var result = await client.ConnectAsync(
            Account(asterisk.ContainerIpAddress, "9999", "secret"),
            new ConnectOptions { Timeout = TimeSpan.FromSeconds(6) });

        Assert.False(result.IsSuccess);
        Assert.Equal(LineState.Failed, result.FinalLineState);
    }

    // Braucht kein Docker: unerreichbarer Server (RFC 5737 TEST-NET-1) → keine Antwort → Timeout.
    // Dies IST das korrekte Timeout-Ergebnis (im Gegensatz zur Auth-Ablehnung, siehe F005).
    [Fact]
    public async Task UnreachableServer_YieldsTimeout()
    {
        using var client = new VoipClient(new VoipConfiguration { UserAgent = "CalloraInteropTest/1.0" });
        var result = await client.ConnectAsync(
            Account("192.0.2.1", "6001", "secret"),
            new ConnectOptions { Timeout = TimeSpan.FromSeconds(4) });

        Assert.False(result.IsSuccess);
        Assert.Equal(ConnectStatus.Timeout, result.Status);
    }

    // ── Skip (F005): idealer Zustand — Auth-Ablehnung sollte SCHNELL als Failed mit Code kommen ──

    [Fact(Skip = "F005 — ConnectAsync short-circuittet nicht bei terminalem Failed: Auth-Ablehnung wird als Timeout+Error=null gemeldet statt Failed+Auth-Code. Siehe docs/audit/INTEROP_SOAK_AUDIT.md")]
    [Trait("Category", "Interop")]
    public async Task WrongPassword_ShouldReportFailedStatusWithAuthCode()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();

        using var client = new VoipClient(new VoipConfiguration { UserAgent = "CalloraInteropTest/1.0" });
        var result = await client.ConnectAsync(
            Account(asterisk.ContainerIpAddress, asterisk.Username, "definitely-wrong"),
            new ConnectOptions { Timeout = TimeSpan.FromSeconds(20) });

        Assert.Equal(ConnectStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
    }
}
