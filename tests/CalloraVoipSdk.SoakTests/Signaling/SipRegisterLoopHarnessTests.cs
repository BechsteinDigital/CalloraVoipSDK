using CalloraVoipSdk.InteropHarness.Signaling;

namespace CalloraVoipSdk.SoakTests.Signaling;

public sealed class SipRegisterLoopHarnessTests
{
    [Fact]
    public async Task Run_ShortExpires_RefreshLoopFiresMultipleRegisters()
    {
        await using var harness = SipRegisterLoopHarness.Start(effectiveExpiresSeconds: 2);

        var cycles = await harness.RunAsync(TimeSpan.FromSeconds(6));

        Assert.True(cycles.Count >= 2, $"Zu wenige Register-Zyklen: {cycles.Count}");
        Assert.True(harness.ReachedRegistered, "LineState.Registered wurde nie erreicht.");
    }
}
