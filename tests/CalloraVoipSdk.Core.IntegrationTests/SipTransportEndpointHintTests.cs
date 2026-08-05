using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// [SIP] #158 P1-4: the runtime learns a per-endpoint transport hint only after a datagram parses as a real
/// SIP message, and keeps the hint map bounded. Before this, every inbound datagram — including spoofed
/// garbage from a forged source address — planted a hint, letting an attacker grow the map without limit and
/// skew outbound transport selection for addresses it never actually used.
/// </summary>
public sealed class SipTransportEndpointHintTests
{
    private static ConcurrentDictionary<string, SipTransportProtocol> Hints(SipTransportRuntime runtime)
    {
        var field = typeof(SipTransportRuntime).GetField(
            "_endpointTransportHints", BindingFlags.NonPublic | BindingFlags.Instance);
        return (ConcurrentDictionary<string, SipTransportProtocol>)field!.GetValue(runtime)!;
    }

    private static byte[] ValidOptions(int seq) => Encoding.UTF8.GetBytes(
        "OPTIONS sip:bob@example.test SIP/2.0\r\n" +
        "Via: SIP/2.0/UDP 127.0.0.1:5060;branch=z9hG4bK-hint-" + seq + "\r\n" +
        "Max-Forwards: 70\r\n" +
        "From: <sip:alice@example.test>;tag=h1\r\n" +
        "To: <sip:bob@example.test>\r\n" +
        "Call-ID: hint-call-" + seq + "@example.test\r\n" +
        "CSeq: 1 OPTIONS\r\n" +
        "Content-Length: 0\r\n\r\n");

    [Fact]
    public async Task A_garbage_datagram_plants_no_transport_hint()
    {
        using var runtime = new SipTransportRuntime(NullLoggerFactory.Instance);
        var udpPort = runtime.GetLocalEndPoint(SipTransportProtocol.Udp).Port;

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await client.SendAsync(Encoding.UTF8.GetBytes("this is not a SIP message\r\n\r\n"),
            new IPEndPoint(IPAddress.Loopback, udpPort));

        // Give the receive loop time to process (and, before the fix, to plant a hint).
        await Task.Delay(300);
        Assert.Empty(Hints(runtime));
    }

    [Fact]
    public async Task A_valid_sip_message_plants_a_transport_hint()
    {
        using var runtime = new SipTransportRuntime(NullLoggerFactory.Instance);
        var udpPort = runtime.GetLocalEndPoint(SipTransportProtocol.Udp).Port;

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await client.SendAsync(ValidOptions(1), new IPEndPoint(IPAddress.Loopback, udpPort));

        for (var i = 0; i < 50 && Hints(runtime).IsEmpty; i++)
            await Task.Delay(20);

        Assert.NotEmpty(Hints(runtime));
        Assert.All(Hints(runtime).Values, t => Assert.Equal(SipTransportProtocol.Udp, t));
    }

    [Fact]
    public async Task The_transport_hint_map_is_bounded()
    {
        const int cap = 4;
        using var runtime = new SipTransportRuntime(
            NullLoggerFactory.Instance,
            new SipWireProtocol(),
            tlsConfiguration: null,
            SipTransportProtocol.Udp,
            routeResolver: null,
            new SipTransportOptions { MaxEndpointHintEntries = cap });
        var udpPort = runtime.GetLocalEndPoint(SipTransportProtocol.Udp).Port;
        var target = new IPEndPoint(IPAddress.Loopback, udpPort);

        // Each valid message from a distinct source port would plant two hint entries; far more than the cap.
        for (var i = 0; i < 20; i++)
        {
            using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            await client.SendAsync(ValidOptions(i), target);
            await Task.Delay(30); // let the receive loop apply this datagram before the next
        }

        await Task.Delay(200);
        // Best-effort eviction keeps the map at (or transiently just over) the cap, never unbounded.
        Assert.True(Hints(runtime).Count <= cap, $"hint map grew to {Hints(runtime).Count}, expected <= {cap}");
    }
}
