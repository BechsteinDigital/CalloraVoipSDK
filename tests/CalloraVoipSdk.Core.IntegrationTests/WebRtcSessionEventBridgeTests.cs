using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The event bridge (4.7.0 slice 3) fans a built <see cref="BundledMediaSession"/>'s inbound-media events onto
/// the peer's raise delegates. This covers the per-layer <c>VideoLayerFrameReceived</c> forwarding delegate the
/// bridge now wires: it is subscribed onto the session (the wiring never throws) and is null-guarded like the
/// other required raise delegates.
/// </summary>
public sealed class WebRtcSessionEventBridgeTests
{
    private const byte VideoPayloadType = 96;

    [Fact]
    public async Task WireSession_subscribes_the_video_layer_forward_without_throwing()
    {
        var bridge = new WebRtcSessionEventBridge(NullLogger<WebRtcSessionEventBridgeTests>.Instance);
        await using var session = Session();
        var layer = new List<(string Mid, string Rid, byte[] Frame, uint Ts, bool Key)>();

        // Wiring only registers handlers; it must accept the per-layer forward and not throw.
        var ex = Record.Exception(() => bridge.WireSession(
            session,
            _ => { },
            _ => { },
            (_, _) => { },
            (_, _, _) => { },
            (_, _, _, _) => { },
            (mid, rid, frame, ts, key) => layer.Add((mid, rid, frame, ts, key)),
            () => { },
            (_, _) => { }));

        Assert.Null(ex);
        Assert.Empty(layer); // no media driven → no frame forwarded yet
    }

    [Fact]
    public async Task WireSession_rejects_a_null_video_layer_forward_delegate()
    {
        var bridge = new WebRtcSessionEventBridge(NullLogger<WebRtcSessionEventBridgeTests>.Instance);
        await using var session = Session();

        Assert.Throws<ArgumentNullException>(() => bridge.WireSession(
            session,
            _ => { },
            _ => { },
            (_, _) => { },
            (_, _, _) => { },
            (_, _, _, _) => { },
            raiseVideoLayerFrameReceived: null!,
            () => { },
            (_, _) => { }));
    }

    // A built (unstarted) single-video bundle bound to loopback — enough to wire events against; no media flows.
    private static BundledMediaSession Session()
    {
        var cert = DtlsCertificate.GenerateEcdsaP256();
        for (var attempt = 1; ; attempt++)
        {
            var localPort = FreeUdpPort();
            var remotePort = FreeUdpPort();
            try
            {
                var remote = new IPEndPoint(IPAddress.Loopback, remotePort);
                var options = new BundledMediaSessionOptions
                {
                    LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort),
                    RemoteEndPoint = remote,
                    MidExtensionId = 3,
                    Audio = new BundledTrackConfig
                    {
                        Mid = "audio", Ssrc = 0x0A0A0A0A, PayloadType = 111, SamplesPerPacket = 160,
                    },
                    AdditionalAudioTracks = [],
                    VideoTracks =
                    [
                        new BundledTrackConfig
                        {
                            Mid = "video", Ssrc = 0x0B0B0B0B, PayloadType = VideoPayloadType, VideoCodecName = "VP8",
                        },
                    ],
                    DtlsIsClient = true,
                    RemoteFingerprint = cert.Fingerprint,
                    Ice = new IceMediaParameters(
                        remote, IceEnabled: true, IceControlling: true,
                        LocalIceUfrag: "cli0", LocalIcePwd: "clientpasswordclientpassword00",
                        RemoteIceUfrag: "srv0", RemoteIcePwd: "serverpasswordserverpassword00"),
                };
                return new BundledMediaSession(
                    options, new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), cert,
                    NullLoggerFactory.Instance);
            }
            catch (SocketException) when (attempt < 8)
            {
                // Port raced between probe and bind — retry with fresh ports.
            }
        }
    }

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
