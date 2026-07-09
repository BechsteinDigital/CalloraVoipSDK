using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// SRTCP wiring into the media path (RFC 3711 §3.4): a negotiated SRTP call now protects its
/// RTCP too. The media session builds SRTCP contexts from the same master keys as SRTP, the
/// RtpSession encrypts outbound RTCP and authenticates/decrypts inbound RTCP on the wire, and
/// a tampered SRTCP packet is dropped before dispatch.
/// </summary>
public sealed class SrtcpMediaPathTests
{
    private const string Suite = "AES_CM_128_HMAC_SHA1_80";

    private static string InlineKey(byte seed)
    {
        var material = new byte[30];
        for (var i = 0; i < material.Length; i++)
            material[i] = (byte)(seed + i);
        return $"inline:{Convert.ToBase64String(material)}";
    }

    private static SrtpKeyMaterial Keys(byte seed) =>
        SrtpKeyMaterial.ParseInline(InlineKey(seed), SrtpCryptoSuite.AesCm128HmacSha1_80);

    private static byte[] Rtcp(uint ssrc)
    {
        var packet = new byte[28];          // SR: 8-byte header + 20-byte sender info
        packet[0] = 0x80;                   // V=2
        packet[1] = 200;                    // PT = SR
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)((packet.Length / 4) - 1));
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), ssrc);
        for (var i = 8; i < packet.Length; i++)
            packet[i] = (byte)(0xB0 + i);
        return packet;
    }

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    // ── Context creation from negotiated SRTP parameters ─────────────────────────

    [Fact]
    public async Task Media_session_creates_and_disposes_srtcp_contexts_for_srtp_call()
    {
        var parameters = new CallMediaParameters
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
            PayloadType = 0,
            ClockRate = 8000,
            SamplesPerPacket = 160,
            SrtpSuite = Suite,
            SrtpLocalKeyParams = InlineKey(70),
            SrtpRemoteKeyParams = InlineKey(90)
        };

        var media = (RtpCallMediaSession)new RtpCallMediaSessionFactory(NullLoggerFactory.Instance)
            .Create(parameters);
        var outbound = media.OutboundSrtcpContext;
        var inbound = media.InboundSrtcpContext;
        Assert.NotNull(outbound);
        Assert.NotNull(inbound);

        await media.DisposeAsync();

        // Contexts are owned by the session and zeroed on dispose.
        Assert.Throws<ObjectDisposedException>(() => outbound!.ProtectRtcp(Rtcp(1)));
        Assert.Throws<ObjectDisposedException>(() => inbound!.UnprotectRtcp(new byte[22]));
    }

    [Fact]
    public async Task Plain_call_has_no_srtcp_contexts()
    {
        var parameters = new CallMediaParameters
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
            PayloadType = 0,
            ClockRate = 8000,
            SamplesPerPacket = 160
        };

        await using var media = (RtpCallMediaSession)new RtpCallMediaSessionFactory(NullLoggerFactory.Instance)
            .Create(parameters);

        Assert.Null(media.OutboundSrtcpContext);
        Assert.Null(media.InboundSrtcpContext);
    }

    // ── E2E on the wire: outbound encrypted, inbound authenticated + decrypted ────

    [Fact]
    public async Task Rtcp_is_srtcp_protected_outbound_and_decrypted_inbound()
    {
        var localKeys = Keys(70);
        var remoteKeys = Keys(90);
        var localPort = FreeUdpPort();
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerPort = ((IPEndPoint)peer.Client.LocalEndPoint!).Port;

        // Peer mirrors the SDK's key assignment: it decrypts our stream with our (local)
        // key and encrypts its stream with the peer (remote) key.
        using var peerDecrypt = new SrtcpContext(Keys(70));
        using var peerEncrypt = new SrtcpContext(Keys(90));

        await using var session = new RtpSession(
            new RtpSessionOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, peerPort),
                PayloadType = 0,
                ClockRate = 8000,
                SamplesPerPacket = 160,
                OutboundSrtcp = new SrtcpContext(localKeys),
                InboundSrtcp = new SrtcpContext(remoteKeys)
            },
            new RtpPacketCodec(), NullLogger<RtpSession>.Instance);

        var inboundReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ControlPacketReceived += d => inboundReceived.TrySetResult(d);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await session.StartAsync(cts.Token);

        // SDK → peer: RTCP must leave the socket as SRTCP (index + tag appended, payload
        // encrypted) and decrypt to the original under the peer's matching key.
        var outboundRtcp = Rtcp(ssrc: 0x11223344);
        await session.SendControlAsync(outboundRtcp, cts.Token);

        var wire = (await peer.ReceiveAsync(cts.Token)).Buffer;
        Assert.Equal(outboundRtcp.Length + 4 + 10, wire.Length);       // + E|index + auth tag
        Assert.Equal(outboundRtcp.AsSpan(0, 8).ToArray(), wire[..8]);   // header stays clear
        Assert.NotEqual(outboundRtcp[8..], wire[8..outboundRtcp.Length]); // payload encrypted
        Assert.Equal(outboundRtcp, peerDecrypt.UnprotectRtcp(wire));

        // Peer → SDK: an SRTCP packet must surface as decrypted RTCP via ControlPacketReceived.
        var peerRtcp = Rtcp(ssrc: 0x55667788);
        var peerSrtcp = peerEncrypt.ProtectRtcp(peerRtcp);
        await peer.SendAsync(peerSrtcp, peerSrtcp.Length, new IPEndPoint(IPAddress.Loopback, localPort));

        var delivered = await inboundReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
        Assert.Equal(peerRtcp, delivered);
    }

    [Fact]
    public async Task Receive_loop_survives_malformed_srtcp_packets()
    {
        var remoteKeys = Keys(90);
        var localPort = FreeUdpPort();
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var peerEncrypt = new SrtcpContext(Keys(90));

        await using var session = new RtpSession(
            new RtpSessionOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)peer.Client.LocalEndPoint!).Port),
                PayloadType = 0,
                ClockRate = 8000,
                SamplesPerPacket = 160,
                InboundSrtcp = new SrtcpContext(remoteKeys)
            },
            new RtpPacketCodec(), NullLogger<RtpSession>.Instance);

        var delivered = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ControlPacketReceived += d => delivered.TrySetResult(d);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await session.StartAsync(cts.Token);

        // A too-short RTCP-looking datagram (passes version/PT demux, shorter than the SRTCP
        // index+tag). Before the fix its ArgumentException escaped and killed the receive loop.
        var runt = new byte[] { 0x80, 200, 0x00, 0x00 };
        await peer.SendAsync(runt, runt.Length, new IPEndPoint(IPAddress.Loopback, localPort));
        await Task.Delay(100, cts.Token);

        // A genuine peer SRTCP packet afterwards must still be delivered — the loop lives.
        var peerRtcp = Rtcp(ssrc: 0x0BADF00D);
        var peerSrtcp = peerEncrypt.ProtectRtcp(peerRtcp);
        await peer.SendAsync(peerSrtcp, peerSrtcp.Length, new IPEndPoint(IPAddress.Loopback, localPort));

        var got = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
        Assert.Equal(peerRtcp, got);
    }

    [Fact]
    public async Task Inbound_tampered_srtcp_is_dropped()
    {
        var remoteKeys = Keys(90);
        var localPort = FreeUdpPort();
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var peerEncrypt = new SrtcpContext(Keys(90));

        await using var session = new RtpSession(
            new RtpSessionOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)peer.Client.LocalEndPoint!).Port),
                PayloadType = 0,
                ClockRate = 8000,
                SamplesPerPacket = 160,
                InboundSrtcp = new SrtcpContext(remoteKeys)
            },
            new RtpPacketCodec(), NullLogger<RtpSession>.Instance);

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ControlPacketReceived += d => received.TrySetResult(d);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await session.StartAsync(cts.Token);

        var tampered = peerEncrypt.ProtectRtcp(Rtcp(ssrc: 9));
        tampered[^1] ^= 0xFF; // corrupt the auth tag
        await peer.SendAsync(tampered, tampered.Length, new IPEndPoint(IPAddress.Loopback, localPort));

        // A follow-up plain-looking datagram would also be dropped, so simply assert the
        // tampered one never surfaces within a generous window.
        await Task.Delay(300, cts.Token);
        Assert.False(received.Task.IsCompleted);
    }
}
