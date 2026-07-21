using CalloraVoipSdk.InteropHarness.Metrics;
using CalloraVoipSdk.InteropHarness.Signaling;
using Xunit;

namespace CalloraVoipSdk.SoakTests.Soak;

/// <summary>
/// SipLineChannel-Refresh-Lifecycle-Soak gegen einen In-Memory-Fake-Registrar.
///
/// Scope-Ehrlichkeit: Dies ist KEIN vollständiger SIP-Soak. Es prüft ausschließlich den
/// Re-REGISTER-Refresh-Scheduler des echten <see cref="SipRegisterLoopHarness"/>-getriebenen
/// SipLineChannel über viele Zyklen — Zyklus-Kadenz, monotone CSeq, stabile Call-ID — plus
/// Ressourcenstabilität des Loops. Der Registrar ist ein aufzeichnender Fake (ISipRegistrationService),
/// daher KEIN Nachweis von Wire-Format, Transaktions-Layer, Digest-Auth, Transport oder Interop.
/// </summary>
public sealed class SipLineChannelRefreshLifecycleSoakTests
{
    [Fact, Trait("Category", "SoakShort")]
    public Task RefreshLifecycle_Short() => RunAsync(SoakProfile.Short);

    [Fact, Trait("Category", "SoakLong")]
    public Task RefreshLifecycle_Long() => RunAsync(SoakProfile.Long);

    private static async Task RunAsync(SoakProfile profile)
    {
        var sampler = new ResourceSampler();
        var samples = new List<ResourceSample>();

        IReadOnlyList<RegisterCycle> cycles;
        await using (var harness = SipRegisterLoopHarness.Start(effectiveExpiresSeconds: 2))
        {
            using var samplingCts = new CancellationTokenSource();
            var sampling = SampleUntilAsync(sampler, samples, samplingCts.Token);

            cycles = await harness.RunAsync(profile.Duration);

            samplingCts.Cancel();
            await sampling;

            Assert.True(harness.ReachedRegistered, "LineState.Registered nie erreicht.");
        }

        // Refresh-Verhalten: genügend Zyklen, monoton steigende CSeq, stabile Call-ID über den Loop.
        var minCycles = profile.Duration.TotalSeconds <= 5 ? 2 : 5;
        Assert.True(cycles.Count >= minCycles, $"Zu wenige Re-REGISTER-Zyklen: {cycles.Count}");

        for (var i = 1; i < cycles.Count; i++)
            Assert.True(cycles[i].StartCSeq > cycles[i - 1].StartCSeq,
                $"CSeq nicht monoton: {cycles[i - 1].StartCSeq} → {cycles[i].StartCSeq}");

        for (var i = 1; i < cycles.Count; i++)
            Assert.Equal("soak-call-id", cycles[i].ExistingCallId);

        // Ressourcenreihe: der Refresh-Loop darf über viele Zyklen weder Speicher noch Threads/FDs
        // lecken. Nur beim Long-Profil (genug Sockel für Regression/Plateau); Short bleibt reiner Smoke.
        if (samples.Count < 10)
            return;

        var managed = TrendAssertions.NoUpwardSlope(
            samples, s => s.ManagedBytes, maxSlopePerSample: 20_000, "ManagedBytes");
        Assert.False(managed.HasDrift, managed.Detail);

        var threads = ResourcePlateauAssertions.WithinPlateau(
            samples, s => s.ThreadCount, absoluteTolerance: 2, "ThreadCount");
        Assert.False(threads.Exceeded, threads.Detail);

        var sockets = ResourcePlateauAssertions.WithinPlateau(
            samples, s => s.SocketDescriptorCount, absoluteTolerance: 2, "SocketDescriptorCount");
        Assert.False(sockets.Exceeded, sockets.Detail);

        var fds = ResourcePlateauAssertions.WithinPlateau(
            samples, s => s.FileDescriptorCount, absoluteTolerance: 8, "FileDescriptorCount");
        Assert.False(fds.Exceeded, fds.Detail);
    }

    /// <summary>Erfasst nach kurzer Einschwing-Zeit alle 500 ms ein Ressourcen-Sample bis Abbruch.</summary>
    private static async Task SampleUntilAsync(
        ResourceSampler sampler, List<ResourceSample> samples, CancellationToken ct)
    {
        try
        {
            await Task.Delay(250, ct);
            while (!ct.IsCancellationRequested)
            {
                samples.Add(sampler.Capture());
                await Task.Delay(500, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Erwartetes Ende der Lauf-Dauer.
        }
    }
}
