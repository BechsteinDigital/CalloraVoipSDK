using Xunit;

namespace CalloraVoipSdk.InteropTests.Asterisk;

[Trait("Category", "Interop")]
public sealed class AsteriskContainerSmokeTests
{
    [DockerRequiredFact]
    public async Task Asterisk_StartsAndBecomesReady_AndExposesSipEndpoint()
    {
        await using var asterisk = new AsteriskContainer();
        await asterisk.StartAsync();

        Assert.True(asterisk.SipUdpPort > 0, "Kein gemappter SIP/UDP-Port.");
        if (asterisk.UsesBrowserSafeNetwork)
        {
            Assert.Equal("127.0.0.1", asterisk.Host);
            Assert.Equal("127.0.0.1", asterisk.ContainerIpAddress);
            Assert.Equal((ushort)5060, asterisk.SipUdpPort);
        }
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void BrowserSafeMode_RecognizesExplicitValues(string? value, bool expected) =>
        Assert.Equal(expected, AsteriskContainer.IsBrowserSafeModeRequested(value));
}
