using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Application.Media.Sessions;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The single-stream playout cursor (<c>_lastDeliveredSequence</c>) drives loss concealment: a forward
/// gap between the cursor and the next delivered packet is unrecoverable loss and is concealed. RFC 4733
/// telephone-events are demuxed before the jitter buffer but still consume sequence numbers, so they bump
/// the cursor — and that bump must be forward-only like every other one. A reordered event arriving behind
/// the cursor used to drag it back, fabricating a gap the size of the reordering (#161 P2-7).
/// </summary>
public sealed class RtpCallMediaSessionPlayoutCursorTests
{
    private const byte TelephoneEventPayloadType = 101;

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    [Fact]
    public async Task A_reordered_telephone_event_does_not_pull_the_playout_cursor_back()
    {
        var localPort = FreeUdpPort();
        await using var session = (RtpCallMediaSession)new RtpCallMediaSessionFactory(
                NullLoggerFactory.Instance, PayloadCodecKind.Pcmu)
            .Create(new CallMediaParameters
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
                PayloadType = 0,
                CodecName = "PCMU",
                ClockRate = 8000,
                SamplesPerPacket = 160,
                TelephoneEventPayloadType = TelephoneEventPayloadType,
            });

        var delivered = new ConcurrentQueue<byte>();
        session.FrameReceived += frame => delivered.Enqueue(frame.Payload[0]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await session.StartAsync(cts.Token);

        using var peer = new UdpClient();
        var codec = new RtpPacketCodec();
        var target = new IPEndPoint(IPAddress.Loopback, localPort);
        var clock = Stopwatch.StartNew();

        // Timestamps follow the wall clock at 8 kHz so every packet's playout slot is still ahead of its
        // arrival — otherwise the buffer would drop it as late and the test would measure that instead.
        async Task Send(ushort sequenceNumber, byte payloadType, byte[] payload)
        {
            var datagram = codec.Encode(new RtpPacket
            {
                PayloadType = payloadType,
                SequenceNumber = sequenceNumber,
                Timestamp = (uint)(clock.ElapsedMilliseconds * 8),
                Ssrc = 0x5150,
                Payload = payload,
            });
            await peer.SendAsync(datagram, datagram.Length, target);
        }

        // Each audio payload is stamped with its own sequence number, so a concealment frame (a copy of the
        // previous payload) is visible in the delivered order, not just in the count.
        Task SendAudio(ushort sequenceNumber)
        {
            var payload = new byte[160];
            Array.Fill(payload, (byte)sequenceNumber);
            return Send(sequenceNumber, 0, payload);
        }

        // RFC 4733 §2.3: event, E-bit + volume, duration.
        Task SendTelephoneEvent(ushort sequenceNumber)
            => Send(sequenceNumber, TelephoneEventPayloadType, [0x01, 0x8A, 0x00, 0xA0]);

        async Task WaitForFrames(int count)
        {
            while (delivered.Count < count)
            {
                cts.Token.ThrowIfCancellationRequested();
                await Task.Delay(10, cts.Token);
            }
        }

        for (ushort seq = 100; seq <= 107; seq++)
        {
            await SendAudio(seq);
            await Task.Delay(20, cts.Token);
        }

        // Wait until the whole first run has played out, so the cursor is provably past 105.
        await WaitForFrames(8);

        await SendTelephoneEvent(105); // reordered: behind the cursor
        await Task.Delay(40, cts.Token);

        for (ushort seq = 108; seq <= 114; seq++)
        {
            await SendAudio(seq);
            await Task.Delay(20, cts.Token);
        }

        await WaitForFrames(15);
        await Task.Delay(200, cts.Token); // let any concealment burst surface before counting

        Assert.Equal(
            Enumerable.Range(100, 15).Select(i => (byte)i).ToArray(),
            delivered.ToArray());
    }
}
