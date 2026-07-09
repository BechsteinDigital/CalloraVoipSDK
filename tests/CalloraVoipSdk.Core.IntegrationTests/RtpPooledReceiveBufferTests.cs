using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The RTP receive loop reuses a single pooled buffer across datagrams. This is only
/// correct if every retained byte is copied out before the next receive overwrites the
/// buffer. These tests push many datagrams with distinct, varying-length payloads through
/// a real UDP <see cref="RtpSession"/> and assert each delivered payload is byte-perfect —
/// a reused buffer that bled data across packets (wrong length or stale trailing bytes)
/// would fail here.
/// </summary>
public sealed class RtpPooledReceiveBufferTests
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

    // Distinct fill byte per packet index; length varies 20..59 so a short packet following
    // a longer one would expose stale trailing bytes if the buffer were not sliced correctly.
    private static byte Fill(int index) => (byte)index;
    private static int PayloadLength(int index) => 20 + (index % 40);

    [Fact]
    public async Task Reused_receive_buffer_delivers_every_payload_without_cross_packet_bleed()
    {
        const int packetCount = 60;

        var portA = FreeUdpPort();
        var portB = FreeUdpPort();
        var codec = new RtpPacketCodec();

        await using var sender = new RtpSession(
            Options(portA, new IPEndPoint(IPAddress.Loopback, portB)),
            codec, NullLogger<RtpSession>.Instance);
        await using var receiver = new RtpSession(
            Options(portB, new IPEndPoint(IPAddress.Loopback, portA)),
            codec, NullLogger<RtpSession>.Instance);

        var corrupted = new ConcurrentBag<string>();
        var received = new ConcurrentDictionary<byte, byte>();

        receiver.PacketReceived += (_, packet) =>
        {
            var payload = packet.Payload.Span;
            if (payload.Length == 0)
            {
                corrupted.Add("empty payload");
                return;
            }

            var marker = payload[0];
            var expectedLength = PayloadLength(marker);
            if (payload.Length != expectedLength)
            {
                corrupted.Add($"marker {marker}: length {payload.Length} != expected {expectedLength}");
                return;
            }

            for (var i = 0; i < payload.Length; i++)
            {
                if (payload[i] != marker)
                {
                    corrupted.Add($"marker {marker}: byte[{i}]={payload[i]} (stale/bled)");
                    return;
                }
            }

            received[marker] = marker;
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await sender.StartAsync(cts.Token);
        await receiver.StartAsync(cts.Token);

        for (var i = 0; i < packetCount; i++)
        {
            var payload = new byte[PayloadLength(i)];
            Array.Fill(payload, Fill(i));
            await sender.SendAsync(payload, cancellationToken: cts.Token);
            await Task.Delay(3, cts.Token);
        }

        // Allow the loopback datagrams to drain.
        for (var i = 0; i < 20 && received.Count < packetCount - 5; i++)
            await Task.Delay(50, cts.Token);

        // Correctness is absolute: any delivered packet must be byte-perfect.
        Assert.True(corrupted.IsEmpty, "Corrupted payloads: " + string.Join("; ", corrupted));

        // Liveness: the reused buffer must not swallow the stream. RTP sequence validation
        // (RFC 3550 §A.1) holds a new SSRC on probation for the first packets, and loopback
        // UDP may drop under burst — so allow a small margin rather than requiring all 60.
        Assert.True(received.Count >= 45, $"Only {received.Count}/{packetCount} distinct payloads delivered.");
    }
}
