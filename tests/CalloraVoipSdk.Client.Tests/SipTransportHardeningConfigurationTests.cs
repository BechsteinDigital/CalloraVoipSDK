using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// [SIP] #158 P1-3/P1-4 (config follow-up): the inbound-listener admission and slowloris limits are now
/// configurable through the public <see cref="VoipConfiguration"/>. These tests pin the public surface's
/// defaults and its mapping onto the internal transport options.
/// </summary>
public sealed class SipTransportHardeningConfigurationTests
{
    [Fact]
    public void Defaults_match_the_builtin_transport_limits()
    {
        var config = new SipTransportHardeningConfiguration();

        Assert.Equal(1024, config.MaxConcurrentInboundConnections);
        Assert.Equal(32, config.MaxInboundConnectionsPerRemote);
        Assert.Equal(4096, config.MaxEndpointHintEntries);
        Assert.Equal(TimeSpan.FromSeconds(10), config.HandshakeTimeout);
    }

    [Fact]
    public void VoipConfiguration_exposes_a_non_null_hardening_section_by_default()
    {
        var config = new VoipConfiguration();

        Assert.NotNull(config.SipTransportHardening);
        Assert.Equal(1024, config.SipTransportHardening.MaxConcurrentInboundConnections);
    }

    [Fact]
    public void ToTransportOptions_maps_every_property()
    {
        var config = new SipTransportHardeningConfiguration
        {
            MaxConcurrentInboundConnections = 11,
            MaxInboundConnectionsPerRemote = 5,
            MaxEndpointHintEntries = 7,
            HandshakeTimeout = TimeSpan.FromSeconds(3),
        };

        SipTransportOptions options = config.ToTransportOptions();

        Assert.Equal(11, options.MaxConcurrentInboundConnections);
        Assert.Equal(5, options.MaxInboundConnectionsPerRemote);
        Assert.Equal(7, options.MaxEndpointHintEntries);
        Assert.Equal(TimeSpan.FromSeconds(3), options.HandshakeTimeout);
    }
}
