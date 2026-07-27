using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// P2 [RTP] #14: RtpSession Start/Dispose lifecycle is coordinated under a lock so a Start racing a Dispose cannot
/// orphan the receive loop or spin one up on the disposed socket. Observable guard behaviours: StartAsync after
/// disposal is a no-op, and DisposeAsync is idempotent.
/// </summary>
public sealed class RtpSessionLifecycleTests
{
    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private static RtpSession NewSession() => new(
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

    [Fact]
    public async Task StartAsync_after_dispose_does_not_start_a_loop()
    {
        var session = NewSession();
        await session.DisposeAsync();

        await session.StartAsync();

        Assert.Null(session.ReceiveLoopForTest);
    }

    [Fact]
    public async Task DisposeAsync_is_idempotent()
    {
        var session = NewSession();
        await session.StartAsync();
        await session.DisposeAsync();

        var second = await Record.ExceptionAsync(async () => await session.DisposeAsync());

        Assert.Null(second);
    }
}
