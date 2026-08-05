using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// [SIP] #158 P1-5/P1-7 (config follow-up): the signaling-layer resource limits are now configurable through
/// the public <see cref="VoipConfiguration"/>. These tests pin the public surface's defaults and its exposure.
/// </summary>
public sealed class SipSignalingHardeningConfigurationTests
{
    [Fact]
    public void Defaults_match_the_builtin_signaling_limits()
    {
        var config = new SipSignalingHardeningConfiguration();

        Assert.Equal(256, config.MaxConcurrentInboundSessions);
        Assert.Equal(32, config.MaxInboundSessionsPerRemote);
        Assert.Equal(TimeSpan.FromSeconds(180), config.InboundRingDeadline);
        Assert.Equal(8192, config.MaxServerTransactions);
        Assert.Equal(TimeSpan.FromSeconds(300), config.AbsoluteServerTransactionLifetime);
    }

    [Fact]
    public void VoipConfiguration_exposes_a_non_null_signaling_hardening_section_by_default()
    {
        var config = new VoipConfiguration();

        Assert.NotNull(config.SipSignalingHardening);
        Assert.Equal(256, config.SipSignalingHardening.MaxConcurrentInboundSessions);
    }
}
