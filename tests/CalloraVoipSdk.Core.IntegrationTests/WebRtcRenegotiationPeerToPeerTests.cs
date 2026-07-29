using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// End-to-end mid-call renegotiation (4.7.0 P3b-3): two peers connect over real DTLS-SRTP with one video track,
/// then the offerer adds a SECOND video track and runs a second offer/answer cycle (AddVideoTrack → CreateOffer →
/// answerer SetRemoteDescription → offerer SetRemoteDescription). The new track begins carrying frames on its own
/// MID/SSRC while the first track keeps flowing uninterrupted — proving the diff is applied to the LIVE session
/// with no transport/DTLS/ICE/SRTP rebuild (the same shared 5-tuple and SRTP context carry both).
/// </summary>
public sealed class WebRtcRenegotiationPeerToPeerTests
{
    private static readonly IReadOnlyList<SdpCodecDefinition> Pcmu =
        [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }];

    private static readonly IReadOnlyList<SdpCodecDefinition> H264 =
        [new SdpCodecDefinition { PayloadType = 96, Name = "H264", ClockRate = 90000 }];

    [Fact]
    public async Task Adding_a_second_video_track_mid_call_starts_it_flowing_while_the_first_keeps_flowing()
    {
        var (offerer, answerer) = await ConnectPeersAsync();
        await using var offererLease = offerer;
        await using var answererLease = answerer;

        var offererConnected = Connected(offerer);
        var answererConnected = Connected(answerer);

        // Capture, per inbound MID at the answerer, the frames each track was routed with.
        var byMid = new Dictionary<string, List<byte[]>>(StringComparer.Ordinal);
        var sync = new object();
        answerer.VideoTrackFrameReceived += (mid, frame, _, _) =>
        {
            lock (sync)
            {
                if (!byMid.TryGetValue(mid, out var frames))
                    byMid[mid] = frames = [];
                frames.Add(frame);
            }
        };

        await offerer.StartAsync();
        await answerer.StartAsync();
        await Task.WhenAll(offererConnected, answererConnected).WaitAsync(TimeSpan.FromSeconds(20));

        var frame1 = AnnexB(Nal(0x67, 20), Nal(0x65, 400));   // MID "1": the first (config) track, flows throughout
        var frame3 = AnnexB(Nal(0x67, 20), Nal(0x65, 900));   // MID "3": the mid-call-added track

        // Phase 1: with only the config video track (MID "1") live, drive frames until MID "1" is received.
        var timestamp = 90000u;
        using var phase1 = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            lock (sync) if (byMid.ContainsKey("1")) break;
            phase1.Token.ThrowIfCancellationRequested();
            await offerer.SendVideoTrackFrameAsync("1", frame1, timestamp);
            timestamp += 3000;
            await Task.Delay(20, phase1.Token);
        }

        // Renegotiate: the offerer adds ANOTHER video track (a third m-line, MID "3") and runs a full second
        // offer/answer cycle on the running peers — no transport rebuild, only the track diff is applied live.
        var addedMid = offerer.AddVideoTrack(new WebRtcAddedVideoTrack { Codecs = H264 });
        Assert.Equal("3", addedMid);
        var reOffer = offerer.CreateOffer();
        var reAnswer = await answerer.SetRemoteDescriptionAsync(reOffer);
        await offerer.SetRemoteDescriptionAsync(reAnswer);

        // The peers never left Connected (no DTLS/ICE rebuild) and the answerer now advertises three remote video
        // tracks (the receiver would materialise a track for the new MID).
        Assert.Equal(WebRtcConnectionState.Connected, offerer.State);
        Assert.Equal(3, offerer.RemoteVideoTracks.Count);

        // Record how many frames MID "1" had received by the renegotiation point, so we can prove it KEEPS
        // flowing afterwards (not merely that it had flowed before).
        int mid1Baseline;
        lock (sync) mid1Baseline = byMid["1"].Count;

        // Phase 2: send on the first AND the new track; wait until the NEW MID "3" is received AND MID "1"
        // advanced past its pre-renegotiation baseline (proving track 1 kept flowing across the renegotiation).
        using var phase2 = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            lock (sync)
                if (byMid.TryGetValue("3", out var m3) && m3.Count > 0 && byMid["1"].Count > mid1Baseline)
                    break;
            phase2.Token.ThrowIfCancellationRequested();
            await offerer.SendVideoTrackFrameAsync("1", frame1, timestamp);
            await offerer.SendVideoTrackFrameAsync("3", frame3, timestamp);
            timestamp += 3000;
            await Task.Delay(20, phase2.Token);
        }

        byte[] track3First;
        lock (sync) track3First = byMid["3"][0];

        // The mid-call-added track's frames reached MID "3" with their own payload (not track 1's) — the live
        // AddVideoTrack wired a working outbound sender + inbound demux on the SAME session, and MID "1" kept
        // flowing across the renegotiation (asserted by the phase-2 break condition).
        AssertSameNalUnits(frame3, track3First);
        Assert.NotEqual(Convert.ToBase64String(frame1), Convert.ToBase64String(track3First));
    }

    // ── harness (mirrors WebRtcMultiTrackPeerToPeerTests) ────────────────────────────

    private static Task Connected(WebRtcPeerConnection peer)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.ConnectionStateChanged += state => { if (state == WebRtcConnectionState.Connected) tcs.TrySetResult(); };
        return tcs.Task;
    }

    private static async Task<(WebRtcPeerConnection Offerer, WebRtcPeerConnection Answerer)> ConnectPeersAsync()
    {
        var offererCert = DtlsCertificate.GenerateEcdsaP256();
        var answererCert = DtlsCertificate.GenerateEcdsaP256();

        for (var attempt = 1; ; attempt++)
        {
            var offererPort = FreeUdpPort();
            var answererPort = FreeUdpPort();
            WebRtcPeerConnection? offerer = null;
            WebRtcPeerConnection? answerer = null;
            try
            {
                offerer = BuildPeer(offererPort, offererCert, "offr");
                answerer = BuildPeer(answererPort, answererCert, "answ");

                // Connect with the offerer ALREADY on the numeric-MID multi-track path (audio=0, video=1): add one
                // video track up front so MIDs are stable numerics from the start, and the mid-call add (a THIRD
                // m-line, MID "2") is a clean incremental renegotiation rather than a semantic→numeric MID churn.
                var preAddedMid = offerer.AddVideoTrack(new WebRtcAddedVideoTrack { Codecs = H264 });
                Assert.Equal("2", preAddedMid);
                var offer = offerer.CreateOffer();
                var answer = await answerer.SetRemoteDescriptionAsync(offer); // binds the answerer's port
                await offerer.SetRemoteDescriptionAsync(answer);              // binds the offerer's port
                return (offerer, answerer);
            }
            catch (SocketException) when (attempt < 8)
            {
                if (offerer is not null) await offerer.DisposeAsync();
                if (answerer is not null) await answerer.DisposeAsync();
            }
        }
    }

    private static WebRtcPeerConnection BuildPeer(int localPort, DtlsCertificate cert, string iceTag) =>
        new(new WebRtcPeerOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
                AudioCodecs = Pcmu,
                VideoTracks = [new SdpVideoMediaOptions { Port = localPort + 1, Codecs = H264 }],
                Dtls = new SdpDtlsParameters { Algorithm = cert.Fingerprint.Algorithm, Fingerprint = cert.Fingerprint.Value },
                Ice = new SdpIceParameters { Ufrag = iceTag, Pwd = iceTag + "password1234567890" },
            },
            new SdpOfferAnswerNegotiator(), new SdpSessionParser(), new SdpSessionSerializer(),
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), cert, NullLoggerFactory.Instance);

    private static void AssertSameNalUnits(byte[] expected, byte[] actual) =>
        Assert.Equal(
            AnnexBParser.ParseNalUnits(expected).Select(n => n.ToArray()),
            AnnexBParser.ParseNalUnits(actual).Select(n => n.ToArray()));

    private static byte[] Nal(byte header, int bodyLength)
    {
        var nal = new byte[1 + bodyLength];
        nal[0] = header;
        for (var i = 1; i < nal.Length; i++)
            nal[i] = (byte)(1 + (i % 250));
        return nal;
    }

    private static byte[] AnnexB(params byte[][] nals)
    {
        var stream = new MemoryStream();
        foreach (var nal in nals)
        {
            stream.Write(new byte[] { 0, 0, 1 });
            stream.Write(nal);
        }

        return stream.ToArray();
    }

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
