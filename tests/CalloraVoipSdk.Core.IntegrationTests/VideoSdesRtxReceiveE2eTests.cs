using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Retransmission;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// SDES-keyed RTX receive end to end (RFC 4568 + RFC 4588): on an SDES leg the RTX repair
/// stream is keyed from the same video key material as its own SRTP context, so a retransmit
/// arriving SRTP-protected on the secondary stream recovers a dropped packet — the same
/// recovery the plain-RTP path proves, now over SRTP. A hand-crafted peer protects both the
/// video and the RTX with the receiver's inbound key. Without secondary keying the retransmit
/// would be dropped as fail-closed, so this test also guards that regression.
/// </summary>
public sealed class VideoSdesRtxReceiveE2eTests
{
    private const byte VideoPt = 96;
    private const byte RtxPt = 98;
    private const uint RemoteMediaSsrc = 0x0A0B0C0D;
    private const uint RtxSsrc = 0x0BADF00D;
    private const string Suite = "AES_CM_128_HMAC_SHA1_80";
    private static readonly RtpPacketCodec Codec = new();
    private static readonly Vp8Packetiser Packetiser = new();

    [Fact]
    public async Task Sdes_keyed_rtx_recovers_a_dropped_video_packet_in_order()
    {
        var receiverPort = FreeUdpPort();
        var peerPort = FreeUdpPort();
        // Receiver keys: local (outbound) 40, remote (inbound) 50 — the peer protects with 50.
        await using var receiver = CreateReceiver(receiverPort, peerPort, localSeed: 40, remoteSeed: 50);

        var delivered = new List<int>();
        var gate = new object();
        receiver.Video!.FrameReceived += (frame, _, _) =>
        {
            lock (gate) delivered.Add(frame[0]);
        };
        await receiver.StartAsync();

        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, peerPort));
        var target = new IPEndPoint(IPAddress.Loopback, receiverPort);

        // The video stream and the RTX stream are distinct SSRCs, so each gets its own SRTP
        // context (same master key, independent per-SSRC session keys and rollover state).
        using var videoSrtp = new SrtpContext(SrtpKeyMaterial.ParseInline(InlineKey(50), SrtpCryptoSuite.AesCm128HmacSha1_80));
        using var rtxSrtp = new SrtpContext(SrtpKeyMaterial.ParseInline(InlineKey(50), SrtpCryptoSuite.AesCm128HmacSha1_80));

        await SendVideo(peer, target, videoSrtp, 1);
        await SendVideo(peer, target, videoSrtp, 2);
        await SendRtx(peer, target, rtxSrtp, originalSeq: 3, rtxSeq: 1);
        for (ushort seq = 4; seq <= 64; seq++)
            await SendVideo(peer, target, videoSrtp, seq);

        await WaitUntil(() => { lock (gate) return delivered.Contains(3); });

        lock (gate)
        {
            Assert.Contains(3, delivered);
            Assert.Equal(delivered.OrderBy(id => id).ToArray(), delivered.ToArray());
        }
    }

    private static async Task SendVideo(UdpClient peer, IPEndPoint target, ISrtpContext srtp, ushort seq)
    {
        var payloads = Packetiser.Packetise(MakeFrame(seq), 1200);
        var packet = new RtpPacket
        {
            PayloadType = VideoPt,
            SequenceNumber = seq,
            Timestamp = seq * 3000u,
            Marker = payloads[0].IsLastOfFrame,
            Ssrc = RemoteMediaSsrc,
            Payload = payloads[0].Payload,
        };
        await peer.SendAsync(srtp.Protect(Codec.Encode(packet)), target);
        await Task.Delay(3);
    }

    private static async Task SendRtx(UdpClient peer, IPEndPoint target, ISrtpContext srtp, ushort originalSeq, ushort rtxSeq)
    {
        var payloads = Packetiser.Packetise(MakeFrame(originalSeq), 1200);
        var original = new RtpPacket
        {
            PayloadType = VideoPt,
            SequenceNumber = originalSeq,
            Timestamp = originalSeq * 3000u,
            Marker = payloads[0].IsLastOfFrame,
            Ssrc = RemoteMediaSsrc,
            Payload = payloads[0].Payload,
        };
        var rtx = RtxPacketFactory.Encapsulate(original, RtxPt, RtxSsrc, rtxSeq);
        await peer.SendAsync(srtp.Protect(Codec.Encode(rtx)), target);
        await Task.Delay(3);
    }

    private static byte[] MakeFrame(ushort id)
    {
        var frame = new byte[40];
        frame[0] = (byte)id;
        for (var i = 1; i < frame.Length; i++)
            frame[i] = (byte)(i * 5 + id);
        return frame;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, deadline.Token);
        }
    }

    private static string InlineKey(byte seed)
    {
        var material = new byte[30]; // AES_CM_128_HMAC_SHA1_80: 16-byte key + 14-byte salt
        for (var i = 0; i < material.Length; i++)
            material[i] = (byte)(seed + i);
        return $"inline:{Convert.ToBase64String(material)}";
    }

    private static RtpCallMediaSession CreateReceiver(int localVideoPort, int peerVideoPort, byte localSeed, byte remoteSeed)
    {
        var parameters = new CallMediaParameters
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
            PayloadType = 0,
            ClockRate = 8000,
            SamplesPerPacket = 160,
            IsSrtpNegotiated = true,
            Video = new CallVideoParameters
            {
                PayloadType = VideoPt,
                CodecName = "VP8",
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localVideoPort),
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, peerVideoPort),
                RtxPayloadType = RtxPt,
                RemoteSupportsNack = true,
                SrtpSuite = Suite,
                SrtpLocalKeyParams = InlineKey(localSeed),
                SrtpRemoteKeyParams = InlineKey(remoteSeed),
            },
        };

        return new RtpCallMediaSession(parameters, NullLoggerFactory.Instance);
    }

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
