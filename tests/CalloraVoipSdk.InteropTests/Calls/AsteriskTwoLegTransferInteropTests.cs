using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.InteropTests.Media;
using CalloraVoipSdk.InteropTests.Pbx;
using Xunit;

namespace CalloraVoipSdk.InteropTests.Calls;

/// <summary>
/// Abstrakte Basis: Attended-Transfer über einen gebrückten Zwei-Bein-Call: A (SDK) ↔ PBX ↔ B (SDK).
/// A wählt einen Beratungs-Call zur Media-Playback-Extension und führt einen Attended-Transfer durch.
/// Der PBX brückt daraufhin B ↔ Media-Playback; A's Calls werden freigegeben.
/// Nachweis: B empfängt nach dem Transfer weiterhin RTP vom neuen Ziel.
/// </summary>
public abstract class TwoLegTransferMatrix
{
    protected abstract IPbxFixture CreatePbx(int bridgePairs = 1);

    [DockerRequiredFact]
    public async Task AttendedTransfer_BridgesCalleeToNewTarget_WithMediaFlow()
    {
        await using var pbx = CreatePbx();
        await pbx.StartAsync();
        await using var bridged = await TwoLegBridgedCall.StartAsync(pbx);

        // Media läuft DURCHGEHEND, nicht nur für ein Messfenster (#256). Vorher pumpte der Harness
        // 8 Sekunden und schwieg dann, während Beratungs-Call und Transfer liefen. Überschritt diese
        // Stille die 15 Sekunden aus MediaSupervisionOptions.InboundMediaTimeout, beendete der eigene
        // Media-Supervisor den Callee-Call — kein BYE von der PBX, der Trace zeigt nur das reguläre
        // BYE auf A's Beratungs-Leg. Der Test maß damit den Timeout statt den Transfer. Ein echtes
        // Endgerät sendet durchgehend RTP; genau das tut der Loop hier.
        await using var media = bridged.StartBidirectionalMedia();

        // Baseline: Media fließt auf dem originalen A↔B-Bridge.
        var baselineDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while ((bridged.CalleeCall.RtpStatistics?.PacketsReceived ?? 0) == 0
               && DateTimeOffset.UtcNow < baselineDeadline)
        {
            await Task.Delay(100);
        }

        Assert.True(bridged.CalleeCall.RtpStatistics is { PacketsReceived: > 0 }, "Kein Baseline-RTP.");

        // A wählt einen Beratungs-Call zur Media-Playback-Extension.
        // Der PBX spielt dort endlos Ton → verbundene Seite empfängt dauerhaft RTP.
        var consultation = await bridged.DialCallerConsultationAsync(
            pbx.MediaPlaybackUri, TimeSpan.FromSeconds(10));

        var before = bridged.CalleeCall.RtpStatistics?.PacketsReceived ?? 0;

        // Attended-Transfer: PBX brückt B ↔ Media-Playback und gibt A's beide Calls frei.
        var ok = await bridged.CallerCall.AttendedTransferAsync(consultation);
        Assert.True(ok, "Attended-Transfer wurde nicht bestätigt.");

        // Nach dem Transfer empfängt B Media vom neuen Ziel (Media-Playback).
        // Der Zähler muss innerhalb der Deadline weiter steigen (re-INVITE/Bridge-Neuaufbau dauert einen Moment).
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        uint after = before;
        while (DateTimeOffset.UtcNow < deadline)
        {
            after = bridged.CalleeCall.RtpStatistics?.PacketsReceived ?? 0;
            if (after > before) break;
            await Task.Delay(500);
        }
        Assert.True(after > before,
            $"Nach Attended-Transfer floss keine Media zum Callee: vorher {before}, nachher {after}.");
    }

