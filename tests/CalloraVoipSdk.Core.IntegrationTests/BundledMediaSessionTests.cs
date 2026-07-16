using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// A full BUNDLE media session end to end (ADR-011 B5): two <see cref="BundledMediaSession"/> instances
/// over loopback assemble one shared transport each — DTLS-keyed, ICE-active — carrying an audio track and
/// a video track. After the shared DTLS handshake, audio packets and a video frame sent by one arrive at
/// the other, demultiplexed by MID over the one socket. This exercises the whole transport stack (B1–B4)
/// as one composed unit: shared socket, per-SSRC SRTP, MID routing, and the video payload format.
/// </summary>
public sealed class BundledMediaSessionTests
{
    private const byte MidExtId = 3;
    private const byte AudioPayloadType = 0;
    private const byte VideoPayloadType = 96;

    [Fact]
    public async Task Audio_and_video_flow_over_one_dtls_keyed_ice_active_bundle()
    {
        var certA = DtlsCertificate.GenerateEcdsaP256();
        var certB = DtlsCertificate.GenerateEcdsaP256();

        var (client, server) = CreatePair(certA, certB);
        await using var clientLease = client;
        await using var serverLease = server;

        var audio = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var video = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AudioReceived += p => audio.TrySetResult(p.Payload.ToArray());
        server.VideoFrameReceived += (f, _, _) => video.TrySetResult(f);

        await server.StartAsync();
        await client.StartAsync();

        var audioPayload = new byte[] { 1, 2, 3, 4 };
        var videoFrame = AnnexB((Nal(0x67, 20), false), (Nal(0x68, 6), false), (Nal(0x65, 3000), false));

        // Media is suppressed until the shared DTLS handshake keys the transport; keep sending so the
        // first audio packet and video frame to land prove the whole keyed bundle carries both tracks.
        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var videoTimestamp = 90000u;
        while (!(audio.Task.IsCompleted && video.Task.IsCompleted))
        {
            overall.Token.ThrowIfCancellationRequested();
            await client.SendAudioAsync(audioPayload);
            await client.SendVideoFrameAsync(videoFrame, videoTimestamp);
            videoTimestamp += 3000;
            await Task.Delay(20, overall.Token);
        }

        Assert.Equal(audioPayload, await audio.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(
            AnnexBParser.ParseNalUnits(videoFrame).Select(n => n.ToArray()),
            AnnexBParser.ParseNalUnits(await video.Task.WaitAsync(TimeSpan.FromSeconds(5))).Select(n => n.ToArray()));
    }

    // ── harness ──────────────────────────────────────────────────────────────────

    private const string ClientPwd = "clienticepassword1234567890";
    private const string ServerPwd = "servericepassword1234567890";

    // Two peers each need the other's port before construction, so ports are pre-allocated. Under the
    // parallel suite two probes can hand out the same free port and one bind then loses the race — retry
    // with fresh ports rather than flake.
    private static (BundledMediaSession Client, BundledMediaSession Server) CreatePair(
        DtlsCertificate certA, DtlsCertificate certB)
    {
        for (var attempt = 1; ; attempt++)
        {
            var portA = FreeUdpPort();
            var portB = FreeUdpPort();
            BundledMediaSession? client = null;
            try
            {
                client = new BundledMediaSession(
                    Options(portA, portB, isClient: true, certB.Fingerprint, controlling: true,
                        localUfrag: "cli0", localPwd: ClientPwd, remoteUfrag: "srv0", remotePwd: ServerPwd),
                    new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), certA, NullLoggerFactory.Instance);
                var server = new BundledMediaSession(
                    Options(portB, portA, isClient: false, certA.Fingerprint, controlling: false,
                        localUfrag: "srv0", localPwd: ServerPwd, remoteUfrag: "cli0", remotePwd: ClientPwd),
                    new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), certB, NullLoggerFactory.Instance);
                return (client, server);
            }
            catch (SocketException) when (attempt < 8)
            {
                client?.DisposeAsync().AsTask().GetAwaiter().GetResult(); // free the port the first peer bound
            }
        }
    }

    private static BundledMediaSessionOptions Options(
        int localPort, int remotePort, bool isClient, DtlsFingerprint remoteFingerprint, bool controlling,
        string localUfrag, string localPwd, string remoteUfrag, string remotePwd)
    {
        var remote = new IPEndPoint(IPAddress.Loopback, remotePort);
        return new BundledMediaSessionOptions
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
            RemoteEndPoint = remote,
            MidExtensionId = MidExtId,
            Audio = new BundledTrackConfig
            {
                Mid = "audio", Ssrc = 0x0A0A0A0A, PayloadType = AudioPayloadType, SamplesPerPacket = 160,
            },
            Video = new BundledTrackConfig
            {
                Mid = "video", Ssrc = 0x0B0B0B0B, PayloadType = VideoPayloadType, VideoCodecName = "H264",
            },
            DtlsIsClient = isClient,
            RemoteFingerprint = remoteFingerprint,
            Ice = new IceMediaParameters(
                remote, IceEnabled: true, IceControlling: controlling,
                LocalIceUfrag: localUfrag, LocalIcePwd: localPwd,
                RemoteIceUfrag: remoteUfrag, RemoteIcePwd: remotePwd),
        };
    }

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private static byte[] Nal(byte header, int bodyLength)
    {
        var nal = new byte[1 + bodyLength];
        nal[0] = header;
        for (var i = 1; i < nal.Length; i++)
            nal[i] = (byte)(1 + (i % 250));
        return nal;
    }

    private static byte[] AnnexB(params (byte[] Nal, bool LongStartCode)[] nals)
    {
        var stream = new MemoryStream();
        foreach (var (nal, longStartCode) in nals)
        {
            stream.Write(longStartCode ? new byte[] { 0, 0, 0, 1 } : new byte[] { 0, 0, 1 });
            stream.Write(nal);
        }

        return stream.ToArray();
    }
}
