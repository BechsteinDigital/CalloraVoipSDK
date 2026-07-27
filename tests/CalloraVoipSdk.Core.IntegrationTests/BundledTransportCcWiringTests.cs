using System.Net;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Rtcp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Transport-wide congestion control on the BUNDLE transport (transport-cc / RFC 8888, #7 slices 5+6). Unlike
/// the single-stream <see cref="VideoRtpStream"/>, the bundle carries every m-line over one 5-tuple, so
/// transport-cc is transport-wide: ONE sequence counter stamps outbound packets across all tracks, one
/// controller folds inbound feedback, and one feedback sender reports our inbound arrivals. These tests drive
/// the pipeline seams the <see cref="BundledMediaSession"/> wires (<c>PacketSent</c>, <c>RtpPacketReceived</c>,
/// the decoded control compound) directly, without a live socket/DTLS handshake.
/// </summary>
public sealed class BundledTransportCcWiringTests
{
    private const byte MidExtId = 3;
    private const byte TransportCcExtId = 5;
    private const byte AudioPayloadType = 0;
    private const byte VideoPayloadType = 96;
    private const uint AudioSsrc = 0x0A0A0A0A;
    private const uint VideoSsrc = 0x0B0B0B0B;
    private const ushort InitialSeq = 1000;
    private const uint InitialTimestamp = 5000;

    private static readonly byte[] MasterKey = Convert.FromHexString("E1F97A0D3E018BE0D64FA32C06DE4139");
    private static readonly byte[] MasterSalt = Convert.FromHexString("0EC675AD498AFEEBB6960B3AABE6");
    private static readonly IPEndPoint Peer = new(IPAddress.Loopback, 40000);

    // ── Outbound stamp: one transport-wide counter across all tracks ────────────────────────────────

    [Fact]
    public async Task Outbound_packets_carry_an_ascending_transport_wide_sequence_across_both_tracks()
    {
        var (outbound, sender) = Outbound(stampsTransportCc: true);
        var receiver = new SrtpContext(Material());
        outbound.InstallOutboundKey(new SrtpContext(Material()));

        // Interleave audio and video sends: the transport-wide sequence must advance across the transport,
        // not restart per track (transport-cc numbers the transport, RFC 8888).
        await outbound.SendAsync("audio", new byte[] { 1 });
        await outbound.SendAsync("video", new byte[] { 2 }, marker: true);
        await outbound.SendAsync("audio", new byte[] { 3 });
        await outbound.SendAsync("video", new byte[] { 4 }, marker: true);

        var sequences = sender.Datagrams
            .Select(d => ReadTransportCc(Decode(d, receiver)))
            .ToArray();

        Assert.Equal(new ushort?[] { 0, 1, 2, 3 }, sequences); // one monotonic counter, both tracks share it
    }

    [Fact]
    public async Task A_suppressed_send_before_keying_does_not_consume_a_transport_wide_sequence()
    {
        var (outbound, sender) = Outbound(stampsTransportCc: true);

        await outbound.SendAsync("audio", new byte[] { 1 }); // fail-closed (no key) — must not burn a tcc seq

        var receiver = new SrtpContext(Material());
        outbound.InstallOutboundKey(new SrtpContext(Material()));
        await outbound.SendAsync("audio", new byte[] { 2 });

        // The first real packet still starts at transport-wide sequence 0 — a hole would look like loss.
        Assert.Equal((ushort?)0, ReadTransportCc(Decode(Assert.Single(sender.Datagrams), receiver)));
    }

    [Fact]
    public async Task Without_negotiation_no_transport_wide_extension_is_stamped()
    {
        var (outbound, sender) = Outbound(stampsTransportCc: false);
        var receiver = new SrtpContext(Material());
        outbound.InstallOutboundKey(new SrtpContext(Material()));

        await outbound.SendAsync("audio", new byte[] { 1 });

        // Byte-preserving: with transport-cc off the packet carries no transport-wide sequence (only its MID).
        Assert.Null(ReadTransportCc(Decode(Assert.Single(sender.Datagrams), receiver)));
    }

    // ── Inbound arrivals → receive-side feedback sender emits a TWCC report ─────────────────────────

    [Fact]
    public void Inbound_stamped_arrivals_drive_the_feedback_sender_to_report()
    {
        // Wire the feedback sender to the inbound pipeline exactly as the session does (RtpPacketReceived).
        var sent = new List<byte[]>();
        long clock = 0;
        var feedback = new TransportCcFeedbackSender(
            new RtcpPacketCodec(), TransportCcExtId, AudioSsrc,
            (data, _) => { sent.Add(data.ToArray()); return ValueTask.CompletedTask; },
            () => clock, 1_000_000, NullLogger.Instance, CancellationToken.None);

        var inbound = InboundWith(feedback.OnVideoPacketReceived, out var receiverKey);

        // Feed three stamped video packets in (protected with the paired sender key), advancing the clock.
        var senderSrtp = new SrtpContext(Material());
        clock = 0;       inbound.ProcessInboundDatagram(StampedInbound(senderSrtp, transportSeq: 200, rtpSeq: 1), Peer);
        clock = 20_000;  inbound.ProcessInboundDatagram(StampedInbound(senderSrtp, transportSeq: 201, rtpSeq: 2), Peer);
        clock = 40_000;  inbound.ProcessInboundDatagram(StampedInbound(senderSrtp, transportSeq: 202, rtpSeq: 3), Peer);
        Assert.Empty(sent); // nothing until a flush

        feedback.FlushForTest();

        var report = Assert.IsType<RtcpTransportFeedback>(
            Assert.Single(new RtcpPacketCodec().Decode(Assert.Single(sent))));
        Assert.Equal(AudioSsrc, report.SenderSsrc);
        Assert.Equal(new ushort[] { 200, 201, 202 }, report.Statuses.Select(s => s.SequenceNumber).ToArray());
        Assert.All(report.Statuses, s => Assert.True(s.Received));

        _ = receiverKey; // keeps the inbound key alive for the datagram round-trip above
    }

    // ── Inbound TWCC feedback → sender-side controller registers overuse ────────────────────────────

    [Fact]
    public void Inbound_feedback_showing_growing_delay_drives_the_controller_to_overusing()
    {
        long clock = 0;
        var controller = new TransportCcCongestionController(
            TransportCcExtId, new TransportCcSendHistory(64),
            new TransportCcDelayTrendEstimator(1.0, 100), new TransportCcLossEstimator(1.0),
            new CongestionBitrateController(1_000_000, 100_000, 5_000_000, 100_000, 0.5, 0.1),
            () => clock, 1_000_000, NullLogger.Instance);

        // Wire it to the outbound pipeline's PacketSent, as the session does: each stamped send is recorded.
        var (outbound, _) = Outbound(stampsTransportCc: true);
        outbound.PacketSent += controller.OnPacketSent;
        outbound.InstallOutboundKey(new SrtpContext(Material()));

        clock = 0;     _ = SendAndCapture(outbound);
        clock = 1_000; _ = SendAndCapture(outbound);
        clock = 2_000; _ = SendAndCapture(outbound);

        // Peer arrivals 1250 µs apart vs 1000 µs inter-send → +250 µs gradient per packet → overuse.
        var feedback = TransportCcFeedbackBuilder.Build(
            new[]
            {
                new TransportCcArrival { SequenceNumber = 0, ArrivalTimestamp = 10_000 },
                new TransportCcArrival { SequenceNumber = 1, ArrivalTimestamp = 11_250 },
                new TransportCcArrival { SequenceNumber = 2, ArrivalTimestamp = 12_500 },
            },
            senderSsrc: 0xAAAA, mediaSsrc: 0x1234, feedbackPacketCount: 0, epochTimestamp: 0, ticksPerSecond: 1_000_000);

        // The session hands the controller the already-decoded compound (OnControlPacketReceived path).
        controller.OnRtcpPackets([feedback]);

        Assert.Equal(CongestionSignal.Overusing, controller.Signal);
        Assert.Equal(500_000, controller.RecommendedBitrateBps); // 1_000_000 × 0.5 back-off
        Assert.Equal(NetworkQuality.Poor, controller.Quality);
    }

    // Sends one audio packet and returns nothing — the controller records it via the PacketSent event.
    private static ValueTask SendAndCapture(BundledOutboundPipeline outbound) =>
        outbound.SendAsync("audio", new byte[] { 1 });

    // ── harness ─────────────────────────────────────────────────────────────────────────────────────

    private static (BundledOutboundPipeline pipeline, CapturingSender sender) Outbound(bool stampsTransportCc)
    {
        var sender = new CapturingSender();
        var pipeline = new BundledOutboundPipeline(
            new RtpPacketCodec(), sender, NullLogger<BundledOutboundPipeline>.Instance, stampsTransportCc);
        pipeline.RegisterTrack("audio", Track(AudioSsrc, AudioPayloadType, "audio", stampsTransportCc));
        pipeline.RegisterTrack("video", Track(VideoSsrc, VideoPayloadType, "video", stampsTransportCc));
        return (pipeline, sender);
    }

    private static BundledOutboundTrack Track(uint ssrc, byte payloadType, string mid, bool stampsTransportCc) =>
        new(ssrc, payloadType, samplesPerPacket: 160,
            new RtpOutboundHeaderExtensionStamper(
                stampsTransportCc ? TransportCcExtId : null, MidExtId, mid),
            InitialSeq, InitialTimestamp);

    // An inbound pipeline that forwards decoded RTP to the given hook (the feedback sender's OnVideoPacketReceived).
    private static BundledInboundPipeline InboundWith(Action<RtpPacket> onRtp, out SrtpContext receiverKey)
    {
        var demux = BundledRtpDemultiplexerFactory.Create(
            MidExtId,
            new Dictionary<string, IReadOnlyCollection<int>>
            {
                ["audio"] = new[] { (int)AudioPayloadType },
                ["video"] = new[] { (int)VideoPayloadType },
            });
        var router = new BundledTrackRouter(demux);
        router.RegisterTrack("audio", _ => { });
        router.RegisterTrack("video", _ => { });
        var pipeline = new BundledInboundPipeline(
            router, new RtpPacketCodec(), NullLogger<BundledInboundPipeline>.Instance);
        pipeline.RtpPacketReceived += onRtp;
        receiverKey = new SrtpContext(Material());
        pipeline.InstallInboundKeys(receiverKey, new SrtcpContext(Material()));
        return pipeline;
    }

    // Builds a video RTP packet stamped with the transport-cc sequence and the video MID, protected with the
    // sender's SRTP context so it round-trips through the paired inbound key.
    private static byte[] StampedInbound(SrtpContext senderSrtp, ushort transportSeq, ushort rtpSeq)
    {
        var packet = new RtpPacket
        {
            PayloadType = VideoPayloadType,
            SequenceNumber = rtpSeq,
            Ssrc = VideoSsrc,
            Payload = new byte[] { 9, 9, 9 },
            HeaderExtension = OneByteRtpHeaderExtensions.Encode(
            [
                RtpMidHeaderExtension.Element(MidExtId, "video"),
                OneByteRtpHeaderExtensions.TransportSequenceNumber(TransportCcExtId, transportSeq),
            ]),
        };
        return senderSrtp.Protect(new RtpPacketCodec().Encode(packet));
    }

    private static ushort? ReadTransportCc(RtpPacket packet) =>
        OneByteRtpHeaderExtensions.TryReadTransportSequenceNumber(packet.HeaderExtension, TransportCcExtId, out var seq)
            ? seq
            : null;

    private static RtpPacket Decode(byte[] srtpDatagram, SrtpContext receiver) =>
        new RtpPacketCodec().Decode(receiver.Unprotect(srtpDatagram));

    private static SrtpKeyMaterial Material() =>
        new()
        {
            MasterKey = MasterKey,
            MasterSalt = MasterSalt,
            Suite = SrtpCryptoSuite.AesCm128HmacSha1_80,
        };

    private sealed class CapturingSender : IBundledDatagramSender
    {
        public List<byte[]> Datagrams { get; } = [];

        public ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken)
        {
            Datagrams.Add(datagram.ToArray());
            return ValueTask.CompletedTask;
        }
    }
}