    /// <summary>
    /// #256/#261 gegen die echte PBX. Der Test oben pumpt seit dem Harness-Fix durchgehend Media und kann die
    /// Ursache des Flakes daher nicht mehr auslösen; dieser stellt sie wieder her — Baseline, dann Sendepause
    /// deutlich über der alten 15-s-Schwelle — und prüft den ausgelieferten Zustand:
    /// <list type="number">
    /// <item>Media-Stille wird als <c>MediaFlowChanged</c> gemeldet.</item>
    /// <item>Der Call überlebt sie: mit den Defaults beendet das SDK einen Call nicht wegen Stille.</item>
    /// <item>Nach dem Transfer fließt wieder Media und die Wiederaufnahme wird gemeldet.</item>
    /// </list>
    /// Vor #261 (RTP-only, 15 s, an by default) war Schritt 2 rot — genau das ist in #256 passiert. Die
    /// mitgeführte RTCP-Reihe belegt zugleich, warum der Teardown ab Werk aus ist: <c>rtcp_rx</c> friert in
    /// der Stille ein, die PBX liefert also kein Lebenszeichen, an dem ein Teardown sich festmachen könnte.
    /// </summary>
    [DockerRequiredFact]
    public async Task AttendedTransfer_SurvivesMediaSilence_WithTheShippedDefaults()
    {
        // Deutlich über der alten 15-s-Schwelle — der Fall, der #256 rot machte.
        var silence = TimeSpan.FromSeconds(20);

        await using var pbx = CreatePbx();
        await pbx.StartAsync();
        await using var bridged = await TwoLegBridgedCall.StartAsync(pbx);

        var flowEvents = new List<bool>();
        var gate = new object();
        bridged.CalleeCall.MediaFlowChanged += (_, e) =>
        {
            lock (gate) flowEvents.Add(e.InboundMediaFlowing);
        };

        // Baseline: Media fließt auf dem originalen A↔B-Bridge.
        var media = bridged.StartBidirectionalMedia();
        var baselineDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while ((bridged.CalleeCall.RtpStatistics?.PacketsReceived ?? 0) == 0
               && DateTimeOffset.UtcNow < baselineDeadline)
        {
            await Task.Delay(100);
        }
        Assert.True(bridged.CalleeCall.RtpStatistics is { PacketsReceived: > 0 }, "Kein Baseline-RTP.");

        // Sendepause — der Zustand aus #256 (Umbrück-Lücke) und der Alltag mit VAD/Comfort Noise.
        await media.DisposeAsync();

        // Die entscheidende Messung: kommt in der Stille weiter RTCP von der PBX? Nur dann kann die
        // Liveness-Regel den Call halten. Die Reihe wandert in die Fehlermeldung, damit ein Rot die
        // Ursache nennt statt sie offenzulassen.
        var rtcpSeries = new List<string>();
        var silenceDeadline = DateTimeOffset.UtcNow + silence;
        while (DateTimeOffset.UtcNow < silenceDeadline)
        {
            var q = bridged.CalleeCall.QualitySnapshot;
            rtcpSeries.Add($"{(int)(silence - (silenceDeadline - DateTimeOffset.UtcNow)).TotalSeconds}s:"
                           + $"rtcp_rx={q.RtcpPacketsReceived},rtp_rx={bridged.CalleeCall.RtpStatistics?.PacketsReceived ?? 0}");
            await Task.Delay(2000);
        }

        // (2) Der Call lebt die Stille durch. Bei Rot nennt die Meldung beide Legs samt Grund: der Callee kann
        // auch indirekt sterben (Caller-Leg fällt weg → PBX bricht die Brücke ab → BYE an B).
        Assert.True(
            bridged.CalleeCall.State != CallState.Terminated,
            $"Callee-Call wurde während {silence.TotalSeconds}s Media-Stille beendet, obwohl der Teardown "
            + "ab Werk aus ist. "
            + $"Callee: {Describe(bridged.CalleeCall)} | Caller: {Describe(bridged.CallerCall)}. "
            + $"RTCP/RTP am Callee während der Stille: [{string.Join(" ", rtcpSeries)}].");

        // (1) Und die Stille wurde gemeldet, statt verschluckt zu werden.
        lock (gate)
            Assert.Contains(false, flowEvents);

        // Das Endgerät nimmt das Senden wieder auf — die Sprechpause ist vorbei. Ohne das bliebe der
        // Bridge-Pfad stumm und der Transfer unten hätte nichts zu messen.
        await using var resumed = bridged.StartBidirectionalMedia();
        var resumeDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < resumeDeadline)
        {
            lock (gate)
                if (flowEvents.Skip(flowEvents.IndexOf(false) + 1).Contains(true)) break;
            await Task.Delay(200);
        }
        lock (gate)
            Assert.Contains(true, flowEvents.Skip(flowEvents.IndexOf(false) + 1)); // Wiederaufnahme gemeldet

        // (3) Transfer wie im Haupttest: danach fließt wieder Media zum Callee.
        var consultation = await bridged.DialCallerConsultationAsync(
            pbx.MediaPlaybackUri, TimeSpan.FromSeconds(10));
        var before = bridged.CalleeCall.RtpStatistics?.PacketsReceived ?? 0;

