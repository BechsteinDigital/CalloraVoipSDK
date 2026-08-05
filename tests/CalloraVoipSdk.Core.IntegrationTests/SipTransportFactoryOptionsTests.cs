using System.Reflection;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// [SIP] #158 P1-3/P1-4 (config follow-up): the transport factory forwards the supplied hardening options to
/// the runtime it builds, so configuration set on the public facade actually reaches the inbound listener.
/// </summary>
public sealed class SipTransportFactoryOptionsTests
{
    [Fact]
    public void Create_forwards_the_supplied_options_to_the_runtime()
    {
        var factory = new SipTransportFactory();
        var options = new SipTransportOptions { MaxEndpointHintEntries = 7 };

        var runtime = factory.Create(
            tls: null,
            NullLoggerFactory.Instance,
            SipTransportProtocol.Udp,
            options);

        try
        {
            var field = typeof(SipTransportRuntime)
                .GetField("_maxEndpointHintEntries", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.Equal(7, (int)field.GetValue(runtime)!);
        }
        finally
        {
            (runtime as IDisposable)?.Dispose();
        }
    }
}
