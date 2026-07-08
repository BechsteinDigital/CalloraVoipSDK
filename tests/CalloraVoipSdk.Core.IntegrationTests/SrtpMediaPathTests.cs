using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// SRTP media path (package S2, RFC 3711): negotiated SDES keys must actually encrypt
/// the RTP packets on the wire and decrypt them at the peer — proven at packet level
/// through real <see cref="RtpSession"/> loopback, including tamper rejection and the
/// recovery of key material from the SDP strings that went over the wire.
/// </summary>
public sealed class SrtpMediaPathTests
{
    private const string Suite = "AES_CM_128_HMAC_SHA1_80";
    private const int AuthTagLength = 10;
    private const int RtpHeaderLength = 12;

    private static string InlineKey(byte seed)
    {
        var material = new byte[30];
        for (var i = 0; i < material.Length; i++)
            material[i] = (byte)(seed + i);
        return $"inline:{Convert.ToBase64String(material)}";
    }

    private static SrtpContext Context(string inlineKey) =>
        new(SrtpKeyMaterial.ParseInline(inlineKey, SrtpCryptoSuite.AesCm128HmacSha1_80));

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private static RtpSessionOptions Options(
        int localPort, IPEndPoint remote, ISrtpContext? outbound = null, ISrtpContext? inbound = null) => new()
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
        RemoteEndPoint = remote,
        PayloadType = 0,
        ClockRate = 8000,
        SamplesPerPacket = 160,
        OutboundSrtp = outbound,
        InboundSrtp = inbound
    };

    // ── Key transport: the answer chain must retain our generated key ────────────

    [Fact]
    public void Answer_chain_retains_local_key_recoverable_from_serialized_sdp()
    {
        var offerKey = InlineKey(1);
        var offer = new SdpSessionDescription
        {
            OriginAddress = "203.0.113.5",
            ConnectionAddress = "203.0.113.5",
            Media =
            [
                new SdpMediaDescription
                {
                    MediaType = "audio",
                    Port = 20000,
                    Profile = "RTP/SAVP",
                    Codecs = [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }],
                    Direction = SdpMediaDirection.SendRecv,
                    Crypto = [new SdpCryptoAttribute { Tag = 1, CryptoSuite = Suite, KeyParams = offerKey }]
                }
            ]
        };

        var result = new SdpOfferAnswerNegotiator().NegotiateAnswer(
            offer,
            new IPEndPoint(IPAddress.Parse("192.0.2.10"), 40000),
            [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }],
            SdpMediaDirection.SendRecv);
        var answerSdp = new SdpSessionSerializer().Serialize(result.Answer!);

        // The key recovered from the serialized answer is exactly the generated local
        // key — and never the peer's (the S1 mirroring bug made these equal).
        var recovered = SdpUtilities.TryExtractAudioCrypto(answerSdp);
        Assert.NotNull(recovered);
        Assert.Equal(result.LocalCrypto!.KeyParams, recovered!.KeyParams);
        Assert.NotEqual(offerKey, recovered.KeyParams);

        // And the remote key is recoverable from the offer side the same way.
        var remoteRecovered = SdpUtilities.TryExtractAudioCrypto(new SdpSessionSerializer().Serialize(offer));
        Assert.Equal(offerKey, remoteRecovered!.KeyParams);
    }

    [Fact]
    public void Plain_rtp_sdp_yields_no_crypto()
    {
        const string plainSdp = "v=0\r\no=- 0 0 IN IP4 192.0.2.1\r\ns=t\r\nc=IN IP4 192.0.2.1\r\nt=0 0\r\n"
            + "m=audio 20000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n";

        Assert.Null(SdpUtilities.TryExtractAudioCrypto(plainSdp));
        Assert.Null(SdpUtilities.TryExtractAudioCrypto(null));
    }

    // ── Wire-level encryption through a real RtpSession ─────────────────────────

    [Fact]
    public async Task Outbound_packets_are_encrypted_on_the_wire_and_carry_the_auth_tag()
    {
        var payload = new byte[160];
        Array.Fill(payload, (byte)0x55);
        var keyA = InlineKey(10);

        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var listenerPort = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        await using var sender = new RtpSession(
            Options(FreeUdpPort(), new IPEndPoint(IPAddress.Loopback, listenerPort), outbound: Context(keyA)),
            new RtpPacketCodec(), NullLogger<RtpSession>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sender.SendAsync(payload, cancellationToken: cts.Token);

        var received = await listener.ReceiveAsync(cts.Token);
        var datagram = received.Buffer;

        // RFC 3711: header in the clear, payload encrypted, 80-bit auth tag appended.
        Assert.Equal(RtpHeaderLength + payload.Length + AuthTagLength, datagram.Length);
        Assert.NotEqual(payload, datagram[RtpHeaderLength..(RtpHeaderLength + payload.Length)]);

        // A context with the same key material decrypts back to the original payload.
        var decrypted = Context(keyA).Unprotect(datagram);
        var packet = new RtpPacketCodec().Decode(decrypted);
        Assert.Equal(payload, packet.Payload.ToArray());
    }

    [Fact]
    public async Task Srtp_loopback_decrypts_to_the_original_payload_in_both_directions()
    {
        var keyA = InlineKey(20);
        var keyB = InlineKey(60);
        var portA = FreeUdpPort();
        var portB = FreeUdpPort();
        var codec = new RtpPacketCodec();

        await using var a = new RtpSession(
            Options(portA, new IPEndPoint(IPAddress.Loopback, portB), outbound: Context(keyA), inbound: Context(keyB)),
            codec, NullLogger<RtpSession>.Instance);
        await using var b = new RtpSession(
            Options(portB, new IPEndPoint(IPAddress.Loopback, portA), outbound: Context(keyB), inbound: Context(keyA)),
            codec, NullLogger<RtpSession>.Instance);

        var atB = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var atA = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.PacketReceived += (_, p) => atB.TrySetResult(p.Payload.ToArray());
        a.PacketReceived += (_, p) => atA.TrySetResult(p.Payload.ToArray());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await a.StartAsync(cts.Token);
        await b.StartAsync(cts.Token);

        await a.SendAsync(new byte[] { 1, 2, 3, 4 }, cancellationToken: cts.Token);
        await b.SendAsync(new byte[] { 9, 8, 7 }, cancellationToken: cts.Token);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await atB.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token));
        Assert.Equal(new byte[] { 9, 8, 7 }, await atA.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token));
    }

    [Fact]
    public async Task Tampered_packets_are_dropped_and_do_not_reach_the_receiver()
    {
        var keyA = InlineKey(30);
        var portB = FreeUdpPort();
        var codec = new RtpPacketCodec();

        await using var receiver = new RtpSession(
            Options(portB, new IPEndPoint(IPAddress.Loopback, FreeUdpPort()), inbound: Context(keyA)),
            codec, NullLogger<RtpSession>.Instance);

        var deliveries = new List<byte[]>();
        var validArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.PacketReceived += (_, p) =>
        {
            lock (deliveries)
                deliveries.Add(p.Payload.ToArray());
            validArrived.TrySetResult();
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await receiver.StartAsync(cts.Token);

        // Craft a genuinely protected packet, then flip one encrypted payload byte:
        // the auth tag no longer matches and the receiver must drop it silently.
        var protectedPacket = Context(keyA).Protect(new RtpPacketCodec().Encode(new RtpPacket
        {
            PayloadType = 0,
            SequenceNumber = 100,
            Timestamp = 1600,
            Ssrc = 0x1234,
            Payload = new byte[] { 0xAA, 0xBB, 0xCC }
        }));
        var tampered = (byte[])protectedPacket.Clone();
        tampered[RtpHeaderLength] ^= 0xFF;

        using var attacker = new UdpClient();
        await attacker.SendAsync(tampered, tampered.Length, new IPEndPoint(IPAddress.Loopback, portB));
        await Task.Delay(150, cts.Token);
        lock (deliveries)
            Assert.Empty(deliveries);

        // The untampered original still decrypts and is delivered.
        await attacker.SendAsync(protectedPacket, protectedPacket.Length, new IPEndPoint(IPAddress.Loopback, portB));
        await validArrived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
        lock (deliveries)
            Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, Assert.Single(deliveries));
    }

    [Fact]
    public async Task Plain_rtp_sessions_stay_plain_without_srtp_contexts()
    {
        var payload = new byte[] { 0x42, 0x43 };
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var listenerPort = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        await using var sender = new RtpSession(
            Options(FreeUdpPort(), new IPEndPoint(IPAddress.Loopback, listenerPort)),
            new RtpPacketCodec(), NullLogger<RtpSession>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sender.SendAsync(payload, cancellationToken: cts.Token);

        var datagram = (await listener.ReceiveAsync(cts.Token)).Buffer;
        Assert.Equal(RtpHeaderLength + payload.Length, datagram.Length);
        Assert.Equal(payload, datagram[RtpHeaderLength..]);
    }
}
