using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Security;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Verifies that negotiated SDES key material also protects the RTCP-MUX control path as
/// SRTCP (RFC 3711 §3.4): outbound control datagrams are protected on the wire, inbound
/// datagrams are unprotected before dispatch, and a mismatched key is dropped without
/// delivery or crashing the receive loop. End-to-end over UDP loopback, mirroring
/// <c>SrtpMediaPathTests</c>. Correctness of the SRTCP crypto itself is proven separately by
/// <c>SrtcpContextTests</c>; these tests prove the wiring, not interop with a real peer.
/// </summary>
public sealed class SrtcpMediaPathTests
{
    private const int PcmuPayloadType = 0;
    private const int ClockRate = 8000;
    private const int SamplesPerPacket = 160;
    private const int SrtcpIndexLength = 4;
    private const int HmacSha1_80TagLength = 10;
    private const uint SenderSsrc = 0x0BADCAFEu;

    private static readonly byte[] KeyA = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");
    private static readonly byte[] KeyB = Convert.FromHexString("101112131415161718191A1B1C1D1E1F");
    private static readonly byte[] SaltA = Convert.FromHexString("A0A1A2A3A4A5A6A7A8A9AAABACAD");
    private static readonly byte[] SaltB = Convert.FromHexString("B0B1B2B3B4B5B6B7B8B9BABBBCBD");

    [Fact]
    public async Task Create_WithSrtpKeys_ProtectsOutboundRtcpOnTheWire()
    {
        var rtcp = CreateSenderReport(SenderSsrc);
        using var sniffer = CreateSniffer(out var snifferEndPoint);
        var local = LoopbackEndPoint(GetFreeUdpPort());
        var keys = CreateKeyMaterial(KeyA, SaltA, KeyB, SaltB);

        await using var session = CreateSession(CreateParameters(local, snifferEndPoint, keys));
        await session.StartAsync();
        await session.SendRtcpMuxDatagramAsync(rtcp);

        var datagram = await ReceiveWithTimeoutAsync(sniffer);

        // SRTCP frames the packet as RTCP + E||index (4B) + HMAC-SHA1-80 tag (10B); the body
        // past the 8-byte cleartext header must be encrypted rather than the original RTCP.
        Assert.Equal(rtcp.Length + SrtcpIndexLength + HmacSha1_80TagLength, datagram.Length);
        Assert.Equal(rtcp.AsSpan(0, 8).ToArray(), datagram.AsSpan(0, 8).ToArray());
        Assert.False(datagram.AsSpan(8, rtcp.Length - 8).SequenceEqual(rtcp.AsSpan(8)));
    }

