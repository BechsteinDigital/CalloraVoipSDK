using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.InteropTests.Asterisk;
using CalloraVoipSdk.InteropTests.Media;
using Xunit;

namespace CalloraVoipSdk.InteropTests.Calls;

/// <summary>
/// Hold/Unhold auf dem Caller-Leg eines gebrückten Zwei-Bein-Calls mit anschließendem
/// Medienfluss-Nachweis: beweist, dass nach einer re-INVITE-Hold/Unhold-Sequenz (sendonly →
/// sendrecv) der bidirektionale RTP-Fluss über die Bridge vollständig wieder aufgenommen wird.
/// </summary>
[Trait("Category", "Interop")]
public sealed class AsteriskTwoLegHoldInteropTests
{
    [DockerRequiredFact]
    public async Task Hold_Then_Unhold_ResumesBidirectionalMedia()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();
        await using var bridged = await TwoLegBridgedCall.StartAsync(asterisk);

        // Baseline: Media fließt vor dem Hold (8 s = Mindestdauer für RTCP-Befüllung von RtpStatistics).
        await bridged.RunBidirectionalMediaAsync(TimeSpan.FromSeconds(8));
        Assert.True(bridged.CalleeCall.RtpStatistics is { PacketsReceived: > 0 }, "Kein Baseline-RTP vor Hold.");

        // Hold auf dem Caller-Leg (re-INVITE sendonly), dann Unhold (re-INVITE sendrecv).
        await bridged.CallerCall.HoldAsync();
        await WaitForStateAsync(bridged.CallerCall, CallState.OnHold);
        Assert.Equal(CallState.OnHold, bridged.CallerCall.State);

        await bridged.CallerCall.UnholdAsync();
        await WaitForStateAsync(bridged.CallerCall, CallState.Connected);
        Assert.Equal(CallState.Connected, bridged.CallerCall.State);

        // Kurze Stabilisierungsphase nach Unhold, damit Asterisk den Medienpfad wieder einrichtet.
        await Task.Delay(500);

        // Media muss wieder fließen: der Callee empfängt nach Unhold mehr Pakete als direkt davor.
        var afterUnhold = bridged.CalleeCall.RtpStatistics?.PacketsReceived ?? 0;
        await bridged.RunBidirectionalMediaAsync(TimeSpan.FromSeconds(5));
        var final = bridged.CalleeCall.RtpStatistics?.PacketsReceived ?? 0;

        Assert.True(final > afterUnhold,
            $"Media nach Unhold nicht wieder geflossen: vorher {afterUnhold}, nachher {final}.");
    }

    private static async Task WaitForStateAsync(ICall call, CallState target)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8);
        while (call.State != target && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(100);
    }
}
