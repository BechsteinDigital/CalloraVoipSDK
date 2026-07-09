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
/// Bridge audio transcoding (package B.2/O2): a µ-law-only media consumer keeps working
/// over any negotiated codec because the SDK transcodes wire &lt;-&gt; tap. Covers the founder
/// case Opus &lt;-&gt; PCMU: the transcoder itself, the wired-up media session (payload types,
/// timestamp mapping, wire encryption-free framing) and the passthrough regressions.
/// </summary>
public sealed class BridgeAudioTranscodingTests
{
    private const int RtpHeaderLength = 12;
    private const byte OpusPayloadType = 107;
    private const int OpusFrameSamples = 960; // 20 ms at the 48 kHz Opus RTP clock

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    // One 20 ms µ-law frame (160 bytes) of a 440 Hz tone at 8 kHz.
    private static byte[] MuLawTone()
    {
        var pcm = new byte[160 * 2];
        for (var i = 0; i < 160; i++)
        {
            var v = (short)(Math.Sin(2 * Math.PI * 440 * i / 8000.0) * 12000);
            pcm[i * 2] = (byte)v;
            pcm[i * 2 + 1] = (byte)(v >> 8);
        }
        return PcmG711Codec.EncodeMuLaw(pcm);
    }

    private static long MuLawEnergy(byte[] mulaw)
    {
        var pcm = PcmG711Codec.DecodeMuLaw(mulaw);
        long energy = 0;
        for (var i = 0; i < pcm.Length; i += 2)
            energy += Math.Abs((short)(pcm[i] | (pcm[i + 1] << 8)));
        return energy / Math.Max(1, pcm.Length / 2);
    }