    [Fact]
    public async Task TwoSessions_WithMatchingSrtpKeys_DeliverRtcpDatagramAsCleartext()
    {
        var rtcp = CreateSenderReport(SenderSsrc);
        var senderEndPoint = LoopbackEndPoint(GetFreeUdpPort());
        var receiverEndPoint = LoopbackEndPoint(GetFreeUdpPort());

        var senderKeys = CreateKeyMaterial(KeyA, SaltA, KeyB, SaltB);
        var receiverKeys = CreateKeyMaterial(KeyB, SaltB, KeyA, SaltA);

        await using var sender = CreateSession(CreateParameters(senderEndPoint, receiverEndPoint, senderKeys));
        await using var receiver = CreateSession(CreateParameters(receiverEndPoint, senderEndPoint, receiverKeys));

        var delivered = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.RtcpMuxDatagramReceived += datagram =>
        {
            if (datagram.AsSpan().SequenceEqual(rtcp))
                delivered.TrySetResult(datagram);
        };

        await receiver.StartAsync();
        await sender.StartAsync();

        await SendControlBurstAsync(sender, rtcp, count: 10, gapMs: 20);

        var winner = await Task.WhenAny(delivered.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(winner == delivered.Task, "Receiver never delivered the SRTCP-protected control datagram as cleartext.");
        Assert.Equal(rtcp, await delivered.Task);
    }

    [Fact]
    public async Task TwoSessions_WithWrongInboundKey_DropRtcpWithoutDeliveryOrCrash()
    {
        var rtcp = CreateSenderReport(SenderSsrc);
        var senderEndPoint = LoopbackEndPoint(GetFreeUdpPort());
        var receiverEndPoint = LoopbackEndPoint(GetFreeUdpPort());

        var senderKeys = CreateKeyMaterial(KeyA, SaltA, KeyB, SaltB);
        // Receiver's inbound (Remote) key is KeyB, but the sender protects with KeyA: auth must fail.
        var mismatchedReceiverKeys = CreateKeyMaterial(KeyB, SaltB, KeyB, SaltB);

        await using var sender = CreateSession(CreateParameters(senderEndPoint, receiverEndPoint, senderKeys));
        await using var receiver = CreateSession(CreateParameters(receiverEndPoint, senderEndPoint, mismatchedReceiverKeys));

        var deliveredCount = 0;
        receiver.RtcpMuxDatagramReceived += _ => Interlocked.Increment(ref deliveredCount);

        await receiver.StartAsync();
        await sender.StartAsync();

        await SendControlBurstAsync(sender, rtcp, count: 10, gapMs: 10);

        await Task.Delay(500);
        Assert.Equal(0, Volatile.Read(ref deliveredCount));
    }

    private static async Task SendControlBurstAsync(ICallMediaSession session, byte[] rtcp, int count, int gapMs)
    {
        for (var i = 0; i < count; i++)
        {
            await session.SendRtcpMuxDatagramAsync(rtcp);
            await Task.Delay(gapMs);
        }
    }

    private static ICallMediaSession CreateSession(CallMediaParameters parameters)
        => new RtpCallMediaSessionFactory(NullLoggerFactory.Instance).Create(parameters);

    private static CallMediaParameters CreateParameters(
        IPEndPoint local,
        IPEndPoint remote,
        SrtpSessionKeyMaterial srtpKeys)
        => new()
        {
            LocalEndPoint = local,
            RemoteEndPoint = remote,
            PayloadType = PcmuPayloadType,
            ClockRate = ClockRate,
            SamplesPerPacket = SamplesPerPacket,
            SrtpKeys = srtpKeys,
        };

    private static SrtpSessionKeyMaterial CreateKeyMaterial(
        byte[] localKey,
        byte[] localSalt,
        byte[] remoteKey,
        byte[] remoteSalt)
        => new()
        {
            Suite = SrtpCryptoSuiteKind.AesCm128HmacSha1_80,
            LocalMasterKey = localKey,
            LocalMasterSalt = localSalt,
            RemoteMasterKey = remoteKey,
            RemoteMasterSalt = remoteSalt,
        };

    private static byte[] CreateSenderReport(uint senderSsrc)
    {
        // Sender Report: 8-byte header + 20-byte sender info = 28 bytes, RC = 0.
        var sr = new byte[28];
        sr[0] = 0x80;            // V=2, P=0, RC=0
        sr[1] = 200;             // PT = SR
        BinaryPrimitives.WriteUInt16BigEndian(sr.AsSpan(2), 6); // length in words - 1
        BinaryPrimitives.WriteUInt32BigEndian(sr.AsSpan(4), senderSsrc);
        for (var i = 8; i < sr.Length; i++)
            sr[i] = (byte)(i * 3 + 1);
        return sr;
    }

    private static UdpClient CreateSniffer(out IPEndPoint boundEndPoint)
    {
        var sniffer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        boundEndPoint = (IPEndPoint)sniffer.Client.LocalEndPoint!;
        return sniffer;
    }

    private static async Task<byte[]> ReceiveWithTimeoutAsync(UdpClient client)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await client.ReceiveAsync(cts.Token);
        return result.Buffer;
    }

    private static IPEndPoint LoopbackEndPoint(int port) => new(IPAddress.Loopback, port);

    private static int GetFreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }
}
