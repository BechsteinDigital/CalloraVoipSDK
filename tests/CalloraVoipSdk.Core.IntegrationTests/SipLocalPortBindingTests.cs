using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #104 — the SIP listener used to bind an ephemeral port unconditionally. That is right for a registering
/// line, where the registrar learns the port from the REGISTER Contact, but it makes an IP-authenticated
/// trunk unreachable: it sends no REGISTER, so the provider delivers to a pre-agreed address, and an
/// ephemeral port additionally changes on every restart, invalidating any static firewall or NAT rule.
/// </summary>
public sealed class SipLocalPortBindingTests
{
    private static SipTransportRuntime NewRuntime(SipTransportOptions options) =>
        new(NullLoggerFactory.Instance, new SipWireProtocol(), null, SipTransportProtocol.Udp, null, options);

    /// <summary>Reserves a port, then releases it — a free port number to bind in the test.</summary>
    private static int FreePort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    [Fact]
    public void The_default_still_binds_an_ephemeral_port()
    {
        // Byte-for-byte the previous behaviour: every existing consumer passes no port.
        using var runtime = NewRuntime(SipTransportOptions.Default);

        Assert.NotEqual(0, runtime.GetLocalEndPoint(SipTransportProtocol.Udp).Port);
    }

    [Fact]
    public void A_configured_port_is_the_port_the_listener_binds()
    {
        var port = FreePort();
        using var runtime = NewRuntime(new SipTransportOptions { LocalSipPort = port });

        Assert.Equal(port, runtime.GetLocalEndPoint(SipTransportProtocol.Udp).Port);
    }

    [Fact]
    public void The_configured_port_covers_TCP_as_well_as_UDP()
    {
        // UDP and TCP are separate protocols and may share the number, which is what a SIP peer expects:
        // one address, whichever transport it picks.
        var port = FreePort();
        using var runtime = NewRuntime(new SipTransportOptions { LocalSipPort = port });

        Assert.Equal(port, runtime.GetLocalEndPoint(SipTransportProtocol.Udp).Port);
        Assert.Equal(port, runtime.GetLocalEndPoint(SipTransportProtocol.Tcp).Port);
    }

    [Fact]
    public void A_port_already_in_use_fails_loudly_instead_of_landing_elsewhere()
    {
        // The important half of the contract. A listener that silently fell back to an ephemeral port
        // would look healthy while every inbound call went missing — the failure mode this feature exists
        // to prevent.
        using var occupier = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        occupier.Bind(new IPEndPoint(IPAddress.Any, 0));
        var taken = ((IPEndPoint)occupier.LocalEndPoint!).Port;

        Assert.Throws<SocketException>(() => NewRuntime(new SipTransportOptions { LocalSipPort = taken }));
    }

    [Fact]
    public void Two_runtimes_cannot_share_one_configured_port()
    {
        // The same guarantee seen from the SDK side: a second client configured onto the same port is a
        // configuration error, not a silent reassignment.
        var port = FreePort();
        using var first = NewRuntime(new SipTransportOptions { LocalSipPort = port });

        Assert.Throws<SocketException>(() => NewRuntime(new SipTransportOptions { LocalSipPort = port }));
    }
}
