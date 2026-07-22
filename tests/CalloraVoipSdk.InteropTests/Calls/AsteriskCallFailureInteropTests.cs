using CalloraVoipSdk;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.InteropTests.Asterisk;
using Xunit;

using DomainSipTransport = CalloraVoipSdk.Core.Domain.Lines.SipTransport;

namespace CalloraVoipSdk.InteropTests.Calls;

/// <summary>
/// Non-Happy-Path ausgehender Calls gegen echten Asterisk (Gruppe B): Ablehnung (486/603/404),
/// no-answer-Timeout und CANCEL. Prüft die Facade-Ergebnis-Taxonomie (<see cref="DialStatus"/>) auf L4.
///
/// Media: bewusst <see cref="SrtpPolicy.Disabled"/> (Plain RTP). Non-Happy-Path prüft die SIP-Rejection-
/// Semantik, nicht die Medien-Sicherheit; das Default-SRTP-Angebot (RTP/SAVP) würde der SRTP-lose
/// Asterisk-Endpoint 6001 mit 488 ablehnen (orthogonal, siehe Audit-Fund F007).
///
/// Befunde F008/F009 (siehe docs/audit/INTEROP_SOAK_AUDIT.md): no-answer ehrt `ConnectTimeout` nicht
/// (wartet den Ring-/Transaktions-Timeout ab) und meldet `Failed` statt `Timeout`; externe Cancellation
/// meldet `Failed` (synthetischer 408) statt `Canceled`. Die grünen Tests halten das reale Verhalten
/// fest; die idealen Zustände sind Skip-blockiert bis zum Fix.
/// </summary>
[Trait("Category", "Interop")]
public sealed class AsteriskCallFailureInteropTests
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

    // ── Grün: Peer-Ablehnung → DialStatus.Failed ─────────────────────────────────────────────────

    [DockerRequiredFact]
    public async Task BusyTarget_YieldsFailed()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        using var client = NewClient();
        var line = await RegisterAsync(asterisk, client);

        var result = await client.DialAndWaitUntilConnectedAsync(
            line, asterisk.CallTargetUri("busy"), new DialWaitOptions { ConnectTimeout = TimeSpan.FromSeconds(10) });

        Assert.False(result.IsSuccess);
        Assert.Equal(DialStatus.Failed, result.Status); // Asterisk: 486 Busy Here
    }

    [DockerRequiredFact]
    public async Task DeclinedTarget_YieldsFailed()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        using var client = NewClient();
        var line = await RegisterAsync(asterisk, client);

        var result = await client.DialAndWaitUntilConnectedAsync(
            line, asterisk.CallTargetUri("decline"), new DialWaitOptions { ConnectTimeout = TimeSpan.FromSeconds(10) });

        Assert.False(result.IsSuccess);
        Assert.Equal(DialStatus.Failed, result.Status); // Asterisk: Hangup(21) → 403 Forbidden
    }

    [DockerRequiredFact]
    public async Task UnknownExtension_YieldsFailed()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        using var client = NewClient();
        var line = await RegisterAsync(asterisk, client);

        var result = await client.DialAndWaitUntilConnectedAsync(
            line, asterisk.CallTargetUri("nonexistent"), new DialWaitOptions { ConnectTimeout = TimeSpan.FromSeconds(10) });

        Assert.False(result.IsSuccess);
        Assert.Equal(DialStatus.Failed, result.Status); // Asterisk: 404 Not Found (kein Dialplan-Eintrag)
    }

    // ── Skip (F008/F009): ideale Ergebnisse — bis Fix blockiert ──────────────────────────────────

    [Fact(Skip = "F008 — DialWaitOptions.ConnectTimeout wird bei ringendem Ziel nicht geehrt: der Call wartet ~RingTimeout und meldet Failed(synth. 408) statt Timeout. Siehe docs/audit/INTEROP_SOAK_AUDIT.md")]
    [Trait("Category", "Interop")]
    public async Task NoAnswer_ShouldTimeoutWithinConnectTimeout()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        using var client = NewClient();
        var line = await RegisterAsync(asterisk, client);

        var started = DateTimeOffset.UtcNow;
        var result = await client.DialAndWaitUntilConnectedAsync(
            line, asterisk.CallTargetUri("noanswer"), new DialWaitOptions { ConnectTimeout = TimeSpan.FromSeconds(4) });

        Assert.Equal(DialStatus.Timeout, result.Status);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(10), "ConnectTimeout wurde nicht geehrt.");
    }

    [Fact(Skip = "F009 — externe Cancellation meldet DialStatus.Failed (synth. 408) statt Canceled (Timing wird geehrt, nur das Status-Mapping ist falsch). Siehe docs/audit/INTEROP_SOAK_AUDIT.md")]
    [Trait("Category", "Interop")]
    public async Task CancelledDial_ShouldYieldCanceled()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        using var client = NewClient();
        var line = await RegisterAsync(asterisk, client);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var result = await client.DialAndWaitUntilConnectedAsync(
            line, asterisk.CallTargetUri("noanswer"), new DialWaitOptions { ConnectTimeout = TimeSpan.FromSeconds(30) }, cts.Token);

        Assert.Equal(DialStatus.Canceled, result.Status);
    }
}
