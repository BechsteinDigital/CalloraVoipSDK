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
    private const byte TransportCcExtId = 5;
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

        // Stats counters (S1) reflect the exchanged media.
        Assert.True(client.SnapshotStats().PacketsSent > 0, "client should have sent RTP");
        Assert.True(client.SnapshotStats().BytesSent > 0, "client should have sent bytes");
        Assert.True(server.SnapshotStats().PacketsReceived > 0, "server should have received RTP");
        Assert.True(server.SnapshotStats().BytesReceived > 0, "server should have received bytes");

        // Stats video counters (S4): the server received frames including the IDR key frame.
        Assert.True(server.SnapshotStats().FramesReceived > 0, "server should have received video frames");
        Assert.True(server.SnapshotStats().KeyFrames > 0, "server should have received the key frame");

        // getStats video feedback/drop counters: present (non-null) once a video track exists; zero here because
        // the loopback is lossless (no reorder gap → no torn frame; no detected loss → no NACK/PLI sent).
        Assert.Equal(0L, server.SnapshotStats().FramesDropped);
        Assert.Equal(0L, server.SnapshotStats().NacksSent);
        Assert.Equal(0L, server.SnapshotStats().PlisSent);
    }

    [Fact]
    public async Task Two_video_tracks_and_audio_flow_over_one_bundle_without_cross_talk()
    {
        // P2b: two video m-lines (a camera and a screen-share pattern) plus audio ride the ONE bundle. Both
        // video tracks share the video payload type (PT 96), so inbound demux cannot rely on the PT — it must
        // route by the MID header extension (RFC 9143). Each track sends on its own bundle-wide-distinct SSRC,
        // and per-SSRC SRTP (ADR-011) keys ROC/replay per SSRC, so two simultaneous video streams decrypt
        // independently. This proves each track's frame lands on its own MID and never on the other's.
        var certA = DtlsCertificate.GenerateEcdsaP256();
        var certB = DtlsCertificate.GenerateEcdsaP256();

        // Distinct MIDs, distinct SSRCs (bundle-wide-distinct across audio + both video), shared PT 96.
        IReadOnlyList<BundledTrackConfig> twoVideos =
        [
            new BundledTrackConfig { Mid = "cam", Ssrc = 0x0B0B0B0B, PayloadType = VideoPayloadType, VideoCodecName = "H264" },
            new BundledTrackConfig { Mid = "scr", Ssrc = 0x0C0C0C0C, PayloadType = VideoPayloadType, VideoCodecName = "H264" },
        ];

        var (client, server) = CreatePair(certA, certB, videoTracks: twoVideos);
        await using var clientLease = client;
        await using var serverLease = server;

        Assert.Equal(2, server.VideoTrackCount);
        Assert.Equal(new[] { "cam", "scr" }, server.VideoMids);

        // Collect the first received frame per MID on the receiver. A cross-talk bug would land the camera's
        // frame under "scr" (or vice versa) — the per-MID content assertion below catches it.
        var cam = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scr = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.VideoTrackFrameReceived += (mid, frame, _, _) =>
        {
            if (mid == "cam") cam.TrySetResult(frame);
            else if (mid == "scr") scr.TrySetResult(frame);
        };

        await server.StartAsync();
        await client.StartAsync();

        // Two visibly different frames so a mixed-up MID mapping is detectable by content, not just by count.
        var camFrame = AnnexB((Nal(0x67, 20), false), (Nal(0x68, 6), false), (Nal(0x65, 3000), false));
        var scrFrame = AnnexB((Nal(0x67, 24), false), (Nal(0x68, 8), false), (Nal(0x65, 4000), false));

        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var videoTimestamp = 90000u;
        while (!(cam.Task.IsCompleted && scr.Task.IsCompleted))
        {
            overall.Token.ThrowIfCancellationRequested();
            await client.SendVideoTrackFrameAsync("cam", camFrame, videoTimestamp);
            await client.SendVideoTrackFrameAsync("scr", scrFrame, videoTimestamp);
            videoTimestamp += 3000;
            await Task.Delay(20, overall.Token);
        }

        // Each MID received exactly its own track's frame content — no cross-talk between the two SSRCs.
        Assert.Equal(
            AnnexBParser.ParseNalUnits(camFrame).Select(n => n.ToArray()),
            AnnexBParser.ParseNalUnits(await cam.Task.WaitAsync(TimeSpan.FromSeconds(5))).Select(n => n.ToArray()));
        Assert.Equal(
            AnnexBParser.ParseNalUnits(scrFrame).Select(n => n.ToArray()),
            AnnexBParser.ParseNalUnits(await scr.Task.WaitAsync(TimeSpan.FromSeconds(5))).Select(n => n.ToArray()));

        // The two distinct frames differ, so a swapped mapping would have failed one of the equalities above;
        // assert they are genuinely different to rule out both tracks accidentally carrying identical content.
        Assert.NotEqual(camFrame, scrFrame);

        // Aggregate video stats (S4) sum across both tracks: at least the two key frames landed.
        Assert.True(server.SnapshotStats().FramesReceived >= 2, "both video tracks should have delivered frames");
        Assert.True(server.SnapshotStats().KeyFrames >= 2, "both video tracks' key frames should have landed");
    }

    [Fact]
    public async Task Two_simultaneous_video_ssrcs_decrypt_independently_with_per_ssrc_replay_windows()
    {
        // per-SSRC SRTP (ADR-011): each SSRC on the bundle has its own ROC + replay window. Two video tracks
        // send a long burst concurrently on distinct SSRCs; every frame decrypts on the receiver and none is
        // rejected as a replay/out-of-window — which could only hold if the SRTP context is keyed per SSRC (a
        // single shared replay window across both SSRCs would false-positive once their sequence spaces overlap).
        var certA = DtlsCertificate.GenerateEcdsaP256();
        var certB = DtlsCertificate.GenerateEcdsaP256();

        IReadOnlyList<BundledTrackConfig> twoVideos =
        [
            new BundledTrackConfig { Mid = "cam", Ssrc = 0x0B0B0B0B, PayloadType = VideoPayloadType, VideoCodecName = "H264" },
            new BundledTrackConfig { Mid = "scr", Ssrc = 0x0C0C0C0C, PayloadType = VideoPayloadType, VideoCodecName = "H264" },
        ];

        var (client, server) = CreatePair(certA, certB, videoTracks: twoVideos);
        await using var clientLease = client;
        await using var serverLease = server;

        var camCount = 0;
        var scrCount = 0;
        server.VideoTrackFrameReceived += (mid, _, _, _) =>
        {
            if (mid == "cam") Interlocked.Increment(ref camCount);
            else if (mid == "scr") Interlocked.Increment(ref scrCount);
        };

        await server.StartAsync();
        await client.StartAsync();

        var frame = AnnexB((Nal(0x67, 20), false), (Nal(0x68, 6), false), (Nal(0x65, 1500), false));

        // Keep pushing a long interleaved burst on both SSRCs until each track has decrypted many frames — far
        // past the point where a shared replay window would begin discarding one SSRC's packets.
        const int target = 25;
        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var videoTimestamp = 90000u;
        while (Volatile.Read(ref camCount) < target || Volatile.Read(ref scrCount) < target)
        {
            overall.Token.ThrowIfCancellationRequested();
            await client.SendVideoTrackFrameAsync("cam", frame, videoTimestamp);
            await client.SendVideoTrackFrameAsync("scr", frame, videoTimestamp);
            videoTimestamp += 3000;
            await Task.Delay(10, overall.Token);
        }

        Assert.True(Volatile.Read(ref camCount) >= target, $"cam decrypted {camCount} frames (expected ≥ {target})");
        Assert.True(Volatile.Read(ref scrCount) >= target, $"scr decrypted {scrCount} frames (expected ≥ {target})");
        // The receiver dropped no datagram as undecryptable/replay across the whole burst on either SSRC.
        Assert.Equal(0L, server.SnapshotStats().DroppedDatagrams);
    }

    [Fact]
    public async Task Transport_cc_feedback_loop_updates_the_senders_recommended_bitrate_end_to_end()
    {
        // Both peers negotiate the transport-wide-cc extension (RFC 8888), so each BundledMediaSession builds a
        // BundledCongestionPlane. The client stamps a transport-wide sequence on its video; the server's plane
        // (receive side) reports those arrivals back over SRTCP; the client's plane (sender side) folds that
        // feedback and updates its recommended bitrate — the RecommendedBitrateChanged event proves BOTH halves
        // are wired end to end (exercising the session-level composition, not just the primitives).
        var certA = DtlsCertificate.GenerateEcdsaP256();
        var certB = DtlsCertificate.GenerateEcdsaP256();

        var (client, server) = CreatePair(certA, certB, transportCcExtId: TransportCcExtId);
        await using var clientLease = client;
        await using var serverLease = server;

        // The extension was negotiated, so the congestion plane exists on both peers.
        Assert.NotNull(client.Congestion);

        // Fires when the client's controller processes peer feedback that moves the recommendation — i.e. the
        // whole loop (stamp → arrive → feedback → fold) completed at least once.
        var recommendationUpdated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Congestion!.RecommendedBitrateChanged += _ => recommendationUpdated.TrySetResult();

        await server.StartAsync();
        await client.StartAsync();

        var videoFrame = AnnexB((Nal(0x67, 20), false), (Nal(0x68, 6), false), (Nal(0x65, 3000), false));

        // Media is suppressed until the shared DTLS handshake keys the transport; keep feeding video so the
        // feedback loop keeps ticking until the client's recommendation moves.
        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var videoTimestamp = 90000u;
        while (!recommendationUpdated.Task.IsCompleted)
        {
            overall.Token.ThrowIfCancellationRequested();
            await client.SendVideoFrameAsync(videoFrame, videoTimestamp);
            videoTimestamp += 3000;
            await Task.Delay(20, overall.Token);
        }

        await recommendationUpdated.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // getStats: the sender-side congestion estimate is surfaced through the stats snapshot — non-null because
        // transport-cc was negotiated, and positive (a controller always holds a target bitrate).
        var recommendedBitrate = client.SnapshotStats().AvailableOutgoingBitrateBps;
        Assert.NotNull(recommendedBitrate);
        Assert.True(recommendedBitrate > 0, $"expected a positive recommended bitrate, got {recommendedBitrate}");
    }

    // ── harness ──────────────────────────────────────────────────────────────────

    private const string ClientPwd = "clienticepassword1234567890";
    private const string ServerPwd = "servericepassword1234567890";

    // The default single video track (matches the pre-P2b 1-audio-1-video path used by the byte-identity tests).
    private static IReadOnlyList<BundledTrackConfig> DefaultVideo() =>
    [
        new BundledTrackConfig { Mid = "video", Ssrc = 0x0B0B0B0B, PayloadType = VideoPayloadType, VideoCodecName = "H264" },
    ];

    // Two peers each need the other's port before construction, so ports are pre-allocated. Under the
    // parallel suite two probes can hand out the same free port and one bind then loses the race — retry
    // with fresh ports rather than flake.
    private static (BundledMediaSession Client, BundledMediaSession Server) CreatePair(
        DtlsCertificate certA, DtlsCertificate certB, byte? transportCcExtId = null,
        IReadOnlyList<BundledTrackConfig>? videoTracks = null)
    {
        videoTracks ??= DefaultVideo();
        for (var attempt = 1; ; attempt++)
        {
            var portA = FreeUdpPort();
            var portB = FreeUdpPort();
            BundledMediaSession? client = null;
            try
            {
                client = new BundledMediaSession(
                    Options(portA, portB, isClient: true, certB.Fingerprint, controlling: true,
                        localUfrag: "cli0", localPwd: ClientPwd, remoteUfrag: "srv0", remotePwd: ServerPwd,
                        transportCcExtId: transportCcExtId, videoTracks: videoTracks),
                    new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), certA, NullLoggerFactory.Instance);
                var server = new BundledMediaSession(
                    Options(portB, portA, isClient: false, certA.Fingerprint, controlling: false,
                        localUfrag: "srv0", localPwd: ServerPwd, remoteUfrag: "cli0", remotePwd: ClientPwd,
                        transportCcExtId: transportCcExtId, videoTracks: videoTracks),
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
        string localUfrag, string localPwd, string remoteUfrag, string remotePwd, byte? transportCcExtId = null,
        IReadOnlyList<BundledTrackConfig>? videoTracks = null)
    {
        var remote = new IPEndPoint(IPAddress.Loopback, remotePort);
        return new BundledMediaSessionOptions
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
            RemoteEndPoint = remote,
            MidExtensionId = MidExtId,
            TransportWideCcExtensionId = transportCcExtId,
            Audio = new BundledTrackConfig
            {
                Mid = "audio", Ssrc = 0x0A0A0A0A, PayloadType = AudioPayloadType, SamplesPerPacket = 160,
            },
            VideoTracks = videoTracks ?? DefaultVideo(),
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
