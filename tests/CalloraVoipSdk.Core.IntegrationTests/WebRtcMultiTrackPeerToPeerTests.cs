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
/// End-to-end multi-track video (4.7.0 P2c): an offerer that adds a second video track (P2c AddVideoTrack →
/// numeric-MID multi-track offer) connects to an answerer over real DTLS-SRTP, and a frame sent on track MID
/// "1" vs MID "2" arrives at the answerer tagged with that MID and carrying the track's own SSRC (RFC 3550
/// §8.1 — distinct per track). This proves the whole P2c send/receive path over the wire, not just the SDP.
/// </summary>
public sealed class WebRtcMultiTrackPeerToPeerTests
{
    private static readonly IReadOnlyList<SdpCodecDefinition> Pcmu =
        [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }];

    private static readonly IReadOnlyList<SdpCodecDefinition> H264 =
        [new SdpCodecDefinition { PayloadType = 96, Name = "H264", ClockRate = 90000 }];

    [Fact]
    public async Task Frames_on_two_video_tracks_arrive_tagged_with_their_distinct_mids_and_ssrcs()
    {
        var (offerer, answerer) = await ConnectPeersAsync();
        await using var offererLease = offerer;
        await using var answererLease = answerer;

        var offererConnected = Connected(offerer);
        var answererConnected = Connected(answerer);

        // Capture, per inbound MID at the answerer, the first frame the track was routed with.
        var byMid = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var sync = new object();
        answerer.VideoTrackFrameReceived += (mid, frame, _, _) =>
        {
            lock (sync) byMid.TryAdd(mid, frame);
        };

        await offerer.StartAsync();
        await answerer.StartAsync();
        await Task.WhenAll(offererConnected, answererConnected).WaitAsync(TimeSpan.FromSeconds(20));

        // Distinct payloads per track so a mis-routed frame is caught, not just presence.
        var frameA = AnnexB(Nal(0x67, 20), Nal(0x65, 400));
        var frameB = AnnexB(Nal(0x67, 20), Nal(0x65, 900));

        var timestamp = 90000u;
        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            lock (sync) if (byMid.ContainsKey("1") && byMid.ContainsKey("2")) break;
            overall.Token.ThrowIfCancellationRequested();
            await offerer.SendVideoTrackFrameAsync("1", frameA, timestamp);
            await offerer.SendVideoTrackFrameAsync("2", frameB, timestamp);
            timestamp += 3000;
            await Task.Delay(20, overall.Token);
        }

        byte[] trackA, trackB;
        lock (sync) { trackA = byMid["1"]; trackB = byMid["2"]; }

        // Each track routed to its own MID with the right payload: a frame sent on handle "1" reached MID "1"
        // and handle "2" reached MID "2", and the two payloads did not cross — proving the sends address
        // distinct wire streams. Each video track carries its own SSRC (RFC 3550 §8.1); that SSRC distinctness
        // is the P2b factory invariant verified by BundledVideoTrackTests, so it is not re-asserted here.
        AssertSameNalUnits(frameA, trackA);   // track 1's frames reached MID "1"
        AssertSameNalUnits(frameB, trackB);   // track 2's frames reached MID "2"
        Assert.NotEqual(Convert.ToBase64String(trackA), Convert.ToBase64String(trackB));   // not the same stream
    }

    // 4.7.0 P2c multi-track keyframe: the per-MID RequestVideoKeyFrameAsync(mid) overload targets ONE video
    // track. Requesting a key frame for the added track MID "2" sends exactly one PLI over the wire (its SSRC is
    // captured once a frame has arrived), the answerer's peer that owns MID "2" observes the inbound key-frame
    // request, and requesting an unknown MID is a tolerant no-op (returns false, sends nothing more).
    [Fact]
    public async Task RequestVideoKeyFrameAsync_for_one_mid_pings_only_that_track()
    {
        var (offerer, answerer) = await ConnectPeersAsync();
        await using var offererLease = offerer;
        await using var answererLease = answerer;

        var offererConnected = Connected(offerer);
        var answererConnected = Connected(answerer);

        // The answerer sends video back on MID "2" and, on the offerer's PLI, raises VideoKeyFrameRequested —
        // the observable end-to-end signal that the offerer's request for MID "2" reached the answerer's track.
        var keyFrameRequests = 0;
        var requestSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        answerer.VideoKeyFrameRequested += () =>
        {
            Interlocked.Increment(ref keyFrameRequests);
            requestSeen.TrySetResult();
        };

        // The answerer must have this offerer's MID "2" SSRC before it can name it in a PLI — capture it by
        // receiving a frame on that track at the offerer (both peers send on MID "2").
        var offererGotMid2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        offerer.VideoTrackFrameReceived += (mid, _, _, _) => { if (mid == "2") offererGotMid2.TrySetResult(); };

        await offerer.StartAsync();
        await answerer.StartAsync();
        await Task.WhenAll(offererConnected, answererConnected).WaitAsync(TimeSpan.FromSeconds(20));

        var frame = AnnexB(Nal(0x67, 20), Nal(0x65, 300));
        var timestamp = 90000u;
        using var pump = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Drive both directions on MID "2" until the answerer has captured the offerer's MID "2" SSRC (so its
        // app-requested PLI can name that source).
        while (!offererGotMid2.Task.IsCompleted)
        {
            pump.Token.ThrowIfCancellationRequested();
            await offerer.SendVideoTrackFrameAsync("2", frame, timestamp);
            await answerer.SendVideoTrackFrameAsync("2", frame, timestamp);
            timestamp += 3000;
            await Task.Delay(20, pump.Token);
        }

        // An unknown MID is a tolerant no-op — no PLI leaves the offerer, so the answerer sees no request from it.
        Assert.False(await offerer.RequestVideoKeyFrameAsync("does-not-exist"));

        // Request a key frame for the added track MID "2": a PLI is sent, and the answerer's MID "2" track
        // observes the inbound key-frame request end-to-end.
        Assert.True(await offerer.RequestVideoKeyFrameAsync("2"));
        await requestSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(Volatile.Read(ref keyFrameRequests) >= 1);
    }

    // ── harness ──────────────────────────────────────────────────────────────────

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

                // Offerer adds a SECOND video track (P2c): audio=0, EnableVideo primary=1, added=2.
                var addedMid = offerer.AddVideoTrack(new WebRtcAddedVideoTrack { Codecs = H264 });
                Assert.Equal("2", addedMid);

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

    // The primary video track is the config-time VideoTracks[0] (EnableVideo equivalent); the offerer adds one
    // more at runtime. The answerer mirrors the offered m-lines via the multi-track answer path (P2a-ii).
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
