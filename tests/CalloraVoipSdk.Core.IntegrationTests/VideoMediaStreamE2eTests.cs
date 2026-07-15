using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Video media stream end to end (WebRTC phase 2b slice 2): two real
/// <see cref="RtpCallMediaSession"/> instances exchange encoded video frames over UDP
/// loopback — plain and DTLS-keyed — proving packetisation, reassembly, and fail-closed
/// guarantees at the media level. Sequence-gap frame discard is covered at the
/// packetisation layer (VideoPacketisationTests); it cannot be injected deterministically
/// through this socket-level harness.
/// </summary>
public sealed class VideoMediaStreamE2eTests
{
    [Theory]
    [InlineData("VP8", 96)]
    [InlineData("H264", 97)]
    public async Task Encoded_video_frames_round_trip_over_the_media_session(string codec, int payloadType)
    {
        var portA = FreeUdpPort();
        var portB = FreeUdpPort();

        await using var sender = CreateSession(VideoParameters(portA, portB, codec, payloadType));
        await using var receiver = CreateSession(VideoParameters(portB, portA, codec, payloadType));

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.Video!.FrameReceived += (frame, _, _) => received.TrySetResult(frame);

        await receiver.StartAsync();
        await sender.StartAsync();

        // A frame large enough to fragment (H.264 as FU-A, VP8 across the payload budget).
        var frame = EncodedFrame(codec, 3500);

        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        uint ts = 3000;
        while (!received.Task.IsCompleted)
        {
            overall.Token.ThrowIfCancellationRequested();
            await sender.Video!.SendFrameAsync(frame, ts, overall.Token);
            ts += 3000;
            await Task.Delay(20, overall.Token);
        }

        Assert.Equal(frame, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Dtls_keyed_video_frames_round_trip_encrypted()
    {
        var portA = FreeUdpPort();
        var portB = FreeUdpPort();
        var certA = DtlsCertificate.GenerateEcdsaP256();
        var certB = DtlsCertificate.GenerateEcdsaP256();

        await using var sender = CreateSession(
            DtlsVideoParameters(portA, portB, isClient: true, certB.Fingerprint), certA);
        await using var receiver = CreateSession(
            DtlsVideoParameters(portB, portA, isClient: false, certA.Fingerprint), certB);

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.Video!.FrameReceived += (frame, _, _) => received.TrySetResult(frame);

        await receiver.StartAsync();
        await sender.StartAsync();

        var frame = EncodedFrame("VP8", 2000);
        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        uint ts = 90000;
        while (!received.Task.IsCompleted)
        {
            overall.Token.ThrowIfCancellationRequested();
            await sender.Video!.SendFrameAsync(frame, ts, overall.Token);
            ts += 3000;
            await Task.Delay(20, overall.Token);
        }

        Assert.Equal(frame, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Dtls_video_never_sends_plaintext_before_handshake()
    {
        var localPort = FreeUdpPort();
        var peerPort = FreeUdpPort();
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, peerPort));
        var rtpLikeDatagrams = 0;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var result = await peer.ReceiveAsync();
                if (result.Buffer.Length > 0 && result.Buffer[0] >= 128) // RTP, not DTLS
                    Interlocked.Increment(ref rtpLikeDatagrams);
            }
        });

        // Peer never completes DTLS — the video stream must stay fail-closed.
        await using var session = CreateSession(
            DtlsVideoParameters(localPort, peerPort, isClient: true,
                DtlsCertificate.GenerateEcdsaP256().Fingerprint),
            DtlsCertificate.GenerateEcdsaP256());
        await session.StartAsync();

        var frame = EncodedFrame("VP8", 2000);
        for (var i = 0; i < 5; i++)
        {
            await session.Video!.SendFrameAsync(frame, (uint)(90000 * (i + 1)));
            await Task.Delay(20);
        }

        await Task.Delay(300);
        Assert.Equal(0, Volatile.Read(ref rtpLikeDatagrams));
    }

    [Fact]
    public void Audio_only_leg_has_no_video_stream()
    {
        var parameters = new CallMediaParameters
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
            PayloadType = 0,
            ClockRate = 8000,
            SamplesPerPacket = 160,
        };

        using var _ = new CancellationTokenSource();
        var session = new RtpCallMediaSession(parameters, NullLoggerFactory.Instance);
        Assert.Null(session.Video);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static CallMediaParameters VideoParameters(
        int localPort, int remotePort, string codec, int payloadType) => new()
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
        PayloadType = 0,
        ClockRate = 8000,
        SamplesPerPacket = 160,
        Video = new CallVideoParameters
        {
            PayloadType = payloadType,
            CodecName = codec,
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, remotePort),
        },
    };

    private static CallMediaParameters DtlsVideoParameters(
        int localPort, int remotePort, bool isClient, DtlsFingerprint remoteFingerprint) => new()
    {
        LocalEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort()),
        PayloadType = 0,
        ClockRate = 8000,
        SamplesPerPacket = 160,
        IsDtlsNegotiated = true,
        DtlsIsClient = isClient,
        DtlsRemoteFingerprintAlgorithm = remoteFingerprint.Algorithm,
        DtlsRemoteFingerprintValue = remoteFingerprint.Value,
        Video = new CallVideoParameters
        {
            PayloadType = 96,
            CodecName = "VP8",
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, remotePort),
        },
    };

    private static RtpCallMediaSession CreateSession(
        CallMediaParameters parameters, DtlsCertificate? certificate = null) =>
        new(parameters, NullLoggerFactory.Instance, bridgeTapCodec: null,
            certificate is null ? null : new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance),
            certificate);

    private static byte[] EncodedFrame(string codec, int length)
    {
        if (codec == "H264")
        {
            // One Annex-B IDR access unit large enough to require FU-A fragmentation.
            var frame = new byte[4 + length];
            frame[0] = 0; frame[1] = 0; frame[2] = 0; frame[3] = 1;
            frame[4] = 0x65; // NAL header: NRI=3, type=5 (IDR)
            for (var i = 5; i < frame.Length; i++)
                frame[i] = (byte)(1 + (i % 250)); // never 0x00 — unambiguous Annex-B
            return frame;
        }

        var vp8 = new byte[length];
        for (var i = 0; i < vp8.Length; i++)
            vp8[i] = (byte)(i * 7);
        return vp8;
    }

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
