using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Simulcast end to end between two SDK peers (#369, criterion 6): one offers simulcast, the other answers,
/// both build their shared BUNDLE transport from the exchange, key it with real DTLS-SRTP, and the sending
/// peer's per-RID layers arrive at the receiving peer each tagged with its RID — proving the answerer-side
/// receive path that was previously reachable only against a browser (an SDK could not answer simulcast, so
/// no SDK↔SDK loopback could exercise it). Both directions are covered: the offerer as sender (case A, the
/// SFU topology) and the answerer as sender (case B, an SDK simulcasting to a peer that asked it to).
/// </summary>
public sealed class WebRtcSimulcastPeerToPeerTests
{
    private static readonly IReadOnlyList<SdpCodecDefinition> Pcmu =
        [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }];

    [Fact]
    public async Task An_offerer_simulcast_send_reaches_the_answerer_as_per_rid_layers()
    {
        // Case A — the common SFU topology: the offerer simulcasts, the answerer receives the layers.
        var (offerer, answerer) = await ConnectPeersAsync(
            offererSend: ["hi", "lo"], offererRecv: [], answererSend: [], answererRecv: []);
        await using var offererLease = offerer;
        await using var answererLease = answerer;

        await BothConnected(offerer, answerer);

        // The answer confirmed the two recv layers (RFC 8853 §5.1) — the receive allowlist is populated.
        Assert.Equal(["hi", "lo"], answerer.NegotiatedReceiveSimulcastRids.OrderBy(Rank));

        var seen = await DriveUntilBothLayersArrive(answerer, from: offerer);
        Assert.Contains("hi", seen);
        Assert.Contains("lo", seen);
    }

    [Fact]
    public async Task An_answerer_simulcast_send_reaches_the_offerer_as_per_rid_layers()
    {
        // Case B — the offerer asks the peer to simulcast (a=simulcast:recv), the answerer is configured to
        // send those layers and confirms a=simulcast:send; the offerer receives them per RID.
        var (offerer, answerer) = await ConnectPeersAsync(
            offererSend: [], offererRecv: ["hi", "lo"], answererSend: ["hi", "lo"], answererRecv: []);
        await using var offererLease = offerer;
        await using var answererLease = answerer;

        await BothConnected(offerer, answerer);

        Assert.Equal(["hi", "lo"], offerer.NegotiatedReceiveSimulcastRids.OrderBy(Rank));

        var seen = await DriveUntilBothLayersArrive(offerer, from: answerer);
        Assert.Contains("hi", seen);
        Assert.Contains("lo", seen);
    }

    // ── harness ─────────────────────────────────────────────────────────────────

    private static int Rank(string rid) => rid == "hi" ? 0 : 1;

    private static async Task BothConnected(WebRtcPeerConnection offerer, WebRtcPeerConnection answerer)
    {
        var offererConnected = Connected(offerer);
        var answererConnected = Connected(answerer);
        await offerer.StartAsync();
        await answerer.StartAsync();
        await Task.WhenAll(offererConnected, answererConnected).WaitAsync(TimeSpan.FromSeconds(25));
    }

    // Drives the sender's two layers until the receiver has surfaced a frame on each RID (or the deadline
    // fires). Keyframes are re-sent every iteration so a receiver that joined mid-handshake still latches both.
    private static async Task<ISet<string>> DriveUntilBothLayersArrive(
        WebRtcPeerConnection receiver, WebRtcPeerConnection from)
    {
        var seen = new ConcurrentDictionary<string, byte>();
        receiver.VideoLayerFrameReceived += (_, rid, _) => seen.TryAdd(rid, 0);

        var frame = KeyFrame();
        var timestamp = 90000u;
        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (!(seen.ContainsKey("hi") && seen.ContainsKey("lo")))
        {
            overall.Token.ThrowIfCancellationRequested();
            await from.SendVideoFrameAsync("hi", frame, timestamp, isKeyFrame: true);
            await from.SendVideoFrameAsync("lo", frame, timestamp, isKeyFrame: true);
            timestamp += 3000;
            await Task.Delay(20, overall.Token);
        }

        return seen.Keys.ToHashSet();
    }

    private static Task Connected(WebRtcPeerConnection peer)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.ConnectionStateChanged += state => { if (state == WebRtcConnectionState.Connected) tcs.TrySetResult(); };
        return tcs.Task;
    }

    private static async Task<(WebRtcPeerConnection Offerer, WebRtcPeerConnection Answerer)> ConnectPeersAsync(
        IReadOnlyList<string> offererSend, IReadOnlyList<string> offererRecv,
        IReadOnlyList<string> answererSend, IReadOnlyList<string> answererRecv)
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
                offerer = BuildPeer(offererPort, offererCert, "offr", offererSend, offererRecv);
                answerer = BuildPeer(answererPort, answererCert, "answ", answererSend, answererRecv);

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

    private static WebRtcPeerConnection BuildPeer(
        int localPort, DtlsCertificate cert, string iceTag,
        IReadOnlyList<string> sendRids, IReadOnlyList<string> recvRids) =>
        new(new WebRtcPeerOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
                AudioCodecs = Pcmu,
                VideoTracks =
                [
                    new SdpVideoMediaOptions
                    {
                        Port = localPort + 1,
                        Codecs = [new SdpCodecDefinition { PayloadType = 96, Name = "VP8", ClockRate = 90000 }],
                        SimulcastSendRids = sendRids,
                        SimulcastRecvRids = recvRids,
                    },
                ],
                Dtls = new SdpDtlsParameters { Algorithm = cert.Fingerprint.Algorithm, Fingerprint = cert.Fingerprint.Value },
                Ice = new SdpIceParameters { Ufrag = iceTag, Pwd = iceTag + "password1234567890" },
            },
            new SdpOfferAnswerNegotiator(), new SdpSessionParser(), new SdpSessionSerializer(),
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), cert, NullLoggerFactory.Instance);

    // A minimal VP8 key frame: bit 0 of the first byte clear marks a key frame (RFC 7741 §4.3); the body is
    // opaque to this transport-only path, which packetises and RID-tags whatever bytes it is handed.
    private static byte[] KeyFrame()
    {
        var frame = new byte[512];
        frame[0] = 0x10; // P bit (bit 0) clear → key frame
        for (var i = 1; i < frame.Length; i++)
            frame[i] = (byte)(1 + (i % 250));
        return frame;
    }

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
