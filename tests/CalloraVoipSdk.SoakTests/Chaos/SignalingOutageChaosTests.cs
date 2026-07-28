using CalloraVoipSdk.InteropHarness.Chaos;
using Xunit;

namespace CalloraVoipSdk.SoakTests.Chaos;

/// <summary>
/// CORE-011 chaos gate — Fault class 2 (signaling outage). The SIP registration loop must survive a registrar
/// that is unreachable — keep retrying (RFC 3261 re-REGISTER with back-off) without wedging or giving up — and
/// recover to <c>Registered</c> once the registrar returns. This is the graceful-degradation guarantee for a
/// SIP provider outage.
/// </summary>
public sealed class SignalingOutageChaosTests
{
    [Fact, Trait("Category", "Chaos")]
    public async Task Registration_survives_a_registrar_outage_and_recovers()
    {
        await using var harness = ChaosSipRegisterHarness.Start(initiallyFaulting: true);

        // Registrar unreachable from the start: the line must not register, but the loop keeps retrying
        // (a transient failure drives the RFC 3261 re-REGISTER back-off), not wedge or terminate.
        Assert.False(
            await harness.WaitForRegistrationsAsync(1, TimeSpan.FromSeconds(1.5)),
            "the line must not register while the registrar is unreachable");
        Assert.True(
            harness.RegisterAttempts >= 2,
            "the registration loop should keep retrying under the outage, not give up");

        // Registrar recovers → the next retry succeeds and the line registers.
        harness.SetRegistrarFault(faulting: false);
        Assert.True(
            await harness.WaitForRegistrationsAsync(1, TimeSpan.FromSeconds(3)),
            "the line must recover and register once the registrar returns");
    }
}
