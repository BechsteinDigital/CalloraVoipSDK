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
    /// <remarks>
    /// The runtime binds a configured port on BOTH transports, so probing UDP alone is not enough: on
    /// Windows a number that is free for UDP can still sit inside an excluded TCP range (Hyper-V/WinNAT
    /// reserve thousands), and binding it then fails with WSAEACCES — "an attempt was made to access a
    /// socket in a way forbidden by its access permissions" — rather than with "address in use". That was
    /// a flaky failure of this class on the windows-latest runner, unrelated to what the tests assert. So
    /// the probe claims the number on TCP as well and moves on if it cannot.
    /// </remarks>
    private static int FreePort()
    {
        for (var attempt = 0; ; attempt++)
        {
            using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)udp.LocalEndPoint!).Port;

            try
            {
                using var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                tcp.Bind(new IPEndPoint(IPAddress.Loopback, port));
                return port;
            }
            catch (SocketException) when (attempt < 20)
            {
                // Free for UDP but not for TCP — ask for another number.
            }
        }
    }

    /// <summary>
    /// Builds a runtime on a free port, retrying if something else claimed it first.
    /// </summary>
    /// <remarks>
    /// Asking the OS for a free port and then binding it is inherently racy: the port is released before
    /// the runtime takes it, and on a machine running the full suite in parallel another test can slip in
    /// between. Retrying keeps the assertion about the feature rather than about timing. The tests that
    /// assert the *failure* path do not use this — they hold the conflicting socket themselves, so there
    /// is nothing to race against.
    /// </remarks>
    private static (SipTransportRuntime Runtime, int Port) OnFreePort(Func<int, SipTransportOptions> options)
    {
        for (var attempt = 0; ; attempt++)
        {
            var port = FreePort();
            try
            {
                return (NewRuntime(options(port)), port);
            }
            catch (SocketException) when (attempt < 20)
            {
                // Someone took it between probe and bind, or the number is administratively unavailable on
                // this host for one of the two transports; try another.
            }
        }
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
        var (runtime, port) = OnFreePort(p => new SipTransportOptions { LocalSipPort = p });
        using (runtime)
            Assert.Equal(port, runtime.GetLocalEndPoint(SipTransportProtocol.Udp).Port);
    }

    [Fact]
    public void The_configured_port_covers_TCP_as_well_as_UDP()
    {
        // UDP and TCP are separate protocols and may share the number, which is what a SIP peer expects:
        // one address, whichever transport it picks.
        var (runtime, port) = OnFreePort(p => new SipTransportOptions { LocalSipPort = p });
        using (runtime)
        {
            Assert.Equal(port, runtime.GetLocalEndPoint(SipTransportProtocol.Udp).Port);
            Assert.Equal(port, runtime.GetLocalEndPoint(SipTransportProtocol.Tcp).Port);
        }
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
        var (first, port) = OnFreePort(p => new SipTransportOptions { LocalSipPort = p });
        using (first)
            Assert.Throws<SocketException>(() => NewRuntime(new SipTransportOptions { LocalSipPort = port }));
    }
}
