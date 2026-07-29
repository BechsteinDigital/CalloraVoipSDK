using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The congestion relay (4.7.0) projects the built <see cref="BundledMediaSession"/>'s sender-side transport-cc
/// controller onto the peer's public surface: it fans each recommended-bitrate revision out as one surface
/// (bitrate + coarse quality) through a raise delegate, and reads the current recommendation through
/// point-in-time snapshot properties. These cover the null/silent behaviour without a session or without
/// transport-cc, and the end-to-end forward driven by a real feedback report on a transport-cc bundle.
/// </summary>
public sealed class WebRtcCongestionRelayTests
{
    private const byte MidExtId = 3;
    private const byte TransportCcExtId = 5;
    private const byte VideoPayloadType = 96;

    [Fact]
    public void Without_a_session_the_snapshot_properties_are_null()
    {
        var relay = new WebRtcCongestionRelay(() => null);

        Assert.Null(relay.RecommendedOutgoingBitrateBps);
        Assert.Null(relay.OutgoingNetworkQuality);
    }

    [Fact]
    public async Task Without_transport_cc_wiring_never_subscribes_and_the_properties_are_null()
    {
        await using var session = Session(transportCc: false);
        var relay = new WebRtcCongestionRelay(() => session);
        var raised = new List<(long Bps, NetworkQuality Quality)>();

        // No transport-cc negotiated → session.Congestion is null → wiring must be a silent no-op.
        var ex = Record.Exception(() => relay.WireSession(session, (bps, quality) => raised.Add((bps, quality))));

        Assert.Null(ex);
        Assert.Empty(raised);
        Assert.Null(relay.RecommendedOutgoingBitrateBps);
        Assert.Null(relay.OutgoingNetworkQuality);
    }

    [Fact]
    public async Task With_transport_cc_the_properties_read_the_controllers_current_recommendation()
    {
        await using var session = Session(transportCc: true);
        var relay = new WebRtcCongestionRelay(() => session);

        // Transport-cc negotiated → the controller exists; before any feedback it sits at its initial 1 Mbps / Good.
        Assert.Equal(1_000_000, relay.RecommendedOutgoingBitrateBps);
        Assert.Equal(NetworkQuality.Good, relay.OutgoingNetworkQuality);
    }

    [Fact]
    public async Task With_transport_cc_a_feedback_report_forwards_the_revised_bitrate_and_current_quality()
    {
        await using var session = Session(transportCc: true);
        var relay = new WebRtcCongestionRelay(() => session);
        var raised = new List<(long Bps, NetworkQuality Quality)>();
        relay.WireSession(session, (bps, quality) => raised.Add((bps, quality)));

        var controller = session.Congestion!;
        // Record three stamped sends so the reported sequences correlate (the seq is read off the stamped packet).
        controller.OnPacketSent(Stamped(0));
        controller.OnPacketSent(Stamped(1));
        controller.OnPacketSent(Stamped(2));

        // A feedback report revises the AIMD recommendation, so the controller raises RecommendedBitrateChanged —
        // the relay must fan it out once, carrying the controller's new bitrate paired with its current quality.
        controller.OnRtcpPackets([Feedback()]);

        var forwarded = Assert.Single(raised);
        Assert.NotEqual(1_000_000, forwarded.Bps); // the recommendation moved off the 1 Mbps start
        Assert.Equal(controller.RecommendedBitrateBps, forwarded.Bps);
        Assert.Equal(controller.Quality, forwarded.Quality);
        // The snapshot properties reflect the same current recommendation the event carried.
        Assert.Equal(forwarded.Bps, relay.RecommendedOutgoingBitrateBps);
        Assert.Equal(forwarded.Quality, relay.OutgoingNetworkQuality);
    }

    private static RtpPacket Stamped(ushort transportSeq) => new()
    {
        PayloadType = VideoPayloadType,
        SequenceNumber = transportSeq,
        HeaderExtension = OneByteRtpHeaderExtensions.Encode(
            [OneByteRtpHeaderExtensions.TransportSequenceNumber(TransportCcExtId, transportSeq)]),
    };

    // A feedback report acknowledging the three sent transport sequences — enough for the AIMD policy to revise
    // the recommended bitrate off its start value and raise the change (direction depends on the delay/loss fold).
    private static RtcpTransportFeedback Feedback() =>
        TransportCcFeedbackBuilder.Build(
            new[]
            {
                new TransportCcArrival { SequenceNumber = 0, ArrivalTimestamp = 10_000 },
                new TransportCcArrival { SequenceNumber = 1, ArrivalTimestamp = 11_000 },
                new TransportCcArrival { SequenceNumber = 2, ArrivalTimestamp = 12_000 },
            },
            senderSsrc: 0xAAAA, mediaSsrc: 0x1234, feedbackPacketCount: 0, epochTimestamp: 0, ticksPerSecond: 1_000_000);

    // A built (unstarted) single-video bundle bound to loopback; transport-cc is toggled via the negotiated extmap.
    private static BundledMediaSession Session(bool transportCc)
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
                    MidExtensionId = MidExtId,
                    TransportWideCcExtensionId = transportCc ? TransportCcExtId : null,
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
