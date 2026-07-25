using CalloraVoipSdk;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Lines;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.InteropTests.Asterisk;
using Xunit;

using DomainSipTransport = CalloraVoipSdk.Core.Domain.Lines.SipTransport;

namespace CalloraVoipSdk.InteropTests.Calls;

/// <summary>
/// Early Media (183 Session Progress, RFC 3960) gegen echten Asterisk: die earlymedia-Extension ruft
/// <c>Progress()</c> und spielt einen Dial-Ton in einem großzügigen Fenster (<c>Wait(10)</c>), bevor sie
/// <c>Answer()</c> ruft.
///
/// F011 ist GEFIXT (Slice 3a–3e): Die SIP-Session wird früh an den Media-Adapter gebunden (3a,
/// <c>onSessionCreated</c>). Trägt ein provisorisches 180/183 eine SDP, publiziert der Channel beim
/// Ringing-Übergang Early-Media-Parameter und startet eine RECEIVE-ONLY Media-Session vor dem 200 OK
/// (3b, <see cref="ICall.MediaParameters"/> + RTP-Empfang schon im Ringing). Das Line-Event
/// <see cref="IPhoneLine.OutboundCallRinging"/> (3c) liefert das Call-Handle im Ringing, WÄHREND
/// <c>DialAsync</c> noch auf das 200 OK wartet — so ist Pre-Answer-Media beobachtbar. DTMF ist im
/// early dialog erlaubt (3d, <see cref="ICall.SendDtmfAsync"/> im <see cref="CallState.Ringing"/>).
///
/// Empirisch (Slice 3e, echter Asterisk): Plain-RTP kommt ~0,6 s nach dem 183 an, SDES-SRTP wegen der
/// Krypto-Setup-Latenz erst ~5,5 s — daher das 10-s-Fenster im Dialplan.
/// </summary>
[Trait("Category", "Interop")]
public sealed class AsteriskEarlyMediaInteropTests
{
    /// <summary>
    /// Beweist Pre-Answer-RTP-Empfang: Über <see cref="IPhoneLine.OutboundCallRinging"/> wird das
    /// Call-Handle im Ringing bezogen (nicht über den blockierenden Dial-Aufruf); dann wird gemessen,
    /// dass <c>RtpStatistics.PacketsReceived &gt; 0</c> wird, WÄHREND der Call noch nicht
    /// <see cref="CallState.Connected"/> ist. Plain RTP (<see cref="SrtpPolicy.Disabled"/>).
    /// </summary>
    [DockerRequiredFact]
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

        // Das Pre-Answer-Handle über das Ringing-Event beziehen, NICHT über den bis zum 200 OK
        // blockierenden Dial-Aufruf.
        var ringing = new TaskCompletionSource<ICall>(TaskCreationOptions.RunContinuationsAsynchronously);
        reg.Line!.OutboundCallRinging += (_, e) => ringing.TrySetResult(e.Call);
        var dialTask = client.DialAndWaitUntilConnectedAsync(
            reg.Line!, asterisk.CallTargetUri("earlymedia"), new DialWaitOptions { ConnectTimeout = TimeSpan.FromSeconds(20) });