        Assert.True(await bridged.CallerCall.AttendedTransferAsync(consultation), "Attended-Transfer wurde nicht bestätigt.");

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        uint after = before;
        while (DateTimeOffset.UtcNow < deadline)
        {
            after = bridged.CalleeCall.RtpStatistics?.PacketsReceived ?? 0;
            if (after > before) break;
            await Task.Delay(500);
        }

        Assert.True(after > before,
            $"Nach der Stille floss keine Media zum Callee: vorher {before}, nachher {after}.");
    }

    /// <summary>
    /// Die Gegenrichtung zu <see cref="AttendedTransfer_SurvivesMediaSilence_WithTheShippedDefaults"/>: wer den
    /// Teardown einschaltet, bekommt ihn — und er ist als solcher erkennbar. Beweist über die echte PBX, dass
    /// der Termination-Grund am Call ankommt (der Kanal terminiert aus seinem eigenen <c>HangupAsync</c>
    /// heraus, der spezifische Grund muss also vorher geparkt werden, #261).
    /// </summary>
    [DockerRequiredFact]
    public async Task A_configured_media_timeout_ends_the_call_with_a_media_timeout_reason()
    {
        var livenessTimeout = TimeSpan.FromSeconds(8);

        await using var pbx = CreatePbx();
        await pbx.StartAsync();
        await using var bridged = await TwoLegBridgedCall.StartAsync(
            pbx,
            inboundMediaTimeout: livenessTimeout,
            mediaSilenceNotifyAfter: TimeSpan.FromSeconds(3));

        var media = bridged.StartBidirectionalMedia();
        var baselineDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while ((bridged.CalleeCall.RtpStatistics?.PacketsReceived ?? 0) == 0
               && DateTimeOffset.UtcNow < baselineDeadline)
        {
            await Task.Delay(100);
        }
        Assert.True(bridged.CalleeCall.RtpStatistics is { PacketsReceived: > 0 }, "Kein Baseline-RTP.");

        await media.DisposeAsync();

        // Beide Legs verstummen; welches zuerst abräumt, entscheidet das Timing — das andere bekommt danach
        // ein BYE der PBX. Geprüft wird daher, dass MINDESTENS ein Leg den eigenen Media-Timeout-Grund trägt.
        var deadline = DateTimeOffset.UtcNow + livenessTimeout + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < deadline
               && bridged.CalleeCall.State != CallState.Terminated
               && bridged.CallerCall.State != CallState.Terminated)
        {
            await Task.Delay(500);
        }

        var reasons = new[] { bridged.CalleeCall.TerminationReason, bridged.CallerCall.TerminationReason };
        Assert.True(
            reasons.Any(r => r?.ReasonPhrase?.Contains("Media timeout", StringComparison.Ordinal) == true
                             && r.Category == CallTerminationCategory.Failed
                             && r.TerminatedBy == CallTerminatedBy.Local),
            "Kein Leg trägt den Media-Timeout-Grund. "
            + $"Callee: {Describe(bridged.CalleeCall)} | Caller: {Describe(bridged.CallerCall)}.");
    }

    // Zustand plus vollständiger Termination-Grund eines Legs — ein SDK-seitiger Media-Timeout trägt einen
    // eigenen ReasonPhrase, ein von der PBX zugestelltes BYE nicht.
    private static string Describe(ICall call)
    {
        var reason = call.TerminationReason;
        return reason is null
            ? $"{call.State}, kein TerminationReason"
            : $"{call.State}, {reason.Category}/{reason.TerminatedBy}, "
              + $"SIP {reason.SipStatusCode?.ToString() ?? "—"}, \"{reason.ReasonPhrase ?? "—"}\"";
    }
}

/// <summary>Fährt die Attended-Transfer-Matrix gegen einen echten Asterisk.</summary>
[Trait("Category", "Interop")]
public sealed class AsteriskTwoLegTransferMatrix : TwoLegTransferMatrix
{
    protected override IPbxFixture CreatePbx(int bridgePairs = 1) => new AsteriskPbxFixture(bridgePairs);
}

/// <summary>Fährt die Attended-Transfer-Matrix gegen echtes FreeSWITCH.</summary>
[Trait("Category", "Interop"), Trait("Category", "InteropFreeSwitch")]
public sealed class FreeSwitchTwoLegTransferMatrix : TwoLegTransferMatrix
{
    protected override IPbxFixture CreatePbx(int bridgePairs = 1) => new FreeSwitchPbxFixture(bridgePairs);
}