    private static CallMediaParameters OpusWire(int localPort, int remotePort) => new()
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, remotePort),
        PayloadType = OpusPayloadType,
        CodecName = "opus",
        ClockRate = 48000,
        SamplesPerPacket = OpusFrameSamples
    };

    // ── Transcoder unit ─────────────────────────────────────────────────────────

    [Fact]
    public void Opus_tap_roundtrip_preserves_frame_size_and_energy()
    {
        var transcoder = BridgeAudioTranscoder.CreateForPcmuTap(
            PayloadCodecKind.Opus, OpusPayloadType, NullLogger.Instance);
        Assert.NotNull(transcoder);
        Assert.Equal(OpusPayloadType, transcoder!.WirePayloadType);
        Assert.Equal(0, transcoder.TapPayloadType);

        var tone = MuLawTone();
        var wire = transcoder.TapToWire(tone);          // µ-law -> Opus
        Assert.InRange(wire.Length, 1, tone.Length);    // compressed
        var backToTap = transcoder.WireToTap(wire);     // Opus -> µ-law
        Assert.Equal(160, backToTap.Length);            // 20 ms µ-law frame
        Assert.True(MuLawEnergy(backToTap) > 300, "transcoded audio is near-silent");
    }

    [Fact]
    public void Alaw_wire_transcodes_to_and_from_mulaw_tap()
    {
        var transcoder = BridgeAudioTranscoder.CreateForPcmuTap(
            PayloadCodecKind.Pcma, wirePayloadType: 8, NullLogger.Instance);
        Assert.NotNull(transcoder);

        var alawIn = PcmG711Codec.EncodeALaw(PcmG711Codec.DecodeMuLaw(MuLawTone()));
        var tap = transcoder!.WireToTap(alawIn);        // A-law -> µ-law
        Assert.Equal(160, tap.Length);
        var wire = transcoder.TapToWire(tap);           // µ-law -> A-law
        Assert.Equal(160, wire.Length);
    }

    [Fact]
    public void Pcmu_wire_needs_no_transcoder()
    {
        Assert.Null(BridgeAudioTranscoder.CreateForPcmuTap(
            PayloadCodecKind.Pcmu, wirePayloadType: 0, NullLogger.Instance));
    }

    [Fact]
    public void Unbridgeable_wire_codec_returns_null_passthrough()
    {
        // G.722 is 16 kHz — needs a resampler, out of scope; must not build a transcoder.
        Assert.Null(BridgeAudioTranscoder.CreateForPcmuTap(
            PayloadCodecKind.G722, wirePayloadType: 9, NullLogger.Instance));
    }

    // ── Session wiring: outbound encodes to wire, timestamp maps 1:1 ────────────

    [Fact]
    public async Task Opus_wire_pcmu_tap_encodes_outbound_frames_with_wire_pt_and_960_timestamp_step()
    {
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerPort = ((IPEndPoint)peer.Client.LocalEndPoint!).Port;

        await using var session = (RtpCallMediaSession)new RtpCallMediaSessionFactory(
                NullLoggerFactory.Instance, PayloadCodecKind.Pcmu)
            .Create(OpusWire(FreeUdpPort(), peerPort));
        Assert.True(session.BridgeTranscodingActive);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await session.StartAsync(cts.Token);

        // The consumer hands us µ-law; two frames must go out as Opus (PT 107) with the
        // wire timestamp advancing exactly one 20 ms Opus frame (960) between them.
        await session.SendFrameAsync(new CallAudioFrame(MuLawTone(), 0, 160), cts.Token);
        await session.SendFrameAsync(new CallAudioFrame(MuLawTone(), 0, 160), cts.Token);

        var first = new RtpPacketCodec().Decode((await peer.ReceiveAsync(cts.Token)).Buffer);
        var second = new RtpPacketCodec().Decode((await peer.ReceiveAsync(cts.Token)).Buffer);

        Assert.Equal(OpusPayloadType, first.PayloadType);
        Assert.Equal(OpusPayloadType, second.PayloadType);
        Assert.Equal(960u, second.Timestamp - first.Timestamp);

        // The wire payload is genuine Opus: a peer decoder recovers audible audio.
        var peerDecoded = new OpusPayloadCodec(8000).Decode(first.Payload.Span);
        Assert.Equal(160 * 2, peerDecoded.Length); // 20 ms PCM16 at 8 kHz
    }

    // ── Session wiring: inbound decodes Opus to µ-law for the consumer ──────────

    [Fact]
    public async Task Opus_wire_pcmu_tap_delivers_inbound_frames_as_mulaw()
    {
        var localPort = FreeUdpPort();
        await using var session = (RtpCallMediaSession)new RtpCallMediaSessionFactory(
                NullLoggerFactory.Instance, PayloadCodecKind.Pcmu)
            .Create(OpusWire(localPort, FreeUdpPort()));

        var received = new TaskCompletionSource<CallAudioFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.FrameReceived += f => received.TrySetResult(f);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await session.StartAsync(cts.Token);

        // A peer sends real Opus packets; the session must decode them to 160-byte µ-law.
        var peerOpus = new OpusPayloadCodec(8000);
        var pcm = new byte[160 * 2];
        for (var i = 0; i < 160; i++)
        {
            var v = (short)(Math.Sin(2 * Math.PI * 440 * i / 8000.0) * 10000);
            pcm[i * 2] = (byte)v;
            pcm[i * 2 + 1] = (byte)(v >> 8);
        }
        var opusPayload = peerOpus.Encode(pcm);

        using var attacker = new UdpClient();
        var codec = new RtpPacketCodec();
        for (ushort seq = 1; seq <= 10 && !received.Task.IsCompleted; seq++)
        {
            var packet = codec.Encode(new RtpPacket
            {
                PayloadType = OpusPayloadType,
                SequenceNumber = seq,
                Timestamp = (uint)(seq * OpusFrameSamples),
                Ssrc = 0xABCD,
                Payload = opusPayload
            });
            await attacker.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, localPort));
            await Task.Delay(20, cts.Token);
        }

        var frame = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        Assert.Equal(0, frame.PayloadType);   // delivered as PCMU
        Assert.Equal(160, frame.Payload.Length);
    }

    // ── Regression: passthrough stays byte-identical ───────────────────────────

    [Fact]
    public async Task Pcmu_wire_pcmu_tap_is_passthrough_byte_identical()
    {
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerPort = ((IPEndPoint)peer.Client.LocalEndPoint!).Port;

        var parameters = new CallMediaParameters
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, peerPort),
            PayloadType = 0,
            CodecName = "PCMU",
            ClockRate = 8000,
            SamplesPerPacket = 160
        };

        await using var session = (RtpCallMediaSession)new RtpCallMediaSessionFactory(
                NullLoggerFactory.Instance, PayloadCodecKind.Pcmu)
            .Create(parameters);
        Assert.False(session.BridgeTranscodingActive); // wire already == tap

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await session.StartAsync(cts.Token);

        var tone = MuLawTone();
        await session.SendFrameAsync(new CallAudioFrame(tone, 0, 160), cts.Token);

        var packet = new RtpPacketCodec().Decode((await peer.ReceiveAsync(cts.Token)).Buffer);
        Assert.Equal(0, packet.PayloadType);
        Assert.Equal(tone, packet.Payload.ToArray()); // unchanged
    }

    [Fact]
    public void Passthrough_default_does_not_transcode_even_for_opus_wire()
    {
        var session = (RtpCallMediaSession)new RtpCallMediaSessionFactory(NullLoggerFactory.Instance)
            .Create(OpusWire(FreeUdpPort(), FreeUdpPort()));
        Assert.False(session.BridgeTranscodingActive);
    }
}
