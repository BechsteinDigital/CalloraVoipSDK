using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Package N1: a configured public host must be advertised in the REGISTER Contact and
/// the Via sent-by, so a public SIP trunk behind NAT binds the AOR to a routable address
/// instead of the private LAN IP (which showed the line "offline").
/// </summary>
public sealed class SipPublicContactTests
{
    private static readonly IPEndPoint Local = new(IPAddress.Parse("192.168.178.76"), 45000);

    // --- Formatter level (deterministic) ---

    [Fact]
    public void Contact_uses_public_host_and_port_when_set()
    {
        var contact = SipSignalingFormat.BuildContactUri(
            "3089553t3", Local, SipTransportProtocol.Udp,
            advertisedHost: "203.0.113.7", advertisedPort: 6000);

        Assert.StartsWith("sip:3089553t3@203.0.113.7:6000", contact);
        Assert.DoesNotContain("192.168.178.76", contact);
    }

    [Fact]
    public void Contact_accepts_an_fqdn_public_host()
    {
        var contact = SipSignalingFormat.BuildContactUri(
            "user", Local, SipTransportProtocol.Udp, advertisedHost: "agent.dyndns.example");

        // No public port → local port is reused.
        Assert.StartsWith("sip:user@agent.dyndns.example:45000", contact);
    }

    [Fact]
    public void Contact_falls_back_to_local_without_override()
    {
        var contact = SipSignalingFormat.BuildContactUri("user", Local, SipTransportProtocol.Udp);

        Assert.StartsWith("sip:user@192.168.178.76:45000", contact);
    }

    [Fact]
    public void Via_uses_public_host_and_keeps_rport()
    {
        var via = SipSignalingFormat.BuildVia(
            Local, "z9hG4bK-test", SipTransportProtocol.Udp,
            advertisedHost: "203.0.113.7", advertisedPort: 6000);

        Assert.StartsWith("SIP/2.0/UDP 203.0.113.7:6000;", via);
        Assert.EndsWith(";rport", via);
        Assert.DoesNotContain("192.168.178.76", via);
    }

    [Fact]
    public void Via_falls_back_to_local_without_override()
    {
        var via = SipSignalingFormat.BuildVia(Local, "z9hG4bK-test", SipTransportProtocol.Udp);

        Assert.StartsWith("SIP/2.0/UDP 192.168.178.76:45000;", via);
        Assert.EndsWith(";rport", via);
    }

    // --- Service level: the wiring from request to emitted REGISTER headers ---

    [Fact]
    public async Task Register_emits_public_host_in_contact_and_via()
    {
        var transport = new CapturingSipTransportRuntime();
        var service = new SipRegistrationService(
            transport, new NoopSipDigestAuthenticator(), NullLoggerFactory.Instance);

        var request = new SipRegistrationRequest
        {
            Username = "3089553t3",
            Password = "secret",
            Domain = "sipconnect.sipgate.de",
            Port = 5060,
            PublicHost = "203.0.113.7",
            PublicPort = 6000,
            Timeout = TimeSpan.FromMilliseconds(200),
        };

        // The REGISTER is captured on send, before any response handling; the transaction
        // may time out without a 200 (no ResponseFactory) — the assertion is on the wire.
        _ = service.RegisterAsync(request);
        var register = await transport.WaitForRequestAsync("REGISTER", TimeSpan.FromSeconds(2));

        Assert.Contains("203.0.113.7:6000", register.Headers["Contact"]);
        Assert.DoesNotContain("192.168.178.76", register.Headers["Contact"]);
        Assert.StartsWith("SIP/2.0/UDP 203.0.113.7:6000;", register.Headers["Via"]);
        Assert.EndsWith(";rport", register.Headers["Via"]);
    }

    [Fact]
    public async Task Register_without_public_host_keeps_local_address()
    {
        var transport = new CapturingSipTransportRuntime();
        var service = new SipRegistrationService(
            transport, new NoopSipDigestAuthenticator(), NullLoggerFactory.Instance);

        var request = new SipRegistrationRequest
        {
            Username = "user",
            Password = "secret",
            Domain = "pbx.example.com",
            Port = 5060,
            Timeout = TimeSpan.FromMilliseconds(200),
        };

        _ = service.RegisterAsync(request);
        var register = await transport.WaitForRequestAsync("REGISTER", TimeSpan.FromSeconds(2));

        Assert.DoesNotContain("203.0.113.7", register.Headers["Contact"]);
        Assert.Contains("127.0.0.1", register.Headers["Contact"]);
    }
}
