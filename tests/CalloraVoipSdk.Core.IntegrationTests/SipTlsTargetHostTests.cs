using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Outbound TLS must present the SIP domain (SNI + certificate name validation), not the resolved
/// IP address — otherwise a standard certificate for the domain fails name validation (CORE-012).
/// </summary>
public class SipTlsTargetHostTests
{
    private const string Key = "Tls:203.0.113.5:5061";

    [Fact]
    public void Uses_resolved_sip_domain_as_tls_target_host()
    {
        var hosts = new Dictionary<string, string> { [Key] = "sip.example.com" };

        var target = SipTransportRuntimeUtilities.SelectTlsTargetHost(hosts, Key, IPAddress.Parse("203.0.113.5"));

        Assert.Equal("sip.example.com", target);
    }

    [Fact]
    public void Falls_back_to_ip_when_no_host_was_resolved()
    {
        var hosts = new Dictionary<string, string>();

        var target = SipTransportRuntimeUtilities.SelectTlsTargetHost(hosts, Key, IPAddress.Parse("203.0.113.5"));

        Assert.Equal("203.0.113.5", target);
    }

    [Fact]
    public void Falls_back_to_ip_when_resolved_host_is_blank()
    {
        var hosts = new Dictionary<string, string> { [Key] = "  " };

        var target = SipTransportRuntimeUtilities.SelectTlsTargetHost(hosts, Key, IPAddress.Parse("203.0.113.5"));

        Assert.Equal("203.0.113.5", target);
    }
}
