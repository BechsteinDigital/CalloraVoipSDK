using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Pins CORE-016: the consumer-selected <see cref="SdkConfiguration.DefaultTransport"/> reaches the
/// SIP transport factory that drives outbound transport, and the default stays UDP.
/// </summary>
public sealed class SipTransportSelectionTests
{
    [Fact]
    public void Configured_default_transport_reaches_the_transport_factory()
    {
        var factory = BuildClientAndCaptureTransport(options => options.DefaultTransport = SipTransport.Tcp);

        Assert.Equal(SipTransportProtocol.Tcp, factory.LastDefaultTransport);
    }

    [Fact]
    public void Default_transport_is_udp_when_unconfigured()
    {
        var factory = BuildClientAndCaptureTransport(_ => { });

        Assert.Equal(SipTransportProtocol.Udp, factory.LastDefaultTransport);
    }

    private static RecordingTransportFactory BuildClientAndCaptureTransport(Action<SdkOptions> configure)
    {
        var factory = new RecordingTransportFactory();
        var services = new ServiceCollection();
        services.AddCallora(options =>
        {
            options.UserAgent = "transport-selection-test/1.0";
            options.EnableAutomaticAudioDeviceSelection = false;
            configure(options);
        });
        services.AddSingleton<ISipTransportFactory>(factory);

        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IVoipClient>();

        return factory;
    }
}
