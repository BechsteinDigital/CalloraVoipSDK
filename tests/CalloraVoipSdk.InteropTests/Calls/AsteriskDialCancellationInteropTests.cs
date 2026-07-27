using System.Globalization;
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
/// L4 (Docker-only) Regressionstest für den Caller-Cancellation-Bug: bei der Abbruch eines KLINGELNDEN
/// ausgehenden Calls meldete das SDK zwar Canceled/Terminated, sendete aber KEIN SIP-CANCEL — der
/// Asterisk-Channel blieb aktiv, weil <c>SipCallSignalingService.InviteAsync</c> die Session bei
/// Token-Cancellation entsorgte, bevor der HangupAsync-CANCEL-Pfad (RFC 3261 §9.1) griff. Der Fix lässt
/// die Session bei Cancellation stehen, sodass ein echter Wire-CANCEL rausgeht und der Peer-Channel
/// abgebaut wird.
///
/// Dieser Test beweist gegen ein echtes Asterisk (nicht Docker-frei mockbar) die zwei PO-Kern-Aussagen —
/// „erst sichtbares Ringing, dann Caller-Cancellation":
/// <list type="number">
///   <item><description>Der ausgehende Call erreicht sichtbar <see cref="CallState.Ringing"/> (über das
///     <see cref="IPhoneLine.OutboundCallRinging"/>-Event des Early-Dialogs), BEVOR abgebrochen wird.</description></item>
///   <item><description>Eine EXPLIZITE Caller-Cancellation (eigene <see cref="CancellationTokenSource"/>,
///     NICHT der ConnectTimeout) treibt den Call nach <see cref="CallState.Terminated"/> mit
///     <see cref="CallTerminationCategory.Canceled"/> (SIP 487, Antwort auf den gesendeten CANCEL) —
///     der SDK-seitige Beleg, dass ein Wire-CANCEL rausging.</description></item>
///   <item><description>Nach kurzem Settle hat Asterisk <b>NULL aktive Channels</b> (via CLI
///     <c>core show channels</c>) — der direkte Beweis, dass der klingelnde Channel wirklich per CANCEL
///     abgebaut wurde und nicht verwaist hängen blieb (der eigentliche Bug).</description></item>
/// </list>
///
/// REQUIRES DOCKER: <see cref="DockerRequiredFact"/> + <c>[Trait("Category", "Interop")]</c> — wird ohne
/// Docker-Daemon übersprungen und läuft nur in der Docker-Interop-CI-Lane, nicht im lokalen Unit-/
/// Integration-Lauf. Das Docker-freie Pendant ist <c>SipInviteCancellationTests</c>.
///
/// Media: <see cref="SrtpPolicy.Disabled"/> (Plain RTP) wie die übrigen Asterisk-Call-Tests — der
/// SRTP-lose Endpoint 6001 würde das Default-RTP/SAVP-Angebot mit 488 ablehnen (Audit-Fund F007).
/// Für den Ring-dann-Cancel-Ablauf ist die Medienverschlüsselung ohnehin orthogonal.
/// </summary>
[Trait("Category", "Interop")]
public sealed class AsteriskDialCancellationInteropTests
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
    public async Task RingingDial_CallerCancellation_SendsWireCancelAndLeavesNoActiveChannel()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        using var client = NewClient();
        var line = await RegisterAsync(asterisk, client);

        // Beobachtbares Ringing: das Early-Dialog-Event liefert den klingelnden Call-Handle, während
        // DialAsync noch auf das 200 OK wartet. Die 'noanswer'-Extension klingelt endlos (Ringing()/
        // Wait(3600)), sodass der Call sicher Ringing erreicht und dort verharrt, bis WIR abbrechen.
        var ringing = new TaskCompletionSource<ICall>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<OutboundCallRingingEventArgs> onRinging = (_, e) => ringing.TrySetResult(e.Call);
        line.OutboundCallRinging += onRinging;

        // Eigene Caller-CancellationTokenSource — NICHT die ConnectTimeout-Variante. Ein großzügiges
        // RingTimeout verhindert, dass das SDK selbst per RingTimeout abbricht, bevor unsere explizite
        // Cancellation greift; die Cancellation soll die alleinige Abbruchursache sein.
        using var callerCts = new CancellationTokenSource();
        var dialOptions = new DialOptions { RingTimeout = TimeSpan.FromSeconds(120) };

        try
        {
            // DialAsync läuft im Hintergrund: es kehrt erst zurück, wenn der Dial aufgelöst ist (hier
            // nach unserem CANCEL mit dem terminierten Call). Wir warten daher auf das Ringing-Event,
            // brechen dann ab und awaiten anschließend DialAsync auf den terminierten Call-Handle.
            var dialTask = line.DialAsync(asterisk.CallTargetUri("noanswer"), dialOptions, callerCts.Token);

            var call = await ringing.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(CallState.Ringing, call.State); // sichtbares Ringing vor der Cancellation

            // Explizite Caller-Cancellation des klingelnden Calls → das SDK muss einen echten Wire-CANCEL
            // senden (RFC 3261 §9.1) statt die Session still zu entsorgen (der abgesicherte Bug).
            callerCts.Cancel();

            var terminated = await dialTask.WaitAsync(TimeSpan.FromSeconds(15));

            // (a) Wire-CANCEL/Canceled: DialAsync gibt bei Ring-Cancellation den bereits terminierten Call
            // zurück; die Klassifikation Canceled (SIP 487 = Antwort auf unseren CANCEL) ist der
            // SDK-seitige Beleg, dass ein CANCEL rausging und Asterisk den INVITE mit 487 beendete.
            Assert.Equal(CallState.Terminated, terminated.State);
            var reason = terminated.TerminationReason;
            Assert.NotNull(reason);
            Assert.Equal(CallTerminationCategory.Canceled, reason!.Category);
            if (reason.SipStatusCode is not null)
                Assert.Equal(487, reason.SipStatusCode); // 487 Request Terminated (RFC 3261 §21.4.26)
        }
        finally
        {
            line.OutboundCallRinging -= onRinging;
        }

        // (b) NULL aktive Asterisk-Channels: der klingelnde Channel wurde durch den CANCEL wirklich
        // abgebaut. Dem Teardown kurz Zeit geben (Deadline-Polling statt fixem Sleep), da der CANCEL/
        // 487-Austausch und Asterisks Channel-Abbau asynchron zum lokalen Terminated-Übergang laufen.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        int activeChannels;
        do
        {
            activeChannels = await CountActiveChannelsAsync(asterisk);
            if (activeChannels == 0)
                break;
            await Task.Delay(250);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.Equal(0, activeChannels); // kein verwaister Asterisk-Channel nach der Caller-Cancellation
    }

    /// <summary>
    /// Fragt Asterisk über die CLI (<c>asterisk -rx "core show channels"</c>) nach der Anzahl aktiver
    /// Channels. Die Ausgabe endet mit einer Summenzeile wie <c>0 active channels</c>; diese wird geparst.
    /// </summary>
    private static async Task<int> CountActiveChannelsAsync(AsteriskContainer asterisk)
    {
        var output = await asterisk.ExecAsync("asterisk", "-rx", "core show channels");
        foreach (var rawLine in output.Split('\n'))
        {
            var trimmed = rawLine.Trim();
            var marker = trimmed.IndexOf("active channel", StringComparison.OrdinalIgnoreCase);
            if (marker <= 0)
                continue;

            var number = trimmed[..marker].Trim();
            if (int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
                return count;
        }

        // Keine parsebare Summenzeile → als Fehlbeleg behandeln, damit ein unerwartetes CLI-Format den
        // Test sichtbar fehlschlagen lässt statt still 0 (Grün) vorzutäuschen. Kein stummer Fallback.
        throw new InvalidOperationException(
            $"Konnte die aktive Channel-Anzahl nicht aus 'core show channels' parsen. Ausgabe:\n{output}");
    }
}
