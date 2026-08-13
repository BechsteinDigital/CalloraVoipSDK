using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Domain.Security;
using CalloraVoipSdk.InteropTests.Media;
using CalloraVoipSdk.InteropTests.Pbx;
using Xunit;

namespace CalloraVoipSdk.InteropTests.Calls;

/// <summary>
/// Call-Transfer (REFER, RFC 3515) gegen echten Asterisk. Blind Transfer: der SDK weist den Peer per
/// REFER an, den Call zu einem anderen Ziel umzuleiten, und gibt den eigenen Call frei. Attended
/// Transfer verbindet einen aktiven mit einem Beratungs-Call. Plain RTP (<see cref="SrtpPolicy.Disabled"/>).
/// </summary>
[Trait("Category", "Interop")]
public sealed class AsteriskTransferInteropTests
{
    // Beide Transfer-Tests brauchen einen GEBRÜCKTEN Call, nicht einen, der in einer Asterisk-
    // Applikation hängt (#256). Ein Kanal in Milliwatt()/Echo() hat keine Gegenstelle, die sich
    // umbrücken ließe — Asterisk beantwortet den REFER zwar mit 202, meldet dann aber per NOTIFY
    // "SIP/2.0 400 Bad Request" (Subscription-State: terminated;reason=noresource) und führt den
    // Transfer nie aus. Vorher liefen diese Tests gegen die answer-Extension und galten als grün,
    // weil nur das 202 geprüft wurde: sie hätten den Fehlschlag gar nicht sehen können.
    [DockerRequiredFact]
    public async Task BlindTransfer_IsAcceptedAndReleasesCall()
    {
        await using var pbx = new AsteriskPbxFixture(1);
        await pbx.StartAsync();
        await using var bridged = await TwoLegBridgedCall.StartAsync(pbx);

        Assert.Equal(CallState.Connected, bridged.CallerCall.State);

        // REFER an den Peer: den gebrückten Call blind zur Media-Playback-Extension umleiten.
        // Erfolg = 202 + NOTIFY(200), der lokale Call wird freigegeben.
        await bridged.CallerCall.BlindTransferAsync(pbx.MediaPlaybackUri);

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8);
        while (bridged.CallerCall.State != CallState.Terminated && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(100);

        // Terminated heißt hier belegt "der Transfer wurde ausgeführt": Call.BlindTransferAsync setzt
        // diesen Zustand nur, wenn das NOTIFY einen Erfolg gemeldet hat (RFC 3515 §2.4.4).
        Assert.Equal(CallState.Terminated, bridged.CallerCall.State);

        // Die Gegenstelle bleibt verbunden — sie wurde umgebrückt, nicht abgeräumt.
        Assert.Equal(CallState.Connected, bridged.CalleeCall.State);
    }

    [DockerRequiredFact]
    public async Task AttendedTransfer_BridgesConsultationCall()
    {
        await using var pbx = new AsteriskPbxFixture(1);
        await pbx.StartAsync();
        await using var bridged = await TwoLegBridgedCall.StartAsync(pbx);

        var consultation = await bridged.DialCallerConsultationAsync(
            pbx.MediaPlaybackUri, TimeSpan.FromSeconds(10));
        Assert.Equal(CallState.Connected, bridged.CallerCall.State);
        Assert.Equal(CallState.Connected, consultation.State);

        // Verbindet den Peer des primären Calls mit dem Peer des Beratungs-Calls (REFER mit Replaces).
        var ok = await bridged.CallerCall.AttendedTransferAsync(consultation);
        Assert.True(ok, "Attended-Transfer wurde nicht bestätigt.");

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8);
        while (bridged.CallerCall.State != CallState.Terminated && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(100);
        Assert.Equal(CallState.Terminated, bridged.CallerCall.State);
    }
}
