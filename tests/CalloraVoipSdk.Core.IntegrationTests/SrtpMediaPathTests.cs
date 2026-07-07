using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using CalloraVoipSdk.Core.Security;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Verifies that negotiated SDES key material is actually applied to the RTP media path:
/// outbound packets are SRTP-protected on the wire, inbound packets are unprotected before
/// decoding, mismatched keys are dropped without crashing the receive loop, and a negotiated
/// but unbuildable suite is refused rather than silently downgraded to cleartext RTP.
/// SRTCP (the RTCP control path) is intentionally out of scope here.
/// </summary>
public sealed class SrtpMediaPathTests
{
    private const int RtpHeaderLength = 12;
    private const int HmacSha1_80TagLength = 10;
    private const int PcmuPayloadType = 0;
    private const int ClockRate = 8000;
    private const int SamplesPerPacket = 160;

    private static readonly byte[] KeyA = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");
    private static readonly byte[] KeyB = Convert.FromHexString("101112131415161718191A1B1C1D1E1F");
    private static readonly byte[] SaltA = Convert.FromHexString("A0A1A2A3A4A5A6A7A8A9AAABACAD");
    private static readonly byte[] SaltB = Convert.FromHexString("B0B1B2B3B4B5B6B7B8B9BABBBCBD");

    [Fact]
    public async Task Create_WithoutSrtpKeys_SendsCleartextRtpOnTheWire()
    {
        var payload = CreatePayload(SamplesPerPacket);
        using var sniffer = CreateSniffer(out var snifferEndPoint);
        var local = LoopbackEndPoint(GetFreeUdpPort());

        await using var session = CreateSession(CreateParameters(local, snifferEndPoint, srtpKeys: null));
        await session.StartAsync();
        await session.SendFrameAsync(new CallAudioFrame(payload, PcmuPayloadType, SamplesPerPacket));

        var datagram = await ReceiveWithTimeoutAsync(sniffer);

        // No SRTP: header + payload, no auth tag, payload is cleartext.
        Assert.Equal(RtpHeaderLength + payload.Length, datagram.Length);
        Assert.Equal(payload, datagram.AsSpan(RtpHeaderLength).ToArray());
    }

    [Fact]
    public async Task Create_WithSrtpKeys_SendsProtectedRtpVerifiableWithMatchingContext()
    {
        var payload = CreatePayload(SamplesPerPacket);
        using var sniffer = CreateSniffer(out var snifferEndPoint);
        var local = LoopbackEndPoint(GetFreeUdpPort());
        var keys = CreateKeyMaterial(KeyA, SaltA, KeyB, SaltB);

        await using var session = CreateSession(CreateParameters(local, snifferEndPoint, keys));
        await session.StartAsync();
        await session.SendFrameAsync(new CallAudioFrame(payload, PcmuPayloadType, SamplesPerPacket));

        var datagram = await ReceiveWithTimeoutAsync(sniffer);

        // SRTP: header + encrypted payload + 10-byte HMAC-SHA1-80 auth tag; payload is not cleartext.
        Assert.Equal(RtpHeaderLength + payload.Length + HmacSha1_80TagLength, datagram.Length);
        Assert.False(datagram.AsSpan(RtpHeaderLength, payload.Length).SequenceEqual(payload));

        // A context keyed with the sender's outbound (Local) key unprotects it back to cleartext.
        using var verifier = new SrtpContext(new SrtpKeyMaterial
        {
            MasterKey = KeyA,
            MasterSalt = SaltA,
            Suite = SrtpCryptoSuite.AesCm128HmacSha1_80,
        });
        var rtp = verifier.Unprotect(datagram);
        Assert.Equal(payload, rtp.AsSpan(RtpHeaderLength).ToArray());
    }

