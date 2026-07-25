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

        // Baseline: Media fließt auf dem originalen A↔B-Bridge.
        await bridged.RunBidirectionalMediaAsync(TimeSpan.FromSeconds(8));
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
}

/// <summary>Fährt die Attended-Transfer-Matrix gegen einen echten Asterisk.</summary>
[Trait("Category", "Interop")]
public sealed class AsteriskTwoLegTransferMatrix : TwoLegTransferMatrix
{
    protected override IPbxFixture CreatePbx(int bridgePairs = 1) => new AsteriskPbxFixture(bridgePairs);
}
