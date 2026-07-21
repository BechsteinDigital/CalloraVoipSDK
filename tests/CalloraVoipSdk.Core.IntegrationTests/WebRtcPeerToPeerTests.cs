using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Two WebRTC peers end to end (Weg 1 slice 3): one offers, the other answers, both build their shared
/// BUNDLE transport from the exchange, run the DTLS-SRTP handshake and ICE consent against each other,
/// reach <see cref="WebRtcConnectionState.Connected"/>, and exchange audio and an H.264 video frame both
/// ways — entirely through the signalling-neutral peer API, with no SIP path involved.
/// </summary>
public sealed class WebRtcPeerToPeerTests
{
    private const byte AudioPayloadType = 0;

    private static readonly IReadOnlyList<SdpCodecDefinition> Pcmu =
        [new SdpCodecDefinition { PayloadType = AudioPayloadType, Name = "PCMU", ClockRate = 8000 }];

    [Fact]
    public async Task Two_peers_connect_and_exchange_audio_and_video()
    {
        var (offerer, answerer) = await ConnectPeersAsync();
        await using var offererLease = offerer;
        await using var answererLease = answerer;

        var offererConnected = Connected(offerer);
        var answererConnected = Connected(answerer);
        var audioAtOfferer = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var audioAtAnswerer = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var videoAtOfferer = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var videoAtAnswerer = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        offerer.AudioReceived += p => audioAtOfferer.TrySetResult(p);
        answerer.AudioReceived += p => audioAtAnswerer.TrySetResult(p);
        offerer.VideoFrameReceived += (f, _, _) => videoAtOfferer.TrySetResult(f);
        answerer.VideoFrameReceived += (f, _, _) => videoAtAnswerer.TrySetResult(f);

        await offerer.StartAsync();
        await answerer.StartAsync();

        await Task.WhenAll(offererConnected, answererConnected).WaitAsync(TimeSpan.FromSeconds(20));

        var offererAudio = new byte[] { 1, 2, 3, 4 };
        var answererAudio = new byte[] { 5, 6, 7, 8 };
        var frame = AnnexB((Nal(0x67, 20), false), (Nal(0x68, 6), false), (Nal(0x65, 800), false));

        var timestamp = 90000u;
        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!(audioAtOfferer.Task.IsCompleted && audioAtAnswerer.Task.IsCompleted
                 && videoAtOfferer.Task.IsCompleted && videoAtAnswerer.Task.IsCompleted))
        {
            overall.Token.ThrowIfCancellationRequested();
            await offerer.SendAudioAsync(offererAudio);
            await answerer.SendAudioAsync(answererAudio);
            await offerer.SendVideoFrameAsync(frame, timestamp);
            await answerer.SendVideoFrameAsync(frame, timestamp);
            timestamp += 3000;
            await Task.Delay(20, overall.Token);
        }

        Assert.Equal(answererAudio, await audioAtOfferer.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(offererAudio, await audioAtAnswerer.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        AssertSameNalUnits(frame, await videoAtOfferer.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        AssertSameNalUnits(frame, await videoAtAnswerer.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Peers_converge_on_each_others_media_endpoint_after_nomination()
    {
        var (offerer, answerer) = await ConnectPeersAsync();
        await using var offererLease = offerer;
        await using var answererLease = answerer;

        var offererConnected = Connected(offerer);
        var answererConnected = Connected(answerer);

        await offerer.StartAsync();  // offerer is the ICE controlling agent — its nomination driver runs
        await answerer.StartAsync();

        await Task.WhenAll(offererConnected, answererConnected).WaitAsync(TimeSpan.FromSeconds(20));

        // The offerer's connectivity-check nomination (RFC 8445 §7/§8) and the symmetric latch converge each
        // transport on the peer's actual bound media endpoint — not a placeholder or a stale candidate.
        Assert.NotNull(offerer.RemoteMediaEndPoint);
        Assert.NotNull(answerer.RemoteMediaEndPoint);
        Assert.Equal(answerer.LocalMediaEndPoint, offerer.RemoteMediaEndPoint);
        Assert.Equal(offerer.LocalMediaEndPoint, answerer.RemoteMediaEndPoint);
    }

    /// <summary>
    /// The full RTCP quality round trip end to end over real DTLS-SRTP (CF-004g): two peers connect, both send
    /// audio, their periodic Sender Reports and reception reports flow over the encrypted SRTCP path, and each
    /// side derives a round-trip time from the peer's echoed LSR/DLSR (RFC 3550 §6.4.1) and a receive-side
    /// interarrival jitter (§A.8) from the decrypted inbound stream. This exercises the whole quality pipeline
    /// against a live peer — SR emission and per-SSRC LSR capture, SRTCP protect/unprotect, RR-block
    /// consumption, and the per-stream fold (CF-004f) — not just the unit-isolated pieces.
    /// </summary>
    [Fact]
    public async Task Rtcp_quality_round_trip_derives_rtt_and_jitter_at_both_peers()
    {
        var (offerer, answerer) = await ConnectPeersAsync();
        await using var offererLease = offerer;
        await using var answererLease = answerer;

        var offererConnected = Connected(offerer);
        var answererConnected = Connected(answerer);

        await offerer.StartAsync();
        await answerer.StartAsync();

        await Task.WhenAll(offererConnected, answererConnected).WaitAsync(TimeSpan.FromSeconds(20));

        var offererAudio = new byte[] { 1, 2, 3, 4 };
        var answererAudio = new byte[] { 5, 6, 7, 8 };

        // Drive continuous audio both ways so each side keeps emitting Sender Reports and the peer keeps echoing
        // them back. The RTCP interval is randomised around Tmin (RFC 3550 §6.2/§6.3.1), so a full SR → RR → RTT
        // round trip spans a few report cycles; budget generously over the default (~5 s) cadence.
        BundledMediaQuality? offererQuality = null;
        BundledMediaQuality? answererQuality = null;
        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        while (offererQuality?.RoundTripTimeMs is null || answererQuality?.RoundTripTimeMs is null)
        {
            overall.Token.ThrowIfCancellationRequested();
            await offerer.SendAudioAsync(offererAudio);
            await answerer.SendAudioAsync(answererAudio);
            await Task.Delay(20, overall.Token);
            offererQuality = offerer.GetQuality();
            answererQuality = answerer.GetQuality();
        }

        // RTT was decrypted from the peer's SRTCP reception report and is a real positive latency, both ways.
        Assert.True(offererQuality!.Value.RoundTripTimeMs > 0);
        Assert.True(answererQuality!.Value.RoundTripTimeMs > 0);

        // Two report cycles in, plenty of inbound RTP has been decrypted, so the receive-side interarrival
        // jitter (RFC 3550 §A.8, negotiated 8 kHz clock) is established at both peers as well.
        Assert.NotNull(offererQuality.Value.JitterMs);
        Assert.NotNull(answererQuality.Value.JitterMs);

        // The per-stream fold (CF-004f) surfaces that same RTT attributed to the audio track's MID.
        Assert.Contains(offerer.GetStreamQuality(), s => s.Mid is not null && s.RoundTripTimeMs > 0);
        Assert.Contains(answerer.GetStreamQuality(), s => s.Mid is not null && s.RoundTripTimeMs > 0);
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
                Video = new SdpVideoMediaOptions
                {
                    Port = localPort + 1,
                    Codecs = [new SdpCodecDefinition { PayloadType = 96, Name = "H264", ClockRate = 90000 }],
                },
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

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
