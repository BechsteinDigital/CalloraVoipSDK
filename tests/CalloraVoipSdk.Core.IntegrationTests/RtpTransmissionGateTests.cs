using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Verifies that <see cref="RtpSession.StopTransmission"/> ceases media transmission (RFC 7675 §5.1)
/// after ICE consent is lost: packets still flow before the call, and stop afterwards.
/// </summary>
public sealed class RtpTransmissionGateTests
{
    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private static RtpSessionOptions Options(int localPort, int remotePort) => new()
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, remotePort),
        PayloadType = 0,
        ClockRate = 8000,
        SamplesPerPacket = 160,
    };

    [Fact]
    public async Task StopTransmission_ceases_media_sends()
    {
        var senderPort = FreeUdpPort();
        var receiverPort = FreeUdpPort();

        await using var sender = new RtpSession(
            Options(senderPort, receiverPort), new RtpPacketCodec(), NullLogger<RtpSession>.Instance);
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, receiverPort));

        var payload = new byte[] { 1, 2, 3, 4 };

        // Before consent loss: media is delivered.
        await sender.SendAsync(payload);
        using (var beforeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            var delivered = await receiver.ReceiveAsync(beforeCts.Token);
            Assert.NotEmpty(delivered.Buffer);
        }

        // After consent loss: transmission ceases, nothing arrives.
        sender.StopTransmission();
        await sender.SendAsync(payload);

        using var afterCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await receiver.ReceiveAsync(afterCts.Token));
    }
}
