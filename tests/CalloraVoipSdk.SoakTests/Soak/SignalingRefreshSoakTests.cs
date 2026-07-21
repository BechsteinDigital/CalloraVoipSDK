using CalloraVoipSdk.InteropHarness.Signaling;

namespace CalloraVoipSdk.SoakTests.Soak;

public sealed class SignalingRefreshSoakTests
{
    [Fact]
    public async Task LongRegisterLoop_CallIdStable_CSeqMonotonic_NoSilentDrop()
    {
        IReadOnlyList<RegisterCycle> cycles;
        await using (var harness = SipRegisterLoopHarness.Start(effectiveExpiresSeconds: 2))
        {
            cycles = await harness.RunAsync(TimeSpan.FromSeconds(20));
            Assert.True(harness.ReachedRegistered, "LineState.Registered nie erreicht.");
        }

        Assert.True(cycles.Count >= 5, $"Zu wenige Re-REGISTER-Zyklen: {cycles.Count}");

        for (var i = 1; i < cycles.Count; i++)
            Assert.True(cycles[i].StartCSeq > cycles[i - 1].StartCSeq,
                $"CSeq nicht monoton: {cycles[i - 1].StartCSeq} → {cycles[i].StartCSeq}");

        for (var i = 1; i < cycles.Count; i++)
            Assert.Equal("soak-call-id", cycles[i].ExistingCallId);
    }
}
