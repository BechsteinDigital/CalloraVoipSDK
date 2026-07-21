using Xunit;

namespace CalloraVoipSdk.InteropTests.Asterisk;

[Trait("Category", "Interop")]
public sealed class AsteriskContainerSmokeTests
{
    [DockerRequiredFact]
    public async Task Asterisk_StartsAndBecomesReady_AndExposesMappedUdpPort()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();

        Assert.True(asterisk.SipUdpPort > 0, "Kein gemappter SIP/UDP-Port.");
    }
}
