using CalloraVoipSdk.InteropHarness.Signaling;
using Xunit;

namespace CalloraVoipSdk.SoakTests.Soak;

public sealed class SignalingRefreshSoakTests
{
    [Fact, Trait("Category", "SoakShort")]
    public Task LongRegisterLoop_Short() => RunAsync(SoakProfile.Short);

    [Fact, Trait("Category", "SoakLong")]
    public Task LongRegisterLoop_Long() => RunAsync(SoakProfile.Long);

    private static async Task RunAsync(SoakProfile profile)
    {
        IReadOnlyList<RegisterCycle> cycles;
        await using (var harness = SipRegisterLoopHarness.Start(effectiveExpiresSeconds: 2))
        {
            cycles = await harness.RunAsync(profile.Duration);
            Assert.True(harness.ReachedRegistered, "LineState.Registered nie erreicht.");
        }

        var minCycles = profile.Duration.TotalSeconds <= 5 ? 2 : 5;
        Assert.True(cycles.Count >= minCycles, $"Zu wenige Re-REGISTER-Zyklen: {cycles.Count}");

        for (var i = 1; i < cycles.Count; i++)
            Assert.True(cycles[i].StartCSeq > cycles[i - 1].StartCSeq,
                $"CSeq nicht monoton: {cycles[i - 1].StartCSeq} → {cycles[i].StartCSeq}");

        for (var i = 1; i < cycles.Count; i++)
            Assert.Equal("soak-call-id", cycles[i].ExistingCallId);
    }
}
