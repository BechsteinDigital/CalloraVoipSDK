using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Symmetric RTP / comedia latching (STUN-free NAT traversal): once a session receives a
/// valid RTP packet it must send outbound media to that observed source, not the
/// SDP-advertised address. This is how the media reaches a NAT'd peer without ICE/STUN.
/// </summary>
public sealed class RtpSymmetricLatchTests
{
    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private static RtpSessionOptions Options(int localPort, IPEndPoint remote) => new()
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
        RemoteEndPoint = remote,
        PayloadType = 0,
        ClockRate = 8000,
        SamplesPerPacket = 160,
    };

    [Fact]
    public async Task Outbound_media_follows_the_observed_source_after_latching()
    {
        var portA = FreeUdpPort();
        var portB = FreeUdpPort();
        var codec = new RtpPacketCodec();

        // A is configured to send to a black-hole address (TEST-NET-1, RFC 5737). Only a
        // symmetric latch onto B's real source can make A's media reach B.
        await using var a = new RtpSession(
            Options(portA, new IPEndPoint(IPAddress.Parse("192.0.2.1"), 5004)),
            codec, NullLogger<RtpSession>.Instance);
        await using var b = new RtpSession(
            Options(portB, new IPEndPoint(IPAddress.Loopback, portA)),
            codec, NullLogger<RtpSession>.Instance);

        var bReceived = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.PacketReceived += (_, p) => bReceived.TrySetResult(p.Payload.Span.Length > 0 ? p.Payload.Span[0] : (byte)0);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await a.StartAsync(cts.Token);
        await b.StartAsync(cts.Token);

        // B → A: A latches onto B's real source (127.0.0.1:portB).
        await b.SendAsync(new byte[] { 0x11 }, cancellationToken: cts.Token);
        await Task.Delay(100, cts.Token);

        // A → (latched) B: must arrive at B despite A's configured remote being the black hole.
        for (var i = 0; i < 5 && !bReceived.Task.IsCompleted; i++)
        {
            await a.SendAsync(new byte[] { 0x22 }, cancellationToken: cts.Token);
            await Task.Delay(50, cts.Token);
        }

        var payload = await bReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
        Assert.Equal(0x22, payload);
    }
}
