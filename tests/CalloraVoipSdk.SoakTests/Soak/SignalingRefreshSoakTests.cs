using CalloraVoipSdk.InteropHarness.Metrics;
using CalloraVoipSdk.InteropHarness.Signaling;

namespace CalloraVoipSdk.SoakTests.Soak;

public sealed class SignalingRefreshSoakTests
{
    [Fact]
    public async Task LongRegisterLoop_CallIdStable_CSeqMonotonic_NoLeak_NoSilentDrop()
    {
        var sampler = new ResourceSampler();
        var before = sampler.Capture();

        IReadOnlyList<RegisterCycle> cycles;
        await using (var harness = SipRegisterLoopHarness.Start(effectiveExpiresSeconds: 2))
        {
            cycles = await harness.RunAsync(TimeSpan.FromSeconds(20));

            // Kein Silent-Drop: der Loop hat wiederholt registriert und den Registered-Zustand erreicht.
            Assert.True(harness.ReachedRegistered, "LineState.Registered nie erreicht.");
        }

        Assert.True(cycles.Count >= 5, $"Zu wenige Re-REGISTER-Zyklen: {cycles.Count}");

        // CSeq streng monoton steigend (RFC 3261 §10.2.4).
        for (var i = 1; i < cycles.Count; i++)
            Assert.True(cycles[i].StartCSeq > cycles[i - 1].StartCSeq,
                $"CSeq nicht monoton: {cycles[i - 1].StartCSeq} → {cycles[i].StartCSeq}");

        // Call-ID stabil ab dem 2. Zyklus (erster ist frisch/null, danach wiederverwendet).
        for (var i = 1; i < cycles.Count; i++)
            Assert.Equal("soak-call-id", cycles[i].ExistingCallId);

        // Kein Ressourcen-Leak über die Zyklen (Sockel nach Dispose vs. vor Start).
        var after = sampler.Capture();
        var trend = TrendAssertions.NoUpwardDrift(
            new[] { before, after }, s => s.ManagedBytes, toleranceRatio: 0.50);
        Assert.False(trend.HasDrift, trend.Detail);
    }
}
