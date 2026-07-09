using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// SRTP hardening (package B.1): header bounds validation (a malformed CSRC/extension
/// header must be a clean drop, not an uncontrolled exception that kills the receive
/// loop), key zeroing on dispose (RFC 3711 §9.4), and thread-safe context state.
/// </summary>
public sealed class SrtpHardeningTests
{
    private static string InlineKey(byte seed)
    {
        var material = new byte[30];
        for (var i = 0; i < material.Length; i++)
            material[i] = (byte)(seed + i);
        return $"inline:{Convert.ToBase64String(material)}";
    }

    private static SrtpContext Context(byte seed = 10) =>
        new(SrtpKeyMaterial.ParseInline(InlineKey(seed), SrtpCryptoSuite.AesCm128HmacSha1_80));

    // ── Header bounds (K4) ───────────────────────────────────────────────────────

    [Fact]
    public void Csrc_count_exceeding_packet_length_throws_cryptographic_exception()
    {
        // V=2, CC=15 claims 12+60 header bytes in a 20-byte packet. Before the fix the
        // negative payload length escaped as ArgumentOutOfRangeException.
        var packet = new byte[20];
        packet[0] = 0x8F;

        var ex = Record.Exception(() => SrtpContext.GetRtpHeaderLength(packet));
        Assert.IsType<CryptographicException>(ex);
    }

    [Fact]
    public void Truncated_extension_header_throws_cryptographic_exception()
    {
        // V=2, X=1 but the packet ends right at the fixed header — no extension header.
        var packet = new byte[13];
        packet[0] = 0x90;

        Assert.IsType<CryptographicException>(Record.Exception(() => SrtpContext.GetRtpHeaderLength(packet)));
    }

    [Fact]
    public void Extension_words_exceeding_packet_length_throw_cryptographic_exception()
    {
        // V=2, X=1, extension header present but claiming 0xFFFF words.
        var packet = new byte[20];
        packet[0] = 0x90;
        packet[14] = 0xFF;
        packet[15] = 0xFF;

        Assert.IsType<CryptographicException>(Record.Exception(() => SrtpContext.GetRtpHeaderLength(packet)));
    }

    [Fact]
    public void Valid_csrc_and_extension_header_yields_correct_offset()
    {
        // V=2, CC=2, X=1, 2 CSRCs (8 bytes) + extension header (4) + 1 extension word (4).
        var packet = new byte[12 + 8 + 4 + 4 + 3];
        packet[0] = 0x92;
        packet[12 + 8 + 2] = 0x00;
        packet[12 + 8 + 3] = 0x01;

        Assert.Equal(12 + 8 + 4 + 4, SrtpContext.GetRtpHeaderLength(packet));
    }

    [Fact]
    public void Protect_rejects_malformed_header_with_cryptographic_exception()
    {
        using var context = Context();
        var malformed = new byte[20];
        malformed[0] = 0x8F; // CC=15 in a 20-byte packet

        Assert.IsType<CryptographicException>(Record.Exception(() => context.Protect(malformed)));
    }

