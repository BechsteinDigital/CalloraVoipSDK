using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.InteropTests.Asterisk;
using CalloraVoipSdk.InteropTests.Media;
using Xunit;

namespace CalloraVoipSdk.InteropTests.Calls;

/// <summary>
/// Attended-Transfer über einen gebrückten Zwei-Bein-Call: A (SDK, 6001) ↔ Asterisk ↔ B (SDK, 6003).
/// A wählt einen Beratungs-Call zur Milliwatt-Extension ('answer') und führt einen Attended-Transfer durch.
/// Asterisk brückt daraufhin B ↔ Milliwatt; A's Calls werden freigegeben.
/// Nachweis: B empfängt nach dem Transfer weiterhin RTP vom neuen Ziel.
/// </summary>
[Trait("Category", "Interop")]
public sealed class AsteriskTwoLegTransferInteropTests
{
    [DockerRequiredFact]
    public async Task AttendedTransfer_BridgesCalleeToNewTarget_WithMediaFlow()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        await using var bridged = await TwoLegBridgedCall.StartAsync(asterisk);

        // Baseline: Media fließt auf dem originalen A↔B-Bridge.
        await bridged.RunBidirectionalMediaAsync(TimeSpan.FromSeconds(8));
        Assert.True(bridged.CalleeCall.RtpStatistics is { PacketsReceived: > 0 }, "Kein Baseline-RTP.");

        // A wählt einen Beratungs-Call zur Milliwatt-Extension 'answer'.
        // Asterisk spielt dort endlos Ton → verbundene Seite empfängt dauerhaft RTP.
        var consultation = await bridged.DialCallerConsultationAsync(
            asterisk.CallTargetUri("answer"), TimeSpan.FromSeconds(10));

        var before = bridged.CalleeCall.RtpStatistics?.PacketsReceived ?? 0;

        // Attended-Transfer: Asterisk brückt B ↔ Milliwatt und gibt A's beide Calls frei.
        var ok = await bridged.CallerCall.AttendedTransferAsync(consultation);
        Assert.True(ok, "Attended-Transfer wurde nicht bestätigt.");

        // Nach dem Transfer empfängt B Media vom neuen Ziel (Milliwatt).
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
}
