using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// P2 [RTP] #14: RtpSession.StartAsync is idempotent — a second call must not replace the receive loop and orphan
/// the first (which would then run un-cancelled until the socket is disposed), mirroring the bundle guard (HARD-C5).
/// </summary>
public sealed class RtpSessionStartIdempotencyTests
{
    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    [Fact]
    public async Task A_second_StartAsync_does_not_replace_the_receive_loop()
    {
        await using var session = new RtpSession(
            new RtpSessionOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
                PayloadType = 0,
                ClockRate = 8000,
                SamplesPerPacket = 160,
            },
            new RtpPacketCodec(),
            NullLogger<RtpSession>.Instance);

        await session.StartAsync();
        var firstLoop = session.ReceiveLoopForTest;

        await session.StartAsync();
        var secondLoop = session.ReceiveLoopForTest;

        Assert.NotNull(firstLoop);
        Assert.Same(firstLoop, secondLoop);
    }
}