    [Fact]
    public async Task Receive_loop_survives_malformed_srtp_packets()
    {
        var keyA = InlineKey(30);
        var portB = FreeUdpPort();
        var codec = new RtpPacketCodec();
        using var inbound = new SrtpContext(SrtpKeyMaterial.ParseInline(keyA, SrtpCryptoSuite.AesCm128HmacSha1_80));

        await using var receiver = new RtpSession(
            new RtpSessionOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, portB),
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
                PayloadType = 0,
                ClockRate = 8000,
                SamplesPerPacket = 160,
                InboundSrtp = inbound
            },
            codec, NullLogger<RtpSession>.Instance);

        var delivered = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.PacketReceived += (_, p) => delivered.TrySetResult(p.Payload.ToArray());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await receiver.StartAsync(cts.Token);

        // Garbage with CC=15 and RTP version bits — fails authentication (clean drop)
        // and must not terminate the receive loop.
        var garbage = new byte[40];
        garbage[0] = 0x8F;
        using var attacker = new UdpClient();
        await attacker.SendAsync(garbage, garbage.Length, new IPEndPoint(IPAddress.Loopback, portB));
        await Task.Delay(100, cts.Token);

        // A genuinely protected packet afterwards must still be delivered.
        using var sender = new SrtpContext(SrtpKeyMaterial.ParseInline(keyA, SrtpCryptoSuite.AesCm128HmacSha1_80));
        var valid = sender.Protect(codec.Encode(new RtpPacket
        {
            PayloadType = 0,
            SequenceNumber = 7,
            Timestamp = 1120,
            Ssrc = 0xBEEF,
            Payload = new byte[] { 0x77 }
        }));
        await attacker.SendAsync(valid, valid.Length, new IPEndPoint(IPAddress.Loopback, portB));

        var payload = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
        Assert.Equal(new byte[] { 0x77 }, payload);
    }

    [Fact]
    public async Task Receive_loop_survives_short_srtp_packets()
    {
        var keyA = InlineKey(31);
        var portB = FreeUdpPort();
        var codec = new RtpPacketCodec();
        using var inbound = new SrtpContext(SrtpKeyMaterial.ParseInline(keyA, SrtpCryptoSuite.AesCm128HmacSha1_80));

        await using var receiver = new RtpSession(
            new RtpSessionOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, portB),
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
                PayloadType = 0,
                ClockRate = 8000,
                SamplesPerPacket = 160,
                InboundSrtp = inbound
            },
            codec, NullLogger<RtpSession>.Instance);

        var delivered = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.PacketReceived += (_, p) => delivered.TrySetResult(p.Payload.ToArray());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await receiver.StartAsync(cts.Token);

        // A short RTP-demuxed datagram (V=2, PT=0, shorter than 12 + auth-tag) makes
        // Unprotect throw ArgumentException. Before the fix that escaped and killed the loop.
        var runt = new byte[] { 0x80, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        using var attacker = new UdpClient();
        await attacker.SendAsync(runt, runt.Length, new IPEndPoint(IPAddress.Loopback, portB));
        await Task.Delay(100, cts.Token);

        using var sender = new SrtpContext(SrtpKeyMaterial.ParseInline(keyA, SrtpCryptoSuite.AesCm128HmacSha1_80));
        var valid = sender.Protect(codec.Encode(new RtpPacket
        {
            PayloadType = 0,
            SequenceNumber = 3,
            Timestamp = 480,
            Ssrc = 0xBEEF,
            Payload = new byte[] { 0x2A }
        }));
        await attacker.SendAsync(valid, valid.Length, new IPEndPoint(IPAddress.Loopback, portB));

        var payload = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
        Assert.Equal(new byte[] { 0x2A }, payload);
    }

    // ── Key zeroing on dispose (K2) ─────────────────────────────────────────────

    [Fact]
    public void Dispose_zeroes_session_keys_and_rejects_further_use()
    {
        var context = Context();
        var keys = context.SessionKeys;
        Assert.Contains(keys.CipherKey, b => b != 0);
        Assert.Contains(keys.AuthKey, b => b != 0);
        Assert.Contains(keys.Salt, b => b != 0);

        context.Dispose();

        Assert.All(keys.CipherKey, b => Assert.Equal(0, b));
        Assert.All(keys.Salt, b => Assert.Equal(0, b));
        Assert.All(keys.AuthKey, b => Assert.Equal(0, b));

        var packet = new byte[172];
        packet[0] = 0x80;
        Assert.Throws<ObjectDisposedException>(() => context.Protect(packet));
        Assert.Throws<ObjectDisposedException>(() => context.Unprotect(new byte[172]));

        context.Dispose(); // idempotent
    }

    // ── Thread safety (K1) ──────────────────────────────────────────────────────

    [Fact]
    public async Task Concurrent_protect_on_one_context_yields_decodable_packets()
    {
        const int threads = 4;
        const int perThread = 250;
        using var shared = Context(50);
        var codec = new RtpPacketCodec();
        var protectedPackets = new byte[threads * perThread][];

        await Task.WhenAll(Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < perThread; i++)
            {
                var seq = (ushort)(t * perThread + i);
                var packet = codec.Encode(new RtpPacket
                {
                    PayloadType = 0,
                    SequenceNumber = seq,
                    Timestamp = (uint)(seq * 160),
                    Ssrc = 0xFEED,
                    Payload = new[] { (byte)(seq & 0xFF), (byte)(seq >> 8) }
                });
                protectedPackets[seq] = shared.Protect(packet);
            }
        })));

        // Unprotect in sequence order (the receiver replay window rejects too-old
        // packets); every packet must authenticate and decrypt to its seq marker.
        using var receiver = Context(50);
        for (var seq = 0; seq < threads * perThread; seq++)
        {
            var plain = codec.Decode(receiver.Unprotect(protectedPackets[seq]));
            Assert.Equal((byte)(seq & 0xFF), plain.Payload.Span[0]);
            Assert.Equal((byte)(seq >> 8), plain.Payload.Span[1]);
        }
    }

    // ── Ownership: media session disposes its contexts (K3) ────────────────────

    [Fact]
    public async Task Media_session_dispose_zeroes_its_srtp_contexts()
    {
        var parameters = new CallMediaParameters
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
            PayloadType = 0,
            ClockRate = 8000,
            SamplesPerPacket = 160,
            SrtpSuite = "AES_CM_128_HMAC_SHA1_80",
            SrtpLocalKeyParams = InlineKey(70),
            SrtpRemoteKeyParams = InlineKey(90)
        };

        var media = (RtpCallMediaSession)new RtpCallMediaSessionFactory(NullLoggerFactory.Instance)
            .Create(parameters);
        var outbound = media.OutboundSrtpContext;
        var inbound = media.InboundSrtpContext;
        Assert.NotNull(outbound);
        Assert.NotNull(inbound);

        await media.DisposeAsync();

        var packet = new byte[172];
        packet[0] = 0x80;
        Assert.Throws<ObjectDisposedException>(() => outbound!.Protect(packet));
        Assert.Throws<ObjectDisposedException>(() => inbound!.Protect(packet));
    }

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