        var call = await ringing.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Early-Media-RTP zählen, während der Call noch nicht Connected ist.
        uint early = 0;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTimeOffset.UtcNow < deadline && call.State != CallState.Connected)
        {
            if (call.RtpStatistics is { PacketsReceived: > 0 } r) { early = r.PacketsReceived; break; }
            await Task.Delay(100);
        }

        Assert.True(early > 0, $"Kein Early-Media vor Answer empfangen (State={call.State}).");

        var result = await dialTask;
        if (result.IsSuccess) await result.Call!.HangupAsync();
    }

    /// <summary>
    /// Beweist DTMF im early dialog (RFC 4733 telephone-event): Über
    /// <see cref="IPhoneLine.OutboundCallRinging"/> wird das Handle im Ringing bezogen, dann wird DTMF
    /// im <see cref="CallState.Ringing"/> gesendet — das darf nicht werfen (Slice 3d) und läuft über den
    /// bereits beim Ringing verdrahteten RTP-telephone-event-Pfad (Slice 3b), belegt durch den in der
    /// Early-Media-SDP verhandelten telephone-event-Payload-Type auf <see cref="ICall.MediaParameters"/>.
    ///
    /// Hinweis zur Nachweisführung (Slice 3e, measure-first): Ein voller Asterisk-seitiger
    /// DTMF-Roundtrip-Nachweis im early dialog ist fragil — die earlymedia-Extension hat im
    /// Early-Media-Fenster keine ziffernkonsumierende App aktiv, und das Container-Image loggt den
    /// empfangenen RTP-telephone-event nicht zuverlässig. Der robuste, deterministische Nachweis ist
    /// daher SDK-seitig: (1) <c>SendDtmfAsync</c> wirft im Ringing nicht und (2) der telephone-event-
    /// Payload-Type ist in der Early-Media-SDP verhandelt, sodass der RTP-telephone-event-Pfad
    /// <em>verfügbar</em> ist. NICHT abgedeckt (bewusst): der zur Laufzeit tatsächlich genommene Sendepfad
    /// — <c>SipCoreCallChannel.SendDtmfAsync</c> fällt bei einem RTP-Fehler still auf SIP INFO zurück — und
    /// der Peer-Empfang des DTMF im early dialog. Der Nachweis belegt „Send erlaubt + RTP-Pfad verhandelt",
    /// nicht „RTP-telephone-event vom Peer empfangen".
    /// </summary>
    [DockerRequiredFact]
    public async Task SendsDtmf_InEarlyDialog_BeforeAnswer()
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

        var ringing = new TaskCompletionSource<ICall>(TaskCreationOptions.RunContinuationsAsynchronously);
        reg.Line!.OutboundCallRinging += (_, e) => ringing.TrySetResult(e.Call);
        var dialTask = client.DialAndWaitUntilConnectedAsync(
            reg.Line!, asterisk.CallTargetUri("earlymedia"), new DialWaitOptions { ConnectTimeout = TimeSpan.FromSeconds(20) });

        var call = await ringing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CallState.Ringing, call.State);

        // Der RFC-4733-telephone-event ist schon in der Early-Media-SDP verhandelt → der DTMF-Sendepfad
        // ist RTP telephone-event (Slice 3b verdrahtet den Delegate beim Ringing), nicht SIP INFO.
        Assert.NotNull(call.MediaParameters);
        Assert.NotNull(call.MediaParameters!.TelephoneEventPayloadType);

        // Mehrere Ziffern im early dialog senden. Darf nicht werfen (Slice 3d) und State bleibt Ringing.
        foreach (var digit in "123")
        {
            if (call.State == CallState.Connected) break; // Answer kam früher als erwartet → früh raus
            await call.SendDtmfAsync(new DtmfTone(digit));
            Assert.True(
                call.State is CallState.Ringing or CallState.Connected,
                $"Unerwarteter State beim DTMF-Senden: {call.State}");
            await Task.Delay(200);
        }

        var result = await dialTask;
        if (result.IsSuccess) await result.Call!.HangupAsync();
    }

    /// <summary>
    /// SDES-Early-Media: Registrierung als 6002 (<c>media_encryption=sdes</c>) mit
    /// <see cref="SrtpPolicy.Required"/>. Belegt, dass der Offerer beim 183 key-vollständig ist — SRTP
    /// wird VOR dem 200 OK verhandelt (<c>IsSrtpNegotiated</c>/<c>RTP/SAVP</c> auf
    /// <see cref="ICall.MediaParameters"/> im Ringing) und verschlüsselte Early-Media-RTP fließt
    /// (entschlüsselt gezählt) VOR <see cref="CallState.Connected"/>.
    /// </summary>
    [DockerRequiredFact]
    public async Task ReceivesSdesEarlyMedia_BeforeAnswer()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        using var client = new VoipClient(new VoipConfiguration
        {
            UserAgent = "CalloraInteropTest/1.0",
            SrtpPolicy = SrtpPolicy.Required,
        });

        var reg = await client.ConnectAsync(
            new SipAccount
            {
                SipServer = asterisk.ContainerIpAddress,
                Port = 5060,
                Username = asterisk.SdesUsername,
                Password = asterisk.SdesPassword,
                Transport = DomainSipTransport.Udp,
            },
            new ConnectOptions { Timeout = TimeSpan.FromSeconds(20) });
        Assert.True(reg.IsSuccess, $"Registrierung fehlgeschlagen: Status={reg.Status}");

        var ringing = new TaskCompletionSource<ICall>(TaskCreationOptions.RunContinuationsAsynchronously);
        reg.Line!.OutboundCallRinging += (_, e) => ringing.TrySetResult(e.Call);
        var dialTask = client.DialAndWaitUntilConnectedAsync(
            reg.Line!, asterisk.CallTargetUri("earlymedia"), new DialWaitOptions { ConnectTimeout = TimeSpan.FromSeconds(20) });

        var call = await ringing.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // SDES-Verhandlung ist schon VOR dem 200 OK sichtbar (Early-Media-Parameter aus der 183-SDP).
        bool srtpBeforeAnswer = false;
        uint early = 0;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(14);
        while (DateTimeOffset.UtcNow < deadline && call.State != CallState.Connected)
        {
            if (!srtpBeforeAnswer && call.MediaParameters is { IsSrtpNegotiated: true } mp)
            {
                srtpBeforeAnswer = true;
                Assert.Equal("RTP/SAVP", mp.MediaProfile);
                Assert.False(string.IsNullOrEmpty(mp.SrtpSuite), "Keine SDES-Crypto-Suite gesetzt.");
            }
            if (call.RtpStatistics is { PacketsReceived: > 0 } r) { early = r.PacketsReceived; break; }
            await Task.Delay(100);
        }

        Assert.True(srtpBeforeAnswer, $"SRTP wurde nicht vor Answer verhandelt (State={call.State}, MP={call.MediaParameters}).");
        Assert.True(early > 0, $"Kein (SRTP-)Early-Media vor Answer empfangen (State={call.State}).");

        var result = await dialTask;
        if (result.IsSuccess) await result.Call!.HangupAsync();
    }

    // F011 Slice 2 (grün): Die auf einem provisorischen 183 getragene SDP ist über die öffentliche API
    // sichtbar (ICall.EarlyMediaSdp), getrennt vom finalen 200-OK-Answer.
    [DockerRequiredFact]
    public async Task EarlyMediaSdp_IsExposedOnCall_WhenProvisionalCarriesSdp()
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
            reg.Line!, asterisk.CallTargetUri("earlymedia"), new DialWaitOptions { ConnectTimeout = TimeSpan.FromSeconds(20) });
        Assert.True(result.IsSuccess, $"DialStatus: {result.Status}");
        var call = result.Call!;

        Assert.False(string.IsNullOrEmpty(call.EarlyMediaSdp), "Early-Media-SDP wurde nicht über die öffentliche API exponiert.");

        await call.HangupAsync();
    }
}
