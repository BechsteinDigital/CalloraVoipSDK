using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// A WebRTC peer end to end (Weg 1 media wiring): the peer answers a remote offer, runs the DTLS-SRTP
/// handshake over its BUNDLE transport against a counterpart, reaches
/// <see cref="WebRtcConnectionState.Connected"/>, and exchanges audio both ways through its own
/// send/receive API. The counterpart is a raw <see cref="BundledMediaSession"/> standing in for a
/// browser offerer until the peer can create its own offer (a later slice).
/// </summary>
public sealed class WebRtcPeerLoopbackTests
{
    private const byte AudioPayloadType = 0;
    private const uint CounterpartSsrc = 0x0C0C0C0C;

    private static readonly IReadOnlyList<SdpCodecDefinition> Pcmu =
        [new SdpCodecDefinition { PayloadType = AudioPayloadType, Name = "PCMU", ClockRate = 8000 }];

    [Fact]
    public async Task The_peer_connects_and_exchanges_audio_with_a_counterpart()
    {
        var peerCert = DtlsCertificate.GenerateEcdsaP256();
        var counterpartCert = DtlsCertificate.GenerateEcdsaP256();

        var (peer, counterpart) = await ConnectPairAsync(peerCert, counterpartCert);
        await using var peerLease = peer;
        await using var counterpartLease = counterpart;

        var peerGotAudio = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var counterpartGotAudio = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.AudioReceived += payload => peerGotAudio.TrySetResult(payload);
        counterpart.AudioReceived += packet => counterpartGotAudio.TrySetResult(packet.Payload.ToArray());
        peer.ConnectionStateChanged += state => { if (state == WebRtcConnectionState.Connected) connected.TrySetResult(); };

        await peer.StartAsync();
        await counterpart.StartAsync();

        await connected.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(WebRtcConnectionState.Connected, peer.State);

        // After the handshake keys the transport, media flows both ways.
        var fromPeer = new byte[] { 1, 2, 3, 4 };
        var fromCounterpart = new byte[] { 5, 6, 7, 8 };
        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!(peerGotAudio.Task.IsCompleted && counterpartGotAudio.Task.IsCompleted))
        {
            overall.Token.ThrowIfCancellationRequested();
            await peer.SendAudioAsync(fromPeer);
            await counterpart.SendAudioAsync(fromCounterpart);
            await Task.Delay(20, overall.Token);
        }

        Assert.Equal(fromCounterpart, await peerGotAudio.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(fromPeer, await counterpartGotAudio.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    // ── harness ──────────────────────────────────────────────────────────────────

    // Two peers each need the other's port before construction, so ports are pre-allocated; retry with
    // fresh ports on a bind race under the parallel suite.
    private static async Task<(WebRtcPeerConnection Peer, BundledMediaSession Counterpart)> ConnectPairAsync(
        DtlsCertificate peerCert, DtlsCertificate counterpartCert)
    {
        for (var attempt = 1; ; attempt++)
        {
            var peerPort = FreeUdpPort();
            var counterpartPort = FreeUdpPort();
            WebRtcPeerConnection? peer = null;
            try
            {
                peer = BuildPeer(peerPort, peerCert);
                var answer = await peer.SetRemoteDescriptionAsync(Offer(counterpartPort, counterpartCert));
                var counterpart = BuildCounterpart(counterpartPort, peerPort, peerCert.Fingerprint, counterpartCert, answer);
                return (peer, counterpart);
            }
            catch (SocketException) when (attempt < 8)
            {
                if (peer is not null)
                    await peer.DisposeAsync();
            }
        }
    }

    private static WebRtcPeerConnection BuildPeer(int localPort, DtlsCertificate cert) =>
        new(new WebRtcPeerOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
                AudioCodecs = Pcmu,
                Dtls = new SdpDtlsParameters { Algorithm = cert.Fingerprint.Algorithm, Fingerprint = cert.Fingerprint.Value },
                Ice = new SdpIceParameters { Ufrag = "peerU", Pwd = "peerpassword1234567890" },
            },
            new SdpOfferAnswerNegotiator(), new SdpSessionParser(), new SdpSessionSerializer(),
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), cert, NullLoggerFactory.Instance);

    // A remote WebRTC offer (BUNDLE + DTLS + ICE, audio only) advertised by the counterpart's cert/port.
    private static string Offer(int counterpartPort, DtlsCertificate counterpartCert) =>
        new SdpSessionSerializer().Serialize(new SdpOfferAnswerNegotiator().CreateOffer(
            new IPEndPoint(IPAddress.Loopback, counterpartPort), Pcmu, SdpMediaDirection.SendRecv,
            new SdpMediaOptions
            {
                Bundle = true,
                RtcpMux = true,
                Dtls = new SdpDtlsParameters
                {
                    Algorithm = counterpartCert.Fingerprint.Algorithm,
                    Fingerprint = counterpartCert.Fingerprint.Value,
                    Setup = "actpass",
                },
                Ice = new SdpIceParameters { Ufrag = "cpU", Pwd = "cppassword1234567890" },
            }));

    private static BundledMediaSession BuildCounterpart(
        int localPort, int peerPort, DtlsFingerprint peerFingerprint, DtlsCertificate counterpartCert, string answerSdp)
    {
        // Take the DTLS role opposite the peer's negotiated a=setup.
        var peerSetup = new SdpSessionParser().Parse(answerSdp)
            .Media.First(m => m.MediaType.Equals("audio", StringComparison.OrdinalIgnoreCase)).DtlsSetup;
        var counterpartIsClient = !string.Equals(peerSetup, "active", StringComparison.OrdinalIgnoreCase);

        var peerEndPoint = new IPEndPoint(IPAddress.Loopback, peerPort);
        return new BundledMediaSession(
            new BundledMediaSessionOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
                RemoteEndPoint = peerEndPoint,
                MidExtensionId = 1, // the offer assigned sdes:mid id 1
                Audio = new BundledTrackConfig { Mid = "audio", Ssrc = CounterpartSsrc, PayloadType = AudioPayloadType, SamplesPerPacket = 160 },
                DtlsIsClient = counterpartIsClient,
                RemoteFingerprint = peerFingerprint,
                Ice = new IceMediaParameters(peerEndPoint, IceEnabled: false, IceControlling: true, null, null, null, null),
            },
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), counterpartCert, NullLoggerFactory.Instance);
    }

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