    [Fact]
    public async Task TwoSessions_WithMatchingSrtpKeys_DeliverFrameAsCleartext()
    {
        var payload = CreatePayload(SamplesPerPacket);
        var senderEndPoint = LoopbackEndPoint(GetFreeUdpPort());
        var receiverEndPoint = LoopbackEndPoint(GetFreeUdpPort());

        var senderKeys = CreateKeyMaterial(KeyA, SaltA, KeyB, SaltB);
        var receiverKeys = CreateKeyMaterial(KeyB, SaltB, KeyA, SaltA);

        await using var sender = CreateSession(CreateParameters(senderEndPoint, receiverEndPoint, senderKeys));
        await using var receiver = CreateSession(CreateParameters(receiverEndPoint, senderEndPoint, receiverKeys));

        var delivered = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.FrameReceived += frame =>
        {
            if (frame.Payload.AsSpan().SequenceEqual(payload))
                delivered.TrySetResult(frame.Payload);
        };

        await receiver.StartAsync();
        await sender.StartAsync();

        await SendBurstAsync(sender, payload, count: 15, gapMs: 20);

        var winner = await Task.WhenAny(delivered.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(winner == delivered.Task, "Receiver never delivered the SRTP-protected frame as cleartext.");
        Assert.Equal(payload, await delivered.Task);
    }

    [Fact]
    public async Task TwoSessions_WithWrongInboundKey_DropPacketsWithoutDeliveryOrCrash()
    {
        var payload = CreatePayload(SamplesPerPacket);
        var senderEndPoint = LoopbackEndPoint(GetFreeUdpPort());
        var receiverEndPoint = LoopbackEndPoint(GetFreeUdpPort());

        var senderKeys = CreateKeyMaterial(KeyA, SaltA, KeyB, SaltB);
        // Receiver's inbound (Remote) key is KeyB, but the sender protects with KeyA: auth must fail.
        var mismatchedReceiverKeys = CreateKeyMaterial(KeyB, SaltB, KeyB, SaltB);

        await using var sender = CreateSession(CreateParameters(senderEndPoint, receiverEndPoint, senderKeys));
        await using var receiver = CreateSession(CreateParameters(receiverEndPoint, senderEndPoint, mismatchedReceiverKeys));

        var deliveredCount = 0;
        receiver.FrameReceived += _ => Interlocked.Increment(ref deliveredCount);

        await receiver.StartAsync();
        await sender.StartAsync();

        await SendBurstAsync(sender, payload, count: 15, gapMs: 10);

        // Allow any wrongly accepted frame to drain through the playout loop before asserting.
        await Task.Delay(500);
        Assert.Equal(0, Volatile.Read(ref deliveredCount));
    }

    [Fact]
    public void Create_WithSrtpKeysButWrongKeyLength_ThrowsInsteadOfDowngradingToCleartext()
    {
        var local = LoopbackEndPoint(GetFreeUdpPort());
        var remote = LoopbackEndPoint(GetFreeUdpPort());
        var badKeys = new SrtpSessionKeyMaterial
        {
            Suite = SrtpCryptoSuiteKind.AesCm128HmacSha1_80,
            LocalMasterKey = new byte[8], // AES-128 requires 16 bytes.
            LocalMasterSalt = SaltA,
            RemoteMasterKey = KeyB,
            RemoteMasterSalt = SaltB,
        };

        var factory = new RtpCallMediaSessionFactory(NullLoggerFactory.Instance);

        Assert.Throws<InvalidOperationException>(
            () => factory.Create(CreateParameters(local, remote, badKeys)));
    }

    [Fact]
    public void Create_WithSrtpKeysButWrongSaltLength_Throws()
    {
        var local = LoopbackEndPoint(GetFreeUdpPort());
        var remote = LoopbackEndPoint(GetFreeUdpPort());
        var badKeys = new SrtpSessionKeyMaterial
        {
            Suite = SrtpCryptoSuiteKind.AesCm128HmacSha1_80,
            LocalMasterKey = KeyA,
            LocalMasterSalt = new byte[8], // Master salt must be 14 bytes.
            RemoteMasterKey = KeyB,
            RemoteMasterSalt = SaltB,
        };

        var factory = new RtpCallMediaSessionFactory(NullLoggerFactory.Instance);

        Assert.Throws<InvalidOperationException>(
            () => factory.Create(CreateParameters(local, remote, badKeys)));
    }

    private static async Task SendBurstAsync(ICallMediaSession session, byte[] payload, int count, int gapMs)
    {
        for (var i = 0; i < count; i++)
        {
            await session.SendFrameAsync(new CallAudioFrame(payload, PcmuPayloadType, SamplesPerPacket));
            await Task.Delay(gapMs);
        }
    }

    private static ICallMediaSession CreateSession(CallMediaParameters parameters)
        => new RtpCallMediaSessionFactory(NullLoggerFactory.Instance).Create(parameters);

    private static CallMediaParameters CreateParameters(
        IPEndPoint local,
        IPEndPoint remote,
        SrtpSessionKeyMaterial? srtpKeys)
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

    private static byte[] CreatePayload(int length)
    {
        var payload = new byte[length];
        for (var i = 0; i < length; i++)
            payload[i] = (byte)(i * 7 + 3);

        return payload;
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
