using CalloraVoipSdk.Core.Domain.Lines;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// Argument-validation contract for <see cref="VoipClient.DialAndWaitUntilConnectedAsync"/> (issue #18.8):
/// the convenience dial overload rejects a null line and a null-or-blank target, matching the guard on
/// <see cref="VoipClient.ConnectAsync"/>. The added guards fail fast at the facade boundary; the
/// orchestrator validates the same arguments one layer deeper as a backstop, so this test pins the
/// observable exception contract rather than which layer raised it.
/// </summary>
public sealed class VoipClientDialArgumentValidationTests
{
    private static VoipConfiguration TestConfiguration() => new()
    {
        UserAgent = "CalloraVoipSdk.Client.Tests/1.0",
        EnableAutomaticAudioDeviceSelection = false,
    };

    private static SipAccount TestAccount() => new()
    {
        Username = "alice",
        SipServer = "192.0.2.1", // TEST-NET-1: never routes, so the background REGISTER stays inert
    };

    [Fact]
    public async Task Null_line_is_rejected()
    {
        using var client = new VoipClient(TestConfiguration());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.DialAndWaitUntilConnectedAsync(null!, "sip:bob@example.com"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Null_or_blank_target_is_rejected(string? targetUri)
    {
        using var client = new VoipClient(TestConfiguration());
        // A non-null line makes the blank-target case exercise the target guard rather than the null-line
        // guard. Register is synchronous and returns the line before the REGISTER completes.
        var line = client.Lines.Register(TestAccount());

        // ThrowsAny: a null target throws ArgumentNullException, a blank one ArgumentException — both are the
        // ArgumentException family the guard is expected to raise.
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => client.DialAndWaitUntilConnectedAsync(line, targetUri!));
    }
}
