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
/// Befund F005 (GEFIXT): Bei permanenter Auth-Ablehnung short-circuittet die Convenience-<c>ConnectAsync</c>
/// jetzt am terminalen <see cref="LineState.Failed"/> und meldet zügig <see cref="ConnectStatus.Failed"/>,
/// statt das volle Timeout abzuwarten (vorher: <see cref="ConnectStatus.Timeout"/> nach vollem Timeout).
/// F005b (GEFIXT): der zugrunde liegende Auth-Fehler (401/403) wird jetzt als
/// <c>ConnectResult.Error</c> durchgereicht — die permanente Failure-Reason wird vor dem
/// State-Übergang erfasst. Siehe docs/audit/INTEROP_SOAK_AUDIT.md.
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

    // ── Grün (F005 gefixt): Auth-Ablehnung meldet zügig Failed, ohne das volle Timeout abzuwarten ──

    [DockerRequiredFact]
    public async Task WrongPassword_ReportsFailedStatusPromptly()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();

        using var client = new VoipClient(new VoipConfiguration { UserAgent = "CalloraInteropTest/1.0" });
        var started = DateTimeOffset.UtcNow;
        var result = await client.ConnectAsync(
            Account(asterisk.ContainerIpAddress, asterisk.Username, "definitely-wrong"),
            new ConnectOptions { Timeout = TimeSpan.FromSeconds(20) });
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.Equal(ConnectStatus.Failed, result.Status);          // F005-Fix: Failed statt Timeout
        Assert.Equal(LineState.Failed, result.FinalLineState);
        Assert.True(elapsed < TimeSpan.FromSeconds(15), $"Auth-Ablehnung wurde nicht short-circuitet: {elapsed}");
    }

    // ── Grün (F005b gefixt): der Auth-Fehler ist als ConnectResult.Error sichtbar ──────────────────

    [DockerRequiredFact]
    public async Task WrongPassword_ShouldExposeAuthError()
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
